using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IExifParserService
{
	ExifData? Parse(string filePath, bool parseDateTime, bool parseCoordinate, bool parseMakeModel = false, bool parseSubseconds = false, bool parseOriginalFileName = false);
}
