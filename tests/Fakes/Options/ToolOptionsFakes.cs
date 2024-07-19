namespace PhotoCli.Tests.Fakes.Options;

public static class ToolOptionsFakes
{
	public static ToolOptions Valid()
	{
		return ToolOptions.Default();
	}

	public static ToolOptions WithConnectionLimit(int connectionLimit)
	{
		var options = ToolOptions.Default();
		options.ConnectionLimit = connectionLimit;
		return options;
	}

	public static ToolOptions WithSupportedExtensions(string[] supportedExtensions)
	{
		var options = ToolOptions.Default();
		options.SupportedExtensions = supportedExtensions;
		return options;
	}

	public static ToolOptions WithCompanionExtensions(string[] companionExtensions)
	{
		var options = ToolOptions.Default();
		options.CompanionExtensions = companionExtensions;
		return options;
	}
}
