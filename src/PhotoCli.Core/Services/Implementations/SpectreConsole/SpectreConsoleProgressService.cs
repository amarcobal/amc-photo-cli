using PhotoCli.Core.Services.Contracts.SpectreConsole;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Services.Implementations.SpectreConsole
{
	public class SpectreConsoleProgressService : IProgressService
	{
		public T ExecuteProgress<T>(string title, Func<IProgressContextWrapper, T> action)
		{
			T result = default;

			AnsiConsole.Progress()
				.Columns(new ProgressColumn[]
				{
				new TaskDescriptionColumn(),
				new ProgressBarColumn(),
				new PercentageColumn(),
				new RemainingTimeColumn(),
				new ElapsedTimeColumn(), // Asegúrate de que ElapsedTimeColumn esté aquí para el tiempo
                new SpinnerColumn(),
				})
				.Start(ctx =>
				{
					var wrapper = new SpectreProgressContextWrapper(ctx);
					result = action(wrapper);
				});

			AnsiConsole.Cursor.Move(CursorDirection.Up, 1);

			return result;
		}
	}

	public class SpectreProgressContextWrapper : IProgressContextWrapper
	{
		private readonly ProgressContext _spectreContext;

		public SpectreProgressContextWrapper(ProgressContext spectreContext)
		{
			_spectreContext = spectreContext;
		}

		public IProgressTask AddTask(string title, double maxValue = 1.0)
		{
			var spectreTask = _spectreContext.AddTask(title, new ProgressTaskSettings { MaxValue = maxValue });
			return new SpectreProgressTask(spectreTask);
		}
	}
}
