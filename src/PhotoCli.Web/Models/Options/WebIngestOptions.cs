using PhotoCli.Core.Options;
using PhotoCli.Core.Models.Enums;
using System.Collections.Generic;
using System;

namespace PhotoCli.Web.Models;

public class WebIngestOptions : IReverseGeocodeOptions
{
	// 1. Enum: La interfaz pide que NO sea nullable
	public ReverseGeocodeProvider ReverseGeocodeProvider { get; set; }

	// 2. API Keys: La interfaz pide que sean strings nullables
	public string? BigDataCloudApiKey { get; set; }
	public string? GoogleMapsApiKey { get; set; }
	public string? LocationIqApiKey { get; set; }

	// 3. Colecciones: La interfaz pide tipos exactos y NO nullables. 
	// Para no tener nulos, las inicializamos vacías por defecto.
	public IEnumerable<int> BigDataCloudAdminLevels { get; set; } = Array.Empty<int>();
	public IEnumerable<string> GoogleMapsAddressTypes { get; set; } = Array.Empty<string>();
	public IEnumerable<string> OpenStreetMapProperties { get; set; } = Array.Empty<string>();

	// 4. Configuración extra: La interfaz pide bool? (nullable) y string?
	public bool? HasPaidLicense { get; set; }
	public string? Language { get; set; }

	// ==========================================================
	// PROPIEDADES EXTRA PARA NUESTRO USO EN LA WEB Y EL RUNNER
	// ==========================================================
	public bool HasAnyApiKey => !string.IsNullOrEmpty(BigDataCloudApiKey) ||
								 !string.IsNullOrEmpty(GoogleMapsApiKey) ||
								 !string.IsNullOrEmpty(LocationIqApiKey);

	public string? OutputPath { get; set; }
	public string? InputPath { get; set; }
}
