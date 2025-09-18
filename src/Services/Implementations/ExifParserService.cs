using System.IO.Abstractions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using XmpCore;
using Directory = MetadataExtractor.Directory;

namespace PhotoCli.Services.Implementations;

public class ExifParserService : IExifParserService
{
	private readonly IFileSystem _fileSystem;
	private readonly ILogger<ExifParserService> _logger;
	private readonly ToolOptions _options;
	private readonly Statistics _statistics;
	private readonly int _coordinatePrecision;

	public ExifParserService(ILogger<ExifParserService> logger, IFileSystem fileSystem, ToolOptions options, Statistics statistics)
	{
		_logger = logger;
		_fileSystem = fileSystem;
		_options = options;
		_statistics = statistics;
		_coordinatePrecision = options.CoordinatePrecision;
	}

	public ExifData? Parse(string filePath, bool parseDateTime, bool parseCoordinate, bool parseMakeModel = false, bool parseSubseconds = false, bool parseOriginalFileName = false)
	{
		var fileStream = _fileSystem.FileStream.New(filePath, FileMode.Open);
		IReadOnlyList<Directory> fileDataDirectories;
		using (fileStream)
		{
			try
			{
				fileDataDirectories = ImageMetadataReader.ReadMetadata(fileStream);
			}
			catch (ImageProcessingException imageProcessingException)
			{
				_logger.LogInformation(imageProcessingException, "MetadataExtractor, invalid format: {Path}", filePath);
				++_statistics.InvalidFormatError;
				return null;
			}
			catch (Exception exception)
			{
				_logger.LogInformation(exception, "MetadataExtractor, unexpected exception: {Path}", filePath);
				++_statistics.InternalError;
				return null;
			}
		}

		DateTime? photoTaken = null;
		if (parseDateTime)
			photoTaken = ParseExifSubIfdDirectory(fileDataDirectories, filePath) ?? ParseExifIfd0Directory(fileDataDirectories, filePath);

		Coordinate? coordinate = null;
		if (parseCoordinate)
			coordinate = ParseCoordinate(fileDataDirectories, filePath);

		string? make = null;
		string? model = null;
		if (parseMakeModel)
			(make, model) = ParseMakeModel(fileDataDirectories, filePath);

		SubSeconds? subSeconds = null;
		if (parseSubseconds)
			subSeconds = ParseSubSeconds(fileDataDirectories, filePath);

		string? originalFileName = null;
		if (parseOriginalFileName)
			originalFileName = ParseOriginalFileName(fileDataDirectories, filePath);

		if (photoTaken.HasValue && coordinate != null)
			++_statistics.PhotoThatHasTakenDateAndCoordinate;
		else if (photoTaken.HasValue)
			++_statistics.PhotoThatHasTakenDateButNoCoordinate;
		else if (coordinate != null)
			++_statistics.PhotoThatHasCoordinateButNoTakenDate;
		else
			++_statistics.PhotoThatNoCoordinateAndNoTakenDate;

		return new ExifData(photoTaken, coordinate, _options.AddressSeparator, make, model, subSeconds, originalFileName);
	}

	private (string? make, string? model) ParseMakeModel(IEnumerable<Directory> directories, string filePath)
	{
		// Primero buscar en SubIfd
		var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
		var make = subIfd?.GetDescription(ExifDirectoryBase.TagMake);
		var model = subIfd?.GetDescription(ExifDirectoryBase.TagModel);

		// Si no hay en SubIfd, buscar en IFD0
		if (string.IsNullOrWhiteSpace(make) || string.IsNullOrWhiteSpace(model))
		{
			var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
			if (string.IsNullOrWhiteSpace(make))
				make = ifd0?.GetDescription(ExifDirectoryBase.TagMake);
			if (string.IsNullOrWhiteSpace(model))
				model = ifd0?.GetDescription(ExifDirectoryBase.TagModel);
		}

		if (string.IsNullOrWhiteSpace(make))
			_logger.LogWarning("No Make found for {FilePath}", filePath);
		if (string.IsNullOrWhiteSpace(model))
			_logger.LogWarning("No Model found for {FilePath}", filePath);

		return (make, model);
	}


	private SubSeconds? ParseSubSeconds(IEnumerable<Directory> directories, string filePath)
	{
		// Primero SubIfd
		var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
		var subSecondsValue = subIfd?.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal);

		// Si no hay en SubIfd, buscar en IFD0
		if (string.IsNullOrWhiteSpace(subSecondsValue))
		{
			var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
			subSecondsValue = ifd0?.GetString(ExifDirectoryBase.TagSubsecondTime);
		}

		if (!string.IsNullOrWhiteSpace(subSecondsValue))
			return new SubSeconds(subSecondsValue);

		_logger.LogDebug("No SubSeconds found for {FilePath}", filePath);
		return null;
	}

	private string? ParseOriginalFileName(IEnumerable<Directory> directories, string filePath)
	{
		string? originalFileName = null;

		// 1️⃣ Intentar leer PreservedFileName desde XMP
		var xmpDir = directories.OfType<XmpDirectory>().FirstOrDefault();
		if (xmpDir?.XmpMeta != null)
		{
			originalFileName = xmpDir.XmpMeta.GetPropertyString(XmpConstants.NsXmp, "PreservedFileName");
			if (!string.IsNullOrWhiteSpace(originalFileName))
			{
				_logger.LogDebug("Found PreservedFileName in XMP: {FileName} for {FilePath}", originalFileName, filePath);
				return originalFileName;
			}

			// 2️⃣ Si no hay PreservedFileName, buscar Title en Dublin Core
			originalFileName = xmpDir.XmpMeta.GetPropertyString(XmpConstants.NsDC, "title");
			if (!string.IsNullOrWhiteSpace(originalFileName))
			{
				_logger.LogDebug("Found Title in XMP (as fallback): {FileName} for {FilePath}", originalFileName, filePath);
				return originalFileName;
			}
		}

		return originalFileName;
	}


	private Coordinate? ParseCoordinate(IEnumerable<Directory> fileDataDirectories, string filePath)
	{
		var gpsDirectory = fileDataDirectories.OfType<GpsDirectory>().SingleOrDefault();
		var geoLocation = gpsDirectory?.GetGeoLocation();
		if (geoLocation != null)
			return new Coordinate(Math.Round(geoLocation.Latitude, _coordinatePrecision), Math.Round(geoLocation.Longitude, _coordinatePrecision));
		_logger.LogWarning("No coordinate found on `Gps` directory for {FilePath}", filePath);
		return null;
	}

	private DateTime? ParseExifSubIfdDirectory(IEnumerable<Directory> fileDataDirectories, string filePath)
	{
		_logger.LogDebug("First looking for `ExifSubIfd` directory for {FilePath}", filePath);
		var exifSubIfdDirectory = fileDataDirectories.OfType<ExifSubIfdDirectory>().SingleOrDefault();
		if (exifSubIfdDirectory == null)
		{
			_logger.LogDebug("No `ExifSubIfd` directory found on {FilePath}", filePath);
			return null;
		}

		if (exifSubIfdDirectory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var parsedDateTime))
			return parsedDateTime;
		if (exifSubIfdDirectory.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out parsedDateTime))
			return parsedDateTime;

		_logger.LogDebug("No datetime found on tags `TagDateTimeOriginal`, `TagDateTimeDigitized` in {FilePath}", filePath);
		return null;
	}

	private DateTime? ParseExifIfd0Directory(IEnumerable<Directory> fileDataDirectories, string filePath)
	{
		_logger.LogDebug("Alternatively looking for `ExifIfd0` directory for {FilePath}", filePath);
		var exifSubIfdDirectory = fileDataDirectories.OfType<ExifIfd0Directory>().SingleOrDefault();
		if (exifSubIfdDirectory == null)
		{
			_logger.LogDebug("No `ExifIfd0` directory found on {FilePath}", filePath);
			return null;
		}

		if (exifSubIfdDirectory.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var parsedDateTime))
			return parsedDateTime;

		_logger.LogDebug("No datetime found on tag `TagDateTime` in {FilePath}", filePath);
		return null;
	}
}
