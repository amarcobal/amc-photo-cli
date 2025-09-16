namespace PhotoCli.Models;

public record MediaIdentity
{
	public List<Author> Authors { get; set; } = new();
	public List<Device> Devices { get; set; } = new();
}

// Root para deserialización YAML
public class AuthorsRoot
{
	public List<Author> Authors { get; set; } = new();
}

public class DevicesRoot
{
	public List<Device> Devices { get; set; } = new();
}
