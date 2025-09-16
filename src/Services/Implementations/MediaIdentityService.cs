using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using PhotoCli.Models;
using PhotoCli.Services.Contracts;

namespace PhotoCli.Services.Implementations;

public class MediaIdentityService : IMediaIdentityService
{
	private readonly MediaIdentity _mediaIdentity;
	private readonly ILogger<MediaIdentityService> _logger;
	private readonly IMediaIdentityOptions _options;

	public MediaIdentityService(IMediaIdentityOptions options, ILogger<MediaIdentityService> logger)
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
				d.Device.ID == deviceId &&
				(d.From == null || takenDate >= d.From) &&
				(d.To == null || takenDate <= d.To)));

		return author ?? GetDefaultAuthor();
	}

	public Device GetDeviceById(string deviceId)
		=> _mediaIdentity.Devices.FirstOrDefault(d => d.ID == deviceId) ?? GetDefaultDevice();

	public Author GetDefaultAuthor() => _mediaIdentity.Authors.First(a => a.ID == "Unknown");
	public Device GetDefaultDevice() => _mediaIdentity.Devices.First(d => d.ID == "Unknown");

	private MediaIdentity LoadMediaIdentity()
	{
		var deserializer = new DeserializerBuilder()
			.WithNamingConvention(CamelCaseNamingConvention.Instance)
			.Build();

		List<Author> authors;
		List<Device> devices;

		try
		{
			var authorsYaml = File.ReadAllText("Config/authors.yaml", Encoding.UTF8);
			authors = deserializer.Deserialize<AuthorsRoot>(authorsYaml)?.Authors ?? new List<Author>();
		}
		catch
		{
			authors = new List<Author>();
		}

		try
		{
			var devicesYaml = File.ReadAllText("Config/devices.yaml", Encoding.UTF8);
			devices = deserializer.Deserialize<DevicesRoot>(devicesYaml)?.Devices ?? new List<Device>();
		}
		catch
		{
			devices = new List<Device>();
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
}
