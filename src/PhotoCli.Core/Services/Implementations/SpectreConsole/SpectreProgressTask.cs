using PhotoCli.Core.Services.Contracts.SpectreConsole;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Services.Implementations.SpectreConsole
{
	public class SpectreProgressTask : IProgressTask
	{
		private readonly ProgressTask _spectreTask;

		public SpectreProgressTask(ProgressTask spectreTask)
		{
			_spectreTask = spectreTask;
		}

		public void Increment(double value) => _spectreTask.Increment(value);
		public void UpdateDescription(string description) => _spectreTask.Description = description;
		public void Stop() => _spectreTask.StopTask();

		public double MaxValue
		{
			get => _spectreTask.MaxValue;
			set => _spectreTask.MaxValue = value;
		}
	}
}
