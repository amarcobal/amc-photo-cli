using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using SharpExifTool;
using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Utils.Constants;
using PhotoCli.Core.Utils.Extensions;

namespace PhotoCli.Core.Services.Implementations
{
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
				// 1. CONFIGURACIÓN DEL MOTOR DE EXIFTOOL (CommonArgs)
				// Estos argumentos se pasan al constructor y afectan a cómo se extraen los datos.
				var commonArgs = new List<string>
				{
					"-a",  // --allowDuplicates: Muestra todos los tags aunque tengan el mismo nombre.
					//"-G1", // --groupNames: Muestra el nombre del grupo específico (ej. [Keys], [UserData]).
					"-s",  // --short: Formato corto de etiquetas (necesario para el parseo de SharpExifTool).
					"-n"   // --printConv: Valores numéricos/crudos (GPS decimal y offsets con signo).
				};

				// Lógica específica según el tipo de archivo (Vídeo vs Foto)
				if (fileType == AssetType.Video)
				{
					// -api QuickTimeUTC=0: Crucial para que ExifTool no intente convertir las fechas
					// a la zona horaria del sistema actual. Leemos el valor literal del archivo.
					commonArgs.Add("-api QuickTimeUTC=0");

					// -api LargeFileSupport=1: Necesario para procesar vídeos que superen los 4GB.
					commonArgs.Add("-api LargeFileSupport=1");
				}

				Dictionary<string, string> metadata;

				// Instanciamos ExifTool con la configuración específica
				using (var exifTool = new ExifTool(
					exiftoolConfigPath: _options.ExifToolFileConfig,
					commonArgs: commonArgs))
				{
					// Extraemos todos los metadatos. El flag -G1 y -s ya vienen en la constante 'Arguments' de tu clase.
					metadata = exifTool.ExtractAllMetadata(filePath);
				}

				DateTimeOffset? photoTaken = null;

				if (parseDateTime)
				{
					var naiveDate = metadata.GetDateTime(ExifToolTagsConstants.MyDate);
					var offsetString = metadata.GetString(ExifToolTagsConstants.MyTimeZoneOffset);
					var utcString = metadata.GetString(ExifToolTagsConstants.MyDateTimeUTC);

					// ESTRATEGIA 1: MyDate + MyTimeZoneOffset (Confianza Alta)
					if (naiveDate.HasValue && TryParseExifOffset(offsetString, out var offset))
					{
						photoTaken = new DateTimeOffset(naiveDate.Value, offset);
						_logger.LogDebug("Time resolved via MyDate + MyTimeZoneOffset: {Offset}", offset);
					}

					// ESTRATEGIA 2: MyDateTimeUTC (Confianza Alta - Normalizado)
					if (!photoTaken.HasValue && !string.IsNullOrEmpty(utcString))
					{
						if (DateTimeOffset.TryParseExact(utcString, new[] { "yyyy:MM:dd HH:mm:ssK", "yyyy:MM:dd HH:mm:sszzz" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utcDto))
						{
							photoTaken = utcDto;
							_logger.LogDebug("Time resolved via MyDateTimeUTC");
						}
					}

					// ESTRATEGIA 3: Heurística de Nombre de Archivo (Confianza Media)
					if (!photoTaken.HasValue)
					{
						var fileNameDateStr = metadata.GetString(ExifToolTagsConstants.DateTimeOriginalFromFileName);
						if (!string.IsNullOrEmpty(fileNameDateStr) && DateTime.TryParseExact(fileNameDateStr, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fnDate))
						{
							TryParseExifOffset(offsetString, out var fnOffset);
							photoTaken = new DateTimeOffset(fnDate, fnOffset != TimeSpan.Zero ? fnOffset : TimeZoneInfo.Local.GetUtcOffset(fnDate));
							_logger.LogDebug("Time resolved via FileName Heuristics");
						}
					}

					// ESTRATEGIA 4: GeolocationTimeZone (Confianza Media - Búsqueda Offline OS)
					if (!photoTaken.HasValue && naiveDate.HasValue)
					{
						var tzName = metadata.GetString(ExifToolTagsConstants.GeolocationTimeZone);
						if (!string.IsNullOrEmpty(tzName))
						{
							try
							{
								var tzInfo = TimeZoneInfo.FindSystemTimeZoneById(tzName);
								photoTaken = new DateTimeOffset(naiveDate.Value, tzInfo.GetUtcOffset(naiveDate.Value));
								_logger.LogDebug("Time resolved via GeolocationTimeZone: {tzName}", tzName);
							}
							catch { /* TZ no soportada por el sistema operativo */ }
						}
					}

					// ESTRATEGIA 5: Fallback total (Hora Local de la máquina)
					if (!photoTaken.HasValue && naiveDate.HasValue)
					{
						photoTaken = new DateTimeOffset(naiveDate.Value, TimeZoneInfo.Local.GetUtcOffset(naiveDate.Value));
						_logger.LogWarning("No timezone metadata found. Assuming system local time.");
					}
				}

				// Subseconds
				SubSeconds? subSeconds = null;
				if (parseSubseconds)
				{
					var ss = metadata.GetString(ExifToolTagsConstants.MySubseconds);
					if (!string.IsNullOrWhiteSpace(ss)) subSeconds = new SubSeconds(ss);
				}

				// Coordinates
				Coordinate? coordinate = null;
				if (parseCoordinate)
				{
					var lat = metadata.GetDouble(ExifToolTagsConstants.GPSLatitude);
					var lon = metadata.GetDouble(ExifToolTagsConstants.GPSLongitude);
					if (lat.HasValue && lon.HasValue) coordinate = new Coordinate(lat.Value, lon.Value);
				}

				// Identity & Conventions
				string? make = parseMakeModel ? metadata.GetString(ExifToolTagsConstants.MyMake) : null;
				string? model = parseMakeModel ? metadata.GetString(ExifToolTagsConstants.MyModel) : null;
				string? serial = parseMakeModel ? metadata.GetString(ExifToolTagsConstants.MySerialNumber) : null;
				string? originalFileName = parseOriginalFileName ? metadata.GetString(ExifToolTagsConstants.PreservedFileName) : null;

				return new ExifData(photoTaken, coordinate, _options.AddressSeparator, make, model, serial, subSeconds, originalFileName, metadata);
			}
			catch (Exception ex)
			{
				_logger.LogInformation(ex, "SharpExifTool failed parsing metadata for {FilePath}", filePath);
				++_statistics.InternalError;
				return null;
			}
		}

		private bool TryParseExifOffset(string? offsetString, out TimeSpan offset)
		{
			offset = TimeSpan.Zero;
			if (string.IsNullOrWhiteSpace(offsetString)) return false;
			offsetString = offsetString.Trim();

			if (offsetString.StartsWith("+") || offsetString.StartsWith("-"))
			{
				bool isNegative = offsetString.StartsWith("-");
				string parts = offsetString.Substring(1);
				if (!parts.Contains(":")) parts += ":00";
				if (TimeSpan.TryParseExact(parts, new[] { @"h\:mm", @"hh\:mm", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out offset))
				{
					if (isNegative) offset = offset.Negate();
					return true;
				}
			}
			return false;
		}
	}
}
