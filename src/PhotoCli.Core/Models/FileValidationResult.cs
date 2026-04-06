using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Models
{
	public record FileValidationResult
	{
		// Contiene tags como TakenDate, Make, Model, Author, Device
		public IReadOnlyDictionary<string, TagValidationResult> IdentityTags { get; init; } = new Dictionary<string, TagValidationResult>();

		// Contiene tags específicos del Template (XMP:Tag1, XMP:Tag2, etc.)
		public IReadOnlyDictionary<string, TagValidationResult> TemplateTags { get; init; } = new Dictionary<string, TagValidationResult>();
	}
}
