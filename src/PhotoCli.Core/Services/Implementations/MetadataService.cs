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
using PhotoCli.Core.Models.SpectreConsole; // Asegúrate de que esta clase exista y TableColumnConfig esté aquí


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
		
		// WORKFLOW (Asegúrate de que NewName y TargetRelativePath estén setteados antes por el Runner)
		{ "DerivedFileName", p => p.NewName },
		{ "DerivedFolderPath", p => p.TargetRelativePath },
		
		// ORIGIN (Datos extraídos del ExifData Appender)
		{ "TakenDateTime", p => p.TakenDateTime?.ToString("yyyy:MM:dd HH:mm:ss") },
		{ "OriginalFileName", p => p.OriginalFileName },
		{ "Make", p => p.Make },
		{ "Model", p => p.Model },
		// Agrega aquí cualquier otra propiedad de Photo que uses como SourceType.Variable
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
		bool isDryRun = false)
	{
		var templateTags = GetTemplateTags(templateName);
		var processedPhotos = new List<Photo>();

		if (!templateTags.Any())
		{
			_logger.LogWarning("Template '{Template}' no tiene tags definidos. Operación abortada.", templateName);
			return photos;
		}

		foreach (var photo in photos)
		{
			var tagsToWriteForPhoto = new List<KeyValuePair<string, string>>();
			bool shouldSkipPhoto = false;

			foreach (var tag in templateTags)
			{
				if (tag.Source.Type == SourceType.Variable)
				{
					if (_photoPropertyMap.TryGetValue(tag.Source.ValueKey, out var propertyGetter))
					{
						var resolvedValue = propertyGetter(photo);

						if (!string.IsNullOrWhiteSpace(resolvedValue))
						{
							tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, resolvedValue));
						}
						else if (tag.Required)
						{
							_logger.LogError("🛑 Required Variable '{variableKey}' for Metadata key '{TagKey}' not resolved in Photo. Skipping file: {File}", tag.Source.ValueKey, tag.Name, photo.PhotoFile.FileName);
							shouldSkipPhoto = true;
							break;
						}
					}
					else
					{
						_logger.LogError("🛑 Unknown Variable Key '{variableKey}' in template '{Template}'. Skipping file: {File}", tag.Source.ValueKey, templateName, photo.PhotoFile.FileName);
						shouldSkipPhoto = true;
						break;
					}
				}
				else if (tag.Source.Type == SourceType.ExifToolTag)
				{
					tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, $"<{tag.Source.ValueKey}"));
				}
				else if (tag.Source.Type == SourceType.Literal)
				{
					tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, tag.Source.ValueKey));
				}
				else
				{
					_logger.LogWarning("Tag {Key} en template {Template} tiene una fuente ({SourceType}) desconocida. Saltando.", tag.Name, templateName, tag.Source.Type);
				}
			}

			if (shouldSkipPhoto)
			{
				_statistics.InternalError++;
				continue;
			}

			if (tagsToWriteForPhoto.Any())
			{
				WriteMetadataBatch(new[] { photo }, tagsToWriteForPhoto, $"Template {templateName}", isDryRun);
				processedPhotos.Add(photo);
			}
			else
			{
				_logger.LogWarning("No se resolvieron tags válidos para escribir en {File} (Template: {Template}).", photo.PhotoFile.FileName, templateName);
			}
		}

		return processedPhotos;
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

	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadata(
		IReadOnlyCollection<Photo> photos, string metadataKey, bool isRequired = true)
	{
		var singleTagList = new List<TemplateTag>
		{
			new() { Name = metadataKey, Required = isRequired }
		};

		// Para una sola clave, se asume que se quiere ver la columna, el detalle y no hay columna Identity.
		// Usamos un valor fijo para simplificar, ya que no se usa --view
		var defaultView = new List<MetadataCheckViewType> { MetadataCheckViewType.TemplateDetails }.AsReadOnly();

		return CheckMetadataFromTemplate(photos, singleTagList, $"Key {metadataKey}", defaultView, allowUnknownIdentity: true);
	}

	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos,
		string templateName,
		IReadOnlyCollection<MetadataCheckViewType> viewTypes)
	{
		var templateTags = GetTemplateTags(templateName);
		// Asumimos aquí que si el comando es CHECK, Identity NO debe ser desconocido (false)
		return CheckMetadataFromTemplate(photos, templateTags, $"Template {templateName}", viewTypes, allowUnknownIdentity: false);
	}

	/// <summary>
	/// MÉTODO BASE DE VALIDACIÓN
	/// </summary>
	private IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadataFromTemplate(
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

		var result = new Dictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>>(StringComparer.OrdinalIgnoreCase);

		// 2. Definición de Columnas
		var columns = new List<TableColumnConfig>
		{
			new() { HeaderText = "File", Width = 40, NoWrap = false },
			new() { HeaderText = "Status", Width = 18, NoWrap = true } // Aumentamos un poco el ancho para "KO (Identity)"
		};

		if (showIdentityColumn)
			columns.Add(new() { HeaderText = "Media Identity", Width = 40, NoWrap = false });

		if (showTemplateColumn)
			columns.Add(new() { HeaderText = $"Tags: {contextName.Replace("Template ", "").Trim()}", NoWrap = true, Width = null });

		var rows = new List<List<string>>();
		bool overallSuccess = true;
		const int MaxValueDisplayLength = 30;

		// 3. Procesamiento
		foreach (var photo in photos)
		{
			// --- A. Validación de Template Tags ---
			var checkResults = new Dictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>(StringComparer.OrdinalIgnoreCase);
			var photoMetadataDict = photo.ExifData.Metadata;

			foreach (var tag in templateTags)
			{
				bool hasValue = photoMetadataDict.TryGetValue(tag.Name, out var value) &&
							!string.IsNullOrWhiteSpace(value) &&
							!value.Equals("undefined", StringComparison.OrdinalIgnoreCase);

				bool isValid = hasValue || !tag.Required;
				checkResults[tag.Name] = (hasValue, value ?? string.Empty, tag.Required, isValid);
			}

			bool templatePassed = checkResults.All(kv => kv.Value.IsValid);

			// --- B. Validación de Identidad ---
			var identityChecks = new (string Key, Func<Photo, string?> Getter, string DisplayName, bool IsExif)[]
			{
				("TakenDate", p => p.TakenDateTime?.ToString("yyyy-MM-dd HH:mm:ss"), "Taken Date", true),
				("Make",      p => p.Make, "Make", true),
				("Model",     p => p.Model, "Model", true),
				("Author",    p => p.Author?.ID, "Author", false),
				("Device",    p => p.Device?.ID, "Device", false)
			};

			bool identityPassed = true;
			var identityBuilder = new StringBuilder();

			foreach (var check in identityChecks)
			{
				var val = check.Getter(photo);
				bool isUnknown = !check.IsExif && val == "Unknown";
				bool ok = !string.IsNullOrWhiteSpace(val) && !isUnknown;

				if (!ok) identityPassed = false;

				// *** IMPORTANTE: Añadimos el resultado de identidad al diccionario de resultados ***
				// Usamos un prefijo especial "[System]" para que el Runner pueda distinguirlos y contarlos,
				// pero no se mezclen con los tags del template si alguien itera ciegamente.
				checkResults[$"[System] {check.Key}"] = (ok, val ?? "", true, ok);

				if (showIdentityDetails)
				{
					if (ok)
						identityBuilder.AppendLine($"[green]✔[/] [dim] {check.DisplayName}:[/] [cyan]{val!.EscapeMarkup()}[/]");
					else
					{
						string statusDisplay = isUnknown ? "[yellow]Unknown[/]" : "[bold white on red]MISSING[/]";
						identityBuilder.AppendLine($"[bold red]✖[/] [dim] {check.DisplayName}:[/] {statusDisplay}");
					}
				}
			}

			// --- C. Lógica de Estado Global (Status Column) ---

			// Si allowUnknownIdentity es true (ej. dry-run o metadata add), identity no bloquea el pase global
			bool identityIsFail = !identityPassed && !allowUnknownIdentity;
			bool templateIsFail = !templatePassed;

			bool rowPassed = !identityIsFail && !templateIsFail;

			if (!rowPassed) overallSuccess = false;

			string statusStyled;
			if (rowPassed)
			{
				if (!identityPassed && allowUnknownIdentity)
					statusStyled = "[bold yellow]⚠️ Warn (Identity)[/]";
				else
					statusStyled = "[bold green]✅ OK[/]";
			}
			else
			{
				// AQUÍ ESTÁ EL CAMBIO VISUAL QUE PEDÍAS
				if (identityIsFail && templateIsFail)
					statusStyled = "[bold red]❌ KO (Both)[/]";
				else if (identityIsFail)
					statusStyled = "[bold red]❌ KO (Identity)[/]";
				else // templateIsFail
					statusStyled = "[bold red]❌ KO (Template)[/]";
			}

			// --- D. Construcción de filas para la tabla ---
			var row = new List<string>
			{
				Markup.Escape(photo.PhotoFile.SourceFullPath), // Solo nombre o ruta según prefieras
				statusStyled
			};

			if (showIdentityColumn)
			{
				if (showIdentityDetails)
					row.Add(identityBuilder.ToString());
				else
					row.Add(identityPassed ? "[green]Valid[/]" : "[red]Invalid[/]");
			}

			if (showTemplateColumn)
			{
				if (showTemplateDetails)
				{
					var templateBuilder = new StringBuilder();
					foreach (var tag in templateTags)
					{
						// Solo mostramos los tags del template, no los de sistema que acabamos de añadir
						if (!checkResults.TryGetValue(tag.Name, out var check)) continue;

						var reqStatus = tag.Required ? "[red]*[/]" : "[dim]*[/]";

						string valueToDisplay;
						if (!check.IsValid) // Falló
							valueToDisplay = check.HasValue ? $"[red]{check.Value.EscapeMarkup()}[/]" : "[bold white on red]MISSING[/]";
						else
							valueToDisplay = string.IsNullOrEmpty(check.Value) ? "[dim](Empty)[/]" : $"[cyan]{check.Value.EscapeMarkup()}[/]";

						templateBuilder.AppendLine($"{reqStatus} [teal]{tag.Name.EscapeMarkup()}[/]: {valueToDisplay}");
					}
					row.Add(templateBuilder.ToString());
				}
				else
				{
					row.Add(templatePassed ? "[green]Valid[/]" : "[red]Invalid[/]");
				}
			}

			rows.Add(row);
			result[photo.PhotoFile.SourceFullPath] = checkResults;
		}

		// 4. Escribir Tabla
		_consoleWriter.WriteMarkup("[dim] [/]");
		_consoleWriter.WriteTable(columns, rows, $"Metadata Check: {contextName}");
		_consoleWriter.WriteMarkup("[dim] [/]");

		return result;
	}


	#endregion

	#region 5. Métodos Privados (Helpers)

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
