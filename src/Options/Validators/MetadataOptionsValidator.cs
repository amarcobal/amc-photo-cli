using FluentValidation;

namespace PhotoCli.Options.Validators;

public class MetadataOptionsValidator : BaseValidator<MetadataOptions>
{
	public MetadataOptionsValidator()
	{
		// --- Operation ---
		RuleFor(r => r.Operation)
			.IsInEnum()
			.WithMessage(r => Required(nameof(MetadataOptions.Operation),
				OptionNames.OperationOptionNameLong,
				OptionNames.OperationOptionNameShort));

		// --- InputPath / InputFiles mutual exclusion ---
		When(r => r.InputPath is not null && r.InputFiles.Any(), () =>
		{
			RuleFor(r => r.InputPath)
				.Null()
				.WithMessage(r => CantUseMessage(nameof(MetadataOptions.InputPath), nameof(MetadataOptions.InputFiles)));

			RuleFor(r => r.InputFiles)
				.Empty()
				.WithMessage(r => CantUseMessage(nameof(MetadataOptions.InputFiles), nameof(MetadataOptions.InputPath)));
		});

		// Require one of them
		When(r => r.InputPath is null && !r.InputFiles.Any(), () =>
		{
			RuleFor(r => r.InputPath)
				.NotNull()
				.WithMessage("Either --input or --input-files must be provided.");
		});

		// --- Rules per operation ---

		// ADD ----------------------------------------------------------------
		When(r => r.Operation == MetadataOperation.Add, () =>
		{
			RuleFor(r => new { r.Key, r.Value, r.Template })
				.Must(x =>
					(!string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value) && string.IsNullOrWhiteSpace(x.Template)) // key/value pair
					|| (string.IsNullOrWhiteSpace(x.Key) && string.IsNullOrWhiteSpace(x.Value) && !string.IsNullOrWhiteSpace(x.Template)) // template
				)
				.WithMessage("For 'add', you must specify either --key and --value together, or --template, but not both or partial.");
		});

		// GET ----------------------------------------------------------------
		When(r => r.Operation == MetadataOperation.Get, () =>
		{
			RuleFor(r => new { r.Key, r.Template, r.Value })
				.Must(x =>
					(!string.IsNullOrWhiteSpace(x.Key) && string.IsNullOrWhiteSpace(x.Template) && string.IsNullOrWhiteSpace(x.Value)) // single key
					|| (string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Template) && string.IsNullOrWhiteSpace(x.Value)) // template
				)
				.WithMessage("For 'get', you must specify either --key or --template (but not both), and --value is not allowed.");
		});

		// DELETE --------------------------------------------------------------
		When(r => r.Operation == MetadataOperation.Delete, () =>
		{
			RuleFor(r => new { r.Key, r.Template, r.Value })
				.Must(x =>
					(!string.IsNullOrWhiteSpace(x.Key) && string.IsNullOrWhiteSpace(x.Template) && string.IsNullOrWhiteSpace(x.Value)) // single key
					|| (string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Template) && string.IsNullOrWhiteSpace(x.Value)) // template
				)
				.WithMessage("For 'delete', you must specify either --key or --template (but not both), and --value is not allowed.");
		});

		// CHECK ---------------------------------------------------------------
		When(r => r.Operation == MetadataOperation.Check, () =>
		{
			RuleFor(r => new { r.Key, r.Template, r.Value })
				.Must(x =>
					(!string.IsNullOrWhiteSpace(x.Key) && string.IsNullOrWhiteSpace(x.Template) && string.IsNullOrWhiteSpace(x.Value)) // single key
					|| (string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Template) && string.IsNullOrWhiteSpace(x.Value)) // template
				)
				.WithMessage("For 'check', you must specify either --key or --template (but not both), and --value is not allowed.");
		});

		// Template y DryRun son opcionales fuera de esas condiciones
	}
}
