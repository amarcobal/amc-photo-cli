using System.IO.Abstractions;

namespace PhotoCli.Runners;

public class ArchiveRunner : BaseRunner, IConsoleRunner
{
	private const string TargetRelativeFolderProgressName = "Processing target folder";

	private readonly IDirectoryGrouperService _directoryGrouperService;
	private readonly IExifDataAppenderService _exifDataAppenderService;
	private readonly IFileNamerService _fileNamerService;
	private readonly IFileService _fileService;
	private readonly ILogger<ArchiveRunner> _logger;
	private readonly ArchiveOptions _options;
	private readonly IPhotoCollectorService _photoCollectorService;
	private readonly IReverseGeocodeFetcherService _reverseGeocodeFetcherService;
	private readonly IConsoleWriter _consoleWriter;
	private readonly IDuplicatePhotoRemoveService _duplicatePhotoRemoveService;
	private readonly IDbService _dbService;

	public ArchiveRunner(ILogger<ArchiveRunner> logger, ArchiveOptions options, IPhotoCollectorService photoCollectorService, IExifDataAppenderService exifDataAppenderService,
		IDirectoryGrouperService directoryGrouperService, IFileNamerService fileNamerService, IFileService fileService, IFileSystem fileSystem, Statistics statistics,
		IReverseGeocodeFetcherService reverseGeocodeFetcherService, IConsoleWriter consoleWriter,
		IDuplicatePhotoRemoveService duplicatePhotoRemoveService, IDbService dbService) : base(logger, fileSystem, statistics, consoleWriter)
	{
		_options = options;
		_logger = logger;
		_photoCollectorService = photoCollectorService;
		_exifDataAppenderService = exifDataAppenderService;
		_directoryGrouperService = directoryGrouperService;
		_fileNamerService = fileNamerService;
		_fileService = fileService;
		_reverseGeocodeFetcherService = reverseGeocodeFetcherService;
		_consoleWriter = consoleWriter;
		_duplicatePhotoRemoveService = duplicatePhotoRemoveService;
		_dbService = dbService;
	}

	public async Task<ExitCode> Execute()
	{
		var sourceFolderPath = _options.InputPath ?? Environment.CurrentDirectory;

		if (!CheckInputFolderExists(sourceFolderPath, out var exitCodeInputFolder))
			return exitCodeInputFolder;

		var photosFound = _photoCollectorService.Collect(sourceFolderPath, true, true);
		if (photosFound.Count == 0)
		{
			Console.WriteLine($"No photo found on folder: {sourceFolderPath}");
			return ExitCode.NoPhotoFoundOnDirectory;
		}

		var isInvalidFileFormatPreventProcessOptionSelected = _options.InvalidFileFormatAction == ArchiveInvalidFormatAction.PreventProcess;
		var isNoPhotoTakenDatePreventProcessOptionSelected = _options.NoPhotoTakenDateAction == ArchiveNoPhotoTakenDateAction.PreventProcess;
		var isNoCoordinatePreventProcessOptionSelected = _options.NoCoordinateAction == ArchiveNoCoordinateAction.PreventProcess;

		var photosWithExif = _exifDataAppenderService.ExtractExifData(photosFound, out var allPhotosAreValid, out var allPhotosHasPhotoTaken, out var allPhotosHasCoordinate, out var allPhotosHasMakeModel, out var allPhotosHasSubseconds, out var allPhotosHasOriginalFileName);

		if (!NoExifDataPreventActions(out var exitCodeNoExif, allPhotosAreValid, allPhotosHasPhotoTaken, allPhotosHasCoordinate, isInvalidFileFormatPreventProcessOptionSelected,
			    isNoPhotoTakenDatePreventProcessOptionSelected, isNoCoordinatePreventProcessOptionSelected, photosWithExif))
		{
			return exitCodeNoExif;
		}

		var photosHashed =  await _fileService.CalculateFileHash(photosWithExif);

		if (_options.ReverseGeocodeProvider != ReverseGeocodeProvider.Disabled)
		{
			_reverseGeocodeFetcherService.RateLimitWarning();
			photosHashed = await _reverseGeocodeFetcherService.Fetch(photosHashed);
		}

		var groupedPhotosByRelativeDirectory = _directoryGrouperService.GroupFiles(photosHashed, sourceFolderPath, FolderProcessType.FlattenAllSubFolders,
			GroupByFolderType.YearMonthDay, true, true, false);

		_consoleWriter.ProgressStart(TargetRelativeFolderProgressName, groupedPhotosByRelativeDirectory.Count);
		var isNotDryRun = !_options.IsDryRun;

		var newCopiedPhotosByRelativeDirectory = new Dictionary<string, IReadOnlyCollection<Photo>>();
		foreach (var (targetRelativeDirectoryPath, photosInRelativeDirectory) in groupedPhotosByRelativeDirectory)
		{
			_logger.LogTrace("Processing {TargetRelativeDirectory}", targetRelativeDirectoryPath);

			var uniquePhotos = _duplicatePhotoRemoveService.GroupAndFilterByPhotoHash(photosInRelativeDirectory);
			var renamedPhotos = _fileNamerService.SetArchiveFileName(uniquePhotos);
			var newCopiedFiles = _fileService.CopyIfNotExists(renamedPhotos, _options.OutputPath, _options.IsDryRun);
			newCopiedPhotosByRelativeDirectory.Add(targetRelativeDirectoryPath, newCopiedFiles);
			if (isNotDryRun)
			{
				var allFilesVerified = await _fileService.VerifyFileIntegrity(uniquePhotos);
				if (!allFilesVerified)
					return ExitCode.FileVerifyErrors;
			}
			_consoleWriter.InProgressItemComplete(TargetRelativeFolderProgressName);
			_logger.LogTrace("Processed {TargetRelativeDirectory}", targetRelativeDirectoryPath);
		}
		_consoleWriter.ProgressFinish(TargetRelativeFolderProgressName);

		if (isNotDryRun)
			_consoleWriter.Write("Verified all photo files copied successfully by comparing file hashes from original photo files.");

		WriteStatistics();
		var newCopiedAllPhotos = newCopiedPhotosByRelativeDirectory.SelectMany(s => s.Value).ToList();
		await _dbService.Archive(newCopiedAllPhotos, _options.IsDryRun);
		return ExitCode.Success;
	}
}
