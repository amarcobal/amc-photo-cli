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
	/// Escribe un resumen de los resultados de la operación 'check' usando la nueva tupla (IsValid).
	/// NOTA: La tabla de detalles se imprime en MetadataService.CheckMetadataFromTemplate.
	/// </summary>
	private void LogCheckSummary(IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool IsRequired, bool IsValid)>> results)
	{
		if (results == null || results.Count == 0)
		{
			_consoleWriter.Write("[dim]Metadata check completed. No files processed.[/]");
			return;
		}

		var totalFiles = results.Count;
		// Un archivo falla si CUALQUIERA de sus tags (Value) es !IsValid
		var filesFailed = results.Count(r => r.Value.Any(kv => !kv.Value.IsValid));
		var filesPassed = totalFiles - filesFailed;

		_consoleWriter.WriteMarkup("[dim] [/]"); // Separador sutil

		// CORRECCIÓN: Resumen final estilizado
		_consoleWriter.WriteMarkup("\n--- [bold white]Metadata Check Summary[/] ---");
		_consoleWriter.WriteMarkup($"[dim]Total Files:[/] [bold]{totalFiles}[/]");
		_consoleWriter.WriteMarkup($"[green]✅ Files Passed Validation:[/] [bold]{filesPassed}[/]");
		_consoleWriter.WriteMarkup($"[red]🛑 Files Failed Validation:[/] [bold]{filesFailed}[/]");

		if (filesFailed > 0)
		{
			// CORRECCIÓN: Instrucción estilizada
			_consoleWriter.WriteError("[yellow]Review the table above for details on which files failed and why.[/]");
		}
	}
}
