using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Services.Contracts.SpectreConsole
{
	public interface IProgressTask
	{
		void Increment(double value);
		void UpdateDescription(string description);
		void Stop();

		// Propiedad para permitir establecer el valor máximo, si es necesario.
		double MaxValue { get; set; }
	}
}
