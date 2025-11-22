using CommandLine;
using PhotoCli.Core.Models.Enums;
using PhotoCli.Core.Services.Contracts; // Necesitas la interfaz IReverseGeocodeOptions en Core
using System.Collections.Generic;

namespace PhotoCli.Console.Options;

[Verb(OptionNames.IngestVerb, HelpText = "Ingests photos, optionally performing metadata injection and organization into a target folder.")]
public class IngestOptions : IReverseGeocodeOptions
{
	// Notes: Constructor parameters and properties should be in same order for Immutable Options Type.
	public IngestOptions(
		// 1. Required Options
		IngestOperation operation,
		string outputPath,
		NamingStyle namingStyle,
		FolderProcessType folderProcessType,
		NumberNamingTextStyle numberNamingTextStyle,

		// 2. Action Options (Required for Runner logic)
		CopyInvalidFormatAction invalidFileFormatAction,
		CopyNoPhotoTakenDateAction noPhotoTakenDateAction,
		CopyNoCoordinateAction noCoordinateAction,
		CopyNoDeviceAction noDeviceAction,
		CopyNoAuthorAction noAuthorAction,

		// 3. Optional General Options
		string? inputPath = null,
		bool isDryRun = false,
		string? template = null,
		GroupByFolderType? groupByFolderType = null,
		FolderAppendType? folderAppendType = null,
		FolderAppendLocationType? folderAppendLocationType = null,

		// 4. ReverseGeocode - Shared
		ReverseGeocodeProvider reverseGeoCodeProvider = ReverseGeocodeProvider.Disabled,
		string? bigDataCloudApiKey = null,
		IEnumerable<int>? bigDataCloudAdminLevels = null,
		IEnumerable<string>? googleMapsAddressTypes = null,
		string? googleMapsApiKey = null,
		IEnumerable<string>? openStreetMapProperties = null,
		string? locationIqApiKey = null,
		bool? hasPaidLicense = null,
		string? language = null)
	{
		// Required
		Operation = operation;
		OutputPath = outputPath;
		NamingStyle = namingStyle;
		FolderProcessType = folderProcessType;
		NumberNamingTextStyle = numberNamingTextStyle;

		// Action Options
		InvalidFileFormatAction = invalidFileFormatAction;
		NoPhotoTakenDateAction = noPhotoTakenDateAction;
		NoCoordinateAction = noCoordinateAction;
		NoDeviceAction = noDeviceAction;
		NoAuthorAction = noAuthorAction;

		// Optional General
		InputPath = inputPath;
		IsDryRun = isDryRun;
		Template = template;
		GroupByFolderType = groupByFolderType;
		FolderAppendType = folderAppendType;
		FolderAppendLocationType = folderAppendLocationType;

		// ReverseGeocode
		ReverseGeocodeProvider = reverseGeoCodeProvider;
		BigDataCloudApiKey = bigDataCloudApiKey;
		BigDataCloudAdminLevels = bigDataCloudAdminLevels ?? new List<int>();
		GoogleMapsAddressTypes = googleMapsAddressTypes ?? new List<string>();
		GoogleMapsApiKey = googleMapsApiKey;
		OpenStreetMapProperties = openStreetMapProperties ?? new List<string>();
		LocationIqApiKey = locationIqApiKey;
		HasPaidLicense = hasPaidLicense;
		Language = language;
	}

	// --- 1. INGEST / GENERAL REQUIRED & OPTIONAL ---

	[Option(OptionNames.OperationOptionNameShort, OptionNames.OperationOptionNameLong, HelpText = HelpTexts.IngestOperation)]
	public IngestOperation Operation { get; }

	[Option(OptionNames.OutputPathOptionNameShort, OptionNames.OutputPathOptionNameLong, HelpText = HelpTexts.OutputPathIngest)]
	public string OutputPath { get; }

	[Option(OptionNames.NamingStyleOptionNameShort, OptionNames.NamingStyleOptionNameLong, HelpText = HelpTexts.NamingStyle)]
	public NamingStyle NamingStyle { get; }

	[Option(OptionNames.FolderProcessTypeOptionNameShort, OptionNames.FolderProcessTypeOptionNameLong, HelpText = HelpTexts.FolderProcessType)]
	public FolderProcessType FolderProcessType { get; }

	[Option(OptionNames.NumberNamingTextStyleOptionNameShort, OptionNames.NumberNamingTextStyleOptionNameLong, HelpText = HelpTexts.NumberNamingTextStyle)]
	public NumberNamingTextStyle NumberNamingTextStyle { get; }

	[Option(OptionNames.InputPathOptionNameShort, OptionNames.InputPathOptionNameLong, HelpText = HelpTexts.InputPath)]
	public string? InputPath { get; }

	[Option(OptionNames.IsDryRunOptionNameShort, OptionNames.IsDryRunOptionNameLong, HelpText = HelpTexts.IsDryRun)]
	public bool IsDryRun { get; }

	[Option(OptionNames.TemplateOptionNameShort, OptionNames.TemplateOptionNameLong, HelpText = HelpTexts.MetadataTemplate)]
	public string? Template { get; }

	// --- 2. ACTION OPTIONS (COPIED FROM COPYOPTIONS) ---

	[Option(OptionNames.CopyInvalidFormatActionOptionNameShort, OptionNames.CopyInvalidFormatActionOptionNameLong, HelpText = HelpTexts.CopyInvalidFormatAction)]
	public CopyInvalidFormatAction InvalidFileFormatAction { get; }

	[Option(OptionNames.CopyNoPhotoDateTimeTakenActionOptionNameShort, OptionNames.CopyNoPhotoDateTimeTakenActionOptionNameLong, HelpText = HelpTexts.CopyNoPhotoTakenDateAction)]
	public CopyNoPhotoTakenDateAction NoPhotoTakenDateAction { get; }

	[Option(OptionNames.CopyNoCoordinateActionOptionNameShort, OptionNames.CopyNoCoordinateActionOptionNameLong, HelpText = HelpTexts.CopyNoCoordinateAction)]
	public CopyNoCoordinateAction NoCoordinateAction { get; }

	[Option(OptionNames.CopyNoDeviceActionOptionNameLong, HelpText = HelpTexts.CopyNoDeviceAction)]
	public CopyNoDeviceAction NoDeviceAction { get; }

	[Option(OptionNames.CopyNoAuthorActionOptionNameLong, HelpText = HelpTexts.CopyNoAuthorAction)]
	public CopyNoAuthorAction NoAuthorAction { get; }

	// --- 3. GROUPING OPTIONS (COPIED FROM COPYOPTIONS) ---

	[Option(OptionNames.GroupByFolderTypeOptionNameShort, OptionNames.GroupByFolderTypeOptionNameLong, HelpText = HelpTexts.GroupByFolderType)]
	public GroupByFolderType? GroupByFolderType { get; }

	[Option(OptionNames.FolderAppendTypeOptionNameShort, OptionNames.FolderAppendTypeOptionNameLong, HelpText = HelpTexts.FolderAppendType)]
	public FolderAppendType? FolderAppendType { get; }

	[Option(OptionNames.FolderAppendLocationTypeOptionNameShort, OptionNames.FolderAppendLocationTypeOptionNameLong, HelpText = HelpTexts.FolderAppendLocationType)]
	public FolderAppendLocationType? FolderAppendLocationType { get; }

	// --- 4. REVERSE GEOCODE (FROM IReverseGeocodeOptions) ---

	[Option(OptionNames.ReverseGeocodeProvidersOptionNameShort, OptionNames.ReverseGeocodeProvidersOptionNameLong, HelpText = HelpTexts.ReverseGeocodeProvider)]
	public ReverseGeocodeProvider ReverseGeocodeProvider { get; }

	[Option(OptionNames.BigDataCloudApiKeyOptionNameShort, OptionNames.BigDataCloudApiKeyOptionNameLong, HelpText = HelpTexts.BigDataCloudApiKey)]
	public string? BigDataCloudApiKey { get; }

	[Option(OptionNames.BigDataCloudAdminLevelsOptionNameShort, OptionNames.BigDataCloudAdminLevelsOptionNameLong, HelpText = HelpTexts.BigDataCloudAdminLevels)]
	public IEnumerable<int> BigDataCloudAdminLevels { get; }

	[Option(OptionNames.GoogleMapsAddressTypesOptionNameShort, OptionNames.GoogleMapsAddressTypesOptionNameLong, HelpText = HelpTexts.GoogleMapsAddressTypes)]
	public IEnumerable<string> GoogleMapsAddressTypes { get; }

	[Option(OptionNames.GoogleMapsApiKeyOptionNameShort, OptionNames.GoogleMapsApiKeyOptionNameLong, HelpText = HelpTexts.GoogleMapsApiKey)]
	public string? GoogleMapsApiKey { get; }

	[Option(OptionNames.OpenStreetMapPropertiesOptionNameShort, OptionNames.OpenStreetMapPropertiesOptionNameLong, HelpText = HelpTexts.OpenStreetMapProperties)]
	public IEnumerable<string> OpenStreetMapProperties { get; }

	[Option(OptionNames.LocationIqApiKeyOptionNameShort, OptionNames.LocationIqApiKeyOptionNameLong, HelpText = HelpTexts.LocationIqApiKey)]
	public string? LocationIqApiKey { get; }

	[Option(OptionNames.HasPaidLicenseOptionNameShort, OptionNames.HasPaidLicenseOptionNameLong, HelpText = HelpTexts.HasPaidLicense)]
	public bool? HasPaidLicense { get; }

	[Option(OptionNames.LanguageOptionNameShort, OptionNames.LanguageOptionNameLong, HelpText = HelpTexts.Language)]
	public string? Language { get; }
}
