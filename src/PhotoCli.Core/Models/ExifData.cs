namespace PhotoCli.Core.Models;

public record ExifData
{
	private readonly string _reverseGeocodeSeparator;

	public ExifData(DateTime? takenDate, Coordinate? coordinate, string reverseGeocodeSeparator, string? make = null, string? model = null, string? serialNumber = null, SubSeconds? subSeconds = null, string? originalFileName = null, Dictionary<string, string>? metadata = null)
	{
		(TakenDate, Coordinate, _reverseGeocodeSeparator, Make, Model, SubSeconds, SerialNumber, OriginalFileName, Metadata) = (takenDate, coordinate, reverseGeocodeSeparator, make, model, subSeconds, serialNumber, originalFileName, metadata);
	}

	public DateTime? TakenDate { get; }
	public Coordinate? Coordinate { get; }

	public string? Make { get; }
	public string? Model { get; }
	public string? SerialNumber { get; }
	public SubSeconds? SubSeconds { get; }
	public string? OriginalFileName { get; }


	public IEnumerable<string>? ReverseGeocodes { get; set; }
	public string? ReverseGeocodeFormatted => ReverseGeocodes != null ? string.Join(_reverseGeocodeSeparator, ReverseGeocodes) : null;

	public Dictionary<string, string> Metadata { get; set; }
}
