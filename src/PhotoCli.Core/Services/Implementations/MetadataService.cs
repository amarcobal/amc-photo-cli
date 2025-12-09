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
		IConsoleWriter consoleWriter)
	{
		_fileSystem = fileSystem;
		_exifParserService = exifParserService;
		_logger = logger;
		_options = options;
		_statistics = statistics;
		_consoleWriter = consoleWriter;

		_templateTagMap = configService.GetTemplateMap();
	}

	#region 1. Métodos Públicos - ESCRITURA (Add)

	public IReadOnlyCollection<Photo> AddMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, string metadataValue, bool isDryRun = false)
	{
		var tagsToWrite = new List<KeyValuePair<string, string>>
		{
			new(metadataKey, metadataValue)
		};
		return WriteMetadataBatch(photos, tagsToWrite, $"Key/Value {metadataKey}", isDryRun);
	}

	public IReadOnlyCollection<Photo> AddMetadataFromTemplate(
	IReadOnlyCollection<Photo> photos,
	string templateName,
	bool isDryRun,
	bool overwriteTags,
	bool allowUnknownIdentity)
	{
		var templateTags = GetTemplateTags(templateName);
		var photosToProcess = new List<Photo>();

		// Usamos esta lista tanto para Dry Run como para el log de ejecución real
		var finalRunResults = new List<AddPreviewResult>();
		var identityDetails = new Dictionary<string, string>();

		int readyToProcessCount = 0;
		int skippedIdentityCount = 0;
		int skippedTemplateErrorCount = 0;


		if (!templateTags.Any())
		{
			_logger.LogWarning("Template '{Template}' no tiene tags definidos.", templateName);
			return photos;
		}

		foreach (var photo in photos)
		{
			var fullPath = photo.PhotoFile.SourceFullPath;

			// 1. Validar Identidad (Generando TagValidationResults)
			var identityCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var (identityPassed, identityLog) = ValidateIdentityAndGenerateTags(photo, allowUnknownIdentity, identityCheckResults);

			identityDetails[fullPath] = identityLog;

			// Inicializar estructuras de resultado para esta foto
			var tagsToWriteForPhoto = new List<KeyValuePair<string, string>>();
			var dryRunFileTags = new Dictionary<string, (string Value, string StatusColor, string DisplayText)>();
			var overwriteDiffs = new Dictionary<string, (string OriginalValue, string NewValue, bool Changed)>();
			var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			bool templateError = false;

			if (!identityPassed)
			{
				skippedIdentityCount++;
				var validationResult = new FileValidationResult { IdentityTags = identityCheckResults, TemplateTags = templateCheckResults };
				finalRunResults.Add(new AddPreviewResult(fullPath, false, false, dryRunFileTags, overwriteDiffs, validationResult));

				if (!isDryRun) _statistics.InternalError++;
				continue;
			}

			// 2. Resolver Tags y aplicar lógica de Overwrite
			foreach (var tag in templateTags)
			{
				string? resolvedValue = ResolveTagValue(photo, tag, isDryRun, templateTags);

				// Manejo de error de configuración
				if (resolvedValue == "CONFIG_ERROR")
				{
					templateError = true;
					if (isDryRun) dryRunFileTags[tag.Name] = ("Config Error", "red", "[bold white on red]CONFIG ERROR[/]");
					templateCheckResults[tag.Name] = new TagValidationResult(false, "CONFIG_ERROR", tag.Required, false);
					continue;
				}

				// Validación del valor resuelto
				if (string.IsNullOrWhiteSpace(resolvedValue) || resolvedValue == new SubSeconds("0").Padded())
				{
					if (tag.Required)
					{
						templateError = true;
						if (isDryRun) dryRunFileTags[tag.Name] = ("MISSING", "red", "[bold white on red]MISSING[/]");
						templateCheckResults[tag.Name] = new TagValidationResult(false, string.Empty, tag.Required, false);
					}
					else if (isDryRun)
					{
						dryRunFileTags[tag.Name] = ("(Empty)", "dim", "[dim](Empty)[/]");
						templateCheckResults[tag.Name] = new TagValidationResult(false, string.Empty, tag.Required, true);
					}
					continue;
				}

				// Valor resuelto (pasa)
				if (isDryRun)
					templateCheckResults[tag.Name] = new TagValidationResult(true, resolvedValue, tag.Required, true);


				// Lógica de Overwrite
				string? existingVal = photo.ExifData?.Metadata.TryGetValue(tag.Name, out var val) == true ? val : null;
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

						dryRunFileTags[tag.Name] = (resolvedValue, statusColor, displayText);
					}
				}
				else
				{
					// El tag existe y NO estamos sobrescribiendo (overwriteTags=false)
					if (isDryRun)
					{
						string displayText = $"[dim]{existingVal!.EscapeMarkup()}[/] (Kept)";
						dryRunFileTags[tag.Name] = (existingVal, "dim", displayText);
					}
				}
			} // Fin foreach tag

			bool templatePassed = !templateError;

			if (templateError) skippedTemplateErrorCount++;

			// 3. Almacenar resultados Dry Run / 4. Ejecución Real

			if (identityPassed && templatePassed)
			{
				readyToProcessCount++;

				bool writeSuccess = true;

				if (!isDryRun && tagsToWriteForPhoto.Any())
				{
					// EJECUCIÓN REAL: Escribir metadatos
					writeSuccess = WriteMetadataForPhoto(photo, tagsToWriteForPhoto, $"Template {templateName}");
				}

				if (!writeSuccess)
				{
					// Si la escritura falló, marcamos el template como fallido para el log final
					templatePassed = false;
					// Esto ya se cuenta en WriteMetadataForPhoto, pero se asegura el skippedTemplateErrorCount
					skippedTemplateErrorCount++;
				}

				// *** Generar el resultado para la tabla/log ***
				var validationResult = new FileValidationResult
				{
					IdentityTags = identityCheckResults,
					TemplateTags = templateCheckResults
				};

				finalRunResults.Add(new AddPreviewResult(
					fullPath,
					identityPassed,
					templatePassed, // templatePassed ahora refleja el fallo real de escritura
					dryRunFileTags,
					overwriteDiffs,
					validationResult
				));

				if (writeSuccess && !isDryRun) photosToProcess.Add(photo);

			}
			else if (!isDryRun)
			{
				_statistics.InternalError++;
				// Si falla en la validación antes de intentar escribir, se registra aquí
				var validationResult = new FileValidationResult { IdentityTags = identityCheckResults, TemplateTags = templateCheckResults };
				finalRunResults.Add(new AddPreviewResult(fullPath, identityPassed, templatePassed, dryRunFileTags, overwriteDiffs, validationResult));
			}
		} // Fin foreach photo

		// 5. Visualización de la Tabla y Resumen (DryRun O EJECUCIÓN REAL)
		// Pasamos 'isDryRun' para que la tabla pueda adaptar el título y el estado.
		PrintAddPreviewTable(photos, templateTags, finalRunResults, identityDetails, templateName, overwriteTags, readyToProcessCount, skippedIdentityCount, skippedTemplateErrorCount, allowUnknownIdentity, isDryRun);

		return photosToProcess;
	}

	#endregion

	#region 2. Métodos Públicos - BORRADO (Delete)

	public IReadOnlyCollection<Photo> DeleteMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, bool isDryRun = false)
	{
		var tagsToDelete = new List<string> { metadataKey };

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

	/// <summary>
	/// MÉTODO BASE DE VALIDACIÓN
	/// </summary>
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

		// CAMBIO DE TIPO DE RESULTADO
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
				bool hasValue = photoMetadataDict.TryGetValue(tag.Name, out var value) &&
							!string.IsNullOrWhiteSpace(value) &&
							!value.Equals("undefined", StringComparison.OrdinalIgnoreCase);

				bool isValid = hasValue || !tag.Required;

				// USAMOS LA NUEVA ESTRUCTURA
				templateCheckResults[tag.Name] = new TagValidationResult(hasValue, value ?? string.Empty, tag.Required, isValid);
			}
			bool templatePassed = templateCheckResults.Values.All(tv => tv.IsValid);

			// --- B. Validación de Identidad ---
			// Utilizamos el helper que genera el log Y los tags de validación
			var (identityPassed, identityLog) = ValidateIdentityAndGenerateTags(photo, allowUnknownIdentity, identityCheckResults);

			// --- C. Lógica de Estado Global (Status Column) ---
			bool identityIsFail = !identityPassed;
			bool templateIsFail = !templatePassed;
			bool rowPassed = !identityIsFail && !templateIsFail;

			string statusStyled;
			if (rowPassed)
			{
				passedCount++;
				if (identityLog.Contains("[yellow]"))
					statusStyled = "[bold yellow]△ Warn (Identity)[/]";
				else
					statusStyled = "[bold green]✔ OK[/]";
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

			// ALMACENAMIENTO DEL RESULTADO FINAL (Nueva Estructura)
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

		// 5. Escribir COMMAND RESULT (como en el check original)
		//var rule = new Rule("[bold]COMMAND RESULT[/]");
		//rule.Justification = Justify.Center;
		//rule.Style = new Style(foreground: Color.Yellow);
		//AnsiConsole.Write(rule);

		//_consoleWriter.WriteMarkup($"[yellow]Total Files processed:[/] [bold]{photos.Count}[/]");

		//if (passedCount == photos.Count)
		//{
		//	_consoleWriter.WriteMarkup($"[green]✅ All files passed validation.[/]");
		//	return result;
		//}

		//_consoleWriter.WriteMarkup($"[green]✅ Passed:[/] {passedCount}");
		//_consoleWriter.WriteMarkup($"[red]🛑 Failed:[/] {failedCount}");

		//// NOTA: El desglose de Check se hace en el Runner. Aquí simplemente se muestra un resumen básico.
		//if (failedCount > 0)
		//	_consoleWriter.WriteMarkup("\n[yellow]Review the table above for specific file details.[/]");

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

			bool isCriticalMissing = !hasValue;

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
				if (check.Key != "TakenDate" && (isCriticalMissing || isUnknown))
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
				if (check.Key == "TakenDate" && isCriticalMissing)
				{
					statusDisplay = "[bold white on red]MISSING (CRITICAL)[/]";
				}
				else if (isCriticalMissing)
				{
					statusDisplay = "[bold white on red]MISSING[/]";
				}
				else
				{
					statusDisplay = "[bold red]Unknown (Not Allowed)[/]";
				}
				sb.AppendLine($"[bold red]✖[/] [dim]{check.DisplayName}:[/] {statusDisplay}");
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
		int skippedTemplateErrorCount,
		bool allowUnknownIdentity,
		bool isDryRun) // <--- NUEVO PARÁMETRO
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
		int failedByTemplate = 0;
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
				if (!result.TemplatePassed) failedByTemplate++;

				if (!result.IdentityPassed && !result.TemplatePassed)
				{
					status = isDryRun ? "[bold red]❌ Skip (Both)[/]" : "[bold red]✖ FAILED (Both)[/]";
				}
				else if (!result.IdentityPassed)
				{
					status = isDryRun ? "[bold red]❌ Skip (Identity)[/]" : "[bold red]✖ FAILED (Identity)[/]";
				}
				else
				{
					status = isDryRun ? "[bold red]❌ Skip (Template)[/]" : "[bold red]✖ FAILED (Write/Tpl)[/]";
				}
			}

			// 2. Contadores Detallados (USANDO TagValidationResult)
			var identityTags = result.ValidationResult.IdentityTags;

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
					if (!makeTag.IsValid || !modelTag.IsValid)
						missingMakeModel++;
				}
			}
			else if (result.IdentityPassed && allowUnknownIdentity)
			{
				// b) Warnings (Yellow △ - Contamos los que pasaron pero tienen valor vacío/unknown)
				// Usamos el log (identityDetails) que tiene el símbolo '△' para un conteo preciso de warnings
				var identityLog = identityDetails[fullPath];
				if (identityLog.Contains("[yellow]△[/]"))
				{
					allowedUnknownWarnings++;
				}
			}

			// 3. Contar Kept Unchanged
			if (result.IdentityPassed && result.TemplatePassed)
			{
				bool hasAnyChange = result.OverwriteDifferences.Any(d => d.Value.Changed) || result.TagsResults.Any(t => t.Value.StatusColor == "cyan");
				if (!hasAnyChange)
				{
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
		var passedSimulated = readyToProcessCount;
		var failedSimulated = skippedIdentityCount + skippedTemplateErrorCount;

		_consoleWriter.WriteMarkup($"[yellow]Total Files processed:[/] [bold]{totalFiles}[/]");

		if (filesReadyToWrite == totalFiles && keptUnchangedCount == 0 && allowedUnknownWarnings == 0)
		{
			_consoleWriter.WriteMarkup($"[green]✅ All files {(isDryRun ? "ready for processing" : "processed successfully")}.[/]");
		}
		else
		{
			_consoleWriter.WriteMarkup($"[green]✅ Passed (Write/Update):[/] {passedSimulated}");
			_consoleWriter.WriteMarkup($"[red]🛑 Failed (Skipped):[/] {failedSimulated}");
		}

		// -------------------------------------------------------------------------
		// FAILURE BREAKDOWN
		// -------------------------------------------------------------------------
		if (failedByIdentity > 0 || failedByTemplate > 0)
			_consoleWriter.WriteMarkup("\n[bold underline]Failure Breakdown:[/]");

		if (failedByIdentity > 0)
			_consoleWriter.WriteMarkup($"• [yellow]Required Data (Identity) Missing/Invalid:[/][bold red] {failedByIdentity}[/] files");

		// El fallo de template incluye el fallo de escritura real si no es Dry Run.
		if (failedByTemplate > 0)
		{
			var failureType = isDryRun ? "Missing" : "Missing / Write Error";
			_consoleWriter.WriteMarkup($"• [yellow]Required Tags (Template) {failureType}:[/][bold red] {failedByTemplate}[/] files");
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
					_consoleWriter.Write($"✅ Added metadata ([dim]{contextName}[/]) to [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/].");
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
	/// Escribe metadatos para una sola foto (usado en AddMetadataFromTemplate en modo real).
	/// </summary>
	private bool WriteMetadataForPhoto(
		Photo photo,
		List<KeyValuePair<string, string>> tagsToWrite,
		string contextName)
	{
		try
		{
			using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
			{
				exifTool.WriteTags(photo.PhotoFile.SourcePath, tagsToWrite, overwriteOriginal: true);
				_statistics.PhotosMetadataProcessed++;
				_consoleWriter.Write($"✅ [bold]Added metadata[/] ([dim]{contextName}[/]) to [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/].");
				return true;
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error adding metadata ({Context}) to {File}", contextName, photo.PhotoFile.SourcePath);
			_statistics.InternalError++;
			_consoleWriter.Write($"❌ [bold red]Failed to add metadata[/] ([dim]{contextName}[/]) to [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/]. See log for details.");
			return false;
		}
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
				return ResolveTagValue(photo, sourceTag, isDryRun, allTemplateTags);
			}

			// C. Si el tag de origen NO está en el template (se asume que existe en el archivo):
			// Fallback: Intentar leer el valor actual del tag de origen desde los metadatos leídos.
			return photo.ExifData?.Metadata.GetString(tagDefinition.Source.ValueKey);
		}

		return null;
	}

	#endregion
}
