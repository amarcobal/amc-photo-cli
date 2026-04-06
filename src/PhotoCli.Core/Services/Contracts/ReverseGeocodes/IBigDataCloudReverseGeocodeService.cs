using PhotoCli.Core.Models;
using PhotoCli.Core.Models.ReverseGeocode;
using PhotoCli.Core.Models.ReverseGeocode.BigDataCloud;

namespace PhotoCli.Core.Services.Contracts.ReverseGeocodes;


public interface IBigDataCloudReverseGeocodeService
{
	Task<IEnumerable<string>> Get(Coordinate coordinate, string? language, IEnumerable<int> adminLevels);
	Task<BigDataCloudResponse?> SerializeFullResponse(ReverseGeocodeRequest request);
	Task<Dictionary<string, object>> AllAvailableReverseGeocodes(Coordinate coordinate, string? language);
}
