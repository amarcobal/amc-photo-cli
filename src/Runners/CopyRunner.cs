using System.IO.Abstractions;

namespace PhotoCli.Runners;

public class CopyRunner : BaseRunner, IConsoleRunner
{
	private const string TargetRelativeFolderProgressName = "Processing target folder";

	private readonly ICsvService _csvService;
	private readonly IDirectoryGrouperService _directoryGrouperService;
	private readonly IExifDataAppenderService _exifDataAppenderService;
	private readonly IExifOrganizerService _exifOrganizerService;
	private readonly IFileNamerService _fileNamerService;
	private readonly IFileService _fileService;
	private readonly IFileSystem _fileSystem;
	private readonly IFolderRenamerService _folderRenamer;
	private readonly ILogger<CopyRunner> _logger;
	private readonly CopyOptions _options;
	private readonly IPhotoCollectorService _photoCollectorService;
	private readonly IReverseGeocodeFetcherService _reverseGeocodeFetcherService;
	private readonly ToolOptions _toolOptions;
	private readonly IConsoleWriter _consoleWriter;

	public CopyRunner(ILogger<CopyRunner> logger, CopyOptions options, IPhotoCollectorService photoCollectorService, IExifDataAppenderService exifDataAppenderService,
		IDirectoryGrouperService directoryGrouperService, IFileNamerService fileNamerService, IFileService fileService, IFileSystem fileSystem, IExifOrganizerService exifOrganizerService,
		IFolderRenamerService folderRenamer, IReverseGeocodeFetcherService reverseGeocodeFetcherService, ICsvService csvService, ToolOptions toolOptions, Statistics statistics,
		IConsoleWriter consoleWriter) : base(logger, fileSystem, statistics, consoleWriter)
	{
		_options = options;
		_logger = logger;
		_photoCollectorService = photoCollectorService;
		_exifDataAppenderService = exifDataAppenderService;
		_directoryGrouperService = directoryGrouperService;
		_fileNamerService = fileNamerService;
		_fileService = fileService;
		_fileSystem = fileSystem;
		_exifOrganizerService = exifOrganizerService;
		_folderRenamer = folderRenamer;
		_reverseGeocodeFetcherService = reverseGeocodeFetcherService;
		_csvService = csvService;
		_toolOptions = toolOptions;
		_consoleWriter = consoleWriter;
	}

	public async Task<ExitCode> Execute()
	{
		var sourceFolderPath = _options.InputPath ?? Environment.CurrentDirectory;

		if (!CheckInputFolderExists(sourceFolderPath, out var exitCodeInputFolder))
			return exitCodeInputFolder;

		if (!CheckOutputPathIsUsing(_options.IsDryRun, out var exitCodeOutputPath))
			return exitCodeOutputPath;

		var processAllSubFolders = _options.FolderProcessType != FolderProcessType.Single;
		var photoPaths = _photoCollectorService.Collect(sourceFolderPath, processAllSubFolders);
		if (photoPaths.Length == 0)
		{
			Console.WriteLine($"No photo found on folder: {sourceFolderPath}");
			return ExitCode.NoPhotoFoundOnDirectory;
		}

		var isInvalidFileFormatPreventProcessOptionSelected = _options.InvalidFileFormatAction == CopyInvalidFormatAction.PreventProcess;
		var isNoPhotoTakenDatePreventProcessOptionSelected = _options.NoPhotoTakenDateAction == CopyNoPhotoTakenDateAction.PreventProcess;
		var isNoCoordinatePreventProcessOptionSelected = _options.NoCoordinateAction == CopyNoCoordinateAction.PreventProcess;

		var photoExifDataByPath = _exifDataAppenderService.ExifDataByPath(photoPaths, out var allPhotosAreValid, out var allPhotosHasPhotoTaken, out var allPhotosHasCoordinate);

		if (!NoExifDataPreventActions(out var exitCodeNoExif, allPhotosAreValid, allPhotosHasPhotoTaken, allPhotosHasCoordinate,
			    isInvalidFileFormatPreventProcessOptionSelected, isNoPhotoTakenDatePreventProcessOptionSelected, isNoCoordinatePreventProcessOptionSelected,
			    photoExifDataByPath))
		{
			return exitCodeNoExif;
		}

		if (_options.ReverseGeocodeProvider != ReverseGeocodeProvider.Disabled)
		{
			_reverseGeocodeFetcherService.RateLimitWarning();
			photoExifDataByPath = await _reverseGeocodeFetcherService.Fetch(photoExifDataByPath);
		}

		var invalidFileFormatGroupedInSubFolder = _options.InvalidFileFormatAction == CopyInvalidFormatAction.InSubFolder;
		var noPhotoDateTimeTakenGroupedInSubFolder = _options.NoPhotoTakenDateAction == CopyNoPhotoTakenDateAction.InSubFolder;
		var noReverseGeocodeGroupedInSubFolder = _options.NoCoordinateAction == CopyNoCoordinateAction.InSubFolder;

		var groupedPhotosByRelativeDirectory = _directoryGrouperService.GroupFiles(photoExifDataByPath, sourceFolderPath, _options.FolderProcessType, _options.GroupByFolderType,
			invalidFileFormatGroupedInSubFolder, noPhotoDateTimeTakenGroupedInSubFolder, noReverseGeocodeGroupedInSubFolder);

		var filteredPhotosByRelativeDirectory = new Dictionary<string, IReadOnlyCollection<Photo>>();

		_consoleWriter.ProgressStart(TargetRelativeFolderProgressName, groupedPhotosByRelativeDirectory.Count);
		var verifyFileIntegrity = _options is { Verify: true, IsDryRun: false };
		foreach (var (targetRelativeDirectoryPath, photoInfos) in groupedPhotosByRelativeDirectory)
		{
			_logger.LogTrace("Processing {TargetRelativeDirectory}", targetRelativeDirectoryPath);

			var (filteredAndOrderedPhotos, keptPhotosNotInFilter) = _exifOrganizerService.FilterAndSortByNoActionTypes(photoInfos,
				_options.InvalidFileFormatAction, _options.NoPhotoTakenDateAction, _options.NoCoordinateAction, targetRelativeDirectoryPath);

			_fileNamerService.SetFileName(filteredAndOrderedPhotos, _options.NamingStyle, _options.NumberNamingTextStyle);

			if (_options.FolderProcessType is FolderProcessType.SubFoldersPreserveFolderHierarchy && _options.FolderAppendType.HasValue && _options.FolderAppendLocationType.HasValue)
				_folderRenamer.RenameByFolderAppendType(filteredAndOrderedPhotos, _options.FolderAppendType.Value, _options.FolderAppendLocationType.Value, targetRelativeDirectoryPath);

			var allPhotos = new List<Photo>(filteredAndOrderedPhotos);
			allPhotos.AddRange(keptPhotosNotInFilter);
			_fileService.Copy(allPhotos, _options.OutputPath, _options.IsDryRun);

			filteredPhotosByRelativeDirectory.Add(targetRelativeDirectoryPath, allPhotos);
			if (verifyFileIntegrity)
			{
				var allFilesVerified = await _fileService.VerifyFileIntegrity(allPhotos, _options.OutputPath);
				if (!allFilesVerified)
					return ExitCode.FileVerifyErrors;
			}
			_consoleWriter.InProgressItemComplete(TargetRelativeFolderProgressName);
			_logger.LogTrace("Processed {TargetRelativeDirectory}", targetRelativeDirectoryPath);
		}
		_consoleWriter.ProgressFinish(TargetRelativeFolderProgressName);
		var filteredAllPhotos = filteredPhotosByRelativeDirectory.SelectMany(s => s.Value).ToList();

		if (verifyFileIntegrity)
		{
			await _fileService.SaveGnuHashFileTree(filteredAllPhotos, _options.OutputPath);
			_consoleWriter.Write("Verified all photo files copied successfully by comparing file hashes from original photo files.");
			_consoleWriter.Write($"All files SHA1 hashes written into file: {Constants.VerifyFileHashFileName}. You may verify yourself with `sha1sum --check {Constants.VerifyFileHashFileName}` tool in Linux/macOS.");
		}

		await _csvService.Report(filteredAllPhotos, _options.OutputPath, _options.IsDryRun);
		WriteStatistics();
		return ExitCode.Success;
	}

	private bool CheckOutputPathIsUsing(bool isDryRun, out ExitCode exitCode)
	{
		if (isDryRun)
		{
			var outputFile = _fileSystem.FileInfo.New(_toolOptions.DryRunCsvReportFileName);
			if (outputFile.Exists)
			{
				_logger.LogCritical("Output file: {Path} is exists", _toolOptions.DryRunCsvReportFileName);
				exitCode = ExitCode.OutputPathIsExists;
				return false;
			}

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
			var subDirectoryCount = outputDirectory.GetDirectories().Length;
			var fileCount = outputDirectory.GetFiles().Length;
			if (subDirectoryCount > 0 || fileCount > 0)
			{
				_logger.LogCritical("Output folder: {Path} is not empty. It has {SubDirectoryCount} directory, {FileCount} files in it", _options.OutputPath, subDirectoryCount, fileCount);
				exitCode = ExitCode.OutputFolderIsNotEmpty;
				return false;
			}
		}

		exitCode = ExitCode.Unset;
		return true;
	}
}
