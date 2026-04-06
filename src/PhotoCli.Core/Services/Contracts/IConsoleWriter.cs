using PhotoCli.Core.Models.SpectreConsole;
using System.Collections.Generic;

namespace PhotoCli.Core.Services.Contracts;

public interface IConsoleWriter
{
	// Métodos de escritura básicos
	void Write(string value);
	void WriteMarkup(string value);
	void WriteError(string value);

	void WriteCommandSummary(string commandName, string sourceFolder, string templateName);

	/// <summary>
	/// Imprime un reporte de validación en formato de tabla usando Spectre.Console.
	/// </summary>
	void WriteTable(IEnumerable<TableColumnConfig> columns, IEnumerable<List<string>> rows, string title);

	/// <summary>
	/// Muestra una estructura jerárquica de archivos usando un Tree.
	/// </summary>
	void WriteFileTree(string title, string rootPath, IReadOnlyCollection<string> filePaths);

	// Métodos de progreso existentes
	void ProgressStart(string name, int? totalCount = null);
	void InProgressItemComplete(string name);
	void ProgressFinish(string name, string? additionalInformation = "");

	/// <summary>
	/// Escribe un mensaje de estado interactivo en la línea actual, sobrescribiendo el anterior.
	/// </summary>
	void WriteStatusLine(string statusMessage);

	/// <summary>
	/// Limpia el mensaje de estado interactivo y avanza a la siguiente línea, asegurando que el log anterior permanezca.
	/// </summary>
	void ClearStatusLine();

	/// <summary>
	/// Indica si el entorno de consola es interactivo (permite el movimiento del cursor).
	/// </summary>
	bool IsInteractive(); // <-- NUEVO MÉTODO
}
