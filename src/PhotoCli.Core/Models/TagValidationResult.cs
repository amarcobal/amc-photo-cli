using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Models
{
	public record TagValidationResult(bool HasValue, string Value, bool IsRequired, bool IsValid);
}
