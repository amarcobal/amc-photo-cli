using PhotoCli.Core.Models.SpectreConsole;

namespace PhotoCli.Core.Services.Contracts;

public interface IConsoleWriter
{
	// Métodos de escritura básicos
	void Write(string value);
	void WriteMarkup(string value);
	void WriteError(string value); // <-- NUEVO: Para mensajes de error resaltados

	void WriteCommandSummary(string commandName, string sourceFolder, string templateName);

	/// <summary>
	/// Imprime un reporte de validación en formato de tabla usando Spectre.Console.
	/// </summary>
	void WriteTable(IEnumerable<TableColumnConfig> columns, IEnumerable<List<string>> rows, string title); 

	// Métodos de progreso existentes
	void ProgressStart(string name, int? totalCount = null);
	void InProgressItemComplete(string name);
	void ProgressFinish(string name, string? additionalInformation = "");
}
