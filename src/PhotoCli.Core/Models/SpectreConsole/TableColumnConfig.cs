using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoCli.Core.Models.SpectreConsole
{
	public class TableColumnConfig
	{
		/// <summary>
		/// El texto que se mostrará en el encabezado de la columna.
		/// </summary>
		public string HeaderText { get; set; } = string.Empty;

		/// <summary>
		/// Indica si el contenido de la columna debe evitar el salto de línea (NoWrap).
		/// </summary>
		public bool NoWrap { get; set; } = false;

		/// <summary>
		/// Ancho fijo opcional para la columna (en caracteres).
		/// Si es null, Specte.Console decide el ancho.
		/// </summary>
		public int? Width { get; set; } = null;
	}
}
