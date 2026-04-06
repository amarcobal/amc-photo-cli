using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IDbService
{
	Task<int> Archive(IEnumerable<Photo> photos, bool isDryRun = false);
}
