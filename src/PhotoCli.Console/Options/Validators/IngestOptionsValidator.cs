using FluentValidation;
using PhotoCli.Console.Options;

namespace PhotoCli.Console.Options.Validators;

public class IngestOptionsValidator : BaseValidator<IngestOptions>
{
	public IngestOptionsValidator()
	{
		Include(new IngestOptionsValidator());
		RuleFor(x => x.Operation).IsInEnum().WithMessage("Operation must be Copy or Move.");
	}
}
