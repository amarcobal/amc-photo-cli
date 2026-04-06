using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IArchiveDbContextProvider
{
	ArchiveDbContext CreateOrGetInstance();
}
