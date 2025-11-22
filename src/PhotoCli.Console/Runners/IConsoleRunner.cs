using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Console.Runners;

public interface IConsoleRunner
{
	Task<ExitCode> Execute();
}
