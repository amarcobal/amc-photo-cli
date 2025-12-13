using System.Collections.ObjectModel;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using PhotoCli.Console.Options;
using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using Spectre.Console;
using System.Linq;
using System;
using System.Threading.Tasks;

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
		// Resumen del comando
		var commandName = $"METADATA {_options.Operation.ToString().ToUpper()}";
		var sourcePath = _options.InputPath ?? Environment.CurrentDirectory;

		if (_options.InputFiles?.Any() == true)
		{
			sourcePath = "Input File List";
		}

		_consoleWriter.WriteCommandSummary(
			commandName,
			sourcePath,
			_options.Template
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

		// 2. Añadimos EXIF Data
		photos = _exifDataAppenderService.ExtractExifData(photos, out var allPhotosAreValid, out var allPhotosHasPhotoTaken, out var allPhotosHasCoordinate, out var allPhotosHasMakeModel, out var allPhotosHasSubseconds, out var allPhotosHasOriginalFileName);

		// 3. Añadimos Media Identity
		photos = _mediaIdentityAppenderService.AppendMediaIdentity(photos, out allPhotosAreValid, out var allPhotosHasAuthor, out var allPhotosHasDevice);

		// 4. Ejecutar operación de metadata
		try
		{
			// Lógica común de vistas para 'Add' y 'Check'
			var viewTypes = _options.View.ToList();

			switch (_options.Operation)
			{
				// ----------------------------------------------------
				// photo-cli metadata add ...
				// ----------------------------------------------------
				case MetadataOperation.Add:
					if (!string.IsNullOrWhiteSpace(_options.Template))
					{
						// [CORRECCIÓN APLICADA]: 
						// No se añaden vistas por defecto. Si viewTypes está vacío, solo se mostrarán File y Status.

						_metadataService.AddMetadataFromTemplate(
							photos,
							_options.Template,
							viewTypes.AsReadOnly(),
							_options.IsDryRun,
							_options.OverwriteTags,
							_options.AllowUnknownIdentity
						);
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
					else if (!string.IsNullOrWhiteSpace(_options.Template))
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
					_consoleWriter.Write("[bold green]Metadata retrieval finished.[/] See output log for details.");
					break;

				// ----------------------------------------------------
				// photo-cli metadata check ...
				// ----------------------------------------------------
				case MetadataOperation.Check:
					IReadOnlyDictionary<string, FileValidationResult> checkResults;

					if (!string.IsNullOrWhiteSpace(_options.Template))
					{
						// La operación 'check' necesita la vista de plantilla por defecto para ser funcional.
						if (!viewTypes.Any())
						{
							viewTypes.Add(MetadataCheckViewType.Template);
						}

						// ASUMIMOS que CheckMetadataFromTemplate ahora devuelve IReadOnlyDictionary<string, FileValidationResult>
						checkResults = _metadataService.CheckMetadataFromTemplate(
							photos,
							_options.Template,
							viewTypes.AsReadOnly(),
							_options.AllowUnknownIdentity
							);
					}
					else if (!string.IsNullOrWhiteSpace(_options.Key))
					{
						// ASUMIMOS que CheckMetadata ahora devuelve IReadOnlyDictionary<string, FileValidationResult>
						checkResults = _metadataService.CheckMetadata(photos, _options.Key, isRequired: true);
					}
					else
					{
						_logger.LogError("Operation 'check' requires either '--key' or '--template'.");
						return ExitCode.InvalidMetadataOperationValue;
					}

					LogCheckSummary(checkResults);
					break;

				default:
					_logger.LogError("Unknown metadata operation.");
					return ExitCode.InvalidMetadataOperationValue;
			}
		}
		catch (ArgumentException ex)
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
	private void LogCheckSummary(IReadOnlyDictionary<string, FileValidationResult> results)
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
		var allowedUnknownWarnings = 0; // NUEVO CONTADOR PARA ARCHIVOS PERMITIDOS

		// Contadores específicos de Identidad
		var missingTakenDate = 0;
		var missingDevice = 0;
		var missingAuthor = 0;
		var missingMakeModel = 0;

		foreach (var fileResult in results.Values)
		{
			// 1. Analizar Identidad (Acceso directo a IdentityTags)
			var identityTags = fileResult.IdentityTags;
			var identityOk = identityTags.Values.All(tv => tv.IsValid);

			// 2. Analizar Template (Acceso directo a TemplateTags)
			var templateTags = fileResult.TemplateTags;
			var templateOk = templateTags.Values.All(tv => tv.IsValid);

			if (identityOk && templateOk)
			{
				passedFiles++;
			}
			else
			{
				failedFiles++;
				if (!identityOk) failedByIdentity++;
				if (!templateOk) failedByTemplate++;
			}

			// -------------------------------------------------------------------------
			// Detección de Warnings (Archivos ACEPTADOS por allow-unknown-identity)
			if (identityOk)
			{
				var isMissingRelaxedData =
					IsTagMissingOrUnknown(identityTags, "Make") ||
					IsTagMissingOrUnknown(identityTags, "Model") ||
					IsTagMissingOrUnknown(identityTags, "Author") ||
					IsTagMissingOrUnknown(identityTags, "Device");

				if (isMissingRelaxedData)
				{
					allowedUnknownWarnings++;
				}
			}
			// -------------------------------------------------------------------------

			// 3. Drill-down de errores de identidad (Solo fallos críticos)
			if (!identityOk)
			{
				// Usamos los nuevos auxiliares
				if (IsIdentityTagInvalid(identityTags, "TakenDate")) missingTakenDate++;
				if (IsIdentityTagInvalid(identityTags, "Device")) missingDevice++;
				if (IsIdentityTagInvalid(identityTags, "Author")) missingAuthor++;
				if (IsIdentityTagInvalid(identityTags, "Make") || IsIdentityTagInvalid(identityTags, "Model")) missingMakeModel++;
			}
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

		if (failedByIdentity > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Missing Identity Data Details:[/]");
			if (missingTakenDate > 0) _consoleWriter.WriteMarkup($"  - Missing Taken Date: [red]{missingTakenDate}[/]");
			if (missingMakeModel > 0) _consoleWriter.WriteMarkup($"  - Missing Make/Model: [red]{missingMakeModel}[/]");
			if (missingDevice > 0) _consoleWriter.WriteMarkup($"  - Missing Device ID:  [red]{missingDevice}[/]");
			if (missingAuthor > 0) _consoleWriter.WriteMarkup($"  - Missing Author ID:  [red]{missingAuthor}[/]");
		}

		// -------------------------------------------------------------------------
		// Escritura del resumen de Warnings
		if (allowedUnknownWarnings > 0)
		{
			_consoleWriter.WriteMarkup("\n[bold underline]Identity Warnings (Allow Unknown):[/]");
			_consoleWriter.WriteMarkup($"• [yellow]Files accepted with missing/unknown identity:[/][bold] {allowedUnknownWarnings}[/] files");
			_consoleWriter.WriteMarkup("  [dim](These files were allowed to pass Identity Check due to the allow-unknown-identity flag)[/]");
		}
		// -------------------------------------------------------------------------

		_consoleWriter.WriteMarkup("\n[yellow]Review the table above for specific file details.[/]");
	}

	// NUEVO MÉTODO AUXILIAR para comprobar la validez en la nueva estructura de identidad
	private bool IsIdentityTagInvalid(IReadOnlyDictionary<string, TagValidationResult> identityResults, string key)
	{
		if (identityResults.TryGetValue(key, out var val))
		{
			return !val.IsValid;
		}
		return false;
	}

	// NUEVO MÉTODO AUXILIAR para comprobar la ausencia de valor (Missing o Unknown)
	private bool IsTagMissingOrUnknown(IReadOnlyDictionary<string, TagValidationResult> identityResults, string key)
	{
		if (identityResults.TryGetValue(key, out var val))
		{
			// Se considera missing/unknown si HasValue es False (Missing) O si el valor literal es "Unknown".
			return !val.HasValue || val.Value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}
}
