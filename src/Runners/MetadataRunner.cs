using System.Collections.ObjectModel;
using System.IO.Abstractions;
using PhotoCli.Models; // Necesario para la clase Photo
using PhotoCli.Models.Enums;
using PhotoCli.Services.Contracts; // Necesario para IMetadataService
using Spectre.Console; // Necesario para Markup.Escape

namespace PhotoCli.Runners;

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

					if (!string.IsNullOrWhiteSpace(_options.Template))
					{
						// El MetadataService ahora se encarga de llamar a WriteValidationTable
						checkResults = _metadataService.CheckMetadataFromTemplate(photos, _options.Template);
					}
					else if (!string.IsNullOrWhiteSpace(_options.Key))
					{
						// El MetadataService ahora se encarga de llamar a WriteValidationTable
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

		_consoleWriter.Write("[dim] [/]"); // Separador sutil

		// CORRECCIÓN: Resumen final estilizado
		_consoleWriter.Write($"\n--- [bold brightwhite]Metadata Check Summary[/] (Total Files: **{totalFiles}**) ---");
		_consoleWriter.Write($"[green]✅ Files Passed Validation:[/] [bold]{filesPassed}[/]");
		_consoleWriter.Write($"[red]🛑 Files Failed Validation:[/] [bold]{filesFailed}[/]");

		if (filesFailed > 0)
		{
			// CORRECCIÓN: Instrucción estilizada
			_consoleWriter.WriteError("[yellow]Review the table above for details on which files failed and why.[/]");
		}
	}
}
