using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using PhotoCli.Core.Models;
using PhotoCli.Core.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace PhotoCli.Core.Services.Implementations;

public class MediaIdentityService : IMediaIdentityService
{
	private readonly MediaIdentity _mediaIdentity;
	private readonly ILogger<MediaIdentityService> _logger;
	private readonly ToolOptions _options;

	public MediaIdentityService(ToolOptions options, ILogger<MediaIdentityService> logger)
	{
		_options = options;
		_logger = logger;
		_mediaIdentity = LoadMediaIdentity();
	}

	public IReadOnlyCollection<Author> GetAuthors() => _mediaIdentity.Authors;
	public IReadOnlyCollection<Device> GetDevices() => _mediaIdentity.Devices;

	public Author GetAuthorByDevice(string deviceId, DateTime takenDate)
	{
		var author = _mediaIdentity.Authors.FirstOrDefault(a =>
			a.Devices.Any(d =>
				d.Device != null &&
				d.Device.ID == deviceId &&
				(d.From == null || takenDate >= d.From) &&
				(d.To == null || takenDate <= d.To)));

		return author ?? GetDefaultAuthor();
	}

	public Device GetDeviceById(string deviceId)
		=> _mediaIdentity.Devices.FirstOrDefault(d => d.ID == deviceId) ?? GetDefaultDevice();

	public Device GetDevice(Photo photo)
	{
		var make = photo.ExifData?.Make;
		var model = photo.ExifData?.Model;
		var serialNumber = photo.ExifData?.SerialNumber;

		Device? device = null;

		// 1. Buscar por serial number primero
		if (!string.IsNullOrEmpty(serialNumber))
		{
			device = _mediaIdentity.Devices
				.FirstOrDefault(d => !string.IsNullOrEmpty(d.SerialNumber) && d.SerialNumber == serialNumber);
		}

		// 2. Buscar por Make/Model exacto
		device = _mediaIdentity.Devices.FirstOrDefault(d =>
		(
			// 1️ Make principal + Model principal
			(d.Make == make && d.Model == model) ||

			// 2️ Make principal + AltModel
			(d.Make == make && d.AltModels.Contains(model)) ||

			// 3️ AltMake + Model principal
			(d.AltMakes.Contains(make) && d.Model == model) ||

			// 4️ AltMake + AltModel
			(d.AltMakes.Contains(make) && d.AltModels.Contains(model))
		)
	);

		return device ??= GetDefaultDevice();

	}

	public Author GetAuthor(Photo photo)
	{
		var takenDate = photo.OriginalDateTimeForFileOperations;
		var device = photo.Device;

		if (takenDate.HasValue)
		{
			return GetAuthorByDevice(device.ID, takenDate.Value);
		}

		return GetDefaultAuthor();
	}


	public Author GetDefaultAuthor() => _mediaIdentity.Authors.First(a => a.ID == "Unknown");
	public Device GetDefaultDevice() => _mediaIdentity.Devices.First(d => d.ID == "Unknown");

	private MediaIdentity LoadMediaIdentity()
	{
		var deserializer = new DeserializerBuilder()
						.IgnoreUnmatchedProperties()
						.WithNamingConvention(NullNamingConvention.Instance)
						.Build();

		List<Author> authors;
		List<Device> devices;

		string currentDir = Path.GetDirectoryName(
		new Uri(System.Reflection.Assembly.GetExecutingAssembly().Location).LocalPath)!;

		string authorsPath = ResolvePath(_options.AuthorsYaml, currentDir);
		string devicesPath = ResolvePath(_options.DevicesYaml, currentDir);

		try
		{
			var devicesYaml = File.ReadAllText(devicesPath, Encoding.UTF8);
			devices = deserializer.Deserialize<DevicesRoot>(devicesYaml)?.Devices ?? new List<Device>();
		}
		catch
		{
			devices = new List<Device>();
		}

		try
		{
			var authorsYaml = File.ReadAllText(authorsPath, Encoding.UTF8);
			authors = deserializer.Deserialize<AuthorsRoot>(authorsYaml)?.Authors ?? new List<Author>();

			// Resolver referencias: conectar AuthorDevice.ID con el objeto Device
			foreach (var author in authors)
			{
				foreach (var ad in author.Devices)
				{
					ad.Device = devices.FirstOrDefault(d => d.ID == ad.ID);
				}
			}
		}
		catch
		{
			authors = new List<Author>();
		}

		// Añadir dispositivo y autor por defecto si no existen
		if (!devices.Any(d => d.ID == "Unknown"))
		{
			devices.Add(new Device
			{
				ID = "Unknown",
				Name = "Unknown Device",
				Alias = "UNK",
				Make = "3rdParty",
				Model = "Unknown",
				SerialNumber = null
			});
		}

		if (!authors.Any(a => a.ID == "Unknown"))
		{
			authors.Add(new Author
			{
				ID = "Unknown",
				Name = "Unknown Author",
				Alias = "UNKN",
				Devices = new List<AuthorDevice>
				{
					new AuthorDevice
					{
						Device = devices.First(d => d.ID == "Unknown"),
						From = null,
						To = null
					}
				}
			});
		}

		return new MediaIdentity
		{
			Authors = authors,
			Devices = devices
		};
	}

	private static string ResolvePath(string path, string baseDir)
	{
		if (Path.IsPathRooted(path))
			return path;

		return Path.GetFullPath(Path.Combine(baseDir, path));
	}
}
