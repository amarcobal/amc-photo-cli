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
using System.Globalization;
using PhotoCli.Core.Utils.Constants;
using PhotoCli.Core.Utils.Extensions;

namespace PhotoCli.Core.Services.Implementations;

// Definición de AddPreviewResult, crucial para el nuevo flujo de estado
public record AddPreviewResult(
	string FullPath,
	bool IdentityPassed,
	bool TemplatePassed,
	Dictionary<string, (string Value, string StatusColor, string DisplayText)> TagsResults,
	Dictionary<string, (string OriginalValue, string NewValue, bool Changed)> OverwriteDifferences,
	FileValidationResult ValidationResult,
	MetadataRunStatus RunStatus // <-- Estado final de ejecución
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

		// ORIGIN ⭐️ ACTUALIZADO
		// 1. Estándar XMP/General (Espacio + Offset: 2024:04:22 17:53:18+02:00)
		{ "OriginalDateTime", p => p.OriginalDateTime?.ToString("yyyy:MM:dd HH:mm:sszzz") },
    
		// 2. Estándar QuickTime Keys (ISO 8601 puro: 2024-04-22T17:53:18+02:00)
		// Cambiamos ':' por '-' en la fecha y añadimos la 'T'. Esto "fuerza" al grupo Keys a no perder el offset.
		{ "OriginalDateTimeISO", p => p.OriginalDateTime?.ToString("yyyy-MM-ddTHH:mm:sszzz") },

		// 3. Hora Local Naive (Sin Offset ni Z)
		{ "OriginalDateTimeLocal", p => p.OriginalDateTimeLocal?.ToString("yyyy:MM:dd HH:mm:ss") },

		// 4. Hora UTC con Z (Para tus tags XMP-AMC de auditoría)
		{ "OriginalDateTimeUTC", p => p.OriginalDateTimeUTC?.ToString("yyyy:MM:dd HH:mm:ssZ") }, 

		// 5. Hora UTC sin Z (Para QuickTime:CreateDate y derivados)
		{ "OriginalDateTimeUTC_NoZ", p => p.OriginalDateTimeUTC?.ToString("yyyy:MM:dd HH:mm:ss") },

		// 6. Solo el Offset
		{ "OriginalTimeZoneOffset", p => p.OriginalTimeZoneOffset }, 

		{ "OriginalFileName", p => p.PhotoFile.FileNameWithExtension },
		{ "Make", p => p.Make },
		{ "Model", p => p.Model },
		{ "OriginalSubseconds", p => p.HasSubSeconds ? p.Subseconds?.Padded() : Constants.MetadataNotSetValue },

		// GEOLOCATION - Se establece valor solo si existen coordenadas (No indicamos fecha/hora para evitar problemas de forzar posición en mapa al consumir en clientes)
		{ "GPSLatitude", p => p.Coordinate != null
			? Math.Abs(p.Coordinate.Latitude).ToString("R", CultureInfo.InvariantCulture)
			: null },

		{ "GPSLongitude", p => p.Coordinate != null
			? Math.Abs(p.Coordinate.Longitude).ToString("R", CultureInfo.InvariantCulture)
			: null },

		// Refs necesarios para EXIF (N, S, E, W)
		{ "GPSLatitudeRef", p => p.Coordinate != null
			? (p.Coordinate.Latitude >= 0 ? "N" : "S")
			: null },

		{ "GPSLongitudeRef", p => p.Coordinate != null
			? (p.Coordinate.Longitude >= 0 ? "E" : "W")
			: null },

		// Formato compatible con ExifTool (Latitud, Longitud)
		{ "GPSCoordinatesISO", p => p.Coordinate != null
			? string.Format(CultureInfo.InvariantCulture, "{0:G} {1:G}",
				p.Coordinate.Latitude, p.Coordinate.Longitude)
			: null },

		// --- Las fechas GPS ahora dependen de la existencia de coordenadas ---
		{ "GPSOriginalDateUTC", p => p.Coordinate != null
			? p.OriginalDateTimeUTC?.ToString("yyyy:MM:dd")
			: null },

		{ "GPSOriginalTimeUTC", p => p.Coordinate != null
			? p.OriginalDateTimeUTC?.ToString("HH:mm:ss")
			: null },

		{ "GPSOriginalDateTime", p => p.Coordinate != null
			? p.OriginalDateTime?.ToString("yyyy:MM:dd HH:mm:sszzz")
			: null },
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
		// 1. Validar que el template base existe antes de empezar
		if (!_templateTagMap.ContainsKey(templateName))
		{
			_logger.LogWarning("Template base '{Template}' no está definido en la configuración. Operación abortada.", templateName);
			return new Dictionary<string, FileValidationResult>();
		}

		// Colecciones estándar (no concurrentes)
		var finalValidationResults = new Dictionary<string, FileValidationResult>(StringComparer.OrdinalIgnoreCase);
		var finalRunLogResults = new List<AddPreviewResult>();

		// ⭐️ CORRECCIÓN DE ÁMBITO: Declarar identityDetails fuera del ExecuteProgress
		var identityDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// -------------------------------------------------------------------------
		// CAMBIO CLAVE: Usar IProgressService para la barra de progreso
		// -------------------------------------------------------------------------
		var validationResults = _progressService.ExecuteProgress("Metadata Add/Check Process", ctx =>
		{
			var totalFiles = photos.Count;
			var task = ctx.AddTask($"[yellow]Processing Template:[/][bold cyan]{templateName}[/]", maxValue: totalFiles);

			var photosArray = photos.ToArray();

			// -------------------------------------------------------------------------
			// !!! INICIO DEL PROCESAMIENTO SECUENCIAL (FOREACH) !!!
			// -------------------------------------------------------------------------
			foreach (var photo in photosArray)
			{
				task.UpdateDescription($"[yellow]Processing Template:[/][bold cyan] {templateName}[/] - [dim]{photo.PhotoFile.FileName}[/]");

				var fullPath = photo.PhotoFile.SourceFullPath;
				var photoTemplateTags = GetTemplateTagsForAssetType(templateName, photo.PhotoFile.Type);

				if (!photoTemplateTags.Any())
				{
					_logger.LogTrace("Skipping file {File} because no tags were found for base template {Template} or specific type {Type}.",
									photo.PhotoFile.FileNameWithExtension, templateName, photo.PhotoFile.Type);

					var skippedResult = new FileValidationResult { IdentityTags = new Dictionary<string, TagValidationResult>() };
					finalValidationResults[fullPath] = skippedResult;

					finalRunLogResults.Add(new AddPreviewResult(
						fullPath,
						false, // IdentityPassed
						false, // TemplatePassed
						new Dictionary<string, (string Value, string StatusColor, string DisplayText)>(),
						new Dictionary<string, (string OriginalValue, string NewValue, bool Changed)>(),
						skippedResult,
						MetadataRunStatus.NotApplicable
					));

					task.Increment(1);
					continue;
				}

				bool templateError = false;
				bool templatePassed = false;
				bool writeSuccess = true;
				MetadataRunStatus runStatus = MetadataRunStatus.NotApplicable;

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
					foreach (var tag in photoTemplateTags)
					{
						string? resolvedValue = ResolveTagValue(currentPhoto, tag, isDryRun: true, photoTemplateTags);

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
								// El valor es la constante de control. No es un error.
								if (isDryRun)
								{
									tagExecutionDetails[tag.Name] = (Constants.MetadataNotSetValue, "dim", "[dim](Not Applicable / Not Written)[/]");
									templateCheckResults[tag.Name] = new TagValidationResult(false, string.Empty, tag.Required, true);
								}
								continue;
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

						// Si llegamos aquí, resolvedValue es un valor real
						if (isDryRun)
							templateCheckResults[tag.Name] = new TagValidationResult(true, resolvedValue, tag.Required, true);


						// ====================================================================================
						// INICIO DE LA CORRECCIÓN CLAVE PARA OBTENER EL VALOR ORIGINAL DE TAGS DE GRUPO (QuickTime)
						// ====================================================================================

						string? existingVal = null;

						// 1. Intentar leer el tag
						// Determinar la clave de lectura: Prioriza ReadTag si no es nulo/vacío, si no, usa Name.
						string readKey = string.IsNullOrWhiteSpace(tag.ReadTag) ? tag.Name : tag.ReadTag;
						if (currentPhoto.ExifData?.Metadata.TryGetValue(readKey, out var val) == true && !string.IsNullOrWhiteSpace(val))
						{
							existingVal = val;
						}

						bool tagExistsInFile = !string.IsNullOrWhiteSpace(existingVal);
						bool valuesAreSame = tagExistsInFile && existingVal!.Equals(resolvedValue, StringComparison.OrdinalIgnoreCase);

						// ====================================================================================
						// FIN DE LA CORRECCIÓN CLAVE
						// ====================================================================================

						// --- INICIO: LÓGICA de shouldWrite ---
						bool shouldWrite;

						if (!tagExistsInFile)
						{
							shouldWrite = true; // Siempre escribimos tags nuevos.
						}
						else // Tag existe (ya sea por Name o por ReadTag)
						{
							// Condición de Sobrescritura: ¿Debe el valor nuevo reemplazar al existente?
							// Esto es TRUE si: [Flag Global está ON] O [El Tag Granular está ON]
							bool mustOverwrite = overwriteTags || tag.Overwrite;

							if (mustOverwrite)
							{
								// Caso 2: El tag existe y se permite sobrescribir. Escribir solo si son diferentes.
								shouldWrite = !valuesAreSame;
							}
							else
							{
								// Caso 3: El tag existe y NO se permite sobrescribir. Nunca escribimos (Kept).
								shouldWrite = false;
							}
						}
						// --- FIN: LÓGICA de shouldWrite ---

						if (shouldWrite)
						{
							string tagToWrite = string.IsNullOrWhiteSpace(tag.WriteTag) ? tag.Name : tag.WriteTag;

							tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tagToWrite, resolvedValue));

							if (isDryRun)
							{
								string statusColor;
								string displayText;

								// Sobrescritura (Old -> New)
								if (tagExistsInFile)
								{
									// AHORA LLENAMOS CORRECTAMENTE EL DIFF CON EL VALOR ORIGINAL ENCONTRADO
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
								// CORRECCIÓN CLAVE PARA DETECCIÓN DE CONFLICTO (DIFFERS / KEPT)
								bool hasDiff = !valuesAreSame;

								// Llenamos overwriteDiffs.
								overwriteDiffs[tag.Name] = (existingVal!.EscapeMarkup(), resolvedValue.EscapeMarkup(), hasDiff);

								// Proporcionamos un placeholder de log temporal 
								string logPlaceholder = hasDiff ? "(Conflict Kept)" : "(Match)";
								tagExecutionDetails[tag.Name] = (existingVal!, "dim", logPlaceholder);
							}
						}
					}

					templatePassed = !templateError;

					if (templatePassed)
					{
						// 3. Ejecución Real y Refresco
						writeSuccess = true;

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
						if (!isDryRun)
						{
							// Revalidamos leyendo del archivo (o del objeto Photo actualizado)
							(templatePassed, templateCheckResults) = RevalidateTemplateTags(currentPhoto, photoTemplateTags);

							if (!writeSuccess && !isDryRun)
							{
								templatePassed = false;
							}
						}

						// Determinación del RunStatus antes de la recalcuación de detalles
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
							// Comprobamos si hubo *diferencias* que no se escribieron (protegidas) o que sí se escribieron (overwrite)
							bool hadMeaningfulDiffs = overwriteDiffs.Any(d => d.Value.Changed);

							if (hasChanges || hadMeaningfulDiffs)
							{
								// Si hay cambios a escribir, O hubo diferencias que se sobrescribieron
								runStatus = MetadataRunStatus.ReadyToWrite;
							}
							else
							{
								// Si no hubo cambios a escribir (todos eran Match)
								runStatus = MetadataRunStatus.Kept;
							}
						}


						RecalculateFinalTagExecutionDetails(
							currentPhoto,
							photoTemplateTags,
							templateCheckResults,
							tagExecutionDetails,
							overwriteDiffs,
							isDryRun,
							overwriteTags,
							runStatus);

					}
					else // !templatePassed (falló en la resolución inicial, ej. MISSING Required)
					{
						runStatus = MetadataRunStatus.FailedTemplate;
					}
				}
				else
				{
					runStatus = MetadataRunStatus.SkippedIdentity;
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
					runStatus
				));

				finalValidationResults[fullPath] = validationResult;

				task.Increment(1);
			}
			// -------------------------------------------------------------------------
			// !!! FIN DEL PROCESAMIENTO SECUENCIAL !!!
			// -------------------------------------------------------------------------

			task.UpdateDescription($"[green]✔ [/] [green]Completed Template:[/][bold cyan] {templateName}[/]");
			task.Stop();

			return finalValidationResults.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
		});


		// 5. Visualización de la Tabla y Resumen
		var templateTagsBase = GetTemplateTags(templateName);

		PrintAddPreviewTable(
			photos,
			templateTagsBase,
			finalRunLogResults.ToList(),
			// ⭐️ CORRECCIÓN: identityDetails ya está en ámbito y se pasa directamente.
			identityDetails,
			templateName,
			overwriteTags,
			0, // Recalculado
			0, // Recalculado
			0, // Recalculado
			0, // Recalculado
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

					// Exclusión de la constante de control de la lógica de error
					if (value == Constants.MetadataNotSetValue)
					{
						hasValue = false;
						isMissingOrEmpty = true;
					}


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

	private IReadOnlyCollection<TemplateTag> GetTemplateTagsForAssetType(string baseTemplateName, AssetType assetType)
	{
		// 1. Obtener tags del template base. 
		if (!_templateTagMap.TryGetValue(baseTemplateName, out var baseTags))
		{
			throw new ArgumentException($"Template base '{baseTemplateName}' no está definido en la configuración.", nameof(baseTemplateName));
		}

		// 2. Definir el MetadataAssetType objetivo
		var targetType = assetType switch
		{
			AssetType.Photo => MetadataAssetType.Photo,
			AssetType.Video => MetadataAssetType.Video,
			_ => MetadataAssetType.All // Incluye AssetType.Unknown y cualquier otro caso
		};

		// 3. Aplicar el filtro:
		var filteredTags = baseTags
			.Where(tag => tag.AssetType == MetadataAssetType.All || tag.AssetType == targetType)
			.ToList();

		if (!filteredTags.Any() && targetType != MetadataAssetType.All)
		{
			_logger.LogDebug("El template base '{Base}' no contiene tags para el AssetType {AssetType}. Esto puede ser esperado si solo se definen tags universales (All).",
							 baseTemplateName, assetType);
		}

		return filteredTags.AsReadOnly();
	}

	private (bool IsValid, string LogOutput) ValidateIdentityAndGenerateTags(
		Photo photo,
		bool allowUnknown,
		Dictionary<string, TagValidationResult> identityCheckResults)
	{
		var checks = new (string Key, Func<Photo, string?> Getter, string DisplayName, bool IsExif)[]
		{
			// ⭐️ 1. HORA CANÓNICA
			("OriginalDateTime", p => p.OriginalDateTime?.ToString("yyyy-MM-dd HH:mm:sszzz"), "Date Canonical (+Offset)", true),

			// ⭐️ 2. HORA LOCAL (CRÍTICA)
			("OriginalDateTimeLocal", p => p.OriginalDateTimeForFileOperations?.ToString("yyyy-MM-dd HH:mm:ss"), "Date Local (File Ops)", true),

			// ⭐️ 3. HORA UTC 
			("OriginalDateTimeUTC", p => p.OriginalDateTimeUTC?.ToString("yyyy-MM-dd HH:mm:ssZ"), "Date UTC", true),

			// ⭐️ 4. OFFSET DE ZONA HORARIA
			("OriginalTimeZoneOffset", p => p.OriginalTimeZoneOffset, "Time Zone Offset", true),

			// 5. Metadatos de Dispositivo/Autor
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

			// Lógica de Validación de Fechas
			if (check.Key == "OriginalDateTimeLocal")
			{
				// CRÍTICO: Debe existir para poder usar la foto.
				isValidCheck = hasValue;
			}
			else if (check.Key == "OriginalDateTime" || check.Key == "OriginalDateTimeUTC")
			{
				// INFORMATIVO: No son críticas.
				isValidCheck = true;
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

			// Requerido: Solo si es la fecha LOCAL o si no se permite Unknown
			bool required = check.Key == "OriginalDateTimeLocal" || !allowUnknown;

			identityCheckResults[check.Key] = new TagValidationResult(
				hasValue,
				val ?? string.Empty,
				IsRequired: required,
				IsValid: isValidCheck);

			// Generación de Salida (LogOutput)
			if (isValidCheck)
			{
				if ((check.Key == "OriginalDateTime" || check.Key == "OriginalDateTimeUTC") && !hasValue)
				{
					sb.AppendLine($"[yellow]△[/] [dim] {check.DisplayName}:[/] [dim]MISSING[/]");
				}
				else if (check.Key != "OriginalDateTimeLocal" && check.Key != "OriginalDateTime" && check.Key != "OriginalDateTimeUTC" && (!hasValue || isUnknown))
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
				// Manejo de Errores (FALLO CRÍTICO)
				string statusDisplay;
				if (check.Key == "OriginalDateTimeLocal" && !hasValue)
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
	int readyToProcessCount, // Se ignora
	int skippedIdentityCount, // Se ignora
	int failedByTemplateValidation, // Se ignora
	int failedByWriteError, // Se ignora
	bool allowUnknownIdentity,
	bool isDryRun,
	IReadOnlyCollection<MetadataCheckViewType> viewTypes)
	{
		var photoMap = photos.ToDictionary(p => p.PhotoFile.SourceFullPath, p => p, StringComparer.OrdinalIgnoreCase);

		bool showIdentityColumn = viewTypes.Contains(MetadataCheckViewType.Identity) || viewTypes.Contains(MetadataCheckViewType.IdentityDetails);
		bool showTemplateColumn = viewTypes.Contains(MetadataCheckViewType.Template) || viewTypes.Contains(MetadataCheckViewType.TemplateDetails);
		bool showIdentityDetails = viewTypes.Contains(MetadataCheckViewType.IdentityDetails);
		bool showTemplateDetails = viewTypes.Contains(MetadataCheckViewType.TemplateDetails);

		// --- 1. CONSTRUCCIÓN CONDICIONAL DE COLUMNAS ---
		var columns = new List<TableColumnConfig>
	{
		new() { HeaderText = "File", Width = 40 },
		new() { HeaderText = "Status", Width = 22, NoWrap = true },
	};

		if (showIdentityColumn)
		{
			columns.Add(new() { HeaderText = "Media Identity", Width = showIdentityDetails ? null : 40 });
		}

		if (showTemplateColumn)
		{
			columns.Add(new() { HeaderText = $"Proposed Tags (Template: {templateName})", Width = null });
		}


		var rows = new List<List<string>>();

		// --- Contadores finales (RECALCULAMOS) ---
		int keptUnchangedCountFinal = 0;
		int writtenUpdatedCount = 0;
		int failedByIdentityFinal = 0;
		int failedByTemplateValidationFinal = 0;
		int failedByWriteErrorFinal = 0;
		int allowedUnknownWarnings = 0;
		int totalTagDifferencesFound = 0;
		int missingTakenDate = 0;
		int missingDevice = 0;
		int missingAuthor = 0;
		int missingMakeModel = 0;

		// --- Bucle de Procesamiento de Resultados ---
		foreach (var result in finalRunResults)
		{
			var fullPath = result.FullPath;

			string status;

			// 1. Buscamos si hay algún tag que REALMENTE se va a sobrescribir
			bool willPerformAnyUpdate = result.OverwriteDifferences.Any(d =>
			{
				if (!d.Value.Changed) return false;

				// Buscamos la definición del tag para ver si tiene Overwrite granular
				var tagDef = templateTags.FirstOrDefault(t => t.Name.Equals(d.Key, StringComparison.OrdinalIgnoreCase));
				bool tagHasGranularOverwrite = tagDef?.Overwrite ?? false;

				// Se sobrescribe si: Flag Global OR Flag Granular
				return overwriteTags || tagHasGranularOverwrite;
			});


			switch (result.RunStatus)
			{
				case MetadataRunStatus.ReadyToWrite:
					// Si hay diferencias pero NINGUNA se va a escribir (todas protegidas) -> Es un KEPT
					if (result.OverwriteDifferences.Any(d => d.Value.Changed) && !willPerformAnyUpdate)
					{
						status = isDryRun ? "[bold dim blue]🔵 KEPT (Protected)[/]" : "[bold dim blue]✔ KEPT[/]";
						keptUnchangedCountFinal++;
					}
					else
					{
						status = isDryRun ? "[bold green]🟢 WRITE[/]" : "[bold green]✔ WRITE[/]";
						writtenUpdatedCount++;
					}
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
				case MetadataRunStatus.NotApplicable:
					status = "[bold dim]➖ N/A (No Tags)[/]";
					break;
				default:
					status = "[bold red]??? ERROR[/]";
					break;
			}

			// Lógica de conteo de Diferencias de Tags
			if (result.OverwriteDifferences.Any())
			{
				totalTagDifferencesFound += result.OverwriteDifferences.Count(d => d.Value.Changed);
			}

			var identityTags = result.ValidationResult.IdentityTags;

			// Lógica de conteo detallado de fallos
			if (result.RunStatus == MetadataRunStatus.SkippedIdentity)
			{
				if (identityTags.TryGetValue("OriginalDateTimeLocal", out var takenDateTag) && !takenDateTag.IsValid)
					missingTakenDate++;

				if (identityTags.TryGetValue("Device", out var deviceTag) && !deviceTag.IsValid && deviceTag.IsRequired)
					missingDevice++;
				if (identityTags.TryGetValue("Author", out var authorTag) && !authorTag.IsValid && authorTag.IsRequired)
					missingAuthor++;

				if (identityTags.TryGetValue("Make", out var makeTag) && identityTags.TryGetValue("Model", out var modelTag) && makeTag.IsRequired)
				{
					if (!makeTag.IsRequired || modelTag.IsRequired) // Corregida la lógica para verificar Make/Model si son requeridos
					{
						if (!makeTag.IsValid || !modelTag.IsValid)
							missingMakeModel++;
					}
				}
			}
			// Lógica de Warnings
			else if ((result.RunStatus == MetadataRunStatus.ReadyToWrite || result.RunStatus == MetadataRunStatus.Kept) && allowUnknownIdentity)
			{
				var identityLog = identityDetails.ContainsKey(fullPath) ? identityDetails[fullPath] : string.Empty;
				if (identityLog.Contains("[yellow]△[/]"))
				{
					allowedUnknownWarnings++;
				}
			}

			// Lógica de construcción de tagsBuilder
			var tagsBuilder = new StringBuilder();
			if (showTemplateColumn)
			{
				IReadOnlyCollection<TemplateTag> relevantTemplateTags = Array.Empty<TemplateTag>();

				if (photoMap.TryGetValue(fullPath, out var photo))
				{
					var assetType = photo.PhotoFile.Type;
					relevantTemplateTags = GetTemplateTagsForAssetType(templateName, assetType);
				}
				else
				{
					// Fallback
					relevantTemplateTags = templateTags;
				}


				if (showTemplateDetails)
				{
					// Iteramos sobre la lista FILTRADA
					foreach (var tag in relevantTemplateTags)
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
				else
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

		var writtenMessage = isDryRun
			? "files (Metadata will be written / updated)"
			: "files (Metadata successfully written)";

		var keptMessage = isDryRun
			? "files (Already had correct metadata and will be kept)"
			: "files (Already had correct metadata)";

		// Written/Updated (Éxito Activo - GREEN)
		_consoleWriter.WriteMarkup($"[green]   • Written / Updated:      [/][green]{writtenUpdatedCount}[/] {writtenMessage}");

		// Kept (Éxito Pasivo/Neutro - BLUE/DIM)
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
				// Formateamos el flag con colores dinámicos
				string flagName = "[yellow]--overwrite-tags[/]";
				string flagStatus = overwriteTags
					? "[bold green]ON[/]"
					: "[bold red]OFF[/]";

				string actionStatus = overwriteTags
					? $"[green]Updated[/] (Global flag {flagName} is {flagStatus})"
					: $"[blue]Protected / Kept[/] (Global flag {flagName} is {flagStatus})";

				_consoleWriter.WriteMarkup($"    • [yellow]Tag value differences found:[/][bold] {totalTagDifferencesFound}[/] tags. Action: {actionStatus}.");

				if (!overwriteTags)
				{
					_consoleWriter.WriteMarkup($"      [dim]Note: Individual tags with [white]'Overwrite: true'[/] in the template bypass the global {flagName}.[/]");
				}

				_consoleWriter.WriteMarkup("      [dim](Review the table for 'Differs / Kept' vs 'Updated' or 'Overwrite' statuses)[/]");
			}

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
		bool overwriteTagsFlag,
		MetadataRunStatus runStatus)
	{
		string valueColor;
		string labelColor;
		string statusLabel;

		// ************************************************************
		// CORRECCIÓN 3: PRIORIDAD DE ESTADO KEPT O SKIPPED
		// ************************************************************
		if (runStatus == MetadataRunStatus.Kept || runStatus == MetadataRunStatus.SkippedIdentity)
		{
			valueColor = "dim";
			labelColor = "dim blue";
			statusLabel = runStatus == MetadataRunStatus.Kept ? "(Match / Kept)" : "(Skipped / Identity)";

			foreach (var tag in templateTags)
			{
				if (templateCheckResults.TryGetValue(tag.Name, out var check))
				{
					string finalValueDisplay = check.Value.EscapeMarkup();

					(string Value, string StatusColor, string DisplayText) currentDisplay;

					if (tagExecutionDetails.TryGetValue(tag.Name, out currentDisplay) && !string.IsNullOrWhiteSpace(currentDisplay.Value))
					{
						finalValueDisplay = currentDisplay.Value.EscapeMarkup();
					}


					if (string.IsNullOrWhiteSpace(finalValueDisplay) || finalValueDisplay == Constants.MetadataNotSetValue.EscapeMarkup())
					{
						tagExecutionDetails[tag.Name] = (check.Value, labelColor, $"[{labelColor}]{statusLabel}[/]");
					}
					else
					{
						tagExecutionDetails[tag.Name] = (check.Value, valueColor, $"[{valueColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]");
					}
				}
				else if (tagExecutionDetails.TryGetValue(tag.Name, out var existingResult))
				{
					tagExecutionDetails[tag.Name] = (existingResult.Value, valueColor, $"[{valueColor}]{existingResult.Value.EscapeMarkup()}[/] [{labelColor}]{statusLabel}[/]");
				}
			}
			return; // Salimos de la función.
		}
		// ************************************************************

		// ---------------------------------------------------------------------
		// LÓGICA DE ESCRITURA/FALLO
		// ---------------------------------------------------------------------

		foreach (var tag in templateTags)
		{
			if (!templateCheckResults.TryGetValue(tag.Name, out var check))
				continue;

			(string Value, string StatusColor, string DisplayText) existingResult = default;
			tagExecutionDetails.TryGetValue(tag.Name, out existingResult);

			string finalValueDisplay = check.Value.EscapeMarkup();

			// ---------------------------------------------------------
			// CASO 1: Kept / Skipped (Prioridad Baja - Solo para lógica de 'Empty')
			// ---------------------------------------------------------

			// B. Respetar Empty / Not Applicable.
			if (check.Value != Constants.MetadataNotSetValue &&
				(existingResult.DisplayText?.Contains("(Empty)") == true ||
				 existingResult.DisplayText?.Contains("(Not Applicable / Not Written)") == true))
			{
				valueColor = "dim";
				labelColor = "dim";
				statusLabel = "(Empty / Skipped)";

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
					var currentTagDefinition = templateTags.First(t => t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase));
					bool tagHasOverwrite = currentTagDefinition.Overwrite;

					// Sobrescribimos si: [Flag Global está ON] O [El Tag Granular está ON]
					bool actualOverwriteWillHappen = overwriteTagsFlag || tagHasOverwrite;

					if (actualOverwriteWillHappen)
					{
						// CASO: Diferente + Sobrescritura permitida (Global O Granular) -> Se escribirá
						valueColor = "dim";
						labelColor = (tagHasOverwrite && !overwriteTagsFlag) ? "yellow" : (isDryRun ? "magenta" : "green");

						statusLabel = (tagHasOverwrite && !overwriteTagsFlag)
							? (isDryRun ? "(Differs / Overwrite: FORCED)" : "(Updated: FORCED)")
							: (isDryRun ? "(Differs / Overwrite)" : "(Updated)");

						// Mostramos: "ValorViejo -> ValorNuevo (Estado)"
						string diffText = $"[{valueColor}]{diff.OriginalValue}[/] → [{labelColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]";
						tagExecutionDetails[tag.Name] = (check.Value, labelColor, diffText);
					}
					else
					{
						// CASO: Diferente + Overwrite apagado (Kept) -> Se protege el original
						valueColor = "yellow";
						labelColor = "red";
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
					// Formato de Match/Kept
					valueColor = "dim";
					labelColor = "dim blue";
					statusLabel = "(Match / Kept)";
					tagExecutionDetails[tag.Name] = (check.Value, valueColor, $"[{valueColor}]{finalValueDisplay}[/] [{labelColor}]{statusLabel}[/]");
				}
				continue;
			}

			// ---------------------------------------------------------
			// CASO 4: Nuevo (Valor previo era null/vacio)
			// ---------------------------------------------------------

			if (check.Value == Constants.MetadataNotSetValue)
			{
				// Este es un valor intencional para tags requeridos que estaban vacíos.
				valueColor = "dim";
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
					valueColor = "cyan";
					labelColor = "yellow";
					statusLabel = "(New / To Write)";
				}
				else
				{
					valueColor = "green";
					labelColor = "green";
					statusLabel = "(Written)";
				}

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
			var distinctTagsToWrite = tagsToWrite
				.GroupBy(kvp => kvp.Key)
				.Select(group => group.First())
				.ToList();

			// 1. Configuración BASE de interpretación para TODO (Fotos y Vídeos)
			// Estos parámetros definen cómo ExifTool "entiende" los datos de cualquier archivo.
			var commonArgs = new List<string>
			{ 
				// -n (Print Conversion): 
				// Desactiva el formateo de texto de ExifTool. 
				// Es CRÍTICO para recibir:
				//   a) Coordenadas GPS en formato decimal puro (ej: 41.64 en lugar de 41 deg 38').
				//   b) Offsets de tiempo literales (ej: +02:00) evitando que ExifTool los oculte.
				//"-n"
			};

			// 2. Comandos de ACCIÓN para la operación de escritura
			// Definen el comportamiento del proceso de modificación del archivo.
			var actionCommands = new List<string>
			{ 
				// -P (Preserve): 
				// Preserva la fecha/hora de modificación del archivo del SISTEMA OPERATIVO (FileModifyDate).
				// Evita que en el Explorador de Windows parezca que el archivo es "nuevo" hoy.
				"-P", 

				// -a (Allow Duplicates): 
				// Permite extraer etiquetas con el mismo nombre si están en grupos distintos.
				// Necesario para leer simultáneamente [XMP-exif]DateTimeOriginal y [UserData]DateTimeOriginal.
				"-a"
			};

			// 3. Lógica específica para VÍDEOS
			if (photo.IsVideo)
			{
				// -api QuickTimeUTC=0: 
				// Indica que NO queremos que ExifTool convierta las fechas de video a la zona horaria del PC local.
				// Queremos leer y escribir el valor exacto que hay en el archivo (WYSIWYG).
				commonArgs.Add("-api QuickTimeUTC=0");

				// -api LargeFileSupport=1: 
				// Habilita el manejo de archivos de más de 4GB. Obligatorio para videos 4K largos.
				commonArgs.Add("-api LargeFileSupport=1");

				// -m (Minor errors): 
				// Ignora errores estructurales no críticos. 
				// Los MP4 a menudo tienen advertencias de "átomos" fuera de orden; sin -m, ExifTool cancela la escritura.
				actionCommands.Add("-m");
			}


			using (var exifTool = new ExifTool(
				exiftoolConfigPath: _options.ExifToolFileConfig,
				commonArgs: commonArgs))
			{
				// Escritura
				exifTool.WriteTags(
					photo.PhotoFile.SourcePath,
					distinctTagsToWrite,
					actionCommands,
					overwriteOriginal: true,
					isVerbose: true);

				_statistics.PhotosMetadataProcessed++;

				// 3. RE-EXTRACCIÓN: Aquí es donde aplicamos la misma lógica de lectura
				var updatedPhotos = _exifDataAppenderService.ExtractExifData(
												new[] { photo },
												out _, out _, out _, out _, out _, out _,
												isSilent: true);

				var updatedPhoto = updatedPhotos.FirstOrDefault();

				return (true, updatedPhoto ?? photo);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error adding metadata ({Context}) to {File}", contextName, photo.PhotoFile.SourcePath);
			_statistics.InternalError++;
			return (false, photo);
		}
	}

	//private (bool Success, Photo? UpdatedPhoto) WriteMetadataForPhoto(
	//	Photo photo,
	//	List<KeyValuePair<string, string>> tagsToWrite,
	//	string contextName)
	//{
	//	try
	//	{
	//		var distinctTagsToWrite = tagsToWrite
	//		.GroupBy(kvp => kvp.Key) // Agrupar por el Tag de Escritura (e.g., "AllDates")
	//		.Select(group => group.First()) // Tomar la última KVP del grupo (o First, si el orden no importa)
	//		.ToList();

	//		var exifToolCommands = new List<string>();
	//		var initialApiFlags = new List<string>();

	//		// 1. Lógica de Negocio: -P para todos los archivos (Preservar la fecha del sistema)
	//		exifToolCommands.Add("-P");
	//		exifToolCommands.Add("-a");

	//		// 2. Lógica de Negocio: -m solo para videos (Ignorar errores menores/desbloquear QuickTime)
	//		if (photo.IsVideo)
	//		{
	//			//initialApiFlags.Add("-api QuickTimeUTC=1");
	//			exifToolCommands.Add("-m");
	//			//exifToolCommands.Add("-n");
	//		}

	//		using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig, initialApiFlags: initialApiFlags))
	//		{
	//			exifTool.WriteTags(photo.PhotoFile.SourcePath, distinctTagsToWrite, exifToolCommands, overwriteOriginal: true, isVerbose: true);
	//			_statistics.PhotosMetadataProcessed++;

	//			// Refrescar los metadatos de la foto desde el archivo para revalidar
	//			var updatedPhotos = _exifDataAppenderService.ExtractExifData(
	//								new[] { photo },
	//								out _, out _, out _, out _, out _, out _,
	//								isSilent: true);

	//			var updatedPhoto = updatedPhotos.FirstOrDefault();

	//			if (updatedPhoto == null)
	//			{
	//				_logger.LogWarning("Failed to refresh metadata for {File} after successful write.", photo.PhotoFile.SourcePath);
	//				return (true, photo); // Asumimos éxito de escritura aunque el refresh falle
	//			}

	//			return (true, updatedPhoto);
	//		}
	//	}
	//	catch (Exception ex)
	//	{
	//		_logger.LogError(ex, "Error adding metadata ({Context}) to {File}", contextName, photo.PhotoFile.SourcePath);
	//		_statistics.InternalError++;
	//		return (false, photo);
	//	}
	//}

	private (bool Passed, Dictionary<string, TagValidationResult> Results) RevalidateTemplateTags(Photo photo, IReadOnlyCollection<TemplateTag> templateTags)
	{
		var templateCheckResults = new Dictionary<string, TagValidationResult>(StringComparer.OrdinalIgnoreCase);
		var photoMetadataDict = photo.ExifData.Metadata;
		bool templateError = false;

		foreach (var tag in templateTags)
		{
			// Revalidamos contra el tag de salida
			string readKey = string.IsNullOrWhiteSpace(tag.ReadTag) ? tag.Name : tag.ReadTag;
			bool hasValue = photoMetadataDict.TryGetValue(readKey, out var value) &&
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
		// 1. Resolve Variable (Local Property Map)
		if (tagDefinition.Source.Type == SourceType.Variable)
		{
			if (_photoPropertyMap.TryGetValue(tagDefinition.Source.ValueKey, out var propertyGetter))
				return propertyGetter(photo);

			return isDryRun ? "CONFIG_ERROR" : null;
		}

		// 2. Resolve Literal
		if (tagDefinition.Source.Type == SourceType.Literal)
		{
			return tagDefinition.Source.ValueKey;
		}

		// 3. Resolve ExifToolTag (Read from File's ExifData OR use ExifTool syntax if needed)
		if (tagDefinition.Source.Type == SourceType.ExifToolTag)
		{
			// Si NO es Dry Run, devolvemos la sintaxis de ExifTool para escritura.
			if (!isDryRun)
			{
				return $"<{tagDefinition.Source.ValueKey}";
			}

			// Si es Dry Run, intentamos leer el valor del tag de origen desde el objeto Photo.
			// Se asume que el tag de origen ya fue leído por ExifDataAppenderService.
			// (Alternativamente, se podría forzar una re-lectura aquí, pero eso es menos eficiente).

			// Opción más simple: Leer el valor del tag de origen directamente de ExifData
			return photo.ExifData?.Metadata.GetString(tagDefinition.Source.ValueKey);
		}

		return null;
	}

	#endregion
}
