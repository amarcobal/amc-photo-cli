namespace PhotoCli.Services.Contracts;

public interface IMetadataConfigurationService
{
	// Devuelve el mapa de templates cargado, listo para ser usado por MetadataService.
	IReadOnlyDictionary<string, IReadOnlyCollection<TemplateTag>> GetTemplateMap();
}
