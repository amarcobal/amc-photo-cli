using System.IO.Abstractions;
using PhotoCli.Core.Models;
using PhotoCli.Core.Models.ReverseGeocode.BigDataCloud;
using PhotoCli.Core.Models.ReverseGeocode.GoogleMaps;
using PhotoCli.Core.Models.ReverseGeocode.OpenStreetMap;
using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Services.Contracts.ReverseGeocodes;
using PhotoCli.Core.Services.Implementations;
using PhotoCli.Core.Services.Implementations.ReverseGeocodes;
using PhotoCli.Core.Services.Contracts.SpectreConsole;
using PhotoCli.Core.Utils;
using PhotoCli.Core.Options;
using PhotoCli.Web.Components;
using PhotoCli.Web.Services.Implementations;
using MudBlazor.Services;
using PhotoCli.Core.Models.ReverseGeocode;
using PhotoCli.Web.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. SERVICIOS DE BLAZOR Y UI
builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();
builder.Services.AddMudServices();

// ==========================================================
// 2. CONFIGURACIÓN DE OPCIONES (CORE)
// ==========================================================

// Leemos la sección ToolOptions del appsettings.json
var toolOptionsRaw = builder.Configuration.GetSection("ToolOptions").Get<ToolOptionsRaw>() ?? new ToolOptionsRaw();

// Requisito crítico para evitar ArgumentNullException en MetadataConfigurationService:
// Si las rutas son nulas en la configuración, las inicializamos con rutas por defecto
// basadas en la carpeta de ejecución de la web.
var baseDir = AppDomain.CurrentDomain.BaseDirectory;
var configFolder = Path.Combine(baseDir, "Config");

toolOptionsRaw.MetadataTemplatesYaml ??= Path.Combine(configFolder, "MetadataTemplates.yaml");
toolOptionsRaw.AuthorsYaml ??= Path.Combine(configFolder, "Authors.yaml");
toolOptionsRaw.DevicesYaml ??= Path.Combine(configFolder, "Devices.yaml");
toolOptionsRaw.ExifToolFileConfig ??= "";

// Creamos la instancia real de ToolOptions que requiere el Core
var toolOptions = new ToolOptions(toolOptionsRaw);
builder.Services.AddSingleton(toolOptions);

// IMPORTANTE: Forzamos el uso de PhotoCli.Core.Models.ApiKeyStore
builder.Services.AddSingleton(new PhotoCli.Core.Models.ApiKeyStore());
builder.Services.AddSingleton<Statistics>();

// Inyectamos nuestras opciones web para el Ingest (Reverse Geocode)
builder.Services.AddSingleton<IReverseGeocodeOptions, WebIngestOptions>();

// ==========================================================
// 3. REGISTRO DE SERVICIOS DE PHOTOCLI.CORE
// ==========================================================
builder.Services.AddTransient<IFileSystem, FileSystem>();
builder.Services.AddTransient<IExifParserService, ExifToolParserService>();
builder.Services.AddTransient<IExifDataAppenderService, ExifDataAppenderService>();
builder.Services.AddTransient<IMediaIdentityAppenderService, MediaIdentityAppenderService>();
builder.Services.AddTransient<IPhotoCollectorService, PhotoCollectorService>();
builder.Services.AddTransient<IDirectoryGrouperService, DirectoryGrouperService>();
builder.Services.AddTransient<IFileNamerService, FileNamerService>();
builder.Services.AddTransient<IFileService, FileService>();
builder.Services.AddTransient<ICsvService, CsvService>();
builder.Services.AddTransient<ISequentialNumberEnumeratorService, SequentialNumberEnumeratorService>();
builder.Services.AddTransient<IExifOrganizerService, ExifOrganizerService>();
builder.Services.AddTransient<IExifDataGrouperService, ExifDataGrouperService>();
builder.Services.AddTransient<IFolderRenamerService, FolderRenamerService>();
builder.Services.AddTransient<IMediaIdentityService, MediaIdentityService>();
builder.Services.AddTransient<IReverseGeocodeService, ReverseGeocodeService>();
builder.Services.AddTransient<IReverseGeocodeFetcherService, ReverseGeocodeFetcherService>();
builder.Services.AddTransient<IMetadataService, MetadataService>();

// Este es el servicio que daba error al inicializarse con rutas null
builder.Services.AddTransient<IMetadataConfigurationService, MetadataConfigurationService>();
builder.Services.AddTransient<IAssetTypeService, AssetTypeService>();

// Servicios para Base de Datos (Archive)
builder.Services.AddTransient<IDuplicatePhotoRemoveService, DuplicatePhotoRemoveService>();
builder.Services.AddTransient<IDbService, DbService>();
builder.Services.AddSingleton<IArchiveDbContextProvider, ArchiveDbContextProvider>();
builder.Services.AddSingleton<ISQLiteConnectionStringProvider, ArchiveIsqLiteConnectionStringProvider>();

// ==========================================================
// 4. IMPLEMENTACIONES ESPECÍFICAS DE LA WEB
// ==========================================================
builder.Services.AddScoped<IConsoleWriter, WebConsoleWriter>();
builder.Services.AddScoped<IProgressService, WebProgressService>();

// ==========================================================
// 5. CLIENTES HTTP (REVERSE GEOCODING)
// ==========================================================
builder.Services.AddSingleton<ICoordinateCache<BigDataCloudResponse>, CoordinateCache<BigDataCloudResponse>>();
builder.Services.AddSingleton<ICoordinateCache<GoogleMapsResponse>, CoordinateCache<GoogleMapsResponse>>();
builder.Services.AddSingleton<ICoordinateCache<OpenStreetMapResponse>, CoordinateCache<OpenStreetMapResponse>>();

var agent = UserAgent.Instance();

builder.Services.AddHttpClient<IBigDataCloudReverseGeocodeService, BigDataCloudReverseGeocodeService>(c =>
{
	c.BaseAddress = new Uri("https://api.bigdatacloud.net/data/reverse-geocode");
	c.DefaultRequestHeaders.UserAgent.Add(agent);
});

builder.Services.AddHttpClient<IOpenStreetMapFoundationReverseGeocodeService, OpenStreetMapFoundationReverseGeocodeService>(c =>
{
	c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/reverse");
	c.DefaultRequestHeaders.UserAgent.Add(agent);
});

builder.Services.AddHttpClient<IGoogleMapsReverseGeocodeService, GoogleMapsReverseGeocodeService>(c =>
{
	c.BaseAddress = new Uri("https://maps.googleapis.com/maps/api/geocode/json");
	c.DefaultRequestHeaders.UserAgent.Add(agent);
});

builder.Services.AddHttpClient<ILocationIqReverseGeocodeService, LocationIqReverseGeocodeService>(c =>
{
	c.BaseAddress = new Uri("https://us1.locationiq.com/v1/reverse.php");
	c.DefaultRequestHeaders.UserAgent.Add(agent);
});

var app = builder.Build();

// CONFIGURACIÓN DEL PIPELINE HTTP
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.Run();
