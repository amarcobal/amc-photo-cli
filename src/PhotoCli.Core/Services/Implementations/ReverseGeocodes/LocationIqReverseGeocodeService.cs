using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models;
using PhotoCli.Core.Models.ReverseGeocode;
using PhotoCli.Core.Models.ReverseGeocode.OpenStreetMap;
using PhotoCli.Core.Options;
using PhotoCli.Core.Services.Contracts.ReverseGeocodes;
using PhotoCli.Core.Utils;

namespace PhotoCli.Core.Services.Implementations.ReverseGeocodes;

public class LocationIqReverseGeocodeService : OpenStreetMapReverseGeocodeServiceBase, ILocationIqReverseGeocodeService
{
	private readonly ApiKeyStore _apiKeyStore;

	public LocationIqReverseGeocodeService(HttpClient httpClient, ILogger<LocationIqReverseGeocodeService> logger, ApiKeyStore apiKeyStore, ICoordinateCache<OpenStreetMapResponse> coordinateCache)
		: base(httpClient, logger, coordinateCache)
	{
		_apiKeyStore = apiKeyStore;
	}

	protected override string RequestUri(ReverseGeocodeRequest request)
	{
		//_ = _apiKeyStore.LocationIq ?? throw new PhotoCliException($"{nameof(CopyOptions.LocationIqApiKey)} must be exists");
		//AMC - TODO: Handle null API key properly
		return $"{base.RequestUri(request)}&key={_apiKeyStore.LocationIq}";
	}
}
