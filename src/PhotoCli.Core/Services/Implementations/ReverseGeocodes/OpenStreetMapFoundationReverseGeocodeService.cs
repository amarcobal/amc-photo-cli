using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models.ReverseGeocode;
using PhotoCli.Core.Models.ReverseGeocode.OpenStreetMap;
using PhotoCli.Core.Services.Contracts.ReverseGeocodes;

namespace PhotoCli.Core.Services.Implementations.ReverseGeocodes;

public class OpenStreetMapFoundationReverseGeocodeService : OpenStreetMapReverseGeocodeServiceBase, IOpenStreetMapFoundationReverseGeocodeService
{
	public OpenStreetMapFoundationReverseGeocodeService(HttpClient httpClient, ILogger<OpenStreetMapFoundationReverseGeocodeService> logger, ICoordinateCache<OpenStreetMapResponse> coordinateCache)
		: base(httpClient, logger, coordinateCache)
	{
	}

	protected override string RequestUri(ReverseGeocodeRequest request)
	{
		return $"?format=json&lat={request.Coordinate.Latitude}&lon={request.Coordinate.Longitude}";
	}
}
