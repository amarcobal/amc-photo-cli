using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Core.Services.Contracts;

public interface IExifOrganizerService
{
	(IReadOnlyCollection<Photo>, IReadOnlyCollection<Photo>) FilterAndSortByNoActionTypes(IReadOnlyCollection<Photo> photos, CopyInvalidFormatAction invalidFormatAction,
		CopyNoPhotoTakenDateAction noPhotoDateTimeTakenAction, CopyNoCoordinateAction noCoordinateAction, CopyNoDeviceAction noDeviceAction, CopyNoAuthorAction noAuthorAction, string targetRelativeDirectoryPath);
}
