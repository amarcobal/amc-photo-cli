namespace PhotoCli.Models;

public record ExifData
{
	private readonly string _reverseGeocodeSeparator;

	public ExifData(DateTime? takenDate, Coordinate? coordinate, string reverseGeocodeSeparator, string? make = null, string? model = null, SubSeconds? subSeconds = null, string? originalFileName = null)
	{
		(TakenDate, Coordinate, _reverseGeocodeSeparator, Make, Model, SubSeconds, OriginalFileName) = (takenDate, coordinate, reverseGeocodeSeparator, make, model, subSeconds, originalFileName);
	}

	public DateTime? TakenDate { get; }
	public Coordinate? Coordinate { get; }

	public string? Make { get; }
	public string? Model { get; }
	public SubSeconds? SubSeconds { get; }
	public string? OriginalFileName { get; }


	public IEnumerable<string>? ReverseGeocodes { get; set; }
	public string? ReverseGeocodeFormatted => ReverseGeocodes != null ? string.Join(_reverseGeocodeSeparator, ReverseGeocodes) : null;
}
