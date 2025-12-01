using System.Collections.ObjectModel;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using PhotoCli.Console.Options;
using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using Spectre.Console; // Necesario para Markup.Escape
using System.Linq; // Necesario para .Any() y .ToList()
using System; // Necesario para Environment

namespace PhotoCli.Console.Runners;

public class MetadataRunner : BaseRunner, IConsoleRunner
{
	private readonly IMetadataService _metadataService;
	private readonly ICsvService _csvService;
	private readonly IPhotoCollectorService _photoCollectorService;
	private readonly IExifDataAppenderService _exifDataAppenderService;
	private readonly IMediaIdentityAppenderService _mediaIdentityAppenderService;
	private readonly IFileSystem _fileSystem;
	private readonly ILogger<MetadataRunner> _logger;
	private readonly MetadataOptions _options;
	private readonly ToolOptions _toolOptions;
	private readonly IConsoleWriter _consoleWriter;

	public MetadataRunner(ILogger<MetadataRunner> logger, MetadataOptions options, IPhotoCollectorService photoCollectorService, IExifDataAppenderService exifDataAppenderService, IMediaIdentityAppenderService mediaIdentityAppenderService,
		IFileSystem fileSystem, ICsvService csvService, IMetadataService metadataService, ToolOptions toolOptions, Statistics statistics, IConsoleWriter consoleWriter) : base(logger, fileSystem, statistics, consoleWriter)
	{
		_options = options;
		_logger = logger;
		_photoCollectorService = photoCollectorService;
		_exifDataAppenderService = exifDataAppenderService;
		_mediaIdentityAppenderService = mediaIdentityAppenderService;
		_fileSystem = fileSystem;
		_metadataService = metadataService;
		_csvService = csvService;
		_toolOptions = toolOptions;
		_consoleWriter = consoleWriter;
	}

	public async Task<ExitCode> Execute()
	{
		// *** CAMBIO: Resumen del comando (Punto 1) ***
		// Usamos el nombre de la operación y el path de entrada
		var commandName = $"METADATA {_options.Operation.ToString().ToUpper()}";
		var sourcePath = _options.InputPath ?? Environment.CurrentDirectory;

		// NOTA: Si usas InputFiles, puedes ajustar sourcePath o usar "Input Files"
		if (_options.InputFiles?.Any() == true)
		{
			sourcePath = "Input File List"; // O la ruta de la primera carpeta
		}

		_consoleWriter.WriteCommandSummary(
			commandName,
			sourcePath,
			_options.Template // Pasa el template si existe
		);

		// 1. Get Photos
		IReadOnlyCollection<Photo> photos;
		if (_options.InputFiles?.Any() == true)
		{
			photos = _options.InputFiles
				.Select(f => new Photo(_fileSystem.FileInfo.New(f)))
				.ToList();

			if (photos.Count == 0)
			{
				_logger.LogWarning("No photo found on input file(s): {Files}", string.Join(", ", _options.InputFiles));
				return ExitCode.InputFileNotExists;
			}
		}
		else
		{
			var sourceFolderPath = _options.InputPath ?? Environment.CurrentDirectory;

			if (!CheckInputFolderExists(sourceFolderPath, out var exitCodeInputFolder))
				return exitCodeInputFolder;

			var processAllSubFolders = _options.FolderProcessType != FolderProcessType.Single;
			photos = _photoCollectorService.Collect(sourceFolderPath, processAllSubFolders, true);

			if (photos.Count == 0)
			{
				_logger.LogWarning("No photo found on folder: {Folder}", sourceFolderPath);
				return ExitCode.NoPhotoFoundOnDirectory;
			}
		}

		// 2. Añadimos EXIF Data (Restaurado)
		photos = _exifDataAppenderService.ExtractExifData(photos, out var allPhotosAreValid, out var allPhotosHasPhotoTaken, out var allPhotosHasCoordinate, out var allPhotosHasMakeModel, out var allPhotosHasSubseconds, out var allPhotosHasOriginalFileName);

		// 3. Añadimos Media Identity (Restaurado)
		photos = _mediaIdentityAppenderService.AppendMediaIdentity(photos, out allPhotosAreValid, out var allPhotosHasAuthor, out var allPhotosHasDevice);

		// 4. Ejecutar operación de metadata
		try
		{
			switch (_options.Operation)
			{
				// ----------------------------------------------------
				// photo-cli metadata add ...
				// ----------------------------------------------------
				case MetadataOperation.Add:
					if (!string.IsNullOrWhiteSpace(_options.Template))
					{
						_metadataService.AddMetadataFromTemplate(photos, _options.Template, _options.IsDryRun);
					}
					else if (!string.IsNullOrWhiteSpace(_options.Key) && _options.Value != null)
					{
						_metadataService.AddMetadata(photos, _options.Key, _options.Value, _options.IsDryRun);
					}
					else
					{
						_logger.LogError("Operation 'add' requires either '--template' or both '--key' and '--value'.");
						return ExitCode.InvalidMetadataOperationValue;
					}
					break;

				// ----------------------------------------------------
				// photo-cli metadata remove ...
				// ----------------------------------------------------
				case MetadataOperation.Delete:
					if (!string.IsNullOrWhiteSpace(_options.Key))
					{
						_metadataService.DeleteMetadata(photos, _options.Key, _options.IsDryRun);
					}
					else if (!string.IsNullOrWhiteSpace(_options.Template)) // Añadimos opción para borrar por template
					{
						_metadataService.DeleteMetadataFromTemplate(photos, _options.Template, _options.IsDryRun);
					}
					else
					{
						_logger.LogError("Operation 'delete' requires either '--key' or '--template'.");
						return ExitCode.InvalidMetadataOperationValue;
					}
					break;

				// ----------------------------------------------------
				// photo-cli metadata get ...
				// ----------------------------------------------------
				case MetadataOperation.Get:
					if (!string.IsNullOrWhiteSpace(_options.Template))
					{
						_metadataService.GetMetadataFromTemplate(photos, _options.Template, showOutput: true);
					}
					else if (!string.IsNullOrWhiteSpace(_options.Key))
					{
						_metadataService.GetMetadata(photos, _options.Key, showOutput: true);
					}
					else
					{
						_logger.LogError("Operation 'get' requires either '--key' or '--template'.");
						return ExitCode.InvalidMetadataOperationValue;
					}
					// CORRECCIÓN: Usar mensaje estilizado
					_consoleWriter.Write("[bold green]Metadata retrieval finished.[/] See output log for details.");
					break;

				// ----------------------------------------------------
				// photo-cli metadata check ...
				// ----------------------------------------------------
				case MetadataOperation.Check:
					IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool IsRequired, bool IsValid)>> checkResults;

					var viewTypes = _options.View.ToList();

					if (!string.IsNullOrWhiteSpace(_options.Template))
					{
						// LÓGICA DE VALOR POR DEFECTO: Si Template se usa y NO hay opciones de vista, forzar Template (resumen).
						if (!viewTypes.Any())
						{
							viewTypes.Add(MetadataCheckViewType.Template);
						}

						checkResults = _metadataService.CheckMetadataFromTemplate(
							photos,
							_options.Template,
							viewTypes.AsReadOnly()
							);
					}
					else if (!string.IsNullOrWhiteSpace(_options.Key))
					{
						// Para una sola clave, la columna de template siempre aparece y siempre es el detalle
						// Nota: El isRequired: true se maneja internamente en CheckMetadata
						checkResults = _metadataService.CheckMetadata(photos, _options.Key, isRequired: true);
					}
					else
					{
						_logger.LogError("Operation 'check' requires either '--key' or '--template'.");
						return ExitCode.InvalidMetadataOperationValue;
					}

					// CORRECCIÓN: El LogCheckSummary ahora sólo resume los números, ya que la tabla se imprime en el servicio.
					LogCheckSummary(checkResults);
					break;

				default:
					_logger.LogError("Unknown metadata operation.");
					return ExitCode.InvalidMetadataOperationValue;
			}
		}
		catch (ArgumentException ex) // Captura "Template not found"
		{
			_logger.LogError(ex, "Operation failed: {Message}", ex.Message);
			return ExitCode.MetadataTemplateNotFound;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "An unexpected error occurred during metadata operation.");
			return ExitCode.Error;
		}

		// 5. Escribir estadísticas de ejecución
		WriteStatistics();

		return ExitCode.Success;
	}

	/// <summary>
	/// Escribe un resumen estadístico detallado de los resultados de la validación.
	/// </summary>
	private void LogCheckSummary(IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool IsRequired, bool IsValid)>> results)
	{
		if (results == null || results.Count == 0)
		{
			_consoleWriter.Write("[dim]No processed files to summarize.[/]");
			return;
		}

		var totalFiles = results.Count;

		// Contadores generales
		var passedFiles = 0;
		var failedFiles = 0;

		// Contadores de causas de fallo
		var failedByIdentity = 0;
		var failedByTemplate = 0;

		// Contadores específicos de Identidad (usando las claves [System] que inyectamos)
		var missingTakenDate = 0;
		var missingDevice = 0;
		var missingAuthor = 0;
		var missingMakeModel = 0;

		foreach (var fileResult in results.Values)
		{
			// 1. Analizar Identidad (Tags que empiezan por [System])
			var identityTags = fileResult.Where(kv => kv.Key.StartsWith("[System]")).ToList();
			var identityOk = identityTags.All(kv => kv.Value.IsValid);

			// 2. Analizar Template (Tags normales)
			var templateTags = fileResult.Where(kv => !kv.Key.StartsWith("[System]")).ToList();
			var templateOk = templateTags.All(kv => kv.Value.IsValid);

			if (identityOk && templateOk)
			{
				passedFiles++;
			}
			else
			{
				failedFiles++;
				if (!identityOk) failedByIdentity++;
				if (!templateOk) failedByTemplate++; // Nota: Un archivo puede fallar por ambos
			}

			// 3. Drill-down de errores de identidad
			// Buscamos si el tag específico es inválido
			if (IsSystemTagInvalid(fileResult, "TakenDate")) missingTakenDate++;
			if (IsSystemTagInvalid(fileResult, "Device")) missingDevice++;
			if (IsSystemTagInvalid(fileResult, "Author")) missingAuthor++;
			if (IsSystemTagInvalid(fileResult, "Make") || IsSystemTagInvalid(fileResult, "Model")) missingMakeModel++;
		}

		var rule = new Rule("[bold]COMMAND RESULT[/]");
		rule.Justification = Justify.Center;
		rule.Style = new Style(foreground: Color.Yellow);
		AnsiConsole.Write(rule);

		_consoleWriter.WriteMarkup($"[yellow]Total Files processed:[/] [bold]{totalFiles}[/]");

		if (passedFiles == totalFiles)
		{
			_consoleWriter.WriteMarkup($"[green]✅ All files passed validation.[/]");
			return;
		}

		_consoleWriter.WriteMarkup($"[green]✅ Passed:[/] {passedFiles}");
		_consoleWriter.WriteMarkup($"[red]🛑 Failed:[/] {failedFiles}");

		if (failedByIdentity > 0 || failedByTemplate > 0)
			_consoleWriter.WriteMarkup("\n[bold underline]Failure Breakdown:[/]");

		if (failedByIdentity > 0)
			_consoleWriter.WriteMarkup($"• [yellow]Required Data (Identity) Missing:[/] [bold red]{failedByIdentity}[/] files");

		if (failedByTemplate > 0)
			_consoleWriter.WriteMarkup($"• [yellow]Required Tags (Template) Missing:[/] [bold red]{failedByTemplate}[/] files");

		// Mostrar detalle de Identidad solo si hay fallos ahí
		if (failedByIdentity > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Missing Identity Data Details:[/]");
			if (missingTakenDate > 0) _consoleWriter.WriteMarkup($"  - Missing Taken Date: [red]{missingTakenDate}[/]");
			if (missingMakeModel > 0) _consoleWriter.WriteMarkup($"  - Missing Make/Model: [red]{missingMakeModel}[/]");
			if (missingDevice > 0) _consoleWriter.WriteMarkup($"  - Missing Device ID:  [red]{missingDevice}[/]");
			if (missingAuthor > 0) _consoleWriter.WriteMarkup($"  - Missing Author ID:  [red]{missingAuthor}[/]");
		}

		_consoleWriter.WriteMarkup("\n[yellow]Review the table above for specific file details.[/]");
	}

	private bool IsSystemTagInvalid(IReadOnlyDictionary<string, (bool HasValue, string Value, bool IsRequired, bool IsValid)> result, string keySuffix)
	{
		// Busca la clave completa ej: "[System] Device"
		var key = $"[System] {keySuffix}";
		if (result.TryGetValue(key, out var val))
		{
			return !val.IsValid;
		}
		return false; // Si no existe la clave por alguna razón, no contamos error aquí para no duplicar lógica
	}
}
