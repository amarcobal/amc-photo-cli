namespace PhotoCli.Core.Models.Enums;

public enum CopyNoAuthorAction : byte
{
	Continue = 0,
	PreventProcess = 1,
	DontCopyToOutput = 2,
	InSubFolder = 3,
}
