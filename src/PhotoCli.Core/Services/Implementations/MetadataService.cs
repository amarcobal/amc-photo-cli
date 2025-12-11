using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using SharpExifTool;
using System.IO.Abstractions;
using System.Text;
using System.Linq;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models.Enums;
using System.Collections.Generic;
using System;
using PhotoCli.Core.Models.SpectreConsole;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace PhotoCli.Core.Services.Implementations;

// ESTRUCTURA MODIFICADA: Ahora incluye FileValidationResult para un conteo detallado robusto
public record AddPreviewResult(
	string FullPath,
	bool IdentityPassed,
	bool TemplatePassed,
	Dictionary<string, (string Value, string StatusColor, string DisplayText)> TagsResults,
	Dictionary<string, (string OriginalValue, string NewValue, bool Changed)> OverwriteDifferences,
	FileValidationResult ValidationResult
);

public class MetadataService : IMetadataService
{
	private readonly IFileSystem _fileSystem;
	private readonly IExifParserService _exifParserService;
	private readonly IExifDataAppenderService _exifDataAppenderService;
	private readonly ILogger<MetadataService> _logger;
	private readonly ToolOptions _options;
	private readonly Statistics _statistics;
	private readonly IConsoleWriter _consoleWriter;
	private readonly IReadOnlyDictionary<string, IReadOnlyCollection<TemplateTag>> _templateTagMap;

	private readonly IReadOnlyDictionary<string, Func<Photo, string?>> _photoPropertyMap =
		new Dictionary<string, Func<Photo, string?>>(StringComparer.OrdinalIgnoreCase)
	{
		// IDENTITY
		{ "AuthorID", p => p.Author?.ID },
		{ "AuthorAlias", p => p.Author?.Alias },
		{ "AuthorName", p => p.Author?.Name },
		{ "DeviceID", p => p.Device?.ID },
		{ "DeviceAlias", p => p.Device?.Alias },
		{ "DeviceName", p => p.Device?.Name },
		{ "DeviceSerialNumber", p => p.Device?.SerialNumber },
		
		// WORKFLOW
		{ "DerivedFileName", p => p.NewName },
		{ "DerivedFolderPath", p => p.TargetRelativePath },
		
		// ORIGIN
		{ "OriginalDateTime", p => p.TakenDateTime?.ToString("yyyy:MM:dd HH:mm:ss") },
		{ "OriginalFileName", p => p.PhotoFile.FileNameWithExtension },
		{ "Make", p => p.Make },
		{ "Model", p => p.Model },
		{ "OriginalSubseconds", p => p.HasSubSeconds ? p.Subseconds?.Padded() : new SubSeconds("0").Padded() },
	};

	public MetadataService(
		IFileSystem fileSystem,
		IExifParserService exifParserService,
		IMetadataConfigurationService configService,
		ILogger<MetadataService> logger,
		ToolOptions options,
		Statistics statistics,
		IConsoleWriter consoleWriter,
		IExifDataAppenderService exifDataAppenderService)
	{
		_fileSystem = fileSystem;
		_exifParserService = exifParserService;
		_logger = logger;
		_options = options;
		_statistics = statistics;
		_consoleWriter = consoleWriter;
		_exifDataAppenderService = exifDataAppenderService;

		_templateTagMap = configService.GetTemplateMap();
	}

	#region 1. Métodos Públicos - ESCRITURA (Add)

	public IReadOnlyCollection<Photo> AddMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, string metadataValue, bool isDryRun = false)
	{
		var tagsToWrite = new List<KeyValuePair<string, string>>
		{
			new(metadataKey, metadataValue)
		};
		// NOTA: WriteMetadataBatch no ha sido modificado para usar el Parallel.ForEach ni el Status Console.
		return WriteMetadataBatch(photos, tagsToWrite, $"Key/Value {metadataKey}", isDryRun);
	}

	public IReadOnlyDictionary<string, FileValidationResult> AddMetadataFromTemplate(
	IReadOnlyCollection<Photo> photos,
	string templateName,
	bool isDryRun,
	bool overwriteTags,
	bool allowUnknownIdentity)
	{
		var templateTags = GetTemplateTags(templateName);

		var finalValidationResults = new ConcurrentDictionary<string, FileValidationResult>(StringComparer.OrdinalIgnoreCase);
		var finalRunLogResults = new ConcurrentBag<AddPreviewResult>();
		var identityDetails = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// Contadores thread-safe
		int readyToProcessCount = 0;
		int skippedIdentityCount = 0;
		int failedByTemplateValidation = 0; // REFACTORIZADO
		int failedByWriteError = 0;         // NUEVO
		int processedCount = 0; // Para el Status Console
		int totalFiles = photos.Count;
		Stopwatch sw = Stopwatch.StartNew();

		if (!templateTags.Any())
		{
			_logger.LogWarning("Template '{Template}' no tiene tags definidos.", templateName);
			return new Dictionary<string, FileValidationResult>();
		}

		// -------------------------------------------------------------------------
		// !!! INICIO DEL PROCESAMIENTO PARALELO !!!
		// -------------------------------------------------------------------------
		Parallel.ForEach(photos, (photo) =>
		{
			var fullPath = photo.PhotoFile.SourceFullPath;

			// --- STATUS CONSOLE UPDATE (Thread-Safe) ---
			int currentProcessed = Interlocked.Increment(ref processedCount);
			UpdateConsoleStatus(currentProcessed, totalFiles, sw.Elapsed, photo.PhotoFile.FileName);
			// ------------------------------------------

			// Inicializar variables de estado y resultados locales
			bool templateError = false;
			bool templatePassed = false;
			bool writeSuccess = true;

			var identityCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var tagsToWriteForPhoto = new List<KeyValuePair<string, string>>();
			var tagExecutionDetails = new Dictionary<string, (string Value, string StatusColor, string DisplayText)>(); // <-- Log de ejecución/preview
			var overwriteDiffs = new Dictionary<string, (string OriginalValue, string NewValue, bool Changed)>();


			// 1. Validar Identidad (Generando TagValidationResults)
			var (identityPassed, identityLog) = ValidateIdentityAndGenerateTags(photo, allowUnknownIdentity, identityCheckResults);
			identityDetails[fullPath] = identityLog;

			Photo currentPhoto = photo; // Usaremos esta variable, que podría ser actualizada

			if (identityPassed)
			{
				// 2. Resolver Tags y aplicar lógica de Overwrite
				foreach (var tag in templateTags)
				{
					// Nota: Usamos isDryRun=true en ResolveTagValue para obtener la simulación del valor literal,
					// incluso si estamos en modo Real, a menos que el tag sea de tipo ExifToolTag.
					string? resolvedValue = ResolveTagValue(currentPhoto, tag, isDryRun, templateTags);

					// Manejo de error de configuración
					if (resolvedValue == "CONFIG_ERROR")
					{
						templateError = true;

						// Capturamos el error de configuración para la tabla de log
						tagExecutionDetails[tag.Name] = ("Config Error", "red", "[bold white on red]CONFIG ERROR (Missing Prop Map)[/]");

						templateCheckResults[tag.Name] = new TagValidationResult(false, "CONFIG_ERROR", tag.Required, false);
						continue;
					}

					// Validación del valor resuelto (Missing)
					if (string.IsNullOrWhiteSpace(resolvedValue) || resolvedValue == new SubSeconds("0").Padded())
					{
						if (tag.Required)
						{
							templateError = true;
							if (isDryRun) tagExecutionDetails[tag.Name] = ("MISSING", "red", "[bold white on red]MISSING[/]");
							templateCheckResults[tag.Name] = new TagValidationResult(false, string.Empty, tag.Required, false);
						}
						else if (isDryRun)
						{
							tagExecutionDetails[tag.Name] = ("(Empty)", "dim", "[dim](Empty)[/]");
							templateCheckResults[tag.Name] = new TagValidationResult(false, string.Empty, tag.Required, true);
						}
						continue;
					}

					// Valor resuelto (pasa)
					if (isDryRun)
						templateCheckResults[tag.Name] = new TagValidationResult(true, resolvedValue, tag.Required, true);


					// Lógica de Overwrite
					string? existingVal = currentPhoto.ExifData?.Metadata.TryGetValue(tag.Name, out var val) == true ? val : null;
					bool tagExistsInFile = existingVal != null;
					bool shouldWrite = !tagExistsInFile || overwriteTags;

					if (shouldWrite)
					{
						tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, resolvedValue));

						if (isDryRun)
						{
							string statusColor;
							string displayText;

							if (tagExistsInFile && overwriteTags)
							{
								// CASO OVERWRITE: Calcular la diferencia
								bool changed = !existingVal!.Equals(resolvedValue, StringComparison.OrdinalIgnoreCase);
								overwriteDiffs[tag.Name] = (existingVal.EscapeMarkup(), resolvedValue.EscapeMarkup(), changed);

								if (changed)
								{
									// Cambio de valor (Amarillo)
									displayText = $"[bold]{existingVal.EscapeMarkup()}[/] → [yellow]{resolvedValue.EscapeMarkup()}[/]";
									statusColor = "yellow";
								}
								else
								{
									// Mismo valor (Dim)
									displayText = $"[dim]{resolvedValue.EscapeMarkup()}[/] (No Change)";
									statusColor = "dim";
								}
							}
							else
							{
								// CASO NUEVO TAG (Cyan)
								displayText = $"[cyan]{resolvedValue.EscapeMarkup()}[/] (New)";
								statusColor = "cyan";
							}

							tagExecutionDetails[tag.Name] = (resolvedValue, statusColor, displayText);
						}
						else
						{
							// MODO REAL: Placeholder (se sobrescribirá si la escritura es exitosa)
							tagExecutionDetails[tag.Name] = (resolvedValue!, "dim", "[dim]Writing...[/]");
						}
					}
					else
					{
						// El tag existe y NO estamos sobrescribiendo (Kept)

						if (isDryRun)
						{
							string displayText = $"[dim]{existingVal!.EscapeMarkup()}[/] (Kept)";
							tagExecutionDetails[tag.Name] = (existingVal, "dim", displayText);
						}
						else
						{
							// MODO REAL: Mantenido. Se muestra inmediatamente
							tagExecutionDetails[tag.Name] = (existingVal!, "dim", $"[dim]{existingVal!.EscapeMarkup()}[/] (Kept)");
						}
					}
				} // Fin foreach tag

				templatePassed = !templateError;

				if (templatePassed)
				{
					// 3. Ejecución Real y Refresco
					// writeSuccess ya está inicializada en true

					// Contar los archivos listos para procesar, incluso en Dry Run.
					Interlocked.Increment(ref readyToProcessCount);

					if (!isDryRun && tagsToWriteForPhoto.Any())
					{
						// Escribir metadatos Y Refrescar la instancia currentPhoto
						(writeSuccess, var tempUpdatedPhoto) = WriteMetadataForPhoto(photo, tagsToWriteForPhoto, $"Template {templateName}");

						if (writeSuccess && tempUpdatedPhoto != null)
						{
							currentPhoto = tempUpdatedPhoto;

							// Revalidar los tags del template con los datos frescos para el log final
							(templatePassed, templateCheckResults) = RevalidateTemplateTags(currentPhoto, templateTags);

							// ¡ACTUALIZAR tagExecutionDetails con los valores leídos del archivo!
							RecalculateTagDetailsForSuccessfulWrite(currentPhoto, templateTags, tagExecutionDetails);

						}
						else
						{
							writeSuccess = false; // Fallo de escritura/refresco
						}
					}

					if (!writeSuccess && !isDryRun)
					{
						// Fallo de ESCRITURA REAL (I/O)
						templatePassed = false;
						Interlocked.Increment(ref failedByWriteError); // USAMOS EL NUEVO CONTADOR
						_statistics.InternalError++;
						Interlocked.Decrement(ref readyToProcessCount); // No se procesó con éxito
					}
				}
				else
				{
					// Fallo de validación del template (MISSING, CONFIG_ERROR, etc.)
					Interlocked.Increment(ref failedByTemplateValidation); // USAMOS EL NUEVO CONTADOR
					if (!isDryRun) _statistics.InternalError++;
				}
			} // Fin if (identityPassed)
			else
			{
				// Fallo de identidad
				Interlocked.Increment(ref skippedIdentityCount);
				if (!isDryRun) _statistics.InternalError++;
			}

			// 4. Almacenar resultados para la tabla y el retorno (Final Validation)
			var finalTemplateCheck = templateCheckResults;

			if (!identityPassed)
			{
				templatePassed = false;
				finalTemplateCheck = new Dictionary<string, TagValidationResult>();
			}

			var validationResult = new FileValidationResult
			{
				IdentityTags = identityCheckResults,
				TemplateTags = finalTemplateCheck
			};

			finalRunLogResults.Add(new AddPreviewResult(
				fullPath,
				identityPassed,
				templatePassed,
				tagExecutionDetails,
				overwriteDiffs,
				validationResult
			));

			finalValidationResults[fullPath] = validationResult;
		}); // FIN Parallel.ForEach

		// -------------------------------------------------------------------------
		// !!! FIN DEL PROCESAMIENTO PARALELO !!!
		// -------------------------------------------------------------------------
		sw.Stop();
		_consoleWriter.ClearStatusLine();

		// 5. Visualización de la Tabla y Resumen (DryRun O EJECUCIÓN REAL)
		PrintAddPreviewTable(
			photos,
			templateTags,
			finalRunLogResults.ToList(),
			identityDetails.ToDictionary(kv => kv.Key, kv => kv.Value),
			templateName,
			overwriteTags,
			readyToProcessCount,
			skippedIdentityCount,
			failedByTemplateValidation, // PASAMOS EL CONTADOR DE VALIDACIÓN
			failedByWriteError,         // PASAMOS EL CONTADOR DE ESCRITURA
			allowUnknownIdentity,
			isDryRun);

		// Devolver el resultado de validación completo al Runner
		return finalValidationResults.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
	}

	#endregion

	#region 2. Métodos Públicos - BORRADO (Delete)

	public IReadOnlyCollection<Photo> DeleteMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, bool isDryRun = false)
	{
		var tagsToDelete = new List<string> { metadataKey };

		// Nota: Considerar Parallel.ForEach aquí si es necesario
		foreach (var photo in photos)
		{
			try
			{
				using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
				{
					if (isDryRun)
					{
						_consoleWriter.Write($"[yellow bold][DryRun][/] Delete metadata [cyan]{metadataKey}[/] from [bold]{photo.PhotoFile.SourceFullPath.EscapeMarkup()}[/]");
						continue;
					}

					exifTool.DeleteTag(photo.PhotoFile.SourcePath, tagsToDelete, overwriteOriginal: true);
					_statistics.PhotosMetadataProcessed++;
					_consoleWriter.Write($"🗑️ [bold]Deleted[/] metadata [cyan]{metadataKey}[/] from [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/]");
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error deleting metadata from {File}", photo.PhotoFile.SourcePath);
				_statistics.InternalError++;
			}
		}
		return photos;
	}

	public IReadOnlyCollection<Photo> DeleteMetadataFromTemplate(IReadOnlyCollection<Photo> photos, string templateName, bool isDryRun = false)
	{
		var tagsToDelete = GetTagNamesFromTemplate(templateName);

		if (!tagsToDelete.Any())
		{
			_logger.LogWarning("Template '{Template}' has no tags defined for deletion. Operación abortada.", templateName);
			return photos;
		}

		// Nota: Considerar Parallel.ForEach aquí si es necesario
		foreach (var photo in photos)
		{
			try
			{
				using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
				{
					if (isDryRun)
					{
						_consoleWriter.Write($"[yellow bold][DryRun][/] Delete metadata from Template [bold]{templateName.EscapeMarkup()}[/] ([dim]{string.Join(", ", tagsToDelete)}[/]) from [bold]{photo.PhotoFile.SourceFullPath.EscapeMarkup()}[/]");
						continue;
					}

					exifTool.DeleteTag(photo.PhotoFile.SourcePath, tagsToDelete, overwriteOriginal: true);
					_statistics.PhotosMetadataProcessed++;
					_consoleWriter.Write($"🗑️ [bold]Deleted[/] metadata from Template [bold]{templateName.EscapeMarkup()}[/] in [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/]");
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error deleting metadata from Template {Template} in {File}", templateName, photo.PhotoFile.SourcePath);
				_statistics.InternalError++;
			}
		}
		return photos;
	}

	#endregion

	#region 3. Métodos Públicos - LECTURA (Get)

	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadata(
		IReadOnlyCollection<Photo> photos, string metadataKey, bool showOutput = false)
	{
		return GetMetadata(photos, new[] { metadataKey }, showOutput);
	}

	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos, string templateName, bool showOutput = false)
	{
		var keys = GetTagNamesFromTemplate(templateName);
		return GetMetadata(photos, keys, showOutput);
	}

	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadata(
		IReadOnlyCollection<Photo> photos, IEnumerable<string> metadataKeys, bool showOutput = false)
	{
		if (metadataKeys == null || !metadataKeys.Any())
			return new Dictionary<string, IReadOnlyDictionary<string, string>>();

		var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
		var keysArray = metadataKeys.ToArray();

		// Nota: Considerar Parallel.ForEach aquí si es necesario
		foreach (var photo in photos)
		{
			try
			{
				ICollection<KeyValuePair<string, string>> extractedMetadata;
				using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
				{
					extractedMetadata = exifTool.GetMetadata(photo.PhotoFile.SourcePath, keysArray);
				}

				var filteredMetadata = extractedMetadata
					.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

				if (showOutput)
				{
					var formatted = filteredMetadata.Count == 0
						? "[yellow]No matching metadata keys found.[/]"
						: string.Join(", ", filteredMetadata.Select(kv => $"[bold]{kv.Key}[/]='[cyan]{kv.Value.EscapeMarkup()}'[/]'"));

					_consoleWriter.Write($"📸 [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/] -> {formatted}");
				}

				result[photo.PhotoFile.SourceFullPath] = filteredMetadata;
				_statistics.PhotosMetadataProcessed++;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error getting metadata from {File}", photo.PhotoFile.SourcePath);
				_statistics.InternalError++;
			}
		}
		return result;
	}

	#endregion

	#region 4. Métodos Públicos - VALIDACIÓN (Check)

	public IReadOnlyDictionary<string, FileValidationResult> CheckMetadata(
		IReadOnlyCollection<Photo> photos, string metadataKey, bool isRequired = true)
	{
		var singleTagList = new List<TemplateTag> { new() { Name = metadataKey, Required = isRequired } };
		var defaultView = new List<MetadataCheckViewType> { MetadataCheckViewType.TemplateDetails }.AsReadOnly();
		return CheckMetadataFromTemplateBase(photos, singleTagList, $"Key {metadataKey}", defaultView, allowUnknownIdentity: true);
	}

	public IReadOnlyDictionary<string, FileValidationResult> CheckMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos,
		string templateName,
		IReadOnlyCollection<MetadataCheckViewType> viewTypes,
		bool allowUnknownIdentity)
	{
		var templateTags = GetTemplateTags(templateName);
		return CheckMetadataFromTemplateBase(photos, templateTags, $"Template {templateName}", viewTypes, allowUnknownIdentity);
	}

	private IReadOnlyDictionary<string, FileValidationResult> CheckMetadataFromTemplateBase(
		IReadOnlyCollection<Photo> photos,
		IReadOnlyCollection<TemplateTag> templateTags,
		string contextName,
		IReadOnlyCollection<MetadataCheckViewType> viewTypes,
		bool allowUnknownIdentity)
	{
		// 1. Configuración de vista
		bool showTemplateColumn = viewTypes.Contains(MetadataCheckViewType.Template) || viewTypes.Contains(MetadataCheckViewType.TemplateDetails);
		bool showTemplateDetails = viewTypes.Contains(MetadataCheckViewType.TemplateDetails);
		bool showIdentityColumn = viewTypes.Contains(MetadataCheckViewType.Identity) || viewTypes.Contains(MetadataCheckViewType.IdentityDetails);
		bool showIdentityDetails = viewTypes.Contains(MetadataCheckViewType.IdentityDetails);

		var result = new Dictionary<string, FileValidationResult>(StringComparer.OrdinalIgnoreCase);

		int passedCount = 0;
		int failedCount = 0;


		// 2. Definición de Columnas
		var columns = new List<TableColumnConfig>
		{
			new() { HeaderText = "File", Width = 40, NoWrap = false },
			new() { HeaderText = "Status", Width = 18, NoWrap = true }
		};

		if (showIdentityColumn) columns.Add(new() { HeaderText = "Media Identity", NoWrap = false, Width = showIdentityDetails ? null : 40 });
		if (showTemplateColumn) columns.Add(new() { HeaderText = $"Tags: {contextName.Replace("Template ", "").Trim()}", NoWrap = true, Width = null });

		var rows = new List<List<string>>();

		// 3. Procesamiento
		foreach (var photo in photos)
		{
			var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var identityCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var photoMetadataDict = photo.ExifData.Metadata;

			// --- A. Validación de Template Tags ---
			foreach (var tag in templateTags)
			{
				// 1. LLAMAR A RESOLVETAGVALUE para obtener el valor real/calculado.
				// Usamos isDryRun:true aquí para obtener el valor literal/calculado, no el comando de copia (<Tag)
				string? resolvedValue = ResolveTagValue(photo, tag, isDryRun: true, templateTags);

				// 2. Determinar si existe y es un valor utilizable
				bool hasValue = !string.IsNullOrWhiteSpace(resolvedValue) &&
								!resolvedValue!.Equals("undefined", StringComparison.OrdinalIgnoreCase) &&
								!resolvedValue!.Equals("CONFIG_ERROR", StringComparison.OrdinalIgnoreCase); // También descartamos errores de configuración

				// 3. Determinar la validez
				bool isValid = hasValue || !tag.Required;

				// 4. Registrar el resultado
				templateCheckResults[tag.Name] = new TagValidationResult(
					hasValue,
					resolvedValue ?? string.Empty,
					tag.Required,
					isValid);
			}
			bool templatePassed = templateCheckResults.Values.All(tv => tv.IsValid);

			// --- B. Validación de Identidad ---
			var (identityPassed, identityLog) = ValidateIdentityAndGenerateTags(photo, allowUnknownIdentity, identityCheckResults);

			// --- C. Lógica de Estado Global (Status Column) ---
			bool identityIsFail = !identityPassed;
			bool templateIsFail = !templatePassed;
			bool rowPassed = !identityIsFail && !templateIsFail;

			string statusStyled;
			if (rowPassed)
			{
				passedCount++;
				if (identityLog.Contains("[yellow]△[/]"))
					statusStyled = "[bold yellow]△  Warn (Identity)[/]";
				else
					statusStyled = "[bold green]✔  OK[/]";
			}
			else
			{
				failedCount++;
				if (identityIsFail && templateIsFail) statusStyled = "[bold red]❌ KO (Both)[/]";
				else if (identityIsFail) statusStyled = "[bold red]❌ KO (Identity)[/]";
				else statusStyled = "[bold red]❌ KO (Template)[/]";
			}

			// --- D. Construcción de filas para la tabla ---
			var row = new List<string> { Markup.Escape(photo.PhotoFile.SourceFullPath), statusStyled };

			if (showIdentityColumn)
			{
				if (showIdentityDetails) row.Add(identityLog);
				else row.Add(identityPassed ? "[green]Valid[/]" : "[red]Invalid[/]");
			}

			if (showTemplateColumn)
			{
				if (showTemplateDetails)
				{
					var templateBuilder = new StringBuilder();
					foreach (var tag in templateTags)
					{
						var check = templateCheckResults[tag.Name];
						var reqStatus = tag.Required ? "[red]*[/]" : "[dim]*[/]";
						string valDisplay = !check.IsValid
							? (check.HasValue ? $"[red]{check.Value.EscapeMarkup()}[/]" : "[bold white on red]MISSING[/]")
							: (check.HasValue ? $"[cyan]{check.Value.EscapeMarkup()}[/]" : "[dim](Empty)[/]");

						templateBuilder.AppendLine($"{reqStatus} [teal]{tag.Name.EscapeMarkup()}[/]: {valDisplay}");
					}
					row.Add(templateBuilder.ToString());
				}
				else row.Add(templatePassed ? "[green]Valid[/]" : "[red]Invalid[/]");
			}

			rows.Add(row);

			// ALMACENAMIENTO DEL RESULTADO FINAL
			result[photo.PhotoFile.SourceFullPath] = new FileValidationResult
			{
				IdentityTags = identityCheckResults.ToDictionary(kv => kv.Key, kv => kv.Value),
				TemplateTags = templateCheckResults.ToDictionary(kv => kv.Key, kv => kv.Value)
			};
		}

		// 4. Escribir Tabla
		_consoleWriter.WriteMarkup("[dim] [/]");
		_consoleWriter.WriteTable(columns, rows, $"Metadata Check: {contextName}");
		_consoleWriter.WriteMarkup("[dim] [/]");

		return result;
	}


	#endregion

	#region 5. Métodos Privados (Helpers)

	/// <summary>
	/// Valida la identidad y genera los resultados detallados (TagValidationResult) y el log de salida.
	/// </summary>
	private (bool IsValid, string LogOutput) ValidateIdentityAndGenerateTags(
		Photo photo,
		bool allowUnknown,
		Dictionary<string, TagValidationResult> identityCheckResults)
	{
		var checks = new (string Key, Func<Photo, string?> Getter, string DisplayName, bool IsExif)[]
		{
			("TakenDate", p => p.TakenDateTime?.ToString("yyyy-MM-dd HH:mm:ss"), "Taken Date", true),
			("Make", p => p.Make, "Make", true),
			("Model", p => p.Model, "Model", true),
			("Author", p => p.Author?.ID, "Author", false),
			("Device", p => p.Device?.ID, "Device", false)
		};

		bool allOk = true;
		var sb = new StringBuilder();

		foreach (var check in checks)
		{
			var val = check.Getter(photo);

			// 1. Determinar el estado del valor
			bool isUnknown = !check.IsExif && val == "Unknown";
			bool hasValue = !string.IsNullOrWhiteSpace(val) && val != "Unknown";

			// 2. Determinar la validez para el chequeo general (allOk)
			bool isValidCheck;

			if (check.Key == "TakenDate")
			{
				isValidCheck = hasValue;
			}
			else if (allowUnknown)
			{
				isValidCheck = true; // Permite Unknown/Missing si allowUnknown es true
			}
			else
			{
				isValidCheck = hasValue && !isUnknown; // Falla si es Unknown/Missing si allowUnknown es false
			}

			if (!isValidCheck)
			{
				allOk = false;
			}

			// 3. Determinar el requerimiento para el TagValidationResult
			bool required = check.Key == "TakenDate" || !allowUnknown;

			// 4. Creación del TagValidationResult
			identityCheckResults[check.Key] = new TagValidationResult(
				hasValue,
				val ?? string.Empty,
				IsRequired: required,
				IsValid: isValidCheck);

			// 5. Generación del Log de Salida (usando '△' en lugar de '⚠️')
			if (isValidCheck)
			{
				if (check.Key != "TakenDate" && (!hasValue || isUnknown))
				{
					// Es Warning (Permitido) -> Usamos '△'. Esto ocurre solo si allowUnknown=true
					string status = isUnknown ? "Unknown" : "MISSING";
					sb.AppendLine($"[yellow]△[/] [dim] {check.DisplayName}:[/] [yellow]{status} (Allowed)[/]");
				}
				else
				{
					// Es OK -> Usamos '✔'
					sb.AppendLine($"[green]✔[/] [dim] {check.DisplayName}:[/] [cyan]{val!.EscapeMarkup()}[/]");
				}
			}
			else
			{
				// Es un Fallo Fatal (Red) -> Usamos '✖'
				string statusDisplay;
				if (check.Key == "TakenDate" && !hasValue)
				{
					statusDisplay = "[bold white on red]MISSING (CRITICAL)[/]";
				}
				else if (!hasValue)
				{
					statusDisplay = "[bold white on red]MISSING[/]";
				}
				else
				{
					statusDisplay = "[bold red]Unknown (Not Allowed)[/]";
				}
				sb.AppendLine($"[bold red]✖[/] [dim] {check.DisplayName}:[/] {statusDisplay}");
			}
		}

		return (allOk, sb.ToString());
	}


	// Método específico para pintar la tabla de Add Preview/Execution Log - REFACTORIZADO
	private void PrintAddPreviewTable(
		IReadOnlyCollection<Photo> photos,
		IReadOnlyCollection<TemplateTag> templateTags,
		IReadOnlyCollection<AddPreviewResult> finalRunResults,
		Dictionary<string, string> identityDetails,
		string templateName,
		bool overwriteTags,
		int readyToProcessCount,
		int skippedIdentityCount,
		int failedByTemplateValidation, // REFACTORIZADO: Fallos de validación/cálculo
		int failedByWriteError,         // NUEVO: Fallos de I/O en la escritura
		bool allowUnknownIdentity,
		bool isDryRun)
	{
		var columns = new List<TableColumnConfig>
		{
			new() { HeaderText = "File", Width = 40 },
			new() { HeaderText = "Status", Width = 18, NoWrap = true },
			new() { HeaderText = "Media Identity", Width = null },
			new() { HeaderText = $"Proposed Tags (Template: {templateName})", Width = null }
		};

		var rows = new List<List<string>>();

		// **********************************************
		// CONTADORES DETALLADOS PARA EL RESUMEN FINAL
		// **********************************************
		int keptUnchangedCount = 0;
		int filesReadyToWrite = 0;
		int failedByIdentity = 0;
		// failedByTemplate es la suma de los dos nuevos contadores de fallo de template/escritura
		int failedByTemplate = failedByTemplateValidation + failedByWriteError;
		int allowedUnknownWarnings = 0;
		int missingTakenDate = 0;
		int missingDevice = 0;
		int missingAuthor = 0;
		int missingMakeModel = 0;


		foreach (var result in finalRunResults)
		{
			var fullPath = result.FullPath;

			string status;
			// 1. Calcular Status de Fila (y contadores de fallo/warning)
			if (result.IdentityPassed && result.TemplatePassed)
			{
				if (isDryRun)
				{
					status = "[bold green]✅ Ready[/]";
				}
				else
				{
					status = "[bold green]✔ SUCCESS[/]";
				}
				filesReadyToWrite++;
			}
			else
			{
				// Fallo de Identidad, Template o de ESCRITURA REAL
				if (!result.IdentityPassed) failedByIdentity++;

				// La fila falló el Template/Escritura si no pasó el Identity Y el Template
				// Nota: Los contadores 'failedByTemplateValidation' y 'failedByWriteError' ya se incrementaron en el bucle Parallel.
				// Aquí solo necesitamos saber si la fila falló el Template para el mensaje de status.
				bool templateFailedForThisRow = !result.TemplatePassed;

				if (!result.IdentityPassed && templateFailedForThisRow)
				{
					status = isDryRun ? "[bold red]❌ Skip (Both)[/]" : "[bold red]✖ FAILED (Both)[/]";
				}
				else if (!result.IdentityPassed)
				{
					status = isDryRun ? "[bold red]❌ Skip (Identity)[/]" : "[bold red]✖ FAILED (Identity)[/]";
				}
				else // templateFailedForThisRow es true
				{
					status = isDryRun ? "[bold red]❌ Skip (Template)[/]" : "[bold red]✖ FAILED (Write/Tpl)[/]";
				}
			}

			// 2. Contadores Detallados (USANDO TagValidationResult)
			var identityTags = result.ValidationResult.IdentityTags;

			// Solo contamos los fallos detallados si el archivo Falló la Identidad (failedByIdentity)
			if (!result.IdentityPassed)
			{
				// a) Fallos Críticos (Red - contamos directamente si el tag falló la validación)

				// TakenDate (siempre requerido y falla si !IsValid)
				if (identityTags.TryGetValue("TakenDate", out var takenDateTag) && !takenDateTag.IsValid)
					missingTakenDate++;

				// Otros tags fallan si son requeridos (!IsValid && IsRequired)
				if (identityTags.TryGetValue("Device", out var deviceTag) && !deviceTag.IsValid && deviceTag.IsRequired)
					missingDevice++;
				if (identityTags.TryGetValue("Author", out var authorTag) && !authorTag.IsValid && authorTag.IsRequired)
					missingAuthor++;

				// Make/Model: Fallan si son requeridos y Make o Model no son válidos.
				if (identityTags.TryGetValue("Make", out var makeTag) && identityTags.TryGetValue("Model", out var modelTag) && makeTag.IsRequired)
				{
					// Se asume que Make y Model son necesarios para considerar una 'identidad de dispositivo' completa.
					if (!makeTag.IsValid || !modelTag.IsValid)
						missingMakeModel++;
				}
			}
			else if (result.IdentityPassed && allowUnknownIdentity)
			{
				// b) Warnings (Yellow △ - Contamos los que pasaron pero tienen valor vacío/unknown)
				var identityLog = identityDetails[fullPath];
				if (identityLog.Contains("[yellow]△[/]"))
				{
					allowedUnknownWarnings++;
				}
			}

			// 3. Contar Kept Unchanged (Solo si la validación final fue exitosa)
			if (result.IdentityPassed && result.TemplatePassed)
			{
				// Verificamos si hay algún tag nuevo (cyan) o cambiado (amarillo/overwrite diff)
				bool hasAnyChange = result.OverwriteDifferences.Any(d => d.Value.Changed) ||
									result.TagsResults.Any(t => t.Value.StatusColor == "cyan"); // StatusColor: cyan = New

				if (!hasAnyChange)
				{
					// Este archivo pasó la validación PERO no tiene nada nuevo que hacer.
					keptUnchangedCount++;
				}
			}

			// Construir celda de tags propuestos (usando DisplayText)
			var tagsBuilder = new StringBuilder();
			foreach (var tag in templateTags)
			{
				string line;
				var reqStatus = tag.Required ? "[red]*[/]" : "[dim]*[/]";

				if (result.TagsResults.TryGetValue(tag.Name, out var tagResult))
				{
					line = $"{reqStatus} [teal]{tag.Name.EscapeMarkup()}[/]: {tagResult.DisplayText}";
				}
				else if (tag.Required)
				{
					// Fallo en la fase de resolución/validación (debería haber sido capturado antes)
					line = $"{reqStatus} [teal]{tag.Name.EscapeMarkup()}[/]: [bold white on red]MISSING CALCULATION[/]";
				}
				else continue;

				tagsBuilder.AppendLine(line);
			}


			rows.Add(new List<string>
			{
				Markup.Escape(fullPath),
				status,
				identityDetails.ContainsKey(fullPath) ? identityDetails[fullPath] : "[bold red]IDENTITY ERROR[/]",
				tagsBuilder.ToString()
			});
		} // Fin foreach result

		// Recalcular ReadyToProcess para el resumen: Solo aquellos que realmente cambian
		readyToProcessCount = filesReadyToWrite - keptUnchangedCount;


		// --- TÍTULO DE LA SECCIÓN ---
		if (isDryRun)
		{
			_consoleWriter.WriteMarkup("\n[bold yellow]--- DRY RUN PREVIEW (No changes applied) ---[/]");
		}
		else
		{
			_consoleWriter.WriteMarkup("\n[bold green]--- EXECUTION LOG (Changes applied) ---[/]");
		}

		if (overwriteTags)
			_consoleWriter.WriteMarkup("[dim]Info: --overwrite-tags is ON. Existing values were updated/kept.[/]");
		else
			_consoleWriter.WriteMarkup("[dim]Info: --overwrite-tags is OFF. Existing values were kept (Kept).[/]");

		_consoleWriter.WriteTable(columns, rows, $"Metadata Add {(isDryRun ? "Simulation" : "Execution")}: {templateName}");
		_consoleWriter.WriteMarkup("[dim] [/]");


		// -------------------------------------------------------------------------
		// REPLICACIÓN EXACTA DEL COMMAND RESULT DEL RUNNER
		// -------------------------------------------------------------------------
		var rule = new Rule("[bold]COMMAND RESULT[/]");
		rule.Justification = Justify.Center;
		rule.Style = new Style(foreground: Color.Yellow);
		AnsiConsole.Write(rule);

		var totalFiles = photos.Count;
		// Passed/Failed para el resumen general son los que terminaron en Ready/Skip.
		var passedSimulated = readyToProcessCount; // Archivos que cambiaron o se van a cambiar
		var failedSimulated = skippedIdentityCount + failedByTemplate; // Archivos saltados o fallidos

		_consoleWriter.WriteMarkup($"[yellow]Total Files processed:[/] [bold]{totalFiles}[/]");

		if (filesReadyToWrite == totalFiles && keptUnchangedCount == 0 && allowedUnknownWarnings == 0)
		{
			_consoleWriter.WriteMarkup($"[green]✅ All files {(isDryRun ? "ready for processing" : "processed successfully")}.[/]");
		}
		else
		{
			_consoleWriter.WriteMarkup($"[green]✅ Passed (Write/Update):[/] [bold]{passedSimulated}[/]");
			_consoleWriter.WriteMarkup($"[red]🛑 Failed (Skipped):[/] [bold]{failedSimulated}[/]");
		}

		// -------------------------------------------------------------------------
		// FAILURE BREAKDOWN
		// -------------------------------------------------------------------------
		if (failedByIdentity > 0 || failedByTemplate > 0)
			_consoleWriter.WriteMarkup("\n[bold underline]Failure Breakdown:[/]");

		if (failedByIdentity > 0)
			_consoleWriter.WriteMarkup($"• [yellow]Required Data (Identity) Missing/Invalid:[/][bold red] {failedByIdentity}[/] files");

		// El fallo de template/escritura (failedByTemplate)
		if (failedByTemplate > 0)
		{
			var failureType = isDryRun ? "Missing" : "Missing / Write Error";
			_consoleWriter.WriteMarkup($"• [yellow]Required Tags (Template) {failureType}:[/][bold red] {failedByTemplate}[/] files");
		}

		// -------------------------------------------------------------------------
		// TEMPLATE / WRITE FAILURE DETAILS (NUEVO DESGLOSE)
		// -------------------------------------------------------------------------
		if (failedByTemplateValidation > 0 || failedByWriteError > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Template / Write Failure Details:[/]");
			if (failedByTemplateValidation > 0)
				_consoleWriter.WriteMarkup($"  - Validation Failed (Missing Tags/Calc Error): [red]{failedByTemplateValidation}[/]");

			if (failedByWriteError > 0)
				_consoleWriter.WriteMarkup($"  - Write Operation Failed (I/O Error): [red]{failedByWriteError}[/]");
		}

		// -------------------------------------------------------------------------
		// MISSING IDENTITY DATA DETAILS
		// -------------------------------------------------------------------------
		if (failedByIdentity > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Missing Identity Data Details:[/]");
			if (missingTakenDate > 0) _consoleWriter.WriteMarkup($"  - Missing Taken Date: [red]{missingTakenDate}[/]");
			if (missingMakeModel > 0) _consoleWriter.WriteMarkup($"  - Missing Make/Model: [red]{missingMakeModel}[/]");
			if (missingDevice > 0) _consoleWriter.WriteMarkup($"  - Missing Device ID:  [red]{missingDevice}[/]");
			if (missingAuthor > 0) _consoleWriter.WriteMarkup($"  - Missing Author ID:  [red]{missingAuthor}[/]");
		}

		// -------------------------------------------------------------------------
		// IDENTITY WARNINGS (ALLOW UNKNOWN)
		// -------------------------------------------------------------------------
		if (allowedUnknownWarnings > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Identity Warnings (Allow Unknown):[/]");
			_consoleWriter.WriteMarkup($"• [yellow]Files accepted with missing/unknown identity:[/][bold] {allowedUnknownWarnings}[/] files");
			_consoleWriter.WriteMarkup("  [dim](These files were allowed to pass Identity Check due to the allow-unknown-identity flag)[/]");
		}

		// -------------------------------------------------------------------------
		// KEPT/UNCHANGED INFO
		// -------------------------------------------------------------------------
		if (keptUnchangedCount > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Kept Information:[/]");
			_consoleWriter.WriteMarkup($"• [yellow]No changes detected (Kept):[/] {keptUnchangedCount} files");
		}


		_consoleWriter.WriteMarkup("\n[yellow]Review the table above for specific file details.[/]");
	}


	/// <summary>
	/// Actualiza el diccionario tagExecutionDetails con los valores leídos del disco después de una escritura exitosa.
	/// </summary>
	private void RecalculateTagDetailsForSuccessfulWrite(
		Photo photo,
		IReadOnlyCollection<TemplateTag> templateTags,
		Dictionary<string, (string Value, string StatusColor, string DisplayText)> tagExecutionDetails)
	{
		var photoMetadataDict = photo.ExifData.Metadata;

		foreach (var tag in templateTags)
		{
			// Si el tag ya fue marcado como 'Kept' en la fase de simulación (que no se iba a escribir), lo conservamos
			// y solo actualizamos los tags que se intentaron escribir (aquellos que no están marcados como Kept/Empty).

			if (tagExecutionDetails.TryGetValue(tag.Name, out var existingResult) &&
				existingResult.DisplayText.Contains("(Kept)"))
			{
				continue;
			}

			// 1. Intentamos obtener el valor final REAL que se leyó del archivo
			bool existsInFile = photoMetadataDict.TryGetValue(tag.Name, out var actualValue) &&
							!string.IsNullOrWhiteSpace(actualValue) &&
							!actualValue.Equals("undefined", StringComparison.OrdinalIgnoreCase);

			string tagValueDisplay = actualValue.EscapeMarkup() ?? string.Empty;

			if (existsInFile)
			{
				// El tag fue escrito y verificado en el disco.
				// Usamos el valor real leído del archivo (actualValue)
				string displayText = $"[green]{tagValueDisplay}[/] (Wrote/Verified)";
				tagExecutionDetails[tag.Name] = (tagValueDisplay, "green", displayText);
			}
			else if (tag.Required)
			{
				// Fallo de verificación: Estaba requerido/se intentó escribir, pero el valor no se encuentra.
				string displayText = $"[bold red]✖ FAILED: Missing After Write[/]";
				tagExecutionDetails[tag.Name] = (string.Empty, "red", displayText);
			}
			// Si no existe y no es requerido (opcional), no actualizamos el log de ejecución.
		}
	}


	// MANTENER WriteMetadataBatch para el modo simple Key/Value
	private IReadOnlyCollection<Photo> WriteMetadataBatch(
		IReadOnlyCollection<Photo> photos,
		List<KeyValuePair<string, string>> tagsToWrite,
		string contextName,
		bool isDryRun)
	{
		var formattedTags = string.Join(" ", tagsToWrite.Select(t => t.Value.StartsWith('<')
			? $"-{t.Key}{t.Value}"
			: $"-{t.Key}='{t.Value.EscapeMarkup()}'"));

		// Nota: Considerar Parallel.ForEach aquí si es necesario
		foreach (var photo in photos)
		{
			try
			{
				using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
				{
					if (isDryRun)
					{
						_consoleWriter.Write($"[yellow bold][DryRun][/] Add metadata ([dim]{contextName}[/]) to [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/]. Commands: [cyan]{formattedTags}[/]");
						continue;
					}

					exifTool.WriteTags(photo.PhotoFile.SourcePath, tagsToWrite, overwriteOriginal: true);
					_statistics.PhotosMetadataProcessed++;
					// ELIMINADO: Log de éxito en paralelo
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error adding metadata ({Context}) to {File}", contextName, photo.PhotoFile.SourcePath);
				_statistics.InternalError++;
			}
		}
		return photos;
	}

	/// <summary>
	/// Escribe metadatos para una sola foto, y si tiene éxito, actualiza los metadatos de la Photo usando el Appender Service.
	/// </summary>
	private (bool Success, Photo? UpdatedPhoto) WriteMetadataForPhoto(
		Photo photo,
		List<KeyValuePair<string, string>> tagsToWrite,
		string contextName)
	{
		try
		{
			// ¡IMPORTANTE! Crear nueva instancia de ExifTool para asegurar Thread Safety en Paralelismo
			using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
			{
				exifTool.WriteTags(photo.PhotoFile.SourcePath, tagsToWrite, overwriteOriginal: true);
				_statistics.PhotosMetadataProcessed++;
				// ELIMINADO: Log de éxito en paralelo

				// REFRESH (ACTUALIZACIÓN DE LA INSTANCIA PHOTO)
				var updatedPhotos = _exifDataAppenderService.ExtractExifData(
									new[] { photo },
									out _, out _, out _, out _, out _, out _,
									isSilent: true);

				var updatedPhoto = updatedPhotos.FirstOrDefault();

				if (updatedPhoto == null)
				{
					_logger.LogWarning("Failed to refresh metadata for {File} after successful write.", photo.PhotoFile.SourcePath);
					return (true, photo);
				}

				return (true, updatedPhoto);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error adding metadata ({Context}) to {File}", contextName, photo.PhotoFile.SourcePath);
			_statistics.InternalError++;
			// ELIMINADO: Log de fallo en paralelo
			return (false, photo);
		}
	}

	/// <summary>
	/// Revalida los tags del template usando la foto recién actualizada (después de la escritura).
	/// </summary>
	private (bool Passed, Dictionary<string, TagValidationResult> Results) RevalidateTemplateTags(Photo photo, IReadOnlyCollection<TemplateTag> templateTags)
	{
		var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
		var photoMetadataDict = photo.ExifData.Metadata;
		bool templateError = false;

		foreach (var tag in templateTags)
		{
			// Solo necesitamos verificar si el tag escrito está presente
			bool hasValue = photoMetadataDict.TryGetValue(tag.Name, out var value) &&
						!string.IsNullOrWhiteSpace(value) &&
						!value.Equals("undefined", StringComparison.OrdinalIgnoreCase);

			bool isValid = hasValue || !tag.Required;

			if (!isValid)
			{
				templateError = true;
			}

			templateCheckResults[tag.Name] = new TagValidationResult(hasValue, value ?? string.Empty, tag.Required, isValid);
		}

		return (!templateError, templateCheckResults);
	}

	/// <summary>
	/// Muestra y actualiza el progreso en una sola línea de consola (Status Console).
	/// </summary>
	private void UpdateConsoleStatus(int processed, int total, TimeSpan elapsed, string currentFile)
	{
		if (total == 0 || processed == 0) return;

		double percent = (double)processed / total;

		// Cálculo simple de tiempo restante
		TimeSpan remaining = TimeSpan.FromMilliseconds(
			(total - processed) * (elapsed.TotalMilliseconds / processed)
		);

		string statusLine = $"[{processed}/{total}] | Progreso: {percent:P0} | Est. Restante: {remaining:mm\\:ss} | Procesando: {currentFile.EscapeMarkup()}";

		_consoleWriter.WriteStatusLine(statusLine);
	}


	private IReadOnlyCollection<TemplateTag> GetTemplateTags(string templateName)
	{
		if (!_templateTagMap.TryGetValue(templateName, out var tags))
			throw new ArgumentException($"Template '{templateName}' not found.", nameof(templateName));
		return tags;
	}

	private IList<string> GetTagNamesFromTemplate(string templateName)
	{
		return GetTemplateTags(templateName).Select(t => t.Name).ToList();
	}

	// ResolveTagValue (El helper dinámico para Dry Run) (SIN CAMBIOS FUNCIONALES)
	private string? ResolveTagValue(
		Photo photo,
		TemplateTag tagDefinition,
		bool isDryRun,
		IReadOnlyCollection<TemplateTag> allTemplateTags)
	{
		// --- 1. Lógica base: Variable de Photo ---
		if (tagDefinition.Source.Type == SourceType.Variable)
		{
			if (_photoPropertyMap.TryGetValue(tagDefinition.Source.ValueKey, out var propertyGetter))
				return propertyGetter(photo);

			// Error de configuración: ValueKey no existe en _photoPropertyMap
			return isDryRun ? "CONFIG_ERROR" : null;
		}

		// --- 2. Lógica base: Valor Literal ---
		if (tagDefinition.Source.Type == SourceType.Literal)
		{
			return tagDefinition.Source.ValueKey;
		}

		// --- 3. Lógica compleja: Dependencia de otro Tag (ExifToolTag) ---
		if (tagDefinition.Source.Type == SourceType.ExifToolTag)
		{
			if (!isDryRun)
			{
				// MODO ESCRITURA: Devolver el comando de copia de tag
				return $"<{tagDefinition.Source.ValueKey}";
			}

			// MODO DRY RUN (Simulación de copia):

			// A. Buscar el tag de origen en la definición del template
			var sourceTag = allTemplateTags
				.FirstOrDefault(t => t.Name.Equals(tagDefinition.Source.ValueKey, StringComparison.OrdinalIgnoreCase));

			// B. Si el tag de origen está definido en el template (se va a escribir/calcular):
			if (sourceTag != null)
			{
				// ¡Resolución dinámica! Llamar recursivamente para obtener el valor que debería tener el tag de origen.
				// OJO: Usamos isDryRun=true en la recursión para que no devuelva comandos de copia (<Tag)
				return ResolveTagValue(photo, sourceTag, isDryRun: true, allTemplateTags);
			}

			// C. Si el tag de origen NO está en el template (se asume que existe en el archivo):
			// Fallback: Intentar leer el valor actual del tag de origen desde los metadatos leídos.
			return photo.ExifData?.Metadata.GetString(tagDefinition.Source.ValueKey);
		}

		return null;
	}

	#endregion
}
