namespace PhotoCli.Core.Utils;

public static class Constants
{
	public const string CsvExtensionRegex = @"^.*\.(csv|CSV)$";
	public const string PhotoExtensionRegex = @"^.*\.(jpg|JPG|jpeg|JPEG|heic|HEIC)$";
	public const string AppSettingsFileName = "appsettings.json";
	public const string VerifyFileHashFileName = "sha1.lst";
	public const string ArchiveSQLiteDatabaseFileName = "photo-cli.sqlite3";

	public const string DefaultDevice = "Unknown";
	public const string DefaultAuthor = "Unknown";

	public const string MetadataNotSetValue = "[NOT_SET]";
}
