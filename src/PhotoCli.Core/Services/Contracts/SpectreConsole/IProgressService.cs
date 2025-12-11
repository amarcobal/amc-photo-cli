using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Services.Contracts.SpectreConsole
{
	public interface IProgressService
	{
		// Método genérico para ejecutar múltiples tareas secuenciales usando una única barra de progreso de Spectre.
		T ExecuteProgress<T>(string title, Func<IProgressContextWrapper, T> action);
	}
}
