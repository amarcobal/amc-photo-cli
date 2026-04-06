using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IDuplicatePhotoRemoveService
{
	IReadOnlyCollection<Photo> GroupAndFilterByPhotoHash(IReadOnlyCollection<Photo> photos);
}
