namespace PhotoCli.Models;

public record Author
{
	public string ID { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string Alias { get; init; } = string.Empty;

	// Relación: un autor puede tener varios dispositivos con rangos de fechas
	public IReadOnlyCollection<AuthorDevice> Devices { get; init; } = Array.Empty<AuthorDevice>();
}
