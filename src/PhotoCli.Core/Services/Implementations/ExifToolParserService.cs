using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using SharpExifTool;
using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Models.Enums;

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
		public const string MyDate = $"{CompositeTagNamespace}:MyDate"; // Hora Naive/Local
		public const string MyTimeZoneOffset = $"{CompositeTagNamespace}:MyTimeZoneOffset"; // Offset de TZ
		public const string MyDateTimeUTC = $"{CompositeTagNamespace}:MyDateTimeUTC"; // Hora Normalizada a UTC
		public const string MyDateTimeLocalOffset = $"{CompositeTagNamespace}:MyDateTimeLocalOffset";
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
			AssetType fileType, // ⭐️ NUEVO PARÁMETRO
			bool parseDateTime,
			bool parseCoordinate,
			bool parseMakeModel = false,
			bool parseSubseconds = false,
			bool parseOriginalFileName = false)
		{
			try
			{
				// 1. Preparamos argumentos opcionales
				var optionalArgs = new List<string>();

				// ⭐️ Curación de Video: Forzar QuickTimeUTC=0 para asegurar la hora local correcta
				if (fileType == AssetType.Video)
				{
					optionalArgs.Add("QuickTimeUTC=0");
				}

				Dictionary<string, string> metadata;
				using (var exifTool = new ExifTool(exiftoolConfigPath: _options.ExifToolFileConfig))
				{
					// ⭐️ Pasamos los argumentos opcionales a ExtractAllMetadata
					metadata = exifTool.ExtractAllMetadata(filePath, optionalArgs.ToArray());
				}

				//Dates
				DateTimeOffset? photoTaken = null;

				// 1. OBTENER LOS COMPONENTES
				// Leemos la hora Naive (local)
				var naiveDate = metadata.GetDateTime(ExifToolTags.MyDate);

				// Leemos el offset calculado por el script Perl (+01:00)
				var offsetString = metadata.GetString(ExifToolTags.MyTimeZoneOffset);

				// 2. VERIFICAR LA CONFIANZA EN EL OFFSET (Nuestra "Más Lógica")
				// Solo si tenemos la hora Naive Y el Offset calculado por GPS/Delta
				if (naiveDate.HasValue && !string.IsNullOrWhiteSpace(offsetString))
				{
					TimeSpan offsetTimeSpan;
					bool success = false;

					// Intento 1: Usar TryParseExact para el formato estricto "+HH:MM"
					// El 'z' en DateTimeOffset usa el formato de Time Zone Offset (ej. +01:00)
					// Pero TimeSpan es más tonto. Usamos 'c' (formato constante HH:MM:SS) o TryParseExact

					// Si tu string es "+01:00", Intenta TryParseExact para manejar el '+'.
					// Necesitas normalizarlo a "01:00:00" si TryParse no lo acepta con el signo.

					// --- Lógica de Manejo de Formato ---
					string normalizedOffset = offsetString;
					// Si la cadena es +HH:MM o -HH:MM, normalizamos para que TimeSpan la entienda como duración:
					if (normalizedOffset.Length == 6 && (normalizedOffset[0] == '+' || normalizedOffset[0] == '-'))
					{
						// Esto separa el signo y el valor (ej: "+01:00" -> "01:00")
						string sign = normalizedOffset[0].ToString();
						string timePart = normalizedOffset.Substring(1);

						// Ahora, TryParseExact solo en la parte del tiempo "01:00"
						if (TimeSpan.TryParseExact(timePart, @"hh\:mm", CultureInfo.InvariantCulture, out offsetTimeSpan))
						{
							// Aplicar el signo para obtener la duración correcta
							if (sign == "-")
							{
								offsetTimeSpan = offsetTimeSpan.Negate();
							}
							success = true;
						}
					}
					else
					{
						// Intento 2: Fallback al TryParse normal (por si la cadena es solo "01:00")
						success = TimeSpan.TryParse(offsetString, CultureInfo.InvariantCulture, out offsetTimeSpan);
					}
					// --- Fin de Lógica de Manejo de Formato ---


					if (success)
					{
						// El Offset es válido. Construimos el DateTimeOffset COMPLETO y CORRECTO.
						photoTaken = new DateTimeOffset(naiveDate.Value, offsetTimeSpan);
						_logger.LogDebug("Timezone resolved using MyTimeZoneOffset: {Offset}", offsetTimeSpan);
					}
				}


				// 3. FALLBACK DE CONFIANZA: Usar MyDateTimeUTC (Prioridad a la Hora Absoluta)
				// Esto se ejecuta si la lógica del Offset falló (ej. formato inválido o MyDate faltante).
				if (!photoTaken.HasValue)
				{
					var utcDateString = metadata.GetString(ExifToolTags.MyDateTimeUTC);

					if (!string.IsNullOrWhiteSpace(utcDateString))
					{
						// Usamos el parser anterior para obtener la hora UTC (ej. 17:42:17 Z)
						if (DateTimeOffset.TryParseExact(
							utcDateString,
							"yyyy:MM:dd HH:mm:ss K",
							CultureInfo.InvariantCulture,
							DateTimeStyles.AssumeUniversal,
							out var dto))
						{
							photoTaken = dto.ToOffset(TimeSpan.Zero); // Garantizar que se maneje como UTC (+00:00)
						}
					}
				}


				// 4. ÚLTIMO FALLBACK: Usar Hora Naive, asumiendo el TZ de la máquina
				// Solo si TODO lo demás falló. ¡Esto es el último recurso!
				if (!photoTaken.HasValue && naiveDate.HasValue)
				{
					// Asumir que la fecha Naive es la fecha en la zona horaria del sistema de ejecución
					photoTaken = new DateTimeOffset(naiveDate.Value, TimeZoneInfo.Local.GetUtcOffset(naiveDate.Value));
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

				// ⭐️ Modificar ExifData para aceptar DateTimeOffset? en lugar de DateTime? 
				// (Asumo que ExifData ha sido actualizado)
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

			// Intentamos analizar el formato Naive YYYY:MM:DD HH:mm:ss
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
