using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Core.Services.Contracts;

public interface IFolderRenamerService
{
	IReadOnlyCollection<Photo> RenameByFolderAppendType(IReadOnlyCollection<Photo> orderedPhotos, FolderAppendType folderAppendType, FolderAppendLocationType folderAppendLocationType,
		string targetRelativeDirectoryPath);
}
