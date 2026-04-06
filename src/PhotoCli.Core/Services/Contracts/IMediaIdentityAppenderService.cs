using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IMediaIdentityAppenderService
{
	IReadOnlyCollection<Photo> AppendMediaIdentity(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasAuthor, out bool allPhotosHasDevice);
}
