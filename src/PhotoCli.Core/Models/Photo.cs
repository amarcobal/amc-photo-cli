using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Utils;
using System.IO.Abstractions;
using System.Collections.Generic;
using System.Linq;
using System;

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

	#region Exif - Photo Taken Date and TimeZone (Curada y Canónica) ⭐️ REFRACTORIZADO

	// ⭐️ 1. Fecha Canónica (Local + Offset) - Prioridad 1
	public DateTimeOffset? OriginalDateTime => ExifData?.OriginalDateTime;
	public bool HasOriginalDateTime => OriginalDateTime.HasValue;

	// ⭐️ 2. Fecha Naive/Local (Sin Offset)
	public DateTime? OriginalDateTimeLocal => ExifData?.OriginalDateTimeLocal;
	public bool HasOriginalDateTimeLocal => OriginalDateTimeLocal.HasValue;

	// ⭐️ 3. Fecha UTC (Universal) - Prioridad 2
	public DateTimeOffset? OriginalDateTimeUTC => ExifData?.OriginalDateTimeUTC;
	public bool HasOriginalDateTimeUTC => OriginalDateTimeUTC.HasValue;

	// ⭐️ 4. TimeZone Offset (Ej: "+01:00")
	public string? OriginalTimeZoneOffset => ExifData?.OriginalTimeZoneOffset;
	public bool HasOriginalTimeZoneOffset => !string.IsNullOrWhiteSpace(OriginalTimeZoneOffset);

	// ⭐️ 5. TimeZone Info (Objeto TimeZoneInfo, calculado de forma Lazy)
	public TimeZoneInfo? OriginalTimeZoneInfo => ExifData?.OriginalTimeZoneInfo;
	public bool HasOriginalTimeZoneInfo => OriginalTimeZoneInfo != null;

	// Propiedad de conveniencia: Offset como TimeSpan (derivado de OriginalDateTime)
	public TimeSpan? TimeZoneOffset => OriginalDateTime?.Offset;
	public bool HasTimeZoneOffset => TimeZoneOffset.HasValue;

	// ⭐️ Propiedad de Conveniencia para el Renombrado
	/// <summary>
	/// La hora local de la cámara (Naive, sin offset). 
	/// Es la hora preferida para operaciones de renombrado y agrupación por carpetas.
	/// Es el valor que antes se mapeaba a TakenDate.
	/// </summary>
	public DateTime? OriginalDateTimeForFileOperations => OriginalDateTimeLocal;
	public bool HasOriginalDateTimeForFileOperations => OriginalDateTimeForFileOperations.HasValue;

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
	public bool HasSubSeconds => Subseconds != null;

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
