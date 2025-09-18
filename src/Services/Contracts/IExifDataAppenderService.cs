namespace PhotoCli.Services.Contracts;

public interface IExifDataAppenderService
{
	IReadOnlyCollection<Photo> ExtractExifData(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasPhotoTaken, out bool allPhotosHasCoordinate);
	IReadOnlyCollection<Photo> ExtractExifData(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasPhotoTaken, out bool allPhotosHasCoordinate, out bool allPhotosHasMakeModel, out bool allPhotosHasSubseconds, out bool allPhotosHasOriginalFileName);
}
