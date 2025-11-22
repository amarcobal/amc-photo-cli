using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoCli.Core.Utils;

public static class StaticOptions
{
	public static readonly JsonSerializerOptions JsonSerializerOptions = new()
	{
		NumberHandling = JsonNumberHandling.AllowReadingFromString
	};
}
