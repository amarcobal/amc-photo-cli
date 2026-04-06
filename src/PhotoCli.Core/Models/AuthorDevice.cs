namespace PhotoCli.Core.Models;

public record AuthorDevice
{
	public string ID { get; init; }
	public Device Device { get; set; }

	public DateTime? From { get; init; }
	public DateTime? To { get; init; }
}
