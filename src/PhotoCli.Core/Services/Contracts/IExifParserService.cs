using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Core.Services.Contracts;

public interface IExifParserService
{
	ExifData? Parse(string filePath, AssetType fileType, bool parseDateTime, bool parseCoordinate, bool parseMakeModel = false, bool parseSubseconds = false, bool parseOriginalFileName = false);
}
