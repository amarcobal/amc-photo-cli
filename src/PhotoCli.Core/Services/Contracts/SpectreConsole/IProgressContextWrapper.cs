using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Services.Contracts.SpectreConsole
{
	public interface IProgressContextWrapper
	{
		IProgressTask AddTask(string title, double maxValue = 1.0);
	}
}
