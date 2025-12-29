using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Services.Contracts.SpectreConsole; // Asegúrate de tener esta referencia
using System.Collections.Generic;
using System.IO;

namespace PhotoCli.Core.Services.Implementations;

public class MediaIdentityAppenderService : IMediaIdentityAppenderService
{
	private const string TaskName = "Appending photo media identity information";

	// CAMBIO 1: Reemplazar IConsoleWriter por IProgressService
	private readonly IProgressService _progressService;
	private readonly IMediaIdentityService _mediaIdentityService;
	private readonly Statistics _statistics;

	// CAMBIO 2: Actualizar el constructor para inyectar IProgressService
	public MediaIdentityAppenderService(
		IMediaIdentityService mediaIdentityService,
		Statistics statistics,
		IProgressService progressService)
	{
		_mediaIdentityService = mediaIdentityService;
		_statistics = statistics;
		_progressService = progressService; // Asignación del nuevo servicio
	}

	public IReadOnlyCollection<Photo> AppendMediaIdentity(IReadOnlyCollection<Photo> photos, out bool allPhotosAreValid, out bool allPhotosHasAuthor, out bool allPhotosHasDevice)
	{
		// CAMBIO 3: Envolver la lógica en _progressService.ExecuteProgress
		var results = _progressService.ExecuteProgress("Media Identity Appending", ctx =>
		{
			// Creamos una única tarea para todo el proceso.
			var task = ctx.AddTask($"[yellow]{TaskName}[/]", photos.Count);

			var photosAreValid = true;
			var photosHasAuthor = true;
			var photosHasDevice = true;

			foreach (var photo in photos)
			{
				// Actualizamos la descripción para mostrar el archivo que se está procesando
				task.UpdateDescription($"[yellow]{TaskName}:[/] [dim]{Path.GetFileName(photo.PhotoFile.SourceFullPath)}[/]");

				if (!photo.HasExifData)
					photosAreValid = false;

				photo.SetDevice(_mediaIdentityService.GetDevice(photo));

				if (photosHasDevice && !photo.HasDevice)
					photosHasDevice = false;

				photo.SetAuthor(_mediaIdentityService.GetAuthor(photo));

				if (photosHasAuthor && !photo.HasAuthor)
					photosHasAuthor = false;


				//Statistics (se mantiene igual)
				// 🔹 Combinaciones con taken date
				if (photo.HasOriginalDateTime && photo.HasDevice)
					++_statistics.PhotoThatHasTakenDateAndDevice;
				else if (photo.HasOriginalDateTime && !photo.HasDevice)
					++_statistics.PhotoThatHasTakenDateButNoDevice;

				if (photo.HasOriginalDateTime && photo.HasAuthor)
					++_statistics.PhotoThatHasTakenDateAndAuthor;
				else if (photo.HasOriginalDateTime && !photo.HasAuthor)
					++_statistics.PhotoThatHasTakenDateButNoAuthor;

				// 🔹 Combinaciones Device / Author
				if (photo.HasDevice && photo.HasAuthor)
					++_statistics.PhotoThatHasDeviceAndAuthor;
				else if (photo.HasDevice && !photo.HasAuthor)
					++_statistics.PhotoThatHasDeviceButNoAuthor;
				else if (!photo.HasDevice && photo.HasAuthor)
					++_statistics.PhotoThatHasAuthorButNoDevice;
				else
					++_statistics.PhotoThatNoAuthorAndNoDevice;

				// Incrementamos el progreso
				task.Increment(1);
			}

			// CAMBIO 4: Limpiar la descripción al finalizar y detener la tarea.
			task.UpdateDescription($"[green]✔ {TaskName}:[/] Completed processing {photos.Count} photo(s).");
			task.Stop();

			// Devolvemos la tupla con los resultados
			return (photos, photosAreValid, photosHasAuthor, photosHasDevice);
		});

		// Desestructuramos y retornamos los resultados
		allPhotosAreValid = results.photosAreValid;
		allPhotosHasAuthor = results.photosHasAuthor;
		allPhotosHasDevice = results.photosHasDevice;
		return results.photos;
	}
}
