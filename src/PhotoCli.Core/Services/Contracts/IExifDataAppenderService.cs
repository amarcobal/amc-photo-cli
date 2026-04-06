using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IExifDataAppenderService
{
	// Versión con 3 out parameters
	IReadOnlyCollection<Photo> ExtractExifData(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasPhotoTaken, out bool allPhotosHasCoordinate, bool isSilent = false);

	// Versión con 6 out parameters (la que usas en MetadataService)
	IReadOnlyCollection<Photo> ExtractExifData(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasPhotoTaken, out bool allPhotosHasCoordinate, out bool allPhotosHasMakeModel, out bool allPhotosHasSubseconds, out bool allPhotosHasOriginalFileName, bool isSilent = false);
}
