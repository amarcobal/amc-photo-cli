using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Core.Services.Contracts;

public interface ISequentialNumberEnumeratorService
{
	IEnumerable<string> NumberIterator(int toNumerateCount, NumberNamingTextStyle numberNamingTextStyle);
}
