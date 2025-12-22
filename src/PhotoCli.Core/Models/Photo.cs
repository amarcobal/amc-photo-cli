using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Utils;
using System.IO.Abstractions;

namespace PhotoCli.Core.Models;

public record Photo
{
	public Photo(IFileInfo photoFile, IAssetTypeService assetTypeService, IFileInfo[]? companionFiles = null)
	{
		AssetType mainAssetType = assetTypeService.GetAssetType(photoFile.Extension);

		PhotoFile = new PhotoFile(photoFile, mainAssetType);
		if (companionFiles != null)
			CompanionFiles = companionFiles.Select(companionFile =>
			{
				AssetType companionAssetType = assetTypeService.GetAssetType(companionFile.Extension);
				return new PhotoFile(companionFile, companionAssetType);
			}).ToArray();
	}

	#region File

	public PhotoFile PhotoFile { get; init; }
	public IReadOnlyCollection<PhotoFile>? CompanionFiles { get; init; }

	public bool IsUnknown => PhotoFile.Type == AssetType.Unknown;
	public bool IsPhoto => PhotoFile.Type == AssetType.Photo;
	public bool IsVideo => PhotoFile.Type == AssetType.Video;

	public string? NewName { get; private set; }

	public string? TargetRelativePath { get; private set; }

	#endregion

	#region Exif - Metadata

	public ExifData? ExifData { get; private set; }
	public bool HasExifData => ExifData != null;

	#region Exif - Photo Taken Date

	public DateTime? TakenDateTime => ExifData?.TakenDate;
	public bool HasTakenDateTime => TakenDateTime.HasValue;

	#endregion

	#region Exif - Coordinate - Reverse Geocode - Address

	public Coordinate? Coordinate => ExifData?.Coordinate;
	public bool HasCoordinate => Coordinate != null;
	public List<string>? ReverseGeocodes => ExifData?.ReverseGeocodes?.ToList() ?? null;
	public bool HasReverseGeocode => ExifData?.ReverseGeocodes != null && ExifData.ReverseGeocodes.Any();
	public int ReverseGeocodeCount => ExifData?.ReverseGeocodes?.Count() ?? 0;
	public string? ReverseGeocodeFormatted => ExifData?.ReverseGeocodeFormatted;

	#endregion

	#region Exif - Subseconds

	public SubSeconds? Subseconds => ExifData?.SubSeconds;
	public bool HasSubSeconds => Subseconds!=null;

	#endregion

	#region Exif - Original File Name

	public string? OriginalFileName => ExifData?.OriginalFileName;
	public bool HasOriginalFileName => !string.IsNullOrWhiteSpace(OriginalFileName);


	#endregion

	#region Exif - Make & Model

	public string? Make => ExifData?.Make;
	public bool HasMake => !string.IsNullOrWhiteSpace(Make);

	public string? Model => ExifData?.Model;
	public bool HasModel => !string.IsNullOrWhiteSpace(Model);


	#endregion

	#endregion

	#region Media Identity

	public Author? Author { get; private set; }
	public bool HasAuthor => Author != null && !IsDefaultAuthor;
	public bool HasNullAuthor => Author == null;
	public bool IsDefaultAuthor => Author?.ID.Equals(Constants.DefaultAuthor, StringComparison.OrdinalIgnoreCase) == true;

	public Device? Device { get; private set; }
	public bool HasDevice => Device != null && !IsDefaultDevice;
	public bool HasNullDevice => Device == null;
	public bool IsDefaultDevice => Device?.ID.Equals(Constants.DefaultDevice, StringComparison.OrdinalIgnoreCase) == true;


	#endregion

	public void SetExifData(ExifData exifData)
	{
		ExifData = exifData;
	}

	public void SetNewName(string newName)
	{
		NewName = newName;
	}

	public void SetTargetRelativePath(string targetRelativePath)
	{
		TargetRelativePath = targetRelativePath;
	}

	public void SetTarget(string outputFolder)
	{
		if (TargetRelativePath == null)
			throw new PhotoCliException($"Can't {nameof(SetTarget)} before setting {nameof(TargetRelativePath)}");

		PhotoFile.SetTarget(TargetRelativePath, outputFolder, NewName);

		if (CompanionFiles != null)
		{
			foreach (var companionFile in CompanionFiles)
				companionFile.SetTarget(TargetRelativePath, outputFolder, NewName);
		}
	}

	public void SetDevice(Device device)
	{
		Device = device;
	}

	public void SetAuthor(Author author)
	{
		Author = author;
	}

	public void SetMediaIdentity(Author author, Device device)
	{
		Author = author;
		Device = device;
	}
}
