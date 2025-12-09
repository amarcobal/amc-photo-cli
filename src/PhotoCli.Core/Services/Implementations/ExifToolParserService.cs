using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using SharpExifTool;
using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;

namespace PhotoCli.Core.Services.Implementations
{
	public static class ExifToolTags
	{
		//CompositeTags
		public const string CompositeTagNamespace = "Composite";

		//Hashes
		public const string MD5 = $"{CompositeTagNamespace}:MD5";

		//Identity
		public const string MyMake = $"{CompositeTagNamespace}:MyMake";
		public const string MyModel = $"{CompositeTagNamespace}:MyModel";
		public const string MySerialNumber = $"{CompositeTagNamespace}:MySerialNumber";

		public const string MyAuthorName = $"{CompositeTagNamespace}:MyAuthorName";
		public const string MyAuthorAlias = $"{CompositeTagNamespace}:MyAuthorAlias";
		public const string MyDeviceName = $"{CompositeTagNamespace}:MyDeviceName";
		public const string MyDeviceAlias = $"{CompositeTagNamespace}:MyDeviceAlias";

		//Event
		public const string MyAlbum = $"{CompositeTagNamespace}:MyAlbum";

		//Dates
		public const string MyDecade = $"{CompositeTagNamespace}:MyDecade";
		public const string MyDate = $"{CompositeTagNamespace}:MyDate";
		public const string MyShortMonthName = $"{CompositeTagNamespace}:MyShortMonthName";
		public const string MySubseconds = $"{CompositeTagNamespace}:MySubseconds";

		//Name Convention
		public const string MyFullFolderFileConvention = $"{CompositeTagNamespace}:MyFullFolderFileConvention";
		public const string MyFolderConvention = $"{CompositeTagNamespace}:MyFolderConvention";
		public const string MyFileNameConvention = $"{CompositeTagNamespace}:MyFileNameConvention";


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
				Dictionary<string, string> metadata;
				using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
				{
					metadata = exifTool.ExtractAllMetadata(filePath);
				}

				//Dates

				DateTime? photoTaken = null;
				if (parseDateTime)
				{
					photoTaken = metadata.GetDateTime(ExifToolTags.MyDate);
				}

				SubSeconds? subSeconds = null;
				if (parseSubseconds)
				{
					var ss = metadata.GetString(ExifToolTags.MySubseconds);
					if (!string.IsNullOrWhiteSpace(ss))
						subSeconds = new SubSeconds(ss);
				}

				//Coordinate

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

				//Identity
				string? make = null;
				string? model = null;
				string? serialNumber = null;
				if (parseMakeModel)
				{
					make = metadata.GetString(ExifToolTags.MyMake);
					model = metadata.GetString(ExifToolTags.MyModel);
					serialNumber = metadata.GetString(ExifToolTags.MySerialNumber);
				}

				//Name Convention
				string? MyFullFolderFileConvention = metadata.GetString(ExifToolTags.MyFullFolderFileConvention);
				string? MyFolderConvention = metadata.GetString(ExifToolTags.MyFolderConvention);
				string? MyFileNameConvention = metadata.GetString(ExifToolTags.MyFileNameConvention);


				string? originalFileName = null;
				if (parseOriginalFileName)
				{
					originalFileName = metadata.GetString(ExifToolTags.PreservedFileName);
									 //?? metadata.GetString(ExifToolTags.FileName);
				}

				// Solo usamos los campos que tu ExifData acepta
				return new ExifData(photoTaken, coordinate, _options.AddressSeparator, make, model, serialNumber, subSeconds, originalFileName, metadata);
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
