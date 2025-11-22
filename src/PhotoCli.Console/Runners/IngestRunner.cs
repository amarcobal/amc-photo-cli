using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using PhotoCli.Console.Options;
using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using Spectre.Console;

namespace PhotoCli.Console.Runners;

public class IngestRunner : BaseRunner, IConsoleRunner
{
	private readonly IngestOptions _options;
	private readonly IPhotoCollectorService _photoCollectorService;
	private readonly IExifDataAppenderService _exifDataAppenderService;
	private readonly IMediaIdentityAppenderService _mediaIdentityAppenderService;
	private readonly IMetadataService _metadataService;
	private readonly IDirectoryGrouperService _directoryGrouperService;
	private readonly IFileNamerService _fileNamerService;
	private readonly IFolderRenamerService _folderRenamer;
	private readonly IFileService _fileService;
	private readonly IExifOrganizerService _exifOrganizerService;
	private readonly IReverseGeocodeFetcherService _reverseGeocodeFetcherService;
	private readonly ICsvService _csvService;
	private readonly IFileSystem _fileSystem;
	private readonly ILogger<IngestRunner> _logger;
	private readonly IConsoleWriter _consoleWriter;
	private readonly ToolOptions _toolOptions;

	public IngestRunner(
		ILogger<IngestRunner> logger,
		IngestOptions options,
		IPhotoCollectorService photoCollectorService,
		IExifDataAppenderService exifDataAppenderService,
		IMediaIdentityAppenderService mediaIdentityAppenderService,
		IMetadataService metadataService,
		IDirectoryGrouperService directoryGrouperService,
		IFileNamerService fileNamerService,
		IFolderRenamerService folderRenamer,
		IFileService fileService,
		IExifOrganizerService exifOrganizerService,
		IReverseGeocodeFetcherService reverseGeocodeFetcherService,
		ICsvService csvService,
		IFileSystem fileSystem,
		IConsoleWriter consoleWriter,
		ToolOptions toolOptions,
		Statistics statistics) : base(logger, fileSystem, statistics, consoleWriter)
	{
		_logger = logger;
		_options = options;
		_photoCollectorService = photoCollectorService;
		_exifDataAppenderService = exifDataAppenderService;
		_mediaIdentityAppenderService = mediaIdentityAppenderService;
		_metadataService = metadataService;
		_directoryGrouperService = directoryGrouperService;
		_fileNamerService = fileNamerService;
		_folderRenamer = folderRenamer;
		_fileService = fileService;
		_exifOrganizerService = exifOrganizerService;
		_reverseGeocodeFetcherService = reverseGeocodeFetcherService;
		_csvService = csvService;
		_fileSystem = fileSystem;
		_consoleWriter = consoleWriter;
		_toolOptions = toolOptions;
	}

	public async Task<ExitCode> Execute()
	{
		// 1. Validation
		var sourceFolderPath = _options.InputPath ?? Environment.CurrentDirectory;
		if (!CheckInputFolderExists(sourceFolderPath, out var exitCodeInputFolder)) return exitCodeInputFolder;
		if (!CheckOutputPathIsUsing(_options.IsDryRun, out var exitCodeOutputPath)) return exitCodeOutputPath;

		// 2. Collect Photos
		var processAllSubFolders = _options.FolderProcessType != FolderProcessType.Single;
		var photos = _photoCollectorService.Collect(sourceFolderPath, processAllSubFolders, true);
		if (photos.Count == 0)
		{
			_logger.LogWarning("No photo found on folder: {Folder}", sourceFolderPath);
			return ExitCode.NoPhotoFoundOnDirectory;
		}

		// 3. Handle Copy Mode (Staging)
		string? tempStagingPath = null;
		if (_options.Operation == IngestOperation.Copy && !_options.IsDryRun)
		{
			tempStagingPath = Path.Combine(Path.GetTempPath(), $"PhotoCli_Ingest_{Guid.NewGuid()}");
			_fileSystem.Directory.CreateDirectory(tempStagingPath);
			_consoleWriter.Write($"[bold yellow]Creating staging copy in:[/] {tempStagingPath}");
			
			// Copy to staging
			photos = _fileService.Copy(photos, tempStagingPath, false);
			
			// Update photos to point to staging files as source
			// The Copy method returns new Photo objects with Target set, but we need them as Source for next steps.
			// Actually, Copy returns photos with TargetFullPath set.
			// We need to re-create Photo objects from these targets to treat them as new sources.
			photos = photos.Select(p => new Photo(_fileSystem.FileInfo.New(p.PhotoFile.TargetFullPath!))).ToList();
		}

		try
		{
			// 4. Metadata Processing Loop (Extract -> Check -> Inject -> Re-Extract)
			
			// 4.1 First Extraction (to check what's missing)
			photos = _exifDataAppenderService.ExtractExifData(photos, out _, out _, out _, out _, out _, out _);
			photos = _mediaIdentityAppenderService.AppendMediaIdentity(photos, out _, out _, out _);

			// 4.2 Check Metadata
			if (!string.IsNullOrWhiteSpace(_options.Template))
			{
				_consoleWriter.Write($"[bold cyan]Checking metadata against template:[/] {_options.Template}");
				var checkResults = _metadataService.CheckMetadataFromTemplate(photos, _options.Template);
				
				// 4.3 Inject Missing Metadata
				// We inject if there are any missing required keys OR if we just want to ensure everything is there.
				// The user said "The missing metadata will be added".
				// We can blindly run AddMetadataFromTemplate, it will overwrite/add based on logic.
				// But usually we only want to add if missing? 
				// MetadataService.AddMetadataFromTemplate adds tags. If they exist, ExifTool overwrites them if we tell it to.
				// Let's assume we want to enforce the template.
				
				_consoleWriter.Write($"[bold cyan]Injecting metadata from template:[/] {_options.Template}");
				_metadataService.AddMetadataFromTemplate(photos, _options.Template, _options.IsDryRun);

				// 4.4 Re-Extract Data (Critical to get new values for grouping)
				if (!_options.IsDryRun)
				{
					_consoleWriter.Write("[dim]Re-scanning metadata after injection...[/]");
					photos = _exifDataAppenderService.ExtractExifData(photos, out _, out _, out _, out _, out _, out _);
					photos = _mediaIdentityAppenderService.AppendMediaIdentity(photos, out _, out _, out _);
				}
			}

			// 5. Reverse Geocoding (Optional)
			if (_options.ReverseGeocodeProvider != ReverseGeocodeProvider.Disabled)
			{
				_reverseGeocodeFetcherService.RateLimitWarning();
				photos = await _reverseGeocodeFetcherService.Fetch(photos);
			}

			// 6. Grouping & Renaming
			var groupedPhotos = _directoryGrouperService.GroupFiles(photos, sourceFolderPath, _options.FolderProcessType, _options.GroupByFolderType,
				_options.InvalidFileFormatAction == CopyInvalidFormatAction.InSubFolder,
				_options.NoPhotoTakenDateAction == CopyNoPhotoTakenDateAction.InSubFolder,
				_options.NoCoordinateAction == CopyNoCoordinateAction.InSubFolder,
				_options.NoDeviceAction == CopyNoDeviceAction.InSubFolder,
				_options.NoAuthorAction == CopyNoAuthorAction.InSubFolder);

			_consoleWriter.ProgressStart("Organizing and Moving files", groupedPhotos.Count);

			foreach (var (targetRelativeDirectoryPath, groupPhotos) in groupedPhotos)
			{
				// Filter
				var (filteredPhotos, keptPhotos) = _exifOrganizerService.FilterAndSortByNoActionTypes(groupPhotos,
					_options.InvalidFileFormatAction, _options.NoPhotoTakenDateAction, _options.NoCoordinateAction, _options.NoDeviceAction, _options.NoAuthorAction, targetRelativeDirectoryPath);

				// Rename
				var renamedPhotos = _fileNamerService.SetFileName(filteredPhotos, _options.NamingStyle, _options.NumberNamingTextStyle);

				// Folder Append
				if (_options.FolderProcessType is FolderProcessType.SubFoldersPreserveFolderHierarchy && _options is { FolderAppendType: not null, FolderAppendLocationType: not null })
					renamedPhotos = _folderRenamer.RenameByFolderAppendType(renamedPhotos, _options.FolderAppendType.Value, _options.FolderAppendLocationType.Value, targetRelativeDirectoryPath);

				var allPhotosToMove = new List<Photo>(renamedPhotos);
				allPhotosToMove.AddRange(keptPhotos);

				// 7. Move to Final Destination
				// Note: If we are in Copy mode, we are moving from Staging to Output.
				// If we are in Move mode, we are moving from Source to Output.
				_fileService.Move(allPhotosToMove, _options.OutputPath!, _options.IsDryRun);
				
				_consoleWriter.InProgressItemComplete("Organizing and Moving files");
			}
			_consoleWriter.ProgressFinish("Organizing and Moving files");

			// 8. Report
			await _csvService.CreateCopyReport(photos, _options.OutputPath!, _options.IsDryRun);
			WriteStatistics();

			return ExitCode.Success;
		}
		finally
		{
			// Cleanup Staging
			if (tempStagingPath != null && _fileSystem.Directory.Exists(tempStagingPath))
			{
				try
				{
					_fileSystem.Directory.Delete(tempStagingPath, true);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to clean up staging directory: {Path}", tempStagingPath);
				}
			}
		}
	}

	private bool CheckOutputPathIsUsing(bool isDryRun, out ExitCode exitCode)
	{
		if (isDryRun)
		{
			exitCode = ExitCode.Unset;
			return true;
		}

		var outputDirectory = _fileSystem.DirectoryInfo.New(_options.OutputPath);
		if (!outputDirectory.Exists)
		{
			if (!HasCreatedDirectory(outputDirectory))
			{
				exitCode = ExitCode.OutputPathDontHaveCreateDirectoryPermission;
				return false;
			}
		}
		else
		{
			// For Ingest, we might be more lenient about non-empty folders if we are merging?
			// But sticking to CopyRunner logic for safety.
			var subDirectoryCount = outputDirectory.GetDirectories().Length;
			var fileCount = outputDirectory.GetFiles().Length;
			if (subDirectoryCount > 0 || fileCount > 0)
			{
				_logger.LogCritical("Output folder: {Path} is not empty.", _options.OutputPath);
				exitCode = ExitCode.OutputFolderIsNotEmpty;
				return false;
			}
		}

		exitCode = ExitCode.Unset;
		return true;
	}
}
