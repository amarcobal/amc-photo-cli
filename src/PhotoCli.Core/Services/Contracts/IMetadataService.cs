using PhotoCli.Core.Models;
using PhotoCli.Core.Models.Enums;
using System.Collections.Generic;

namespace PhotoCli.Core.Services.Contracts;

public interface IMetadataService
{
	#region ESCRITURA (Add)
	IReadOnlyCollection<Photo> AddMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, string metadataValue, bool isDryRun = false);

	/// <summary>
	/// Añade metadatos basados en un template, resolviendo las variables directamente desde el objeto Photo.
	/// Devuelve el resultado detallado de la validación y ejecución para el log de resumen.
	/// </summary>
	/// <param name="overwriteTags">Indica si los tags existentes deben ser sobrescritos.</param>
	/// <param name="allowUnknownIdentity">Permite procesar archivos aunque falten datos de identidad (Author/Device) obligatorios.</param>
	IReadOnlyDictionary<string, FileValidationResult> AddMetadataFromTemplate( // <-- ¡ESTE ES EL CAMBIO CLAVE!
		IReadOnlyCollection<Photo> photos,
		string templateName,
		bool isDryRun = false,
		bool overwriteTags = false,
		bool allowUnknownIdentity = false);
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
	/// <summary>
	/// Realiza la validación de un template contra una colección de fotos.
	/// </summary>
	/// <param name="allowUnknownIdentity">Permite que el chequeo de identidad pase si Author/Device es 'Unknown'.</param>
	IReadOnlyDictionary<string, FileValidationResult> CheckMetadataFromTemplate(
		IReadOnlyCollection<Photo> photos,
		string templateName,
		IReadOnlyCollection<MetadataCheckViewType> metadataViewTypes,
		bool allowUnknownIdentity = false);

	IReadOnlyDictionary<string, FileValidationResult> CheckMetadata(IReadOnlyCollection<Photo> photos, string metadataKey, bool isRequired = true);
	#endregion
}
