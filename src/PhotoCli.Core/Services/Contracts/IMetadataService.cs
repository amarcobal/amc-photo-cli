using PhotoCli.Core.Models;

namespace PhotoCli.Core.Services.Contracts;

public interface IMetadataService
{
	#region ESCRITURA (Add)
	IReadOnlyCollection<Photo> AddMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, string metadataValue, bool isDryRun = false);

	/// <summary>
	/// Añade metadatos basados en un template, resolviendo las variables directamente desde el objeto Photo.
	/// </summary>
	IReadOnlyCollection<Photo> AddMetadataFromTemplate(IReadOnlyCollection<Photo> photos, string templateName, bool isDryRun = false);
	#endregion

	#region BORRADO (Delete)

	/// <summary>
	/// Borra un único tag de metadatos de una colección de fotos.
	/// </summary>
	IReadOnlyCollection<Photo> DeleteMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, bool isDryRun = false);

	/// <summary>
	/// Borra todos los tags definidos en un template de una colección de fotos.
	/// </summary>
	IReadOnlyCollection<Photo> DeleteMetadataFromTemplate(IReadOnlyCollection<Photo> photos, string templateName, bool isDryRun = false);

	#endregion

	#region LECTURA (Get)
	IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadata(IReadOnlyCollection<Photo> photos, IEnumerable<string> metadataKeys, bool showOutput = false);
	IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, bool showOutput = false);
	IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetMetadataFromTemplate(IReadOnlyCollection<Photo> photos, string templateName, bool showOutput = false);
	#endregion

	#region VALIDACIÓN (Check)
	IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadataFromTemplate(IReadOnlyCollection<Photo> photos, string templateName);
	IReadOnlyDictionary<string, IReadOnlyDictionary<string, (bool HasValue, string Value, bool Required, bool IsValid)>> CheckMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, bool isRequired = true);
	#endregion
}
