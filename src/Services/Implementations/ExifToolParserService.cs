using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using SharpExifTool;
using PhotoCli.Models;

namespace PhotoCli.Services.Implementations
{
	public static class ExifToolTags
	{
		// Fechas
		public const string DateTimeOriginal = "ExifIFD:DateTimeOriginal";
		public const string CreateDate = "ExifIFD:CreateDate";
		public const string SubSecTimeOriginal = "ExifIFD:SubSecTimeOriginal";
		public const string SubSecTimeDigitized = "ExifIFD:SubSecTimeDigitized";
		public const string CompositeDateTimeOriginal = "Composite:SubSecDateTimeOriginal";
		public const string CompositeCreateDate = "Composite:SubSecCreateDate";
		public const string CompositeDateTimeCreated = "Composite:DateTimeCreated";
		public const string PanasonicTimeStamp = "Panasonic:TimeStamp";

		// Ubicación
		public const string GPSLatitude = "GPSLatitude";
		public const string GPSLongitude = "GPSLongitude";

		// Cámara
		public const string Make = "IFD0:Make";
		public const string Model = "IFD0:Model";
		public const string QuicktimeMake = "com.apple.quicktime.make";
		public const string QuicktimeModel = "com.apple.quicktime.model";

		// Otros
		public const string PreservedFileName = "PreservedFileName";
		public const string FileName = "FileName";
	}


	public class ExifToolParserService : IExifParserService
	{
		private readonly IFileSystem _fileSystem;
		private readonly ILogger<ExifToolParserService> _logger;
		private readonly ToolOptions _options;
		private readonly Statistics _statistics;
		private readonly int _coordinatePrecision;

		public ExifToolParserService(
			ILogger<ExifToolParserService> logger,
			IFileSystem fileSystem,
			ToolOptions options,
			Statistics statistics)
		{
			_logger = logger;
			_fileSystem = fileSystem;
			_options = options;
			_statistics = statistics;
			_coordinatePrecision = options.CoordinatePrecision;
		}

		public ExifData? Parse(
			string filePath,
			bool parseDateTime,
			bool parseCoordinate,
			bool parseMakeModel = false,
			bool parseSubseconds = false,
			bool parseOriginalFileName = false)
		{
			try
			{
				ICollection<KeyValuePair<string, string>> metadata;
				using (var exifTool = new ExifTool())
				{
					metadata = exifTool.ExtractAllMetadata(filePath, "-config \"C:\\Users\\amarcobal\\Desktop\\ExifToolTest\\Config\\EXIFTOOL-CONFIG_AMC-Photography_Custom.config\"");
				}

				DateTime? photoTaken = null;
				if (parseDateTime)
				{
					photoTaken = metadata.GetDateTime(ExifToolTags.DateTimeOriginal)
							  ?? metadata.GetDateTime(ExifToolTags.CreateDate)
							  ?? metadata.GetDateTime(ExifToolTags.CompositeDateTimeOriginal)
							  ?? metadata.GetDateTime(ExifToolTags.CompositeCreateDate)
							  ?? metadata.GetDateTime(ExifToolTags.CompositeDateTimeCreated);
				}

				Coordinate? coordinate = null;
				if (parseCoordinate)
				{
					var lat = metadata.GetDouble(ExifToolTags.GPSLatitude);
					var lon = metadata.GetDouble(ExifToolTags.GPSLongitude);
					if (lat.HasValue && lon.HasValue)
						coordinate = new Coordinate(
							Math.Round(lat.Value, _coordinatePrecision),
							Math.Round(lon.Value, _coordinatePrecision));
				}

				string? make = null;
				string? model = null;
				if (parseMakeModel)
				{
					make = metadata.GetString(ExifToolTags.Make) ?? metadata.GetString(ExifToolTags.QuicktimeMake);
					model = metadata.GetString(ExifToolTags.Model) ?? metadata.GetString(ExifToolTags.QuicktimeModel);
				}

				SubSeconds? subSeconds = null;
				if (parseSubseconds)
				{
					var ss = metadata.GetString(ExifToolTags.SubSecTimeOriginal);
					if (!string.IsNullOrWhiteSpace(ss))
						subSeconds = new SubSeconds(ss);
				}

				string? originalFileName = null;
				if (parseOriginalFileName)
				{
					originalFileName = metadata.GetString(ExifToolTags.PreservedFileName)
									 ?? metadata.GetString(ExifToolTags.FileName);
				}

				// Solo usamos los campos que tu ExifData acepta
				return new ExifData(photoTaken, coordinate, _options.AddressSeparator, make, model, subSeconds, originalFileName);
			}
			catch (Exception ex)
			{
				_logger.LogInformation(ex, "SharpExifTool failed parsing metadata for {FilePath}", filePath);
				++_statistics.InternalError;
				return null;
			}
		}
	}

	public static class ExifToolExtensions
	{
		public static DateTime? GetDateTime(this IEnumerable<KeyValuePair<string, string>> metadata, string key)
		{
			var kv = metadata.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
			if (kv.Equals(default(KeyValuePair<string, string>)) || string.IsNullOrWhiteSpace(kv.Value))
				return null;

			if (DateTime.TryParseExact(kv.Value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
				return dt;

			if (DateTime.TryParse(kv.Value, out dt))
				return dt;

			return null;
		}

		public static double? GetDouble(this IEnumerable<KeyValuePair<string, string>> metadata, string key)
		{
			var kv = metadata.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
			if (kv.Equals(default(KeyValuePair<string, string>)) || string.IsNullOrWhiteSpace(kv.Value))
				return null;

			if (double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
				return value;

			return null;
		}

		public static string? GetString(this IEnumerable<KeyValuePair<string, string>> metadata, string key)
		{
			var kv = metadata.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
			return kv.Equals(default(KeyValuePair<string, string>)) ? null : kv.Value;
		}
	}
}
