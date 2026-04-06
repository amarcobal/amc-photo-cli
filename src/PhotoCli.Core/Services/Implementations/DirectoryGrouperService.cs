using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Utils;
using PhotoCli.Core.Utils.Extensions;
using System.IO.Abstractions;

namespace PhotoCli.Core.Services.Implementations;

public class DirectoryGrouperService : IDirectoryGrouperService
{
	private const string ProgressName = "Directory grouping";
	private readonly IFileSystem _fileSystem;
	private readonly ToolOptions _options;
	private readonly ILogger<DirectoryGrouperService> _logger;
	private readonly IConsoleWriter _consoleWriter;

	public DirectoryGrouperService(IFileSystem fileSystem, ToolOptions options, ILogger<DirectoryGrouperService> logger, IConsoleWriter consoleWriter)
	{
		_fileSystem = fileSystem;
		_options = options;
		_logger = logger;
		_consoleWriter = consoleWriter;
	}

	public Dictionary<string, IReadOnlyCollection<Photo>> GroupFiles(IReadOnlyCollection<Photo> photos, string sourceRootPath, FolderProcessType folderProcessType,
		GroupByFolderType? groupByFolderType, bool invalidFileFormatGroupedInSubFolder, bool noPhotoDateTimeTakenGroupedInSubFolder, bool noReverseGeocodeGroupedInSubFolder, bool noDeviceGroupedInSubFolder, bool noAuthorGroupedInSubFolder)
	{
		_consoleWriter.ProgressStart(ProgressName);
		var groupedPhotosByRelativeDirectoryInternal = new Dictionary<string, List<Photo>>();
		var sourceRootDirectoryPath = _fileSystem.DirectoryInfo.New(sourceRootPath).FullName;
		var sourcePathTrimmed = PathHelper.TrimFolderSeparators(sourceRootDirectoryPath);
		foreach (var photo in photos)
		{
			var fileInfo = _fileSystem.FileInfo.New(photo.PhotoFile.SourcePath);
			var directory = fileInfo.Directory!.FullName.Trim('/');
			string targetRelativeDirectoryPath;

			var exifData = photo.ExifData;
			if (exifData != null && groupByFolderType is GroupByFolderType.AddressFlat)
			{
				targetRelativeDirectoryPath = exifData.ReverseGeocodeFormatted ?? string.Empty;
			}
			else if (exifData?.ReverseGeocodes != null && groupByFolderType is GroupByFolderType.AddressHierarchy)
			{
				targetRelativeDirectoryPath = string.Join(Path.DirectorySeparatorChar, exifData.ReverseGeocodes);
			}
			else if (exifData?.OriginalDateTime != null && groupByFolderType is GroupByFolderType.Year)
			{
				targetRelativeDirectoryPath = exifData.OriginalDateTimeForFileOperations!.Value.ToString(_options.YearFormat);
			}
			else if (exifData?.OriginalDateTime != null && groupByFolderType is GroupByFolderType.YearMonth)
			{
				targetRelativeDirectoryPath = $"{exifData.OriginalDateTimeForFileOperations!.Value.ToString(_options.YearFormat)}{Path.DirectorySeparatorChar}{exifData.OriginalDateTimeForFileOperations!.Value.ToString(_options.MonthFormat)}";
			}
			else if (exifData?.OriginalDateTime != null && groupByFolderType is GroupByFolderType.YearMonthDay)
			{
				targetRelativeDirectoryPath = $"{exifData.OriginalDateTimeForFileOperations!.Value.ToString(_options.YearFormat)}{Path.DirectorySeparatorChar}{exifData.OriginalDateTimeForFileOperations!.Value.ToString(_options.MonthFormat)}{Path.DirectorySeparatorChar}{exifData.OriginalDateTimeForFileOperations!.Value.ToString(_options.DayFormat)}";
			}
			else if (exifData?.OriginalDateTime != null && groupByFolderType is GroupByFolderType.DecadeYearYearShortMonthNameYearMonthDay)
			{
				var taken = exifData.OriginalDateTimeForFileOperations!.Value;

				// Decade
				var decade = $"{taken.Year / 10 * 10}s"; // 2025 -> "2020s"

				// Year
				var year = taken.ToString(_options.YearFormat); // "2025"

				// Month
				var month = taken.ToString(_options.MonthFormat);
				var shortMonthName = $"{taken:MMM}";

				// Day
				var day = taken.ToString(_options.DayFormat);

				//Event
				//var eventOrAlbum = photo.EventName ?? string.Empty; // <-- dinámico
				var eventDate = $"{taken:yyyy-MM-MMM}";
				var eventName = $"{taken:yyyy-MM} Moments";
				var fullEvent = $"{eventDate}" + (!string.IsNullOrWhiteSpace(eventName) ? $"{Path.DirectorySeparatorChar}{eventName}" : string.Empty);

				targetRelativeDirectoryPath = $"{decade}{Path.DirectorySeparatorChar}{year}{Path.DirectorySeparatorChar}{fullEvent}";
			}

			else if (folderProcessType is FolderProcessType.Single)
			{
				if (sourcePathTrimmed != directory)
					throw new PhotoCliException($"All files should be located in source path in {nameof(FolderProcessType.Single)}");
				targetRelativeDirectoryPath = string.Empty;
			}
			else if (folderProcessType is FolderProcessType.FlattenAllSubFolders)
			{
				targetRelativeDirectoryPath = string.Empty;
			}
			else if (sourcePathTrimmed == directory)
			{
				targetRelativeDirectoryPath = string.Empty;
			}
			else
			{
				var relativeDirectoryPath = PathHelper.TrimFolderSeparators(directory.RemoveFirst(sourcePathTrimmed));
				targetRelativeDirectoryPath = relativeDirectoryPath;
			}

			if (exifData == null && invalidFileFormatGroupedInSubFolder)
			{
				targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.PhotoFormatInvalidFolderName);
			}
			else
			{
				var noPhotoTakenShouldBeInSubFolder = exifData?.OriginalDateTime == null && noPhotoDateTimeTakenGroupedInSubFolder;
				var noReverseGeocodeShouldBeInSubFolder = exifData?.ReverseGeocodes == null && noReverseGeocodeGroupedInSubFolder;
				if (noPhotoTakenShouldBeInSubFolder && noReverseGeocodeShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoAddressAndPhotoTakenDateFolderName);
				else if (noPhotoTakenShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoPhotoTakenDateFolderName);
				else if (noReverseGeocodeShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoAddressFolderName);


				var noDeviceShouldBeInSubFolder = !photo.HasDevice && noDeviceGroupedInSubFolder;
				var noAuthorShouldBeInSubFolder = !photo.HasAuthor && noAuthorGroupedInSubFolder;

				if (noDeviceShouldBeInSubFolder && noAuthorShouldBeInSubFolder && noPhotoTakenShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoAuthorAndDeviceAndPhotoTakenDateFolderName);
				else if (noDeviceShouldBeInSubFolder && noAuthorShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoAuthorAndDeviceFolderName);
				else if (noDeviceShouldBeInSubFolder && noPhotoTakenShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoDeviceAndPhotoTakenDateFolderName);
				else if (noAuthorShouldBeInSubFolder && noPhotoTakenShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoAuthorAndDeviceAndPhotoTakenDateFolderName);
				else if (noDeviceShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoDeviceFolderName);
				else if (noAuthorShouldBeInSubFolder)
					targetRelativeDirectoryPath = Path.Combine(targetRelativeDirectoryPath, _options.NoAuthorFolderName);

			}

			_logger.LogTrace("File ({FilePath}) target directory: {TargetRelativeDirectoryPath} ", photo.PhotoFile.SourcePath, targetRelativeDirectoryPath);
			photo.SetTargetRelativePath(targetRelativeDirectoryPath);
			if (groupedPhotosByRelativeDirectoryInternal.TryGetValue(targetRelativeDirectoryPath, out var groupPhotos))
				groupPhotos.Add(photo);
			else
				groupedPhotosByRelativeDirectoryInternal.Add(targetRelativeDirectoryPath, [photo]);
		}

		var groupedPhotosByRelativeDirectoryReadOnly = groupedPhotosByRelativeDirectoryInternal.ToDictionary(k => k.Key, e => (IReadOnlyCollection<Photo>)e.Value.AsReadOnly());
		_consoleWriter.ProgressFinish(ProgressName);
		return groupedPhotosByRelativeDirectoryReadOnly;
	}
}
