using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Services.Implementations; // Asegúrate de que esta línea esté, si ConsoleWriter está aquí
using SharpExifTool;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Spectre.Console;
using Microsoft.Extensions.Logging; // Necesario para usar Markup.Escape

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

	// Mapeo de las claves de la Variable/CLI (ValueKey) a las propiedades del objeto Photo.
	// Esto centraliza la lógica de dónde sacar los datos resueltos.
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

		// Cargar el mapa de templates desde el YAML (a través del servicio)
		_templateTagMap = configService.GetTemplateMap();
	}

	#region 1. Métodos Públicos - ESCRITURA (Add)

	/// <summary>
	/// Añade un único par clave/valor a una colección de fotos.
	/// </summary>
	public IReadOnlyCollection<Photo> AddMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, string metadataValue, bool isDryRun = false)
	{
		// Prepara una lista simple con el único tag a escribir
		var tagsToWrite = new List<KeyValuePair<string, string>>
		{
			new(metadataKey, metadataValue)
		};

		// Llama al método de escritura base
		return WriteMetadataBatch(photos, tagsToWrite, $"Key/Value {metadataKey}", isDryRun);
	}

	/// <summary>
	/// Añade metadatos basados en un template.
	/// Resuelve los tags de tipo Variable/CLI directamente desde las propiedades del objeto Photo.
	/// </summary>
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
				// 1. Variable (Resuelve a través del mapeo de Photo)
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
						// Si no es requerido y no hay valor, simplemente no se añade el tag.
					}
					else
					{
						_logger.LogError("🛑 Unknown Variable Key '{variableKey}' in template '{Template}'. Skipping file: {File}", tag.Source.ValueKey, templateName, photo.PhotoFile.FileName);
						shouldSkipPhoto = true;
						break;
					}
				}

				// 2. ExifTool Tag (Redirección: -Tag=<SourceTag)
				else if (tag.Source.Type == SourceType.ExifToolTag)
				{
					// Utilizamos la sintaxis de redirección de ExifTool para tags que referencian a otros.
					tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, $"<{tag.Source.ValueKey}"));
				}

				// 3. Literal
				else if (tag.Source.Type == SourceType.Literal)
				{
					tagsToWriteForPhoto.Add(new KeyValuePair<string, string>(tag.Name, tag.Source.ValueKey));
				}
				else
				{
					_logger.LogWarning("Tag {Key} en template {Template} tiene una fuente ({SourceType}) desconocida. Saltando.", tag.Name, templateName, tag.Source.Type);
				}
			} // Fin foreach tag

			if (shouldSkipPhoto)
			{
				_statistics.InternalError++;
				continue;
			}

			if (tagsToWriteForPhoto.Any())
			{
				// Llama al método de escritura base (solo para la foto actual)
				WriteMetadataBatch(new[] { photo }, tagsToWriteForPhoto, $"Template {templateName}", isDryRun);
				processedPhotos.Add(photo);
			}
			else
			{
				_logger.LogWarning("No se resolvieron tags válidos para escribir en {File} (Template: {Template}).", photo.PhotoFile.FileName, templateName);
			}
		} // Fin foreach photo

		return processedPhotos;
	}

	#endregion

	#region 2. Métodos Públicos - BORRADO (Delete)

	/// <summary>
	/// Borra un único tag de metadatos de una colección de fotos.
	/// </summary>
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
						// USO DEL CONSOLE WRITER: DryRun estilizado
						_consoleWriter.Write($"[yellow bold][DryRun][/] Delete metadata [cyan]{metadataKey}[/] from [bold]{photo.PhotoFile.SourceFullPath.EscapeMarkup()}[/]");
						continue;
					}

					exifTool.DeleteTag(photo.PhotoFile.SourcePath, tagsToDelete, overwriteOriginal: true);
					_statistics.PhotosMetadataProcessed++;
					// USO DEL CONSOLE WRITER: Éxito estilizado
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

	/// <summary>
	/// Implementación de DeleteMetadataFromTemplate.
	/// Borra todos los tags definidos en un template.
	/// </summary>
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
						// USO DEL CONSOLE WRITER: DryRun estilizado
						_consoleWriter.Write($"[yellow bold][DryRun][/] Delete metadata from Template [bold]{templateName.EscapeMarkup()}[/] ([dim]{string.Join(", ", tagsToDelete)}[/]) from [bold]{photo.PhotoFile.SourceFullPath.EscapeMarkup()}[/]");
						continue;
					}

					// ExifTool acepta una colección de tags a borrar
					exifTool.DeleteTag(photo.PhotoFile.SourcePath, tagsToDelete, overwriteOriginal: true);
					_statistics.PhotosMetadataProcessed++;
					// USO DEL CONSOLE WRITER: Éxito estilizado
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

	/// <summary>
	/// Obtiene un único tag de metadatos.
	/// </summary>
	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadata(
		IReadOnlyCollection<Photo> photos, string metadataKey, bool showOutput = false)
	{
		return GetMetadata(photos, new[] { metadataKey }, showOutput);
	}

	/// <summary>
	/// Obtiene los metadatos de un template.
	/// </summary>
	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos, string templateName, bool showOutput = false)
	{
		var keys = GetTagNamesFromTemplate(templateName);
		return GetMetadata(photos, keys, showOutput);
	}

	/// <summary>
	/// MÉTODO BASE DE LECTURA (SERIAL): Obtiene una lista de claves de metadatos de las fotos.
	/// Se mantiene SERIAL (`foreach`) hasta que se decida usar Parallel.ForEach.
	/// </summary>
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
						? "[yellow]No matching metadata keys found.[/]" // Estilizado
						: string.Join(", ", filteredMetadata.Select(kv => $"[bold]{kv.Key}[/]='[cyan]{kv.Value.EscapeMarkup()}[/]'")); // Estilizado

					// USO DEL CONSOLE WRITER: Output estilizado
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

	/// <summary>
	/// Comprueba si un único tag de metadatos existe y tiene valor.
	/// </summary>
	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadata(
		IReadOnlyCollection<Photo> photos, string metadataKey, bool isRequired = true)
	{
		// Simula un "mini-template" para un solo tag
		var singleTagList = new List<TemplateTag>
	{
	new() { Name = metadataKey, Required = isRequired }
	};

		return CheckMetadataFromTemplate(photos, singleTagList, $"Key {metadataKey}");
	}

	/// <summary>
	/// Comprueba si los metadatos de un template son válidos (según la regla Required).
	/// </summary>
	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos, string templateName)
	{
		var templateTags = GetTemplateTags(templateName);
		return CheckMetadataFromTemplate(photos, templateTags, $"Template {templateName}");
	}

	/// <summary>
	/// MÉTODO BASE DE VALIDACIÓN: Comprueba una lista de TemplateTags contra las fotos, generando una salida en formato tabla.
	/// </summary>
	private IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos,
		IReadOnlyCollection<TemplateTag> templateTags,
		string contextName)
	{
		var metadataKeys = templateTags.Select(t => t.Name).ToList();
		var result = new Dictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>>(StringComparer.OrdinalIgnoreCase);

		// 1. Obtener todos los valores de una vez
		//var allMetadata = GetMetadata(photos, metadataKeys, showOutput: false);

		// Lista para construir la tabla de resultados (Ahora solo 3 columnas)
		var rows = new List<List<string>>();
		bool overallSuccess = true;
		const int MaxValueDisplayLength = 30; // Límite para evitar celdas demasiado anchas

		// 2. Definir los encabezados de las 3 columnas
		var headers = new List<string> { "File (Full Path)", "Status", $"Template: {contextName} Tags" };

		// 3. Validar cada foto y construir las filas
		foreach (var photo in photos)
		{
			var checkResults = new Dictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>(StringComparer.OrdinalIgnoreCase);
			var photoMetadataDict = photo.ExifData.Metadata;

			// Lógica de validación
			foreach (var tag in templateTags)
			{
				bool hasValue = photoMetadataDict.TryGetValue(tag.Name, out var value) &&
							!string.IsNullOrWhiteSpace(value) &&
							!value.Equals("undefined", StringComparison.OrdinalIgnoreCase);

				bool isValid = hasValue || !tag.Required;

				checkResults[tag.Name] = (hasValue, value ?? string.Empty, tag.Required, isValid);
			}

			// Determinación del estado y preparación de la fila
			var missingRequiredKeys = checkResults
				.Where(kv => kv.Value.Required && !kv.Value.IsValid)
				.Select(kv => kv.Key)
				.ToList();

			string status;
			string statusStyled;

			if (missingRequiredKeys.Count == 0)
			{
				status = "OK";
				statusStyled = "[bold green]:check_mark_button: OK[/]";
			}
			else
			{
				status = "KO";
				statusStyled = "[bold red]:cross_mark: KO[/]";
				overallSuccess = false;
			}

			// 4.3. Construcción del Contenido Anidado (Columna Template)
			var templateCellContent = new StringBuilder();

			foreach (var tag in templateTags)
			{
				if (checkResults.TryGetValue(tag.Name, out var check))
				{
					// INICIO: Envolvemos TODO el contenido de la línea en [dim]
					templateCellContent.Append("[dim]");

					// 1. Tag Name y Requisito
					var requiredStatus = tag.Required
					// Solución FINAL: Usamos [[R]] para garantizar que los corchetes interiores
					// se interpreten como texto literal. Luego, lo envolvemos en [bold red].
					? "[bold red][[R]][/]"
					: "[dim](O)[/]";

					// Usamos [blue] para el Tag Name, pero dentro de [dim]
					templateCellContent.Append($"* [blue]{tag.Name.EscapeMarkup()}[/] {requiredStatus}: ");

					// 2. Valor
					var valueToDisplay = check.Value;

					if (!check.HasValue)
					{
						// CORRECCIÓN APLICADA AQUÍ: Aseguramos que "(Empty)" también sea markup válido.
						valueToDisplay = check.Required ? "[bold red](MISSING!)[/]" : "[dim](Empty)[/]";
					}
					else if (valueToDisplay.Length > MaxValueDisplayLength)
					{
						valueToDisplay = $"[cyan]{valueToDisplay[..MaxValueDisplayLength].EscapeMarkup()}...[/]";
					}
					else
					{
						valueToDisplay = $"[cyan]{valueToDisplay.EscapeMarkup()}[/]";
					}

					templateCellContent.AppendLine(valueToDisplay + "[/]"); // FIN: Cerramos la etiqueta [dim]
				}
			}

			// Construcción de la fila de 3 columnas
			var row = new List<string>
		{
			// Columna 1: File (Ruta Completa)
			photo.PhotoFile.SourceFullPath.EscapeMarkup(),
			
			// Columna 2: Status (Estilizado)
			statusStyled,
			
			// Columna 3: Template (Contenido Anidado con formato [dim])
			templateCellContent.ToString()
		};

			rows.Add(row);
			result[photo.PhotoFile.SourceFullPath] = checkResults;
		} // Fin foreach photo

		// 4. Salida en la Consola (Tabla Final de 3 columnas)

		_consoleWriter.WriteMarkup("[dim] [/]"); // Línea vacía sutil con Spectre.Console

		_consoleWriter.WriteValidationTable(headers, rows, $"Metadata Validation Report: {contextName}");

		_consoleWriter.WriteMarkup("[dim] [/]"); // Línea vacía sutil

		// Mensaje de resumen final
		if (overallSuccess)
		{
			// USO DEL CONSOLE WRITER: Éxito estilizado
			_consoleWriter.WriteMarkup("✨ [bold green]All files passed the required metadata check.[/]");
		}
		else
		{
			// USO DEL CONSOLE WRITER: Error resaltado
			_consoleWriter.WriteError("⚠️ One or more files failed the required metadata check. Check the 'Status' column for details.");
		}

		return result;
	}

	#endregion

	#region 5. Métodos Privados (Helpers)

	/// <summary>
	/// MÉTODO BASE DE ESCRITURA: Escribe un lote de tags en las fotos.
	/// </summary>
	private IReadOnlyCollection<Photo> WriteMetadataBatch(
		IReadOnlyCollection<Photo> photos,
		List<KeyValuePair<string, string>> tagsToWrite,
		string contextName,
		bool isDryRun)
	{
		// Corregir la representación de tags para DryRun/Log (Aseguramos EscapeMarkup):
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
						// USO DEL CONSOLE WRITER: DryRun estilizado
						_consoleWriter.Write($"[yellow bold][DryRun][/] Add metadata ([dim]{contextName}[/]) to [bold]{photo.PhotoFile.FileName.EscapeMarkup()}[/]. Commands: [cyan]{formattedTags}[/]");
						continue;
					}

					exifTool.WriteTags(photo.PhotoFile.SourcePath, tagsToWrite, overwriteOriginal: true);
					_statistics.PhotosMetadataProcessed++;
					// USO DEL CONSOLE WRITER: Éxito estilizado
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
	/// Obtiene los objetos TemplateTag de un template dado (desde el YAML).
	/// </summary>
	private IReadOnlyCollection<TemplateTag> GetTemplateTags(string templateName)
	{
		if (!_templateTagMap.TryGetValue(templateName, out var tags))
			throw new ArgumentException($"Template '{templateName}' not found.", nameof(templateName));
		return tags;
	}

	/// <summary>
	/// Obtiene solo los nombres de los tags de un template (para GetMetadata).
	/// </summary>
	private IList<string> GetTagNamesFromTemplate(string templateName)
	{
		return GetTemplateTags(templateName).Select(t => t.Name).ToList();
	}

	#endregion
}
