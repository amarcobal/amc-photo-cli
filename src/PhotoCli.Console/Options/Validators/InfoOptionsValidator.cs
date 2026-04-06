using FluentValidation;
using PhotoCli.Console.Options.Validators;
using PhotoCli.Console.Options;

namespace PhotoCli.Console.Options.Validators;

public class InfoOptionsValidator : BaseValidator<InfoOptions>
{
	public InfoOptionsValidator()
	{
		RuleFor(r => r.OutputPath).NotNull().WithMessage(Required(nameof(CopyOptions.OutputPath), OptionNames.OutputPathOptionNameLong, OptionNames.OutputPathOptionNameShort));
		//.Matches(Constants.CsvExtensionRegex).WithMessage($"{nameof(CopyOptions.OutputPath)} should have .csv extension");

		Include(new SharedReverseGeocodeValidator());
	}
}
