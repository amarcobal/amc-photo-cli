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

namespace PhotoCli.Core.Services.Implementations;

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
	bool isDryRun,
	bool overwriteTags,
	bool allowUnknownIdentity)
	{
		var templateTags = GetTemplateTags(templateName);

		// Colecciones estándar (no concurrentes)
		var finalValidationResults = new Dictionary<string, FileValidationResult>(StringComparer.OrdinalIgnoreCase);
		var finalRunLogResults = new List<AddPreviewResult>();
		var identityDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// Contadores simples
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
									bool changed = !existingVal!.Equals(resolvedValue, StringComparison.OrdinalIgnoreCase);
									overwriteDiffs[tag.Name] = (existingVal.EscapeMarkup(), resolvedValue.EscapeMarkup(), changed);

									if (changed)
									{
										displayText = $"[bold]{existingVal.EscapeMarkup()}[/] → [yellow]{resolvedValue.EscapeMarkup()}[/]";
										statusColor = "yellow";
									}
									else
									{
										displayText = $"[dim]{resolvedValue.EscapeMarkup()}[/] (No Change)";
										statusColor = "dim";
									}
								}
								else
								{
									displayText = $"[cyan]{resolvedValue.EscapeMarkup()}[/] (New)";
									statusColor = "cyan";
								}

								tagExecutionDetails[tag.Name] = (resolvedValue, statusColor, displayText);
							}
						}
						else
						{
							if (isDryRun)
							{
								string displayText = $"[dim]{existingVal!.EscapeMarkup()}[/] (Kept)";
								tagExecutionDetails[tag.Name] = (existingVal, "dim", displayText);
							}
						}
					}

					templatePassed = !templateError;

					if (templatePassed)
					{
						// 3. Ejecución Real y Refresco
						writeSuccess = true;
						readyToProcessCount++;

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
						(templatePassed, templateCheckResults) = RevalidateTemplateTags(currentPhoto, templateTags);

						RecalculateFinalTagExecutionDetails(currentPhoto, templateTags, templateCheckResults, tagExecutionDetails, overwriteDiffs, isDryRun);


						if (!writeSuccess && !isDryRun)
						{
							templatePassed = false;
							failedByWriteError++;
							readyToProcessCount--;
							_statistics.InternalError++;
						}
						else if (!templatePassed)
						{
							if (writeSuccess || isDryRun)
							{
								failedByTemplateValidation++;
								if (!isDryRun) _statistics.InternalError++;
							}
						}
					}
					else
					{
						failedByTemplateValidation++;
						if (!isDryRun) _statistics.InternalError++;
					}
				}
				else
				{
					skippedIdentityCount++;
					if (!isDryRun) _statistics.InternalError++;
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
					validationResult
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
			isDryRun);

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
					bool isMissingOrEmpty = string.IsNullOrWhiteSpace(value) || value == new SubSeconds("0").Padded();

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
		int readyToProcessCount,
		int skippedIdentityCount,
		int failedByTemplateValidation,
		int failedByWriteError,
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

		int keptUnchangedCount = 0;
		int filesReadyToWrite = 0;
		int failedByIdentity = 0;
		int allowedUnknownWarnings = 0;
		int missingTakenDate = 0;
		int missingDevice = 0;
		int missingAuthor = 0;
		int missingMakeModel = 0;
		int failedByTemplate = failedByTemplateValidation + failedByWriteError;


		foreach (var result in finalRunResults)
		{
			var fullPath = result.FullPath;

			string status;
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
				bool templateFailedForThisRow = !result.TemplatePassed;

				if (!result.IdentityPassed) failedByIdentity++;

				if (!result.IdentityPassed && templateFailedForThisRow)
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

			var identityTags = result.ValidationResult.IdentityTags;

			if (!result.IdentityPassed)
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
			else if (result.IdentityPassed && allowUnknownIdentity)
			{
				var identityLog = identityDetails[fullPath];
				if (identityLog.Contains("[yellow]△[/]"))
				{
					allowedUnknownWarnings++;
				}
			}

			if (result.IdentityPassed && result.TemplatePassed)
			{
				bool hasAnyChange = result.OverwriteDifferences.Any(d => d.Value.Changed) ||
									result.TagsResults.Any(t => t.Value.StatusColor == "cyan" || t.Value.StatusColor == "green");

				if (!hasAnyChange)
				{
					keptUnchangedCount++;
				}
			}

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
		}

		readyToProcessCount = filesReadyToWrite - keptUnchangedCount;


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
		// COMMAND RESULT
		// -------------------------------------------------------------------------
		var rule = new Rule("[bold]COMMAND RESULT[/]");
		rule.Justification = Justify.Center;
		rule.Style = new Style(foreground: Color.Yellow);
		AnsiConsole.Write(rule);

		var totalFiles = photos.Count;
		var passedSimulated = readyToProcessCount;
		var failedSimulated = skippedIdentityCount + failedByTemplate;

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

		if (failedByTemplate > 0)
		{
			var failureType = isDryRun ? "Missing" : "Missing / Write Error";
			_consoleWriter.WriteMarkup($"• [yellow]Required Tags (Template) {failureType}:[/][bold red] {failedByTemplate}[/] files");
		}

		// -------------------------------------------------------------------------
		// TEMPLATE / WRITE FAILURE DETAILS
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


	private void RecalculateFinalTagExecutionDetails(
		Photo photo,
		IReadOnlyCollection<TemplateTag> templateTags,
		Dictionary<string, TagValidationResult> templateCheckResults,
		Dictionary<string, (string Value, string StatusColor, string DisplayText)> tagExecutionDetails,
		Dictionary<string, (string OriginalValue, string NewValue, bool Changed)> overwriteDiffs,
		bool isDryRun)
	{
		foreach (var tag in templateTags)
		{
			if (!templateCheckResults.TryGetValue(tag.Name, out var check))
				continue;

			if (tagExecutionDetails.TryGetValue(tag.Name, out var existingResult) && existingResult.DisplayText.Contains("(Kept)"))
			{
				continue;
			}

			string tagValueDisplay = check.Value.EscapeMarkup();
			string statusColor;
			string displayText;

			if (check.IsValid)
			{
				if (overwriteDiffs.TryGetValue(tag.Name, out var diff))
				{
					if (diff.Changed)
					{
						statusColor = "yellow";
						displayText = $"[bold]{diff.OriginalValue}[/] → [yellow]{tagValueDisplay}[/]";
					}
					else
					{
						statusColor = "dim";
						displayText = $"[dim]{tagValueDisplay}[/] (No Change)";
					}
				}
				else
				{
					statusColor = isDryRun ? "cyan" : "green";
					displayText = $"[{(isDryRun ? "cyan" : "green")}]{tagValueDisplay}[/] (New/Verified)";
				}
			}
			else
			{
				statusColor = "red";
				displayText = isDryRun
					? "[bold white on red]MISSING/ERROR (DryRun)[/]"
					: "[bold red]✖ FAILED: Missing After Write[/]";
			}

			tagExecutionDetails[tag.Name] = (check.Value, statusColor, displayText);
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
