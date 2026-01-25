using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PhotoCli.Core.Utils.Extensions;

public static class ExifToolExtensions
{
	public static DateTime? GetDateTime(this IDictionary<string, string> metadata, string key)
	{
		if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return null;
		if (DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
		return DateTime.TryParse(value, out dt) ? dt : null;
	}

	public static double? GetDouble(this IDictionary<string, string> metadata, string key)
	{
		if (metadata.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)) return result;
		return null;
	}

	public static string? GetString(this IDictionary<string, string> metadata, string key)
	{
		return metadata.TryGetValue(key, out var value) ? value : null;
	}
}
