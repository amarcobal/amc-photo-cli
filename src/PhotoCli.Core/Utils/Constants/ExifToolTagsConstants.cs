namespace PhotoCli.Core.Utils.Constants;

public static class ExifToolTagsConstants
{
	//CompositeTags
	public const string CompositeTagNamespace = "Composite";

	//Hashes
	public const string MD5 = $"{CompositeTagNamespace}:MD5";

	//Identity
	public const string MyMake = $"{CompositeTagNamespace}:MyMake";
	public const string MyModel = $"{CompositeTagNamespace}:MyModel";
	public const string MySerialNumber = $"{CompositeTagNamespace}:MySerialNumber";
	public const string MyAuthorName = $"{CompositeTagNamespace}:MyAuthorName";
	public const string MyAuthorAlias = $"{CompositeTagNamespace}:MyAuthorAlias";
	public const string MyDeviceName = $"{CompositeTagNamespace}:MyDeviceName";
	public const string MyDeviceAlias = $"{CompositeTagNamespace}:MyDeviceAlias";

	//Event
	public const string MyAlbum = $"{CompositeTagNamespace}:MyAlbum";

	//Dates
	public const string MyDecade = $"{CompositeTagNamespace}:MyDecade";
	public const string MyDate = $"{CompositeTagNamespace}:MyDate"; // Hora Naive/Local
	public const string MyTimeZoneOffset = $"{CompositeTagNamespace}:MyTimeZoneOffset"; // Offset de TZ
	public const string MyDateTimeUTC = $"{CompositeTagNamespace}:MyDateTimeUTC"; // Hora Normalizada a UTC
	public const string MyDateTimeLocalOffset = $"{CompositeTagNamespace}:MyDateTimeLocalOffset";
	public const string MyShortMonthName = $"{CompositeTagNamespace}:MyShortMonthName";
	public const string MySubseconds = $"{CompositeTagNamespace}:MySubseconds";

	// Video Dates (Custom composite tags)
	public const string TrackCreateDate = $"{CompositeTagNamespace}:MyTrackCreateDate";
	public const string TrackModifyDate = $"{CompositeTagNamespace}:MyTrackModifyDate";
	public const string MediaCreateDate = $"{CompositeTagNamespace}:MyMediaCreateDate";
	public const string MediaModifyDate = $"{CompositeTagNamespace}:MyMediaModifyDate";

	//Name Convention
	public const string MyFullFolderFileConvention = $"{CompositeTagNamespace}:MyFullFolderFileConvention";
	public const string MyFolderConvention = $"{CompositeTagNamespace}:MyFolderConvention";
	public const string MyFileNameConvention = $"{CompositeTagNamespace}:MyFileNameConvention";
	public const string DateTimeOriginalFromFileName = $"{CompositeTagNamespace}:DateTimeOriginalFromFileName";


	// Fechas
	public const string DateTimeOriginal = "ExifIFD:DateTimeOriginal";
	public const string CreateDate = "ExifIFD:CreateDate";
	public const string SubSecTimeOriginal = "ExifIFD:SubSecTimeOriginal";
	public const string SubSecTimeDigitized = "ExifIFD:SubSecTimeDigitized";
	public const string CompositeDateTimeOriginal = $"{CompositeTagNamespace}:SubSecDateTimeOriginal";
	public const string CompositeCreateDate = $"{CompositeTagNamespace}:SubSecCreateDate";
	public const string CompositeDateTimeCreated = $"{CompositeTagNamespace}:DateTimeCreated";
	public const string PanasonicTimeStamp = "Panasonic:TimeStamp";
	public const string GeolocationTimeZone = "ExifTool:GeolocationTimeZone";

	// GPS
	public const string GpsTagNamespace = "GPS";

	// GPS - Usamos Composite para obtener el decimal directo (ej. -0.891)
	// Si usaras el del grupo GPS, tendrías que lidiar con LatitudeRef por separado
	public const string GPSLatitude = $"{CompositeTagNamespace}:GPSLatitude";
	public const string GPSLongitude = $"{CompositeTagNamespace}:GPSLongitude";

	// Tags específicos del grupo GPS para el DateStamp/TimeStamp
	public const string GPSDateStamp = $"{GpsTagNamespace}:GPSDateStamp";
	public const string GPSTimeStamp = $"{GpsTagNamespace}:GPSTimeStamp";

	// Otros
	public const string PreservedFileName = "PreservedFileName";
	public const string FileName = "FileName";
}
