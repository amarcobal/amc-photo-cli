using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Services.Contracts.SpectreConsole; // Asegúrate de que esta referencia es correcta
using System.Collections.Generic;
using System.IO;

namespace PhotoCli.Core.Services.Implementations;

public class ExifDataAppenderService : IExifDataAppenderService
{
	private const string TaskName = "Parsing photo exif information";

	// Reemplazamos IConsoleWriter por IProgressService para el manejo de progreso interactivo
	private readonly IProgressService _progressService;
	private readonly IExifParserService _exifParserService;
	private readonly Statistics _statistics;

	public ExifDataAppenderService(IExifParserService exifParserService, Statistics statistics, IProgressService progressService)
	{
		_exifParserService = exifParserService;
		_statistics = statistics;
		_progressService = progressService; // Inyectamos el nuevo servicio
	}

	public IReadOnlyCollection<Photo> ExtractExifData(
		IReadOnlyCollection<Photo> photos,
		out bool allPhotosAreValid,
		out bool allPhotosHasPhotoTaken,
		out bool allPhotosHasCoordinate,
		bool isSilent = false)
	{
		// Si es silencioso, ejecutamos la lógica sin la barra de progreso.
		if (isSilent)
		{
			return ExtractExifDataInternal(photos, out allPhotosAreValid, out allPhotosHasPhotoTaken, out allPhotosHasCoordinate, out _, out _, out _);
		}

		// Si NO es silencioso, usamos IProgressService.
		var results = _progressService.ExecuteProgress("EXIF Data Extraction (Basic)", ctx =>
		{
			// Creamos una única tarea para todo el proceso.
			var task = ctx.AddTask($"[yellow]{TaskName}[/]", photos.Count);

			var photosAreValid = true;
			var photosHasPhotoTaken = true;
			var photosHasCoordinate = true;

			foreach (var photo in photos)
			{
				task.UpdateDescription($"[yellow]{TaskName}:[/] [dim]{Path.GetFileName(photo.PhotoFile.SourceFullPath)}[/]");

				var exifData = _exifParserService.Parse(photo.PhotoFile.SourcePath, photo.PhotoFile.Type, true, true, true, true, true);

				if (exifData == null)
					photosAreValid = false;
				if (photosHasPhotoTaken && exifData?.OriginalDateTime == null)
					photosHasPhotoTaken = false;
				if (photosHasCoordinate && exifData?.Coordinate == null)
					photosHasCoordinate = false;
				if (exifData != null)
					photo.SetExifData(exifData);

				task.Increment(1);
			}

			// Limpiar la descripción al finalizar
			task.UpdateDescription($"[green]✔ [/] [green]{TaskName}:[/] Completed parsing {photos.Count} photo(s).");
			task.Stop();

			return (photos, photosAreValid, photosHasPhotoTaken, photosHasCoordinate);
		});

		allPhotosAreValid = results.photosAreValid;
		allPhotosHasPhotoTaken = results.photosHasPhotoTaken;
		allPhotosHasCoordinate = results.photosHasCoordinate;
		return results.photos;
	}

	public IReadOnlyCollection<Photo> ExtractExifData(
		IReadOnlyCollection<Photo> photos,
		out bool allPhotosAreValid,
		out bool allPhotosHasPhotoTaken,
		out bool allPhotosHasCoordinate,
		out bool allPhotosHasMakeModel,
		out bool allPhotosHasSubseconds,
		out bool allPhotosHasOriginalFileName,
		bool isSilent = false)
	{
		// Si es silencioso, ejecutamos la lógica sin la barra de progreso.
		if (isSilent)
		{
			return ExtractExifDataInternal(photos, out allPhotosAreValid, out allPhotosHasPhotoTaken, out allPhotosHasCoordinate, out allPhotosHasMakeModel, out allPhotosHasSubseconds, out allPhotosHasOriginalFileName);
		}

		// Si NO es silencioso, usamos IProgressService.
		var results = _progressService.ExecuteProgress("EXIF Data Extraction (Extended)", ctx =>
		{
			// Creamos una única tarea para todo el proceso.
			var task = ctx.AddTask($"[yellow]{TaskName}[/]", photos.Count);

			var photosAreValid = true;
			var photosHasPhotoTaken = true;
			var photosHasCoordinate = true;
			var photosHasMakeModel = true;
			var photosHasSubSeconds = true;
			var photosHasOriginalFileName = true;

			foreach (var photo in photos)
			{
				task.UpdateDescription($"[yellow]{TaskName}:[/] [dim]{Path.GetFileName(photo.PhotoFile.SourceFullPath)}[/]");

				var exifData = _exifParserService.Parse(photo.PhotoFile.SourcePath, photo.PhotoFile.Type, true, true, true, true, true);

				if (exifData == null)
					photosAreValid = false;
				if (photosHasPhotoTaken && exifData?.OriginalDateTime == null)
					photosHasPhotoTaken = false;
				if (photosHasCoordinate && exifData?.Coordinate == null)
					photosHasCoordinate = false;
				if (photosHasMakeModel && (exifData?.Make == null || exifData?.Model == null))
					photosHasMakeModel = false;
				if (photosHasSubSeconds && exifData?.SubSeconds == null)
					photosHasSubSeconds = false;
				if (photosHasOriginalFileName && string.IsNullOrWhiteSpace(exifData?.OriginalFileName))
					photosHasOriginalFileName = false;
				if (exifData != null)
					photo.SetExifData(exifData);

				task.Increment(1);
			}

			// Limpiar la descripción al finalizar
			task.UpdateDescription($"[green]✔ [/] [green]{TaskName}:[/] Completed parsing {photos.Count} photo(s).");
			task.Stop();

			return (photos, photosAreValid, photosHasPhotoTaken, photosHasCoordinate, photosHasMakeModel, photosHasSubSeconds, photosHasOriginalFileName);
		});

		allPhotosAreValid = results.photosAreValid;
		allPhotosHasPhotoTaken = results.photosHasPhotoTaken;
		allPhotosHasCoordinate = results.photosHasCoordinate;
		allPhotosHasMakeModel = results.photosHasMakeModel;
		allPhotosHasSubseconds = results.photosHasSubSeconds;
		allPhotosHasOriginalFileName = results.photosHasOriginalFileName;
		return results.photos;
	}

	/// <summary>
	/// Lógica de extracción interna unificada para evitar duplicación.
	/// </summary>
	private IReadOnlyCollection<Photo> ExtractExifDataInternal(
		IReadOnlyCollection<Photo> photos,
		out bool photosAreValid,
		out bool photosHasPhotoTaken,
		out bool photosHasCoordinate,
		out bool photosHasMakeModel,
		out bool photosHasSubSeconds,
		out bool photosHasOriginalFileName)
	{
		photosAreValid = true;
		photosHasPhotoTaken = true;
		photosHasCoordinate = true;
		photosHasMakeModel = true;
		photosHasSubSeconds = true;
		photosHasOriginalFileName = true;

		// El segundo método de sobrecarga (el extenso) siempre llama al parser completo,
		// por lo que este método interno solo necesita verificar todas las banderas.
		foreach (var photo in photos)
		{
			var exifData = _exifParserService.Parse(photo.PhotoFile.SourcePath, photo.PhotoFile.Type, true, true, true, true, true);

			if (exifData == null)
				photosAreValid = false;
			if (photosHasPhotoTaken && exifData?.OriginalDateTime == null)
				photosHasPhotoTaken = false;
			if (photosHasCoordinate && exifData?.Coordinate == null)
				photosHasCoordinate = false;
			if (photosHasMakeModel && (exifData?.Make == null || exifData?.Model == null))
				photosHasMakeModel = false;
			if (photosHasSubSeconds && exifData?.SubSeconds == null)
				photosHasSubSeconds = false;
			if (photosHasOriginalFileName && string.IsNullOrWhiteSpace(exifData?.OriginalFileName))
				photosHasOriginalFileName = false;
			if (exifData != null)
				photo.SetExifData(exifData);
		}
		return photos;
	}
}
