using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Core.Services.Contracts;

public interface IExifDataGrouperService
{
	Dictionary<string, List<Photo>> Group(IEnumerable<Photo> photos, NamingStyle namingStyle);
}
