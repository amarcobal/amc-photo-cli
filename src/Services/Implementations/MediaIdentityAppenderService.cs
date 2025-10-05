using PhotoCli.Models;
using PhotoCli.Services.Contracts;

namespace PhotoCli.Services.Implementations;

public class MediaIdentityAppenderService : IMediaIdentityAppenderService
{
	private const string ProgressName = "Appending photo media identity information";
	private readonly IConsoleWriter _consoleWriter;
	private readonly IMediaIdentityService _mediaIdentityService;
	private readonly Statistics _statistics;

	public MediaIdentityAppenderService(IMediaIdentityService mediaIdentityService, Statistics statistics, IConsoleWriter consoleWriter)
	{
		_mediaIdentityService = mediaIdentityService;
		_statistics = statistics;
		_consoleWriter = consoleWriter;
	}

	public IReadOnlyCollection<Photo> AppendMediaIdentity(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasAuthor, out bool allPhotosHasDevice)
	{
		_consoleWriter.ProgressStart(ProgressName, _statistics.PhotosFound);
		var photosAreValid = true;
		var photosHasAuthor = true;
		var photosHasDevice = true;

		foreach (var photo in photos)
		{
			if (!photo.HasExifData)
				photosAreValid = false;

			photo.SetDevice(_mediaIdentityService.GetDevice(photo));

			if (photosHasDevice && !photo.HasDevice)
				photosHasDevice = false;

			photo.SetAuthor(_mediaIdentityService.GetAuthor(photo));

			if (photosHasAuthor && !photo.HasAuthor)
				photosHasAuthor = false;
			
		}

		_consoleWriter.ProgressFinish(ProgressName);
		allPhotosAreValid = photosAreValid;
		allPhotosHasAuthor = photosHasAuthor;
		allPhotosHasDevice = photosHasDevice;
		return photos;
	}
}
