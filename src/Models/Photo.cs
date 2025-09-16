using System.IO.Abstractions;

namespace PhotoCli.Models;

public record Photo
{
	public Photo(IFileInfo photoFile, IFileInfo[]? companionFiles = null)
	{
		PhotoFile = new PhotoFile(photoFile);
		if (companionFiles != null)
			CompanionFiles = companionFiles.Select(companionFile => new PhotoFile(companionFile)).ToArray();
	}

	#region File

	public PhotoFile PhotoFile { get; init; }
	public IReadOnlyCollection<PhotoFile>? CompanionFiles { get; init; }

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

	#endregion

	#region Media Identity

	public Author? Author { get; private set; }
	public bool HasAuthor => Author != null;
	public Device? Device { get; private set; }
	public bool HasDevice => Device != null;

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

	/// <summary>
	/// Asigna el autor y dispositivo basado en la información de metadatos y MediaIdentityService
	/// </summary>
	public void AssignMediaIdentity(IMediaIdentityService mediaIdentityService)
	{
		if (ExifData == null)
			throw new PhotoCliException("ExifData must be set before assigning media identity");

		// Obtener make y model de los metadatos
		string? make = ExifData.Make;
		string? model = ExifData.Model;
		DateTime? takenDate = TakenDateTime;

		if (string.IsNullOrEmpty(model) && string.IsNullOrEmpty(make))
		{
			// No hay info, usar genéricos
			Device = mediaIdentityService.GetDefaultDevice();
			Author = mediaIdentityService.GetDefaultAuthor();
			return;
		}

		// Buscar dispositivo
		Device = mediaIdentityService.GetDevices()
			.FirstOrDefault(d =>
				(string.IsNullOrEmpty(d.Make) || d.Make == make) &&
				(string.IsNullOrEmpty(d.Model) || d.Model == model))
			?? mediaIdentityService.GetDefaultDevice();

		// Buscar autor por dispositivo y fecha
		if (takenDate.HasValue)
		{
			Author = mediaIdentityService.GetAuthors()
				.FirstOrDefault(a =>
					a.Devices.Any(ad =>
						ad.Device.ID == Device.ID &&
						(ad.From == null || takenDate >= ad.From) &&
						(ad.To == null || takenDate <= ad.To)))
				?? mediaIdentityService.GetDefaultAuthor();
		}
		else
		{
			Author = mediaIdentityService.GetDefaultAuthor();
		}
	}
}
