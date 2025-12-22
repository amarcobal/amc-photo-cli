using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Utils;
using System.IO.Abstractions;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
// Asumo que PhotoCli.Core.Services.Contracts.SpectreConsole contiene las interfaces IProgressService, IProgressTask, etc.
using PhotoCli.Core.Services.Contracts.SpectreConsole;

namespace PhotoCli.Core.Services.Implementations;

public class PhotoCollectorService : IPhotoCollectorService
{
	private const string TaskPhotoMainFilesName = "Searching photo main files";
	private const string TaskPhotoCompanionFilesName = "Searching photo companion files";
	private const string TaskPhotoCollectionName = "Collecting photos";

	private readonly IProgressService _progressService;
	private readonly Statistics _statistics;
	private readonly ToolOptions _toolOptions;
	private readonly ILogger<PhotoCollectorService> _logger;
	private readonly IFileSystem _fileSystem;
	private readonly IAssetTypeService _assetTypeService;

	public PhotoCollectorService(
		IFileSystem fileSystem,
		IProgressService progressService,
		Statistics statistics,
		ToolOptions toolOptions,
		ILogger<PhotoCollectorService> logger,
		IAssetTypeService assetTypeService)
	{
		_fileSystem = fileSystem;
		_progressService = progressService;
		_statistics = statistics;
		_toolOptions = toolOptions;
		_logger = logger;
		_assetTypeService = assetTypeService;
	}

	public IReadOnlyCollection<Photo> Collect(string folderPath, bool allDirectories, bool searchCompanionFiles)
	{
		var supportedExtensions = AppendDotToRawExtensionValues(_toolOptions.SupportedExtensions);

		if (allDirectories)
			_logger.LogTrace("Getting all photos on all sub directories in {FolderPath} with extensions {Extensions}", folderPath, supportedExtensions);
		else
			_logger.LogTrace("Getting photos in just {FolderPath} with extensions {Extensions}", folderPath, supportedExtensions);


		// La lógica se envuelve en ExecuteProgress para usar una única barra animada
		var readOnlyPhotos = _progressService.ExecuteProgress("Photo Collection Initialization", ctx =>
		{
			// 1. CREAR UNA ÚNICA TAREA PRINCIPAL (Indeterminate para la búsqueda)
			var mainTask = ctx.AddTask($"[yellow]{TaskPhotoMainFilesName}[/]", maxValue: 1.0);
			mainTask.UpdateDescription($"[yellow]{TaskPhotoMainFilesName}:[/] Preparing search...");

			var searchOption = allDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
			string[] filePaths;

			// --- ETAPA 1: Búsqueda de Archivos Principales ---
			try
			{
				filePaths = _fileSystem.Directory
					.EnumerateFiles(folderPath, "*.*", searchOption)
					.Where(w => supportedExtensions.Any(a => w.EndsWith(a, StringComparison.InvariantCultureIgnoreCase))).ToArray();
			}
			catch (DirectoryNotFoundException directoryNotFoundException)
			{
				const string message = "Directory not found, do not change the file system after start processing.";
				_logger.LogCritical(directoryNotFoundException, message);
				mainTask.UpdateDescription($"[red]❌ {TaskPhotoMainFilesName}:[/] Directory Not Found");
				mainTask.Stop();
				throw new PhotoCliException($"{message} -> {directoryNotFoundException.Message}");
			}
			catch (UnauthorizedAccessException unauthorizedAccessException)
			{
				const string message = "Cannot read files with the current user. Give more specific folder as input or give user a read access for the path listed in error.";
				_logger.LogCritical(unauthorizedAccessException, message);
				mainTask.UpdateDescription($"[red]❌ {TaskPhotoMainFilesName}:[/] Unauthorized Access");
				mainTask.Stop();
				throw new PhotoCliException($"{message} -> {unauthorizedAccessException.Message}");
			}

			// Actualización de estado de la búsqueda
			_statistics.PhotosFound = filePaths.Length;
			mainTask.UpdateDescription($"[green]✔ {TaskPhotoMainFilesName}:[/] [bold]{filePaths.Length}[/] photo(s) found. Starting collection...");


			var photosInternal = new List<Photo>();

			// --- ETAPA 2: Búsqueda y Emparejamiento de Compañeros ---
			if (searchCompanionFiles && _toolOptions.CompanionExtensions.Length > 0)
			{
				mainTask.UpdateDescription($"[yellow]{TaskPhotoCompanionFilesName}:[/] Collecting companion files...");

				var companionExtensions = AppendDotToRawExtensionValues(_toolOptions.CompanionExtensions);

				var companionFilePaths = _fileSystem.Directory
					.EnumerateFiles(folderPath, "*.*", searchOption)
					.Where(w => companionExtensions.Any(a => w.EndsWith(a, StringComparison.InvariantCultureIgnoreCase))).ToArray();


				// Configurar la TAREA ÚNICA para la colección (fase determinada)
				mainTask.UpdateDescription($"[yellow]{TaskPhotoCollectionName}:[/] Initializing photo collection...");

				// Reinicio del progreso (solución para actualizar el porcentaje a 0)
				mainTask.MaxValue = 0;
				mainTask.MaxValue = filePaths.Length;

				var companionFileCount = 0;
				for (int i = 0; i < filePaths.Length; i++)
				{
					var filePath = filePaths[i];
					mainTask.UpdateDescription($"[yellow]{TaskPhotoCollectionName}:[/] [dim]{Path.GetFileName(filePath)}[/]");

					var filePathWithoutExtension = PathHelper.FilePathWithoutExtension(filePath);
					var possibleCompanionFiles = companionExtensions.Select(s => filePathWithoutExtension + s).ToArray();

					var photoCompanionFiles = companionFilePaths.Where(w =>
						w.StartsWith(filePathWithoutExtension, StringComparison.InvariantCultureIgnoreCase)
						&&
						possibleCompanionFiles.Any(a => a.Equals(w, StringComparison.InvariantCultureIgnoreCase))
					).ToList();

					var photoFile = _fileSystem.FileInfo.New(filePath);
					var photoCompanionFileInfo = photoCompanionFiles.Select(photoCompanionFile => _fileSystem.FileInfo.New(photoCompanionFile)).ToList();
					var photo = new Photo(photoFile, _assetTypeService, photoCompanionFileInfo.Count > 0 ? photoCompanionFileInfo.ToArray() : null);
					companionFileCount += photoCompanionFileInfo.Count;
					photosInternal.Add(photo);

					mainTask.Increment(1);
				}

				// Finalización de la tarea con resultados
				mainTask.UpdateDescription($"[green]✔ {TaskPhotoCollectionName}:[/] [bold]{photosInternal.Count}[/] photos collected with [bold]{companionFileCount}[/] companion files.");
			}
			else
			{
				// --- ETAPA 3: Caso sin compañeros ---

				// Configurar la TAREA ÚNICA para la colección (fase determinada)
				mainTask.UpdateDescription($"[yellow]{TaskPhotoCollectionName}:[/] Initializing photo collection...");

				// Reinicio del progreso (solución para actualizar el porcentaje a 0)
				mainTask.MaxValue = 0;
				mainTask.MaxValue = filePaths.Length;

				foreach (var filePath in filePaths)
				{
					mainTask.UpdateDescription($"[yellow]{TaskPhotoCollectionName}:[/] [dim]{Path.GetFileName(filePath)}[/]");
					var photo = new Photo(_fileSystem.FileInfo.New(filePath), _assetTypeService);
					photosInternal.Add(photo);
					mainTask.Increment(1);
				}
				mainTask.UpdateDescription($"[green]✔ {TaskPhotoCollectionName}:[/] [bold]{photosInternal.Count}[/] photos collected.");
			}

			// --- ETAPA FINAL: Detener la Tarea Única ---
			mainTask.Stop();

			return photosInternal.AsReadOnly();
		});

		return readOnlyPhotos;
	}

	private static List<string> AppendDotToRawExtensionValues(IEnumerable<string> rawExtensionValues)
	{
		return rawExtensionValues.Select(s => "." + s).ToList();
	}
}
