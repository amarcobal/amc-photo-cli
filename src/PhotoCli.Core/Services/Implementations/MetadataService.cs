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
using System.Threading.Tasks;
using PhotoCli.Migrations;
using PhotoCli.Core.Services.Contracts.SpectreConsole;
using System.Reflection.Metadata;
using PhotoCli.Core.Utils;

namespace PhotoCli.Core.Services.Implementations;

public record AddPreviewResult(
	string FullPath,
	bool IdentityPassed,
	bool TemplatePassed,
	Dictionary<string, (string Value, string StatusColor, string DisplayText)> TagsResults,
	Dictionary<string, (string OriginalValue, string NewValue, bool Changed)> OverwriteDifferences,
	FileValidationResult ValidationResult,
	MetadataRunStatus RunStatus // <-- NUEVO: Estado final de ejecución
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
	private readonly IProgressService _progressService;
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
		{ "OriginalSubseconds", p => p.HasSubSeconds ? p.Subseconds?.Padded() : Constants.MetadataNotSetValue },
	};

	public MetadataService(
		IFileSystem fileSystem,
		IExifParserService exifParserService,
		IMetadataConfigurationService configService,
		ILogger<MetadataService> logger,
		ToolOptions options,
		Statistics statistics,
		IConsoleWriter consoleWriter,
		IExifDataAppenderService exifDataAppenderService,
		IProgressService progressService)
	{
		_fileSystem = fileSystem;
		_exifParserService = exifParserService;
		_logger = logger;
		_options = options;
		_statistics = statistics;
		_consoleWriter = consoleWriter;
		_exifDataAppenderService = exifDataAppenderService;
		_progressService = progressService;
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

	public IReadOnlyDictionary<string, FileValidationResult> AddMetadataFromTemplate(
	IReadOnlyCollection<Photo> photos,
	string templateName,
	IReadOnlyCollection<MetadataCheckViewType> viewTypes,
	bool isDryRun,
	bool overwriteTags,
	bool allowUnknownIdentity)
	{
		var templateTags = GetTemplateTags(templateName);

		// Colecciones estándar (no concurrentes)
		var finalValidationResults = new Dictionary<string, FileValidationResult>(StringComparer.OrdinalIgnoreCase);
		var finalRunLogResults = new List<AddPreviewResult>();
		var identityDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// Contadores simples (Se mantienen pero se ignora la cuenta al final de este método, se delega a PrintAddPreviewTable)
		int readyToProcessCount = 0;
		int skippedIdentityCount = 0;
		int failedByTemplateValidation = 0;
		int failedByWriteError = 0;
		int totalFiles = photos.Count;

		if (!templateTags.Any())
		{
			_logger.LogWarning("Template '{Template}' no tiene tags definidos.", templateName);
			return new Dictionary<string, FileValidationResult>();
		}

		var photosArray = photos.ToArray();

		// -------------------------------------------------------------------------
		// CAMBIO CLAVE: Usar IProgressService para la barra de progreso
		// -------------------------------------------------------------------------
		var validationResults = _progressService.ExecuteProgress("Metadata Add/Check Process", ctx =>
		{
			// Usamos el constructor AddTask simple con MaxValue
			var task = ctx.AddTask($"[yellow]Processing Template:[/][bold cyan]{templateName}[/]", maxValue: totalFiles);

			// -------------------------------------------------------------------------
			// !!! INICIO DEL PROCESAMIENTO SECUENCIAL (FOREACH) !!!
			// -------------------------------------------------------------------------
			foreach (var photo in photosArray)
			{
				// CAMBIO 1: Usar UpdateDescription
				task.UpdateDescription($"[yellow]Processing Template:[/][bold cyan] {templateName}[/] - [dim]{photo.PhotoFile.FileName}[/]");

				var fullPath = photo.PhotoFile.SourceFullPath;

				bool templateError = false;
				bool templatePassed = false;
				bool writeSuccess = true;
				MetadataRunStatus runStatus = MetadataRunStatus.NotApplicable; // <--- INICIALIZACIÓN DEL NUEVO ESTADO

				var identityCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
				var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
				var tagsToWriteForPhoto = new List<KeyValuePair<string, string>>();
				var tagExecutionDetails = new Dictionary<string, (string Value, string StatusColor, string DisplayText)>();
				var overwriteDiffs = new Dictionary<string, (string OriginalValue, string NewValue, bool Changed)>();


				// 1. Validar Identidad
				var (identityPassed, identityLog) = ValidateIdentityAndGenerateTags(photo, allowUnknownIdentity, identityCheckResults);
				identityDetails[fullPath] = identityLog;

				Photo currentPhoto = photo;

				if (identityPassed)
				{
					// 2. Resolver Tags y aplicar lógica de Overwrite
					foreach (var tag in templateTags)
					{
						string? resolvedValue = ResolveTagValue(currentPhoto, tag, isDryRun: true, templateTags);

						if (resolvedValue == "CONFIG_ERROR")
						{
							templateError = true;
							tagExecutionDetails[tag.Name] = ("Config Error", "red", "[bold white on red]CONFIG ERROR (Missing Prop Map)[/]");
							templateCheckResults[tag.Name] = new TagValidationResult(false, "CONFIG_ERROR", tag.Required, false);
							continue;
						}

						// --- INICIO: LÓGICA DE VALOR FALTANTE / SENTINELA ---

						bool isNotApplicable = resolvedValue?.Equals(Constants.MetadataNotSetValue, StringComparison.Ordinal) ?? false;

						if (string.IsNullOrWhiteSpace(resolvedValue) || isNotApplicable)
						{
							if (isNotApplicable)
							{
								// El valor es la constante de control. No es un error, el dato está ausente.
								if (isDryRun)
								{
									tagExecutionDetails[tag.Name] = (Constants.MetadataNotSetValue, "dim", "[dim](Not Applicable / Not Written)[/]");
									templateCheckResults[tag.Name] = new TagValidationResult(false, string.Empty, tag.Required, true);
								}
								continue; // No hay valor real para escribir/comparar.
							}

							// Si es realmente null/empty (y no es la constante de control)
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

						// --- FIN: LÓGICA DE VALOR FALTANTE / SENTINELA ---

						// Si llegamos aquí, resolvedValue es un valor real (no null/empty/sentinela).
						if (isDryRun)
							templateCheckResults[tag.Name] = new TagValidationResult(true, resolvedValue, tag.Required, true);


						// Lógica de Overwrite
						string? existingVal = currentPhoto.ExifData?.Metadata.TryGetValue(tag.Name, out var val) == true ? val : null;
						bool tagExistsInFile = existingVal != null;
						bool valuesAreSame = tagExistsInFile && existingVal!.Equals(resolvedValue, StringComparison.OrdinalIgnoreCase);

						// --- INICIO: LÓGICA de shouldWrite ---
						bool shouldWrite;

						if (!tagExistsInFile)
						{
							shouldWrite = true; // Siempre escribimos tags nuevos.
						}
						else // Tag existe
						{
							if (overwriteTags)
							{
								// Escribir solo si hay una diferencia (Old -> New)
								shouldWrite = !valuesAreSame;
							}
							else
							{
								// Si no hay overwrite, y el tag ya existe, NUNCA escribimos (Kept).
								shouldWrite = false;
							}
						}
						// --- FIN: LÓGICA de shouldWrite ---

						if (shouldWrite)
						{
							tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, resolvedValue));

							if (isDryRun)
							{
								string statusColor;
								string displayText;

								// Sobrescritura (Old -> New)
								if (tagExistsInFile) // Llegamos aquí si overwriteTags es TRUE y !valuesAreSame
								{
									overwriteDiffs[tag.Name] = (existingVal!.EscapeMarkup(), resolvedValue.EscapeMarkup(), true);

									displayText = $"[bold]{existingVal.EscapeMarkup()}[/] → [yellow]{resolvedValue.EscapeMarkup()}[/]";
									statusColor = "yellow";
								}
								else // Nuevo (New)
								{
									displayText = $"[cyan]{resolvedValue.EscapeMarkup()}[/] (New)";
									statusColor = "cyan";
								}

								tagExecutionDetails[tag.Name] = (resolvedValue, statusColor, displayText);
							}
						}
						else
						{
							// shouldWrite es FALSE (Tag existe y no hay que sobrescribir/no hay diferencia)
							if (isDryRun && tagExistsInFile)
							{
								// ***************************************************************
								// INICIO: CORRECCIÓN CLAVE PARA DETECCIÓN DE CONFLICTO (DIFFERS / KEPT)
								// ***************************************************************
								// Determinar si hay alguna diferencia real.
								bool hasDiff = !valuesAreSame;

								// Llenamos overwriteDiffs. Esto es CRUCIAL para que RecalculateFinalTagExecutionDetails
								// pueda entrar en el CASO 3 (Diferencias) y aplicar el estado (Differs / Kept) o (Match / Kept).
								overwriteDiffs[tag.Name] = (existingVal!.EscapeMarkup(), resolvedValue.EscapeMarkup(), hasDiff);

								// Proporcionamos un placeholder de log temporal para evitar errores en RecalculateFinalTagExecutionDetails
								// (que se sobrescribirá inmediatamente después con el display correcto)
								string logPlaceholder = hasDiff ? "(Conflict Kept)" : "(Match)";
								tagExecutionDetails[tag.Name] = (existingVal!, "dim", logPlaceholder);
								// ***************************************************************
								// FIN: CORRECCIÓN CLAVE
								// ***************************************************************
							}
						}
					}

					templatePassed = !templateError;

					if (templatePassed)
					{
						// 3. Ejecución Real y Refresco
						writeSuccess = true;
						readyToProcessCount++; // Contador temporal para Dry Run

						if (!isDryRun && tagsToWriteForPhoto.Any())
						{
							(writeSuccess, var tempUpdatedPhoto) = WriteMetadataForPhoto(photo, tagsToWriteForPhoto, $"Template {templateName}");

							if (writeSuccess && tempUpdatedPhoto != null)
							{
								currentPhoto = tempUpdatedPhoto;
							}
							else
							{
								writeSuccess = false;
							}
						}

						// 4. VALIDACIÓN FINAL Y LOG (Unificado)

						// --------------------------------------------------------------------------
						// INICIO: LÓGICA DE REVALIDACIÓN DRY RUN
						// --------------------------------------------------------------------------
						if (!isDryRun)
						{
							// En la ejecución REAL, revalidamos leyendo del archivo (o del objeto Photo actualizado)
							(templatePassed, templateCheckResults) = RevalidateTemplateTags(currentPhoto, templateTags);

							if (!writeSuccess && !isDryRun)
							{
								templatePassed = false;
								failedByWriteError++;
								readyToProcessCount--;
							}
							else if (!templatePassed)
							{
								failedByTemplateValidation++;
							}
						}
						else // DRY RUN: Simulamos la validación final
						{
							// Si hubo un error en la primera pasada (ej. MISSING Required), ya templatePassed es false
							if (!templatePassed)
							{
								failedByTemplateValidation++;
							}

							// Creación del templateCheckResults simulado (sin cambios)
							foreach (var tag in templateTags)
							{
								if (tagExecutionDetails.TryGetValue(tag.Name, out var detail))
								{
									bool isMissingError = detail.DisplayText.Contains("MISSING") || detail.DisplayText.Contains("CONFIG ERROR");

									templateCheckResults[tag.Name] = new TagValidationResult(
										HasValue: !isMissingError,
										Value: detail.Value,
										IsRequired: tag.Required,
										IsValid: !isMissingError);
								}
							}
						}
						// --------------------------------------------------------------------------
						// FIN: LÓGICA DE REVALIDACIÓN DRY RUN
						// --------------------------------------------------------------------------

						RecalculateFinalTagExecutionDetails(currentPhoto, templateTags, templateCheckResults, tagExecutionDetails, overwriteDiffs, isDryRun, overwriteTags);

						// --------------------------------------------------------------------------
						// INICIO: DETERMINACIÓN DEL RUN STATUS (AÑADIDO)
						// --------------------------------------------------------------------------
						if (!writeSuccess)
						{
							runStatus = MetadataRunStatus.FailedWrite;
						}
						else if (!templatePassed)
						{
							runStatus = MetadataRunStatus.FailedTemplate;
						}
						else // Pasó todo: ReadyToWrite o Kept
						{
							bool hasChanges = tagsToWriteForPhoto.Any();
							// También comprobamos si había diferencias que se sobrescribieron
							bool hadOverwriteDiffs = overwriteDiffs.Any(d => d.Value.Changed && overwriteTags);

							if (hasChanges || hadOverwriteDiffs)
							{
								runStatus = MetadataRunStatus.ReadyToWrite;
							}
							else
							{
								runStatus = MetadataRunStatus.Kept;
							}
						}
						// --------------------------------------------------------------------------
						// FIN: DETERMINACIÓN DEL RUN STATUS
						// --------------------------------------------------------------------------

					}
					else // !templatePassed (falló en la resolución inicial, ej. MISSING Required)
					{
						runStatus = MetadataRunStatus.FailedTemplate; // <--- ASIGNACIÓN DEL ESTADO DE FALLO
						failedByTemplateValidation++;
					}
				}
				else
				{
					runStatus = MetadataRunStatus.SkippedIdentity; // <--- ASIGNACIÓN DEL ESTADO DE SALTO
					skippedIdentityCount++;
				}

				// 5. Almacenar resultados para el retorno
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
					validationResult,
					runStatus // <--- ¡NUEVO CAMPO!
				));

				finalValidationResults[fullPath] = validationResult;

				task.Increment(1);
			}
			// -------------------------------------------------------------------------
			// !!! FIN DEL PROCESAMIENTO SECUENCIAL !!!
			// -------------------------------------------------------------------------

			// CAMBIO 2: Mensaje final y Stop()
			task.UpdateDescription($"[green]✅ Completed Template:[/][bold cyan] {templateName}[/]");
			task.Stop();

			// Devolvemos el resultado que queremos de la ejecución de la barra
			return finalValidationResults.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
		});


		// 5. Visualización de la Tabla y Resumen
		PrintAddPreviewTable(
			photos,
			templateTags,
			finalRunLogResults.ToList(),
			identityDetails.ToDictionary(kv => kv.Key, kv => kv.Value),
			templateName,
			overwriteTags,
			readyToProcessCount,
			skippedIdentityCount,
			failedByTemplateValidation,
			failedByWriteError,
			allowUnknownIdentity,
			isDryRun,
			viewTypes);

		return validationResults;
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

	private IReadOnlyDictionary<string, FileValidationResult> CheckMetadataFromTemplateBase(
		IReadOnlyCollection<Photo> photos,
		IReadOnlyCollection<TemplateTag> templateTags,
		string contextName,
		IReadOnlyCollection<MetadataCheckViewType> viewTypes,
		bool allowUnknownIdentity)
	{
		bool showTemplateColumn = viewTypes.Contains(MetadataCheckViewType.Template) || viewTypes.Contains(MetadataCheckViewType.TemplateDetails);
		bool showTemplateDetails = viewTypes.Contains(MetadataCheckViewType.TemplateDetails);
		bool showIdentityColumn = viewTypes.Contains(MetadataCheckViewType.Identity) || viewTypes.Contains(MetadataCheckViewType.IdentityDetails);
		bool showIdentityDetails = viewTypes.Contains(MetadataCheckViewType.IdentityDetails);

		var result = new Dictionary<string, FileValidationResult>(StringComparer.OrdinalIgnoreCase);

		int passedCount = 0;
		int failedCount = 0;

		var columns = new List<TableColumnConfig>
		{
			new() { HeaderText = "File", Width = 40, NoWrap = false },
			new() { HeaderText = "Status", Width = 18, NoWrap = true }
		};

		if (showIdentityColumn) columns.Add(new() { HeaderText = "Media Identity", NoWrap = false, Width = showIdentityDetails ? null : 40 });
		if (showTemplateColumn) columns.Add(new() { HeaderText = $"Tags: {contextName.Replace("Template ", "").Trim()}", NoWrap = true, Width = null });

		var rows = new List<List<string>>();

		foreach (var photo in photos)
		{
			var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var identityCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
			var photoMetadataDict = photo.ExifData.Metadata;

			// --- A. Validación de Template Tags ---
			foreach (var tag in templateTags)
			{
				string? value = null;
				bool hasValue;

				if (tag.Source.Type == SourceType.ExifToolTag)
				{
					hasValue = photoMetadataDict.TryGetValue(tag.Source.ValueKey, out value) &&
							 !string.IsNullOrWhiteSpace(value) &&
							 !value.Equals("undefined", StringComparison.OrdinalIgnoreCase);
				}
				else
				{
					value = ResolveTagValue(photo, tag, isDryRun: true, templateTags);

					bool isConfigError = value == "CONFIG_ERROR";
					bool isMissingOrEmpty = string.IsNullOrWhiteSpace(value);

					hasValue = !isConfigError && !isMissingOrEmpty;

					if (tag.Required && (isConfigError || isMissingOrEmpty))
					{
						hasValue = false;
					}
					else if (!tag.Required && hasValue)
					{
						value = ResolveTagValue(photo, tag, isDryRun: true, templateTags);
					}
					else if (!tag.Required && !hasValue)
					{
						value = $"[dim](Empty/N/A)[/]";
						hasValue = true;
					}
				}

				bool isValid = hasValue || !tag.Required;

				string displayValue = !isValid && !hasValue ? "[bold white on red]MISSING/ERROR[/]" : value ?? string.Empty;

				templateCheckResults[tag.Name] = new TagValidationResult(
					hasValue,
					displayValue,
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

						string valDisplay = check.Value;

						if (!valDisplay.Contains('[') && !check.IsValid)
						{
							valDisplay = check.HasValue
								? $"[red]{check.Value.EscapeMarkup()}[/]"
								: "[bold white on red]MISSING[/]";
						}
						else if (!valDisplay.Contains('[') && check.IsValid)
						{
							valDisplay = check.HasValue
								? $"[cyan]{check.Value.EscapeMarkup()}[/]"
								: "[dim](Empty)[/]";
						}

						templateBuilder.AppendLine($"{reqStatus} [teal]{tag.Name.EscapeMarkup()}[/]: {valDisplay}");
					}
					row.Add(templateBuilder.ToString());
				}
				else row.Add(templatePassed ? "[green]Valid[/]" : "[red]Invalid[/]");
			}

			rows.Add(row);

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

			bool isUnknown = !check.IsExif && val == "Unknown";
			bool hasValue = !string.IsNullOrWhiteSpace(val) && val != "Unknown";

			bool isValidCheck;

			if (check.Key == "TakenDate")
			{
				isValidCheck = hasValue;
			}
			else if (allowUnknown)
			{
				isValidCheck = true;
			}
			else
			{
				isValidCheck = hasValue && !isUnknown;
			}

			if (!isValidCheck)
			{
				allOk = false;
			}

			bool required = check.Key == "TakenDate" || !allowUnknown;

			identityCheckResults[check.Key] = new TagValidationResult(
				hasValue,
				val ?? string.Empty,
				IsRequired: required,
				IsValid: isValidCheck);

			if (isValidCheck)
			{
				if (check.Key != "TakenDate" && (!hasValue || isUnknown))
				{
					string status = isUnknown ? "Unknown" : "MISSING";
					sb.AppendLine($"[yellow]△[/] [dim] {check.DisplayName}:[/] [yellow]{status} (Allowed)[/]");
				}
				else
				{
					sb.AppendLine($"[green]✔[/] [dim] {check.DisplayName}:[/] [cyan]{val!.EscapeMarkup()}[/]");
				}
			}
			else
			{
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


	private void PrintAddPreviewTable(
	IReadOnlyCollection<Photo> photos,
	IReadOnlyCollection<TemplateTag> templateTags,
	IReadOnlyCollection<AddPreviewResult> finalRunResults,
	Dictionary<string, string> identityDetails,
	string templateName,
	bool overwriteTags,
	int readyToProcessCount, // Se ignora, se recalcula
	int skippedIdentityCount, // Se ignora, se recalcula
	int failedByTemplateValidation, // Se ignora, se recalcula
	int failedByWriteError, // Se ignora, se recalcula
	bool allowUnknownIdentity,
	bool isDryRun,
	IReadOnlyCollection<MetadataCheckViewType> viewTypes)
	{
		// --- Derivación de booleanos a partir de viewTypes ---
		bool showIdentityColumn = viewTypes.Contains(MetadataCheckViewType.Identity) || viewTypes.Contains(MetadataCheckViewType.IdentityDetails);
		bool showTemplateColumn = viewTypes.Contains(MetadataCheckViewType.Template) || viewTypes.Contains(MetadataCheckViewType.TemplateDetails);
		bool showIdentityDetails = viewTypes.Contains(MetadataCheckViewType.IdentityDetails);
		bool showTemplateDetails = viewTypes.Contains(MetadataCheckViewType.TemplateDetails);

		// --- 1. CONSTRUCCIÓN CONDICIONAL DE COLUMNAS ---
		var columns = new List<TableColumnConfig>
	{
		// Columna 1: File (Always visible)
		new() { HeaderText = "File", Width = 40 },
		// Columna 2: Status (Always visible)
		new() { HeaderText = "Status", Width = 22, NoWrap = true }, // Aumentado el ancho para los nuevos estados
	};

		// Columna 3: Media Identity (Conditional)
		if (showIdentityColumn)
		{
			columns.Add(new() { HeaderText = "Media Identity", Width = showIdentityDetails ? null : 40 });
		}

		// Columna 4: Proposed Tags (Conditional)
		if (showTemplateColumn)
		{
			columns.Add(new() { HeaderText = $"Proposed Tags (Template: {templateName})", Width = null });
		}


		var rows = new List<List<string>>();

		// --- Contadores inicializados o re-calculados (RECALCULAMOS) ---
		int keptUnchangedCountFinal = 0;
		int writtenUpdatedCount = 0;
		int failedByIdentityFinal = 0;
		int failedByTemplateValidationFinal = 0;
		int failedByWriteErrorFinal = 0;
		int allowedUnknownWarnings = 0;
		int totalTagDifferencesFound = 0; // <--- ¡NUEVO CONTADOR!
		int missingTakenDate = 0;
		int missingDevice = 0;
		int missingAuthor = 0;
		int missingMakeModel = 0;

		// --- Bucle de Procesamiento de Resultados ---
		foreach (var result in finalRunResults)
		{
			var fullPath = result.FullPath;

			string status;

			// --------------------------------------------------------------------------
			// NUEVA LÓGICA DE STATUS BASADA EN RUNSTATUS (CORRECCIÓN CLAVE)
			// --------------------------------------------------------------------------
			switch (result.RunStatus)
			{
				case MetadataRunStatus.ReadyToWrite:
					status = isDryRun ? "[bold green]🟢 WRITE[/]" : "[bold green]✔ WRITE[/]";
					writtenUpdatedCount++;
					break;
				case MetadataRunStatus.Kept:
					status = isDryRun ? "[bold dim blue]🔵 KEPT[/]" : "[bold dim blue]✔ KEPT[/]";
					keptUnchangedCountFinal++;
					break;
				case MetadataRunStatus.SkippedIdentity:
					status = isDryRun ? "[bold red]❌ SKIP (Identity)[/]" : "[bold red]✖ FAILED (Identity)[/]";
					failedByIdentityFinal++;
					break;
				case MetadataRunStatus.FailedTemplate:
					status = isDryRun ? "[bold red]❌ SKIP (Template)[/]" : "[bold red]✖ FAILED (Template)[/]";
					failedByTemplateValidationFinal++;
					break;
				case MetadataRunStatus.FailedWrite:
					status = "[bold red]✖ FAILED (Write Error)[/]";
					failedByWriteErrorFinal++;
					break;
				default:
					status = "[bold red]??? ERROR[/]";
					break;
			}
			// --------------------------------------------------------------------------
			// FIN: NUEVA LÓGICA DE STATUS
			// --------------------------------------------------------------------------

			// --------------------------------------------------------------------------
			// Lógica de conteo de Diferencias de Tags
			// Solo contamos si hubo un cambio real entre Original y New.
			// Esto cubre tanto los tags que se sobrescriben como los que se mantienen.
			// --------------------------------------------------------------------------
			if (result.OverwriteDifferences.Any())
			{
				totalTagDifferencesFound += result.OverwriteDifferences.Count(d => d.Value.Changed);
			}

			var identityTags = result.ValidationResult.IdentityTags;

			// Lógica de conteo detallado de fallos (solo si se saltó por identidad)
			if (result.RunStatus == MetadataRunStatus.SkippedIdentity)
			{
				if (identityTags.TryGetValue("TakenDate", out var takenDateTag) && !takenDateTag.IsValid)
					missingTakenDate++;

				if (identityTags.TryGetValue("Device", out var deviceTag) && !deviceTag.IsValid && deviceTag.IsRequired)
					missingDevice++;
				if (identityTags.TryGetValue("Author", out var authorTag) && !authorTag.IsValid && authorTag.IsRequired)
					missingAuthor++;

				if (identityTags.TryGetValue("Make", out var makeTag) && identityTags.TryGetValue("Model", out var modelTag) && makeTag.IsRequired)
				{
					if (!makeTag.IsValid || !modelTag.IsValid)
						missingMakeModel++;
				}
			}
			// Lógica de Warnings (Solo si el archivo se va a procesar o mantener)
			else if ((result.RunStatus == MetadataRunStatus.ReadyToWrite || result.RunStatus == MetadataRunStatus.Kept) && allowUnknownIdentity)
			{
				var identityLog = identityDetails[fullPath];
				if (identityLog.Contains("[yellow]△[/]"))
				{
					allowedUnknownWarnings++;
				}
			}

			// Lógica de construcción de tagsBuilder (Solo se necesita si showTemplateDetails o showTemplateColumn es TRUE)
			var tagsBuilder = new StringBuilder();
			if (showTemplateColumn)
			{
				if (showTemplateDetails) // Mostrar detalles (línea por línea)
				{
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
				}
				else // Mostrar resumen simple (Valid/Invalid)
				{
					tagsBuilder.Append(result.TemplatePassed ? "[green]Valid[/]" : "[red]Invalid[/]");
				}
			}

			// --- 3. CONSTRUCCIÓN CONDICIONAL DE LA FILA (rows.Add) ---
			var rowData = new List<string>
		{
			Markup.Escape(fullPath),
			// [Columna 2: Status]
			status,
		};

			// [Columna 3: Media Identity]
			if (showIdentityColumn)
			{
				if (showIdentityDetails)
				{
					rowData.Add(identityDetails.ContainsKey(fullPath) ? identityDetails[fullPath] : "[bold red]IDENTITY ERROR[/]");
				}
				else
				{
					// Mostrar resumen simple (Valid/Invalid)
					rowData.Add(result.IdentityPassed ? "[green]Valid[/]" : "[red]Invalid[/]");
				}
			}

			// [Columna 4: Proposed Tags]
			if (showTemplateColumn)
			{
				rowData.Add(tagsBuilder.ToString());
			}

			rows.Add(rowData);
		}

		// Recalculamos los contadores finales del resumen
		var totalFiles = photos.Count;
		var failedByTemplateFinal = failedByTemplateValidationFinal + failedByWriteErrorFinal;
		var failedSkippedCount = failedByIdentityFinal + failedByTemplateFinal;

		// --- Escritura de la Tabla y Título de Contexto ---
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
		// SIMPLIFIED AND DIFFERENTIATED FINAL REPORT (COMMAND RESULT)
		// -------------------------------------------------------------------------

		// --- 1. Final Counter Preparation
		var ruleTitle = isDryRun ? "SIMULATION RESULT" : "EXECUTION RESULT";
		var rule = new Rule($"[bold]{ruleTitle}[/]");
		rule.Justification = Justify.Center;
		rule.Style = new Style(foreground: Color.Yellow);
		AnsiConsole.Write(rule);

		_consoleWriter.WriteMarkup($"[yellow]Total Files Processed:[/][bold] {totalFiles}[/]");
		_consoleWriter.WriteMarkup(""); // New line

		// --- 2. SUCCESSFUL OPERATIONS ---
		_consoleWriter.WriteMarkup("[bold green]🟢 SUCCESSFUL OPERATIONS:[/]");

		// Execution Counters
		var writtenMessage = isDryRun
			? "files (Metadata will be written / updated)"
			: "files (Metadata successfully written)";

		var keptMessage = isDryRun
			? "files (Already had correct metadata and will be kept)"
			: "files (Already had correct metadata)";

		// Written/Updated (Éxito Activo - GREEN)
		_consoleWriter.WriteMarkup($"[green]   • Written / Updated:      [/][green]{writtenUpdatedCount}[/] {writtenMessage}");

		// Kept (Éxito Pasivo/Neutro - CYAN o DIM)
		_consoleWriter.WriteMarkup($"[blue]   • Unchanged (Kept):       [/][blue]{keptUnchangedCountFinal}[/] {keptMessage}");
		_consoleWriter.WriteMarkup(""); // New line


		// --- 3. FAILURES (SKIPPED/ERROR) ---

		if (failedSkippedCount > 0)
		{
			_consoleWriter.WriteMarkup("[bold red]🛑 FAILED OPERATIONS (SKIPPED/ERROR):[/]");

			// Identity Failures (Missing Critical Data)
			if (failedByIdentityFinal > 0)
			{
				_consoleWriter.WriteMarkup($"   • [red]Identity Missing:[/][bold red] {failedByIdentityFinal}[/] files (Missing critical data like Date/Required Tags)");
			}

			// Template Failures (Validation or Write Error)
			if (failedByTemplateFinal > 0)
			{
				var errorType = isDryRun ? "Failed by Template Validation" : "Failed by Validation / Write Error";
				_consoleWriter.WriteMarkup($"   • [red]Template / Write Error:[/][bold red] {failedByTemplateFinal}[/] files ({errorType})");
			}
			_consoleWriter.WriteMarkup(""); // New line

			// Failure Details (Template / Write)
			if (failedByTemplateValidationFinal > 0 || failedByWriteErrorFinal > 0)
			{
				_consoleWriter.WriteMarkup("[bold underline]Template / Write Failure Details:[/]");
				if (failedByTemplateValidationFinal > 0)
					_consoleWriter.WriteMarkup($"  - Validation Failed (Missing Tags/Calc Error): [red]{failedByTemplateValidationFinal}[/]");
				if (failedByWriteErrorFinal > 0)
					_consoleWriter.WriteMarkup($"  - Write Operation Failed (I/O Error): [red]{failedByWriteErrorFinal}[/]");
			}

			// Failure Details (Identity)
			if (failedByIdentityFinal > 0)
			{
				_consoleWriter.WriteMarkup("[bold underline]Missing Identity Data Details:[/]");
				if (missingTakenDate > 0) _consoleWriter.WriteMarkup($"  - Missing Taken Date: [red]{missingTakenDate}[/]");
				if (missingMakeModel > 0) _consoleWriter.WriteMarkup($"  - Missing Make/Model: [red]{missingMakeModel}[/]");
				if (missingDevice > 0) _consoleWriter.WriteMarkup($"  - Missing Device ID:  [red]{missingDevice}[/]");
				if (missingAuthor > 0) _consoleWriter.WriteMarkup($"  - Missing Author ID:  [red]{missingAuthor}[/]");
			}
			_consoleWriter.WriteMarkup(""); // New line
		}


		// --- 4. WARNINGS ---
		bool hasWarnings = allowedUnknownWarnings > 0 || totalTagDifferencesFound > 0;

		if (hasWarnings)
		{
			// Título de la sección
			_consoleWriter.WriteMarkup("[bold yellow]⚠️ WARNINGS (PROCESSED WITH ATTENTION):[/]");


			if (totalTagDifferencesFound > 0)
			{
				string actionMessage = overwriteTags
					? $"value conflicts [dim](Overwritten)[/]"
					: $"value conflicts [dim](Protected / Kept)[/]";

				// El texto ahora es "Tag Value Differences Found"
				_consoleWriter.WriteMarkup($"   • [red]Tag value differences found:[/][bold red] {totalTagDifferencesFound}[/] tags with {actionMessage}.");
				_consoleWriter.WriteMarkup($"  [dim](Review the table for tags with 'Overwrite' or 'Differs / Kept' statuses)[/]");
			}
			// -------------------------------------------------------------

			if (allowedUnknownWarnings > 0)
			{
				var unknownUsedMessage = isDryRun
					? "files (The 'Unknown' value will be used for Author/Device)"
					: "files (The 'Unknown' value was used for Author/Device)";

				_consoleWriter.WriteMarkup($"   • Unknown Identity used: [yellow]{allowedUnknownWarnings}[/] {unknownUsedMessage}");
				_consoleWriter.WriteMarkup("  [dim](Files allowed due to the --allow-unknown-identity flag)[/]");
			}
			_consoleWriter.WriteMarkup(""); // New line
		}


		// --- 5. END OF REPORT ---
		_consoleWriter.WriteMarkup("[yellow]Review the table above for specific file details.[/]");

		// Final rule to close the report
		var finalRule = new Rule("");
		finalRule.Style = new Style(foreground: Color.Yellow);
		AnsiConsole.Write(finalRule);
	}


	private void RecalculateFinalTagExecutionDetails(
	Photo photo,
	IReadOnlyCollection<TemplateTag> templateTags,
	Dictionary<string, TagValidationResult> templateCheckResults,
	Dictionary<string, (string Value, string StatusColor, string DisplayText)> tagExecutionDetails,
	Dictionary<string, (string OriginalValue, string NewValue, bool Changed)> overwriteDiffs,
	bool isDryRun,
	bool overwriteTagsFlag)
	{
		// Usamos una constante ficticia para representar el valor [NOT_SET]
		// En tu código real, esto sería 'PhotoCli.Core.Constants.MetadataNotSetValue' o similar.
		const string MetadataNotSetValue = "[NOT_SET]";

		foreach (var tag in templateTags)
		{
			if (!templateCheckResults.TryGetValue(tag.Name, out var check))
				continue;

			tagExecutionDetails.TryGetValue(tag.Name, out var existingResult);

			string finalValueDisplay = check.Value.EscapeMarkup();
			string valueColor;
			string labelColor;
			string statusLabel;

			// ---------------------------------------------------------
			// CASO 1: Kept / Skipped (Prioridad Alta)
			// ---------------------------------------------------------

			// A. Respetar Kept (Coincidencia Perfecta o Protección).
			if (existingResult.DisplayText?.Contains("(Kept)") == true || existingResult.DisplayText?.Contains("(Match)") == true)
			{
				// AJUSTE: El valor es tenue, pero la acción de 'Kept' es afirmada en verde
				valueColor = "dim";
				labelColor = "green";
				statusLabel = "(Match / Kept)";

				tagExecutionDetails[tag.Name] = (
					check.Value,
					valueColor,
					$"[{valueColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]"
				);
				continue;
			}

			// B. Respetar Empty / Not Applicable.
			// ************************************************************
			// CORRECCIÓN CLAVE: Excluimos el valor [NOT_SET] intencional
			// ************************************************************
			if (check.Value != MetadataNotSetValue && // <--- EXCLUSIÓN DEL VALOR INTENCIONAL
				(existingResult.DisplayText?.Contains("(Empty)") == true ||
				 existingResult.DisplayText?.Contains("(Not Applicable / Not Written)") == true))
			{
				valueColor = "dim";
				labelColor = "dim";
				statusLabel = "(Empty / Skipped)";

				// Como el valor es vacío, solo mostramos la etiqueta
				tagExecutionDetails[tag.Name] = (
					check.Value,
					valueColor,
					$"[{labelColor}]{statusLabel}[/]"
				);
				continue;
			}

			// ---------------------------------------------------------
			// CASO 2: Error / Missing (IsValid = false)
			// ---------------------------------------------------------
			if (!check.IsValid)
			{
				valueColor = "red";
				statusLabel = isDryRun ? "MISSING / Required" : "FAILED: Missing After Write";

				string errorDisplay = isDryRun
					? $"[bold white on red]{statusLabel}[/]"
					: $"[bold red]✖ {statusLabel}[/]";

				tagExecutionDetails[tag.Name] = (check.Value, valueColor, errorDisplay);
				continue;
			}

			// ---------------------------------------------------------
			// CASO 3: Diferencias y Overwrite
			// ---------------------------------------------------------
			if (overwriteDiffs.TryGetValue(tag.Name, out var diff))
			{
				// A. Tienen valores diferentes
				if (diff.Changed)
				{
					if (overwriteTagsFlag)
					{
						// CASO: Diferente + Overwrite activado -> Se escribirá
						valueColor = "dim"; // El valor original que se va (en dim)
						labelColor = isDryRun ? "magenta" : "green"; // La acción
						statusLabel = isDryRun ? "(Differs / Overwrite)" : "(Updated)";

						// Mostramos: "ValorViejo -> ValorNuevo (Estado)"
						// Usamos finalValueDisplay (que debería ser el valor de la plantilla)
						string diffText = $"[{valueColor}]{diff.OriginalValue}[/] → [{labelColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]";
						tagExecutionDetails[tag.Name] = (check.Value, labelColor, diffText);
					}
					else
					{
						// CASO: Diferente + Overwrite apagado -> Se protege el original
						valueColor = "yellow"; // El valor original protegido (llama la atención)
						labelColor = "red"; // AJUSTE: La etiqueta es roja para el conflicto
						statusLabel = "(Differs / Kept)";

						string targetValueDisplay = diff.NewValue;

						// Mostramos: "ValorViejo (Target: ValorNuevo) (Estado)"
						string diffText = $"[{valueColor}]{diff.OriginalValue}[/] [dim](Target: {targetValueDisplay})[/] [{labelColor}]{statusLabel}[/]";
						tagExecutionDetails[tag.Name] = (check.Value, valueColor, diffText);
					}
				}
				// B. Tienen el mismo valor (Coinciden)
				else
				{
					// AJUSTE: Formato de Match/Kept
					valueColor = "dim";
					labelColor = "green";
					statusLabel = "(Match / Kept)";
					// Usamos el valor existente (finalValueDisplay) ya que es igual al valor de la plantilla
					tagExecutionDetails[tag.Name] = (check.Value, valueColor, $"[{valueColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]");
				}
				continue;
			}

			// ---------------------------------------------------------
			// CASO 4: Nuevo (Valor previo era null/vacio)
			// ---------------------------------------------------------

			// ************************************************************
			// AJUSTE: Manejo de Placeholder [NOT_SET]
			// ************************************************************
			if (check.Value == MetadataNotSetValue)
			{
				// Este es un valor intencional para tags requeridos que estaban vacíos.
				valueColor = "dim"; // El valor es [NOT_SET]
				labelColor = "yellow";
				statusLabel = isDryRun ? "(New Placeholder)" : "(Written Placeholder)";

				// Construimos la salida: [NOT_SET] (New Placeholder)
				tagExecutionDetails[tag.Name] = (
					check.Value,
					valueColor,
					$"[{valueColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]"
				);
			}
			else
			{
				// Este es un valor nuevo REAL (no un placeholder)
				if (isDryRun)
				{
					// CONFIGURACIÓN DRY RUN
					valueColor = "cyan";    // El valor: Unknown (Llama la atención)
					labelColor = "yellow";     // AJUSTE: La acción: (New / To Write) (Aviso)
					statusLabel = "(New / To Write)";
				}
				else
				{
					// CONFIGURACIÓN REAL RUN (Todo verde indica éxito)
					valueColor = "green";
					labelColor = "green";
					statusLabel = "(Written)";
				}

				// Construimos el string con los dos colores diferenciados
				tagExecutionDetails[tag.Name] = (
					check.Value,
					valueColor,
					$"[{valueColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]"
				);
			}
		}
	}


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

	private (bool Success, Photo? UpdatedPhoto) WriteMetadataForPhoto(
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
			return (false, photo);
		}
	}

	private (bool Passed, Dictionary<string, TagValidationResult> Results) RevalidateTemplateTags(Photo photo, IReadOnlyCollection<TemplateTag> templateTags)
	{
		var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
		var photoMetadataDict = photo.ExifData.Metadata;
		bool templateError = false;

		foreach (var tag in templateTags)
		{
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

	private string? ResolveTagValue(
		Photo photo,
		TemplateTag tagDefinition,
		bool isDryRun,
		IReadOnlyCollection<TemplateTag> allTemplateTags)
	{
		if (tagDefinition.Source.Type == SourceType.Variable)
		{
			if (_photoPropertyMap.TryGetValue(tagDefinition.Source.ValueKey, out var propertyGetter))
				return propertyGetter(photo);

			return isDryRun ? "CONFIG_ERROR" : null;
		}

		if (tagDefinition.Source.Type == SourceType.Literal)
		{
			return tagDefinition.Source.ValueKey;
		}

		if (tagDefinition.Source.Type == SourceType.ExifToolTag)
		{
			if (!isDryRun)
			{
				return $"<{tagDefinition.Source.ValueKey}";
			}

			var sourceTag = allTemplateTags
				.FirstOrDefault((TemplateTag t) => t.Name.Equals(tagDefinition.Source.ValueKey, StringComparison.OrdinalIgnoreCase));

			if (sourceTag != null)
			{
				return ResolveTagValue(photo, sourceTag, isDryRun: true, allTemplateTags);
			}

			return photo.ExifData?.Metadata.GetString(tagDefinition.Source.ValueKey);
		}

		return null;
	}

	#endregion
}
