namespace PhotoCli.Models;

public record AuthorDevice
{
	public Device Device { get; init; }

	public DateTime? From { get; init; }
	public DateTime? To { get; init; }
}
