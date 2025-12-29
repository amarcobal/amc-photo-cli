using System.Collections.Generic;
using System;
using System.Globalization;
using System.Linq;

namespace PhotoCli.Core.Models;

public record ExifData
{
	private readonly string _reverseGeocodeSeparator;

	// -----------------------------------------------------------
	// ⭐️ CONSTRUCTOR PRINCIPAL (Llamado por el Parser curado)
	// Este constructor debe recibir los 4 valores de fecha ya curados
	public ExifData(
		DateTimeOffset? originalDateTime,
		DateTime? originalDateTimeLocal,
		DateTimeOffset? originalDateTimeUTC,
		string? originalTimeZoneOffset,
		Coordinate? coordinate,
		string reverseGeocodeSeparator,
		string? make = null,
		string? model = null,
		string? serialNumber = null,
		SubSeconds? subSeconds = null,
		string? originalFileName = null,
		Dictionary<string, string>? metadata = null)
	{
		(OriginalDateTime, OriginalDateTimeLocal, OriginalDateTimeUTC, OriginalTimeZoneOffset, Coordinate, _reverseGeocodeSeparator, Make, Model, SerialNumber, SubSeconds, OriginalFileName, Metadata) =
			(originalDateTime, originalDateTimeLocal, originalDateTimeUTC, originalTimeZoneOffset, coordinate, reverseGeocodeSeparator, make, model, serialNumber, subSeconds, originalFileName, metadata);
	}

	// ⭐️ CONSTRUCTOR FLEXIBLE 1: Desde DateTimeOffset?
	// Mantiene la compatibilidad mapeando el DTO de entrada a las nuevas propiedades curadas.
	public ExifData(
		DateTimeOffset? takenDateOffset, // Input: El valor que antes era TakenDateOffset
		Coordinate? coordinate,
		string reverseGeocodeSeparator,
		string? make = null,
		string? model = null,
		string? serialNumber = null,
		SubSeconds? subSeconds = null,
		string? originalFileName = null,
		Dictionary<string, string>? metadata = null)
		: this(
			takenDateOffset, // OriginalDateTime (Asumimos que el input DTO es la fecha canónica Local+Offset)
			takenDateOffset?.DateTime, // OriginalDateTimeLocal (Naive)
			takenDateOffset?.ToUniversalTime(), // OriginalDateTimeUTC
			takenDateOffset.HasValue ? takenDateOffset.Value.Offset.ToString(@"\+hh\:mm") : null, // OriginalTimeZoneOffset
			coordinate,
			reverseGeocodeSeparator,
			make,
			model,
			serialNumber,
			subSeconds,
			originalFileName,
			metadata)
	{
	}

	// ⭐️ CONSTRUCTOR FLEXIBLE 2: Desde DateTime?
	// Mantiene la compatibilidad, asumiendo el Offset Local de la máquina para inferir los DTO.
	public ExifData(
		DateTime? takenDate, // Input: El valor que antes era TakenDate
		Coordinate? coordinate,
		string reverseGeocodeSeparator,
		string? make = null,
		string? model = null,
		string? serialNumber = null,
		SubSeconds? subSeconds = null,
		string? originalFileName = null,
		Dictionary<string, string>? metadata = null)
		: this(
			// Lógica: Si solo tenemos la hora Naive, asumimos que fue tomada en la zona horaria local de la máquina.
			takenDate.HasValue ? new DateTimeOffset(takenDate.Value, TimeZoneInfo.Local.GetUtcOffset(takenDate.Value)) : null,
			takenDate,
			// La hora UTC se deriva del DTO asumido
			takenDate.HasValue ? new DateTimeOffset(takenDate.Value, TimeZoneInfo.Local.GetUtcOffset(takenDate.Value)).ToUniversalTime() : null,
			// El Offset se deriva del DTO asumido
			takenDate.HasValue ? TimeZoneInfo.Local.GetUtcOffset(takenDate.Value).ToString(@"\+hh\:mm") : null,
			coordinate,
			reverseGeocodeSeparator,
			make,
			model,
			serialNumber,
			subSeconds,
			originalFileName,
			metadata)
	{
	}

	// -----------------------------------------------------------
	// ⭐️ PROPIEDADES CANÓNICAS DE FECHA
	// -----------------------------------------------------------

	// Corresponde a XMP-AMC:OriginOriginalDateTime (Local + Offset Canónico)
	public DateTimeOffset? OriginalDateTime { get; }

	// Corresponde a XMP-AMC:OriginOriginalDateTimeLocal (Hora Naive)
	public DateTime? OriginalDateTimeLocal { get; }

	// Corresponde a XMP-AMC:OriginOriginalDateTimeUTC (UTC)
	public DateTimeOffset? OriginalDateTimeUTC { get; }

	// Corresponde a XMP-AMC:OriginOriginalTimeZoneOffset (String de Offset)
	public string? OriginalTimeZoneOffset { get; }

	public TimeZoneInfo? OriginalTimeZoneInfo
	{
		get
		{
			if (string.IsNullOrWhiteSpace(OriginalTimeZoneOffset))
			{
				return null;
			}

			if (TimeSpan.TryParseExact(
				OriginalTimeZoneOffset,
				@"hh\:mm",
				CultureInfo.InvariantCulture,
				out var offsetTimeSpan))
			{
				try
				{
					// Se crea un TimeZoneInfo genérico basado únicamente en el Offset
					var id = $"UTC{OriginalTimeZoneOffset}";
					return TimeZoneInfo.CreateCustomTimeZone(
						id,
						offsetTimeSpan,
						$"(UTC{OriginalTimeZoneOffset}) Time",
						$"(UTC{OriginalTimeZoneOffset}) DST Time");
				}
				catch (Exception)
				{
					return null;
				}
			}

			return null;
		}
	}

	// ⭐️ Propiedad de Conveniencia para el Renombrado
	/// <summary>
	/// La hora local de la cámara (Naive, sin offset). 
	/// Es la hora preferida para operaciones de renombrado y agrupación por carpetas.
	/// Es el valor que antes se mapeaba a TakenDate.
	/// </summary>
	public DateTime? OriginalDateTimeForFileOperations => OriginalDateTimeLocal;

	public Coordinate? Coordinate { get; }

	public string? Make { get; }
	public string? Model { get; }
	public string? SerialNumber { get; }
	public SubSeconds? SubSeconds { get; }
	public string? OriginalFileName { get; }

	public IEnumerable<string>? ReverseGeocodes { get; set; }
	public string? ReverseGeocodeFormatted => ReverseGeocodes != null ? string.Join(_reverseGeocodeSeparator, ReverseGeocodes) : null;

	public Dictionary<string, string> Metadata { get; set; }
}
