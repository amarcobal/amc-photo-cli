namespace PhotoCli.Services.Contracts;

public interface IMediaIdentityService
{
	IReadOnlyCollection<Author> GetAuthors();
	IReadOnlyCollection<Device> GetDevices();

	Author GetAuthorByDevice(string deviceId, DateTime takenDate);
	Device GetDeviceById(string deviceId);

	Author GetDefaultAuthor();
	Device GetDefaultDevice();
}
