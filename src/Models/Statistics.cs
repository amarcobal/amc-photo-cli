namespace PhotoCli.Models;

public record Statistics
{
	public int PhotosFound { get; set; }
	public int PhotosCopied { get; set; }
	public int DirectoriesCreated { get; set; }

	public int PhotoThatHasTakenDateAndCoordinate { get; set; }

	public int PhotoThatHasTakenDateButNoCoordinate { get; set; }

	public int PhotoThatHasCoordinateButNoTakenDate { get; set; }
	public int PhotoThatNoCoordinateAndNoTakenDate { get; set; }

	public int HasCoordinateCount => PhotoThatHasTakenDateAndCoordinate + PhotoThatHasCoordinateButNoTakenDate;

	public List<string> FileIoErrors { get; } = new();

	public int InvalidFormatError { get; set; }
	public int InternalError { get; set; }
	public int PhotosExisted { get; set; }
	public int PhotosSame { get; set; }

	public int CompanionFilesCopied { get; set; }
	public int CompanionFilesExisted { get; set; }
}
