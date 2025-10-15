using FluentValidation;

namespace PhotoCli.Options.Validators;

public class MetadataOptionsValidator : BaseValidator<MetadataOptions>
{
	public MetadataOptionsValidator()
	{
		// Operation must be set
		RuleFor(r => r.Operation)
			.IsInEnum()
			.WithMessage(r => Required(nameof(MetadataOptions.Operation), OptionNames.OperationOptionNameLong, OptionNames.OperationOptionNameShort));

		// Must use either InputPath or InputFiles, but not both
		When(r => r.InputPath is not null && r.InputFiles.Any(), () =>
		{
			RuleFor(r => r.InputPath)
				.Null()
				.WithMessage(r => CantUseMessage(nameof(MetadataOptions.InputPath), nameof(MetadataOptions.InputFiles)));

			RuleFor(r => r.InputFiles)
				.Empty()
				.WithMessage(r => CantUseMessage(nameof(MetadataOptions.InputFiles), nameof(MetadataOptions.InputPath)));
		});

		When(r => r.InputPath is null && !r.InputFiles.Any(), () =>
		{
			RuleFor(r => r.InputPath)
				.NotNull()
				.WithMessage("Either --input or --input-files must be provided.");
		});

		// Key and Value validation depending on Operation
		When(r => r.Operation is MetadataOperation.Add or MetadataOperation.Remove, () =>
		{
			RuleFor(r => r.Key)
				.NotEmpty()
				.WithMessage(r => MustUseMessage(nameof(MetadataOptions.Key), r.Operation.ToString(),
					OptionNames.KeyOptionNameLong, OptionNames.KeyOptionNameShort));

			RuleFor(r => r.Value)
				.NotEmpty()
				.When(r => r.Operation == MetadataOperation.Add)
				.WithMessage(r => MustUseMessage(nameof(MetadataOptions.Value), r.Operation.ToString(),
					OptionNames.ValueOptionNameLong, OptionNames.ValueOptionNameShort));
		});

		When(r => r.Operation is MetadataOperation.Get or MetadataOperation.Check, () =>
		{
			RuleFor(r => r.Key)
				.Null()
				.WithMessage(r => CantUseMessage(nameof(MetadataOptions.Key), r.Operation.ToString()));

			RuleFor(r => r.Value)
				.Null()
				.WithMessage(r => CantUseMessage(nameof(MetadataOptions.Value), r.Operation.ToString()));
		});

		// Template and IsDryRun are optional, no validation needed
	}
}
