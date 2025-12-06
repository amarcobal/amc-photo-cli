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
		
		// WORKFLOW
		{ "DerivedFileName", p => p.NewName },
		{ "DerivedFolderPath", p => p.TargetRelativePath },
		
		// ORIGIN
		{ "TakenDateTime", p => p.TakenDateTime?.ToString("yyyy:MM:dd HH:mm:ss") },
		{ "OriginalFileName", p => p.OriginalFileName },
		{ "Make", p => p.Make },
		{ "Model", p => p.Model },
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

		var dryRunResults = new Dictionary<string, Dictionary<string, (string Value, string StatusColor)>>();
		var identityResults = new Dictionary<string, bool>();
		var identityDetails = new Dictionary<string, string>();

		if (!templateTags.Any())
		{
			_logger.LogWarning("Template '{Template}' no tiene tags definidos.", templateName);
			return photos;
		}

		foreach (var photo in photos)
		{
			// 1. Validar Identidad
			var (identityPassed, identityLog) = ValidateIdentity(photo, allowUnknownIdentity);

			identityResults[photo.PhotoFile.SourceFullPath] = identityPassed;
			identityDetails[photo.PhotoFile.SourceFullPath] = identityLog;

			if (!identityPassed)
			{
				if (isDryRun) dryRunResults[photo.PhotoFile.SourceFullPath] = new Dictionary<string, (string Value, string StatusColor)>();
				else _statistics.InternalError++;
				continue;
			}

			// 2. Resolver Tags y aplicar lógica de Overwrite
			var tagsToWriteForPhoto = new List<KeyValuePair<string, string>>();
			var dryRunFileTags = new Dictionary<string, (string Value, string StatusColor)>();
			bool templateError = false;

			foreach (var tag in templateTags)
			{
				string? resolvedValue = null;

				// Resolución del valor
				if (tag.Source.Type == SourceType.Variable)
				{
					if (_photoPropertyMap.TryGetValue(tag.Source.ValueKey, out var propertyGetter))
						resolvedValue = propertyGetter(photo);
					else
					{
						templateError = true;
						if (isDryRun) dryRunFileTags[tag.Name] = ("Config Error", "red");
					}
				}
				else if (tag.Source.Type == SourceType.ExifToolTag)
					resolvedValue = $"<{tag.Source.ValueKey}";
				else if (tag.Source.Type == SourceType.Literal)
					resolvedValue = tag.Source.ValueKey;

				// Validación del valor resuelto
				if (string.IsNullOrWhiteSpace(resolvedValue))
				{
					if (tag.Required)
					{
						templateError = true;
						if (isDryRun) dryRunFileTags[tag.Name] = ("MISSING", "bold white on red");
					}
					else if (isDryRun)
					{
						dryRunFileTags[tag.Name] = ("(Empty)", "dim");
					}
					continue;
				}

				// Lógica de Overwrite
				bool tagExistsInFile = photo.ExifData.Metadata.ContainsKey(tag.Name);
				bool shouldWrite = !tagExistsInFile || overwriteTags;

				if (shouldWrite)
				{
					tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, resolvedValue));
					if (isDryRun) dryRunFileTags[tag.Name] = (resolvedValue, "cyan"); // Cyan = Nuevo/Update
				}
				else
				{
					// El tag existe y NO estamos sobrescribiendo
					if (isDryRun)
					{
						var existingVal = photo.ExifData.Metadata[tag.Name];
						dryRunFileTags[tag.Name] = ($"{existingVal.EscapeMarkup()} (Kept)", "dim");
					}
				}
			}

			if (isDryRun)
			{
				dryRunResults[photo.PhotoFile.SourceFullPath] = dryRunFileTags;
			}

			if (identityPassed && !templateError)
			{
				if (!isDryRun && tagsToWriteForPhoto.Any())
				{
					WriteMetadataBatch(new[] { photo }, tagsToWriteForPhoto, $"Template {templateName}", isDryRun: false);
					photosToProcess.Add(photo);
				}
			}
			else if (!isDryRun)
			{
				_statistics.InternalError++;
			}
		}

		// 3. Visualización de la Tabla (Solo DryRun)
		if (isDryRun)
		{
			PrintAddPreviewTable(photos, templateTags, dryRunResults, identityResults, identityDetails, templateName, overwriteTags);
		}

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

	// CAMBIO DE FIRMA: Ahora devuelve la nueva estructura FileValidationResult
	public IReadOnlyDictionary<string, FileValidationResult> CheckMetadata(
		IReadOnlyCollection<Photo> photos, string metadataKey, bool isRequired = true)
	{
		var singleTagList = new List<TemplateTag> { new() { Name = metadataKey, Required = isRequired } };
		var defaultView = new List<MetadataCheckViewType> { MetadataCheckViewType.TemplateDetails }.AsReadOnly();
		return CheckMetadataFromTemplateBase(photos, singleTagList, $"Key {metadataKey}", defaultView, allowUnknownIdentity: true);
	}

	// CAMBIO DE FIRMA: Ahora devuelve la nueva estructura FileValidationResult
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
	// CAMBIO DE FIRMA: Ahora devuelve la nueva estructura FileValidationResult
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
			var (identityPassed, identityLog) = ValidateIdentity(photo, allowUnknownIdentity);

			// --- B.1. Inyección de resultados de Identidad en el nuevo diccionario (SIN [System]) ---
			bool isRequiredForCheck(string key, bool allowUnknown) => key == "TakenDate" || !allowUnknown;

			var identityChecks = new (string Key, Func<Photo, string?> Getter, string DisplayName, bool IsExif)[]
			{
				("TakenDate", p => p.TakenDateTime?.ToString("yyyy-MM-dd HH:mm:ss"), "Taken Date", true),
				("Make", p => p.Make, "Make", true),
				("Model", p => p.Model, "Model", true),
				("Author", p => p.Author?.ID, "Author", false),
				("Device", p => p.Device?.ID, "Device", false)
			};

			foreach (var check in identityChecks)
			{
				var val = check.Getter(photo);
				bool isUnknown = !check.IsExif && val == "Unknown";
				bool hasValue = !string.IsNullOrWhiteSpace(val) && val != "Unknown";

				// 1. Determinar si el campo es requerido para esta ejecución (CORRECCIÓN isRequired)
				bool required = isRequiredForCheck(check.Key, allowUnknownIdentity);

				// 2. Determinar la validez
				bool isValidCheck;
				if (check.Key == "TakenDate") isValidCheck = hasValue;
				else isValidCheck = hasValue || allowUnknownIdentity;

				// 3. Creación del TagValidationResult
				identityCheckResults[check.Key] = new TagValidationResult(
					hasValue,
					val ?? string.Empty,
					IsRequired: required, // <-- CORRECCIÓN APLICADA
					IsValid: isValidCheck);
			}

			// --- C. Lógica de Estado Global (Status Column) ---
			bool identityIsFail = !identityPassed;
			bool templateIsFail = !templatePassed;
			bool rowPassed = !identityIsFail && !templateIsFail;

			string statusStyled;
			if (rowPassed)
			{
				if (identityLog.Contains("[yellow]"))
					statusStyled = "[bold yellow]△ Warn (Identity)[/]";
				else
					statusStyled = "[bold green]✔ OK[/]";
			}
			else
			{
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

		return result;
	}


	#endregion

	#region 5. Métodos Privados (Helpers)

	// Helper unificado para validar identidad respetando AllowUnknownIdentity
	private (bool IsValid, string LogOutput) ValidateIdentity(Photo photo, bool allowUnknown)
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

			// 3. Generación del Log de Salida (usando '△' en lugar de '⚠️')
			if (isValidCheck)
			{
				if (check.Key != "TakenDate" && (isCriticalMissing || isUnknown))
				{
					// Es Warning (Permitido) -> Usamos '△'
					string status = isUnknown ? "Unknown" : "MISSING";
					sb.AppendLine($"[yellow]✔[/] [dim] {check.DisplayName}:[/] [yellow]{status} (Allowed)[/]");
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


	// Método específico para pintar la tabla de Add Preview (Dry Run)
	private void PrintAddPreviewTable(
		IReadOnlyCollection<Photo> photos,
		IReadOnlyCollection<TemplateTag> templateTags,
		Dictionary<string, Dictionary<string, (string Value, string StatusColor)>> dryRunResults,
		Dictionary<string, bool> identityResults,
		Dictionary<string, string> identityDetails,
		string templateName,
		bool overwriteTags)
	{
		var columns = new List<TableColumnConfig>
		{
			new() { HeaderText = "File", Width = 40 },
			new() { HeaderText = "Status", Width = 18, NoWrap = true },
			new() { HeaderText = "Identity Check", Width = 40 },
			new() { HeaderText = $"Proposed Tags (Template: {templateName})", Width = null }
		};

		var rows = new List<List<string>>();

		foreach (var photo in photos)
		{
			var path = photo.PhotoFile.SourceFullPath;
			bool idOk = identityResults.ContainsKey(path) && identityResults[path];

			var tagResults = dryRunResults.ContainsKey(path)
				? dryRunResults[path]
				: new Dictionary<string, (string Value, string StatusColor)>();

			// Calcular si faltan tags requeridos
			bool tagsOk = true;
			foreach (var t in templateTags)
			{
				if (t.Required && (!tagResults.ContainsKey(t.Name) || tagResults[t.Name].StatusColor.Contains("red")))
					tagsOk = false;
			}

			string status;
			if (idOk && tagsOk) status = "[bold green]✅ Ready[/]";
			else if (!idOk && !tagsOk) status = "[bold red]❌ Skip (Both)[/]";
			else if (!idOk) status = "[bold red]❌ Skip (Identity)[/]";
			else status = "[bold red]❌ Skip (Template)[/]";

			// Construir celda de tags propuestos
			var tagsBuilder = new StringBuilder();
			foreach (var tag in templateTags)
			{
				string line;

				if (tagResults.TryGetValue(tag.Name, out var res))
				{
					line = $"[teal]{tag.Name}[/]: [{res.StatusColor}]{res.Value.EscapeMarkup()}[/]";
				}
				else if (tag.Required)
				{
					line = $"[teal]{tag.Name}[/]: [bold white on red]MISSING CALCULATION[/]";
				}
				else continue; // Tag opcional no resuelto, no se muestra

				tagsBuilder.AppendLine(line);
			}

			rows.Add(new List<string>
			{
				Markup.Escape(path),
				status,
				identityDetails.ContainsKey(path) ? identityDetails[path] : "",
				tagsBuilder.ToString()
			});
		}

		_consoleWriter.WriteMarkup("\n[bold yellow]--- DRY RUN PREVIEW (No changes applied) ---[/]");
		if (!overwriteTags) _consoleWriter.WriteMarkup("[dim]Info: --overwrite-tags is OFF. Existing values will be kept.[/]");

		_consoleWriter.WriteTable(columns, rows, $"Metadata Add Simulation: {templateName}");
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

	#endregion
}
