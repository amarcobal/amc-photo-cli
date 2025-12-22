using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Core.Models;

public class MetadataTemplatesRoot // <-- ¡Este es el nombre!
{
	// Mapea la sección "Templates" del YAML
	public Dictionary<string, List<TemplateTag>> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// Clase dependiente (el contenido de cada lista)
public class TemplateTag
{
	public string Name { get; set; } = string.Empty;
	public bool Required { get; set; } = false;
	// El tag aplica a un tipo específico de asset (Photo, Video, All)
	public MetadataAssetType AssetType { get; set; }
	public MetadataSource Source { get; set; }
}


// 1. Enum para definir los tipos de origen
public enum SourceType
{
	// El valor proviene de un tag o composite tag que ExifTool debe calcular o leer.
	ExifToolTag,

	// El valor es una cadena literal o un valor fijo definido en el YAML.
	Literal,

	// El valor proviene de un diccionario de "variable globales" inyectados por el Runner o el sistema.
	Variable
}

// 2. Modelo de la Fuente de Datos (para deserializar el YAML)
public class MetadataSource
{
	// El tipo de fuente (ExifToolTag, Literal, CodeConstant)
	public required SourceType Type { get; set; }

	// La clave o valor real, dependiendo del Type.
	// - Si Type = ExifToolTag: Es el nombre del Composite/Tag (e.g., "MyAuthorAlias", "FileName").
	// - Si Type = Literal: Es el valor fijo (e.g., "Mantenimiento").
	// - Si Type = CodeConstant: Es la clave del diccionario de código (e.g., "Input:ForceMake").
	public required string ValueKey { get; set; }
}
