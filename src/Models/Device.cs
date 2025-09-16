namespace PhotoCli.Models;

public record Device
{
	public string ID { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string Alias { get; init; } = string.Empty;
	public string Make { get; init; } = string.Empty;
	public string Model { get; init; } = string.Empty;
	public string? SerialNumber { get; init; }
}
