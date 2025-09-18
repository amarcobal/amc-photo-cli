namespace PhotoCli.Services.Implementations;

public class ExifDataAppenderService : IExifDataAppenderService
{
	private const string ProgressName = "Parsing photo exif information";
	private readonly IConsoleWriter _consoleWriter;
	private readonly IExifParserService _exifParserService;
	private readonly Statistics _statistics;

	public ExifDataAppenderService(IExifParserService exifParserService, Statistics statistics, IConsoleWriter consoleWriter)
	{
		_exifParserService = exifParserService;
		_statistics = statistics;
		_consoleWriter = consoleWriter;
	}

	public IReadOnlyCollection<Photo> ExtractExifData(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasPhotoTaken, out bool allPhotosHasCoordinate)
	{
		_consoleWriter.ProgressStart(ProgressName, _statistics.PhotosFound);
		var photosAreValid = true;
		var photosHasPhotoTaken = true;
		var photosHasCoordinate = true;
		var photosHasMakeModel = true;
		var photosHasSubSeconds = true;
		var photosHasOriginalFileName = true;

		foreach (var photo in photos)
		{
			var exifData = _exifParserService.Parse(photo.PhotoFile.SourcePath, true, true, true, true, true);
			if (exifData == null)
				photosAreValid = false;
			if (photosHasPhotoTaken && exifData?.TakenDate == null)
				photosHasPhotoTaken = false;
			if (photosHasCoordinate && exifData?.Coordinate == null)
				photosHasCoordinate = false;
			if (exifData != null)
				photo.SetExifData(exifData);
			_consoleWriter.InProgressItemComplete(ProgressName);
		}

		_consoleWriter.ProgressFinish(ProgressName);
		allPhotosAreValid = photosAreValid;
		allPhotosHasPhotoTaken = photosHasPhotoTaken;
		allPhotosHasCoordinate = photosHasCoordinate;
		return photos;
	}

	public IReadOnlyCollection<Photo> ExtractExifData(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasPhotoTaken, out bool allPhotosHasCoordinate, out bool allPhotosHasMakeModel, out bool allPhotosHasSubseconds, out bool allPhotosHasOriginalFileName)
	{
		_consoleWriter.ProgressStart(ProgressName, _statistics.PhotosFound);
		var photosAreValid = true;
		var photosHasPhotoTaken = true;
		var photosHasCoordinate = true;
		var photosHasMakeModel = true;
		var photosHasSubSeconds = true;
		var photosHasOriginalFileName = true;

		foreach (var photo in photos)
		{
			var exifData = _exifParserService.Parse(photo.PhotoFile.SourcePath, true, true, true, true, true);
			if (exifData == null)
				photosAreValid = false;
			if (photosHasPhotoTaken && exifData?.TakenDate == null)
				photosHasPhotoTaken = false;
			if (photosHasCoordinate && exifData?.Coordinate == null)
				photosHasCoordinate = false;
			if (photosHasMakeModel && (exifData?.Make == null || exifData?.Model == null))
				photosHasMakeModel = false;
			if (photosHasSubSeconds && exifData?.SubSeconds == null)
				photosHasSubSeconds = false;
			if (photosHasOriginalFileName && string.IsNullOrWhiteSpace(exifData?.OriginalFileName))
				photosHasOriginalFileName = false;
			if (exifData != null)
				photo.SetExifData(exifData);
			_consoleWriter.InProgressItemComplete(ProgressName);
		}

		_consoleWriter.ProgressFinish(ProgressName);
		allPhotosAreValid = photosAreValid;
		allPhotosHasPhotoTaken = photosHasPhotoTaken;
		allPhotosHasCoordinate = photosHasCoordinate;
		allPhotosHasMakeModel = photosHasMakeModel;
		allPhotosHasSubseconds = photosHasSubSeconds;
		allPhotosHasOriginalFileName = photosHasOriginalFileName;
		return photos;
	}
}
