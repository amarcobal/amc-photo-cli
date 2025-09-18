namespace PhotoCli.Services.Implementations;

public class ExifDataGrouperService : IExifDataGrouperService
{
	private readonly ToolOptions _options;
	private readonly ILogger<ExifDataGrouperService> _logger;

	public ExifDataGrouperService(ToolOptions options, ILogger<ExifDataGrouperService> logger)
	{
		_options = options;
		_logger = logger;
	}

	public Dictionary<string, List<Photo>> Group(IEnumerable<Photo> photos, NamingStyle namingStyle)
	{
		if (namingStyle is NamingStyle.Numeric)
			throw new PhotoCliException($"{nameof(NamingStyle)} can't be {namingStyle}");
		Dictionary<string, List<Photo>> photosGrouped;

		switch (namingStyle)
		{
			case NamingStyle.Day:
				photosGrouped = Filter(photos, true, false).GroupBy(g => g.TakenDateTime!.Value.Date.ToString(_options.DateFormatWithDay))
					.ToDictionary(k => k.Key, grouping => grouping.ToList());
				break;
			case NamingStyle.DateTimeWithMinutes:
				photosGrouped = Filter(photos, true, false).GroupBy(g => new
				{
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day, k.Key.Hour, k.Key.Minute, 0);
					return dateTime.ToString(_options.DateTimeFormatWithMinutes);
				}, grouping => grouping.ToList());
				break;
			case NamingStyle.DateTimeWithSeconds:
				photosGrouped = Filter(photos, true, false).GroupBy(g => new
				{
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute,
					g.TakenDateTime!.Value.Second,
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day, k.Key.Hour, k.Key.Minute, k.Key.Second);
					return dateTime.ToString(_options.DateTimeFormatWithSeconds);
				}, grouping => grouping.ToList());
				break;
			case NamingStyle.Address:
				photosGrouped = Filter(photos, false, true).GroupBy(g => g.ReverseGeocodeFormatted)
					.ToDictionary(k => k.Key!, grouping => grouping.ToList());
				break;
			case NamingStyle.DayAddress or NamingStyle.AddressDay:
				photosGrouped = Filter(photos, true, true).GroupBy(g => new
				{
					ReverseGeocode = g.ReverseGeocodeFormatted,
					g.TakenDateTime!.Value.Date
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day);
					var dateTimeFormat = dateTime.ToString(_options.DateFormatWithDay);
					return FormatOrderAddressAndDateTime(namingStyle is NamingStyle.DayAddress, k.Key.ReverseGeocode!, dateTimeFormat);
				}, grouping => grouping.ToList());
				break;
			case NamingStyle.DateTimeWithMinutesAddress or NamingStyle.AddressDateTimeWithMinutes:
				photosGrouped = Filter(photos, true, true).GroupBy(g => new
				{
					ReverseGeocode = g.ReverseGeocodeFormatted,
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute,
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day, k.Key.Hour, k.Key.Minute, 0);
					var dateTimeFormat = dateTime.ToString(_options.DateTimeFormatWithMinutes);
					return FormatOrderAddressAndDateTime(namingStyle is NamingStyle.DateTimeWithMinutesAddress, k.Key.ReverseGeocode!, dateTimeFormat);
				}, grouping => grouping.ToList());
				break;
			case NamingStyle.DateTimeWithSecondsAddress or NamingStyle.AddressDateTimeWithSeconds:
				photosGrouped = Filter(photos, true, true).GroupBy(g => new
				{
					ReverseGeocode = g.ReverseGeocodeFormatted,
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute,
					g.TakenDateTime!.Value.Second,
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day, k.Key.Hour, k.Key.Minute, k.Key.Second);
					var dateTimeFormat = dateTime.ToString(_options.DateTimeFormatWithSeconds);
					return FormatOrderAddressAndDateTime(namingStyle is NamingStyle.DateTimeWithSecondsAddress, k.Key.ReverseGeocode!, dateTimeFormat);
				}, grouping => grouping.ToList());
				break;
			case NamingStyle.DateTimeWithSubseconds:
				photosGrouped = Filter(photos, true, false).GroupBy(g => new
				{
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute,
					g.TakenDateTime!.Value.Second,
					SubSeconds = g.ExifData?.SubSeconds?.Value
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day,
						k.Key.Hour, k.Key.Minute, k.Key.Second);

					return FormatDateTimeWithSubseconds(dateTime, k.Key.SubSeconds);
				}, grouping => grouping.ToList());
				break;

			case NamingStyle.DateTimeWithSubsecondsAuthorDevice:
				photosGrouped = Filter(photos, true, false, filterAuthor: true, filterDevice: true).GroupBy(g => new
				{
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute,
					g.TakenDateTime!.Value.Second,
					SubSeconds = g.ExifData?.SubSeconds?.Value,
					Author = g.Author!.Name,
					Device = g.Device!.Model
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day,
						k.Key.Hour, k.Key.Minute, k.Key.Second);

					return FormatDateTimeWithSubsecondsAuthorDevice(dateTime, k.Key.SubSeconds, k.Key.Author, k.Key.Device);
				}, grouping => grouping.ToList());
				break;

			case NamingStyle.DateTimeWithSubsecondsAuthorDeviceOriginalName:
				photosGrouped = Filter(photos, true, false, filterAuthor: true, filterDevice: true, filterSubseconds: true).GroupBy(g => new
				{
					g.TakenDateTime!.Value.Date,
					g.TakenDateTime!.Value.Hour,
					g.TakenDateTime!.Value.Minute,
					g.TakenDateTime!.Value.Second,
					SubSeconds = g.ExifData?.SubSeconds?.Value,
					Author = g.Author!.Alias,
					Device = g.Device!.Alias,
					OriginalFileName = g.OriginalFileName,
				}).ToDictionary(k =>
				{
					var dateTime = new DateTime(k.Key.Date.Year, k.Key.Date.Month, k.Key.Date.Day,
						k.Key.Hour, k.Key.Minute, k.Key.Second);

					return FormatDateTimeWithSubsecondsAuthorDeviceOriginalName(dateTime, k.Key.SubSeconds, k.Key.Author, k.Key.Device, k.Key.OriginalFileName);
				}, grouping => grouping.ToList());
				break;


			default:
				throw new PhotoCliException($"Not implemented {nameof(NamingStyle)}: {namingStyle}");
		}

		_logger.LogInformation("Grouped photo exif data into {ExifGroupCount}", photosGrouped.Count);
		return photosGrouped;
	}

	private string FormatOrderAddressAndDateTime(bool isDateBeforeAddress, string address, string dateTimeFormat)
	{
		return isDateBeforeAddress ? $"{dateTimeFormat}-{address}" : $"{address}-{dateTimeFormat}";
	}

	private string FormatDateTimeWithSubseconds(DateTime dateTime, int? subSeconds)
	{
		var sub = subSeconds.HasValue
			? subSeconds.Value.ToString("D3")
			: "000";

		return $"{dateTime:yyyyMMdd_HHmmss}_{sub}";
	}

	private string FormatDateTimeWithSubsecondsAuthorDevice(DateTime dateTime, int? subSeconds, string author, string device)
	{
		var baseName = FormatDateTimeWithSubseconds(dateTime, subSeconds);
		return $"{baseName}-{author}-{device}";
	}

	private string FormatDateTimeWithSubsecondsAuthorDeviceOriginalName(DateTime dateTime, int? subSeconds, string author, string device, string originalName)
	{
		var baseName = FormatDateTimeWithSubsecondsAuthorDevice(dateTime, subSeconds, author, device);
		return $"{baseName}-{originalName}";
	}


	private static IEnumerable<Photo> Filter(IEnumerable<Photo> photos, bool filterPhotoTakenDate, bool filterReverseGeocode, bool filterAuthor = false, bool filterDevice = false, bool filterSubseconds = false)
	{
		//if (filterPhotoTakenDate && filterReverseGeocode)
		//	return photos.Where(w => w is { HasTakenDateTime: true, HasReverseGeocode: true }).ToList();
		//if (filterPhotoTakenDate)
		//	return photos.Where(w => w.HasTakenDateTime).ToList();
		//if (filterReverseGeocode)
		//	return photos.Where(w => w.HasReverseGeocode).ToList();

		return photos.Where(w =>
			(!filterPhotoTakenDate || w.HasTakenDateTime) &&
			(!filterReverseGeocode || w.HasReverseGeocode) &&
			(!filterAuthor || w.HasAuthor) &&
			(!filterDevice || w.HasDevice) &&
			(!filterSubseconds || w.HasSubSeconds)
		);

		throw new PhotoCliException($"One of this {nameof(filterPhotoTakenDate)} or {nameof(filterReverseGeocode)} should be true");
	}
}
