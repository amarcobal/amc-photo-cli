using System.IO.Abstractions;
using FluentValidation;

namespace PhotoCli.Runners;

public class MetadataRunner : BaseRunner, IConsoleRunner
{
	private readonly MetadataOptions _options;
	private readonly IFileSystem _fileSystem;
	private readonly IConsoleWriter _consoleWriter;
	private readonly IMetadataService _metadataService;
	private readonly IValidator<MetadataOptions> _validator;

	public MetadataRunner(
		ILogger<MetadataRunner> logger,
		MetadataOptions options,
		IMetadataService metadataService,
		IValidator<MetadataOptions> validator,
		IFileSystem fileSystem,
		IConsoleWriter consoleWriter,
		Statistics statistics
	) : base(logger, fileSystem, statistics, consoleWriter)
	{
		_options = options;
		_metadataService = metadataService;
		_validator = validator;
		_fileSystem = fileSystem;
		_consoleWriter = consoleWriter;
	}

	public async Task<ExitCode> Execute()
	{
		// 1️⃣ Validación de opciones
		var validationResult = _validator.Validate(_options);
		if (!validationResult.IsValid)
		{
			foreach (var error in validationResult.Errors)
				_consoleWriter.Write(error.ErrorMessage);
			return ExitCode.InvalidSettingsValue;
		}

		// 2️⃣ Obtener fotos según prioridad: InputFiles > InputPath
		List<Photo> photos;
		if (_options.InputFiles?.Any() == true)
		{
			photos = _options.InputFiles
				.Select(f => new Photo(_fileSystem.FileInfo.New(f)))
				.ToList();
		}
		else
		{
			var sourceFolder = _options.InputPath ?? Environment.CurrentDirectory;

			if (!_fileSystem.Directory.Exists(sourceFolder))
			{
				_consoleWriter.Write($"Input folder does not exist: {sourceFolder}");
				return ExitCode.InputFolderNotExists;
			}

			photos = _fileService.CollectPhotosFromDirectory(sourceFolder);
		}

		// 3️⃣ Validar que hay fotos
		if (!ValidatePhotoPaths(out var exitCode, photos, _options.InputPath ?? Environment.CurrentDirectory))
			return exitCode;

		// 4️⃣ Ejecutar operación de metadata
		switch (_options.Operation)
		{
			case MetadataOperation.Add:
				_metadataService.AddMetadata(photos, _options.Key!, _options.Value!);
				break;

			case MetadataOperation.Remove:
				_metadataService.RemoveMetadata(photos, _options.Key!);
				break;

			case MetadataOperation.Get:
				var metadataResults = _metadataService.GetMetadata(photos);
				foreach (var result in metadataResults)
					_consoleWriter.Write(result);
				break;

			case MetadataOperation.Check:
				var checkResults = _metadataService.CheckMetadata(photos, _options.Key!);
				foreach (var result in checkResults)
					_consoleWriter.Write(result);
				break;

			default:
				_consoleWriter.Write("Unknown metadata operation.");
				return ExitCode.InvalidSettingsValue;
		}

		// 5️⃣ Escribir estadísticas de ejecución
		WriteStatistics();

		return ExitCode.Success;
	}
}
