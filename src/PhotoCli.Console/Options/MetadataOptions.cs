using CommandLine;
using PhotoCli.Core.Models.Enums;

namespace PhotoCli.Console.Options;

[Verb(OptionNames.MetadataVerb, HelpText = "Manages photo metadata operations such as add, get, remove, and check.")]
public class MetadataOptions
{
	public MetadataOptions(
		// Required
		MetadataOperation operation, FolderProcessType folderProcessType,
		// Optional
		string? inputPath = null,
		IEnumerable<string>? inputFiles = null,
		string? key = null,
		string? value = null,
		string? template = null,
		bool isDryRun = false,
		IEnumerable<MetadataCheckViewType>? view = null)
	{
		Operation = operation;
		FolderProcessType = folderProcessType;
		InputPath = inputPath;
		InputFiles = inputFiles ?? new List<string>();
		Key = key;
		Value = value;
		Template = template;
		IsDryRun = isDryRun;
		View = view ?? new List<MetadataCheckViewType>();
	}

	#region Required

	[Option(OptionNames.OperationOptionNameShort, OptionNames.OperationOptionNameLong, Required = true, HelpText = HelpTexts.MetadataOperation)]
	public MetadataOperation Operation { get; }

	[Option(OptionNames.FolderProcessTypeOptionNameShort, OptionNames.FolderProcessTypeOptionNameLong, HelpText = HelpTexts.FolderProcessType)]
	public FolderProcessType FolderProcessType { get; }

	#endregion

	#region Optional

	[Option(OptionNames.InputPathOptionNameShort, OptionNames.InputPathOptionNameLong, HelpText = HelpTexts.InputPath)]
	public string? InputPath { get; }

	[Option(OptionNames.InputFilesOptionNameShort, OptionNames.InputFilesOptionNameLong, HelpText = HelpTexts.InputFiles)]
	public IEnumerable<string> InputFiles { get; }

	[Option(OptionNames.KeyOptionNameShort, OptionNames.KeyOptionNameLong, HelpText = HelpTexts.MetadataKey)]
	public string? Key { get; }

	[Option(OptionNames.ValueOptionNameShort, OptionNames.ValueOptionNameLong, HelpText = HelpTexts.MetadataValue)]
	public string? Value { get; }

	[Option(OptionNames.TemplateOptionNameShort, OptionNames.TemplateOptionNameLong, HelpText = HelpTexts.MetadataTemplate)]
	public string? Template { get; }

	[Option(OptionNames.IsDryRunOptionNameShort, OptionNames.IsDryRunOptionNameLong, HelpText = HelpTexts.IsDryRun)]
	public bool IsDryRun { get; }

	#region Metadata Check Options

	[Option(OptionNames.ViewOptionNameShort, OptionNames.ViewOptionNameLong, HelpText = HelpTexts.MetadataView)]
	public IEnumerable<MetadataCheckViewType> View { get; }

	#endregion

	#endregion
}
