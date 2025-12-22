using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Implementations;
using PhotoCli.Core.Utils;
using System.IO.Abstractions;

namespace PhotoCli.Core.Models;

public record PhotoFile
{
	public PhotoFile(IFileInfo source, AssetType type)
	{
		var sourcePath = source.ToString();
		SourcePath = sourcePath ?? throw new PhotoCliException("Source path don't have any value");
		SourceFullPath = source.FullName;
		(FileName, Extension) = PathHelper.GetFileNameExtensionSeparately(sourcePath);
		Type = type;
	}

	public void SetTarget(string targetRelativePath, string outputFolder, string? newName)
	{
		var fileName = newName ?? FileName;
		var normalizedExtension = PathHelper.NormalizePath(Extension);
		TargetRelativePath = Path.Combine(targetRelativePath, $"{fileName}.{normalizedExtension}");
		TargetFullPath = Path.Combine(outputFolder, TargetRelativePath);
	}

	public string SourcePath { get; private set; }
	public string SourceFullPath { get; private set; }
	public string? TargetFullPath { get; private set; }
	public string? TargetRelativePath { get; private set; }
	public string FileName { get; }
	public string Extension { get; }
	public string? Sha1Hash { get; set; }
	public string FileNameWithExtension => $"{FileName}.{Extension}";
	public AssetType Type { get; }

}
