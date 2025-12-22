using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts;
using System.Collections.Generic;
using System.Linq;

namespace PhotoCli.Core.Services.Implementations;

public class AssetTypeService : IAssetTypeService
{
	private readonly HashSet<string> _photoExtensions;
	private readonly HashSet<string> _videoExtensions;
	private readonly HashSet<string> _supportedExtensions; 

	// Constructor que recibe la configuración externa (ToolOptions)
	public AssetTypeService(ToolOptions options)
	{
		// Almacenamos las extensiones configuradas para Photo en un HashSet
		_photoExtensions = new HashSet<string>(
			options.PhotoExtensions.Select(ext => ext.TrimStart('.').ToLowerInvariant()),
			StringComparer.OrdinalIgnoreCase
		);

		// Almacenamos las extensiones configuradas para Video en un HashSet
		_videoExtensions = new HashSet<string>(
			options.VideoExtensions.Select(ext => ext.TrimStart('.').ToLowerInvariant()),
			StringComparer.OrdinalIgnoreCase
		);

		_supportedExtensions = new HashSet<string>(
			options.SupportedExtensions.Select(ext => ext.TrimStart('.').ToLowerInvariant()),
			StringComparer.OrdinalIgnoreCase
		);
	}

	/// <summary>
	/// Clasifica el archivo por su extensión como Photo, Video o Unknown.
	/// </summary>
	/// <param name="extension">La extensión del archivo (e.g., "jpg", ".mp4").</param>
	/// <returns>El tipo de activo AssetType.</returns>
	public AssetType GetAssetType(string extension)
	{
		// 1. Normalizar la extensión para la búsqueda
		string normalizedExtension = extension.TrimStart('.').ToLowerInvariant();

		// 2. Clasificar como Video
		if (_videoExtensions.Contains(normalizedExtension))
		{
			return AssetType.Video;
		}

		// 3. Clasificar como Photo
		if (_photoExtensions.Contains(normalizedExtension))
		{
			return AssetType.Photo;
		}

		// 4. Si no se encuentra en ninguna lista configurada
		return AssetType.Unknown;
	}
}
