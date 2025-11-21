using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using PhotoCli.Models; // Asume que TemplateTag y MetadataTemplateConfigurationRoot están aquí
using PhotoCli.Services.Contracts;

namespace PhotoCli.Services.Implementations;

public class MetadataConfigurationService : IMetadataConfigurationService
{
	private readonly IReadOnlyDictionary<string, IReadOnlyCollection<TemplateTag>> _templateTagMap;
	private readonly ILogger<MetadataConfigurationService> _logger;
	private readonly ToolOptions _options;

	public MetadataConfigurationService(ToolOptions options, ILogger<MetadataConfigurationService> logger)
	{
		_options = options;
		_logger = logger;
		_templateTagMap = LoadTemplates();
	}

	public IReadOnlyDictionary<string, IReadOnlyCollection<TemplateTag>> GetTemplateMap() => _templateTagMap;

	private IReadOnlyDictionary<string, IReadOnlyCollection<TemplateTag>> LoadTemplates()
	{
		var deserializer = new DeserializerBuilder()
			.IgnoreUnmatchedProperties()
			.WithNamingConvention(NullNamingConvention.Instance)
			.Build();

		string currentDir = Path.GetDirectoryName(
			new Uri(System.Reflection.Assembly.GetExecutingAssembly().Location).LocalPath)!;

		// Asumimos que ToolOptions tiene una propiedad MetadataTemplatesYaml
		string templatesPath = ResolvePath(_options.MetadataTemplatesYaml, currentDir);

		try
		{
			if (!File.Exists(templatesPath))
			{
				_logger.LogWarning("Metadata templates file not found at {Path}. Using empty configuration.", templatesPath);
				return new Dictionary<string, IReadOnlyCollection<TemplateTag>>(StringComparer.OrdinalIgnoreCase);
			}

			var templatesYaml = File.ReadAllText(templatesPath, Encoding.UTF8);
			var root = deserializer.Deserialize<MetadataTemplatesRoot>(templatesYaml);

			if (root?.Templates == null)
			{
				_logger.LogWarning("Metadata templates file {Path} is empty or invalid.", templatesPath);
				return new Dictionary<string, IReadOnlyCollection<TemplateTag>>(StringComparer.OrdinalIgnoreCase);
			}

			// Convertir la estructura deserializada a IReadOnlyDictionary<string, IReadOnlyCollection<TemplateTag>>
			return root.Templates.ToDictionary(
				kv => kv.Key,
				kv => (IReadOnlyCollection<TemplateTag>)kv.Value.AsReadOnly(),
				StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "FATAL: Error loading and deserializing metadata templates from {Path}.", templatesPath);
			// Devolver un diccionario vacío o re-lanzar, dependiendo de tu política de manejo de errores
			return new Dictionary<string, IReadOnlyCollection<TemplateTag>>(StringComparer.OrdinalIgnoreCase);
		}
	}

	private static string ResolvePath(string path, string baseDir)
	{
		if (Path.IsPathRooted(path))
			return path;

		return Path.GetFullPath(Path.Combine(baseDir, path));
	}
}
