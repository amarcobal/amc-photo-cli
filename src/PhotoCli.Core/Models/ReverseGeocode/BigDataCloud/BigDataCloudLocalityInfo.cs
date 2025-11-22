using System.Text.Json.Serialization;

namespace PhotoCli.Core.Models.ReverseGeocode.BigDataCloud;

public record BigDataCloudLocalityInfo
{
	[JsonPropertyName("administrative")]
	public List<BigDataCloudAdministrative>? Administrative { get; set; }

	[JsonPropertyName("informative")]
	public List<BigDataCloudInformative>? Informative { get; set; }
}
