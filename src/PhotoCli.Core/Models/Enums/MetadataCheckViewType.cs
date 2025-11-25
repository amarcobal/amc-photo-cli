namespace PhotoCli.Core.Models.Enums;

public enum MetadataCheckViewType
{
	// Muestra la columna Template solo con estado (OK/KO)
	Template = 1,

	// Muestra la columna Media Identity solo con estado (OK/KO)
	Identity = 2,

	// Muestra la columna Template expandida con la lista de tags
	TemplateDetails = 3,

	// Muestra la columna Media Identity expandida (Author, Device, Make, Model)
	IdentityDetails = 4
}
