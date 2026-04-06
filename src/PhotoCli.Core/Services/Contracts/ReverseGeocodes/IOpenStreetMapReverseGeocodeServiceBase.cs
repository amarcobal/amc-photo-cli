using PhotoCli.Core.Models;
using PhotoCli.Core.Models.ReverseGeocode;
using PhotoCli.Core.Models.ReverseGeocode.OpenStreetMap;
using System.Reflection;

namespace PhotoCli.Core.Services.Contracts.ReverseGeocodes;

public interface IOpenStreetMapReverseGeocodeServiceBase
{
	Task<IEnumerable<string>> Get(Coordinate coordinate, List<PropertyInfo> requestedAddressPropertyInfos);
	Task<OpenStreetMapResponse?> SerializeFullResponse(ReverseGeocodeRequest request);
	Task<Dictionary<string, object>> AllAvailableReverseGeocodes(Coordinate coordinate);
}
