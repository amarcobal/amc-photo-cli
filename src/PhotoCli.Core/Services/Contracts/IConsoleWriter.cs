namespace PhotoCli.Core.Services.Contracts;

public interface IConsoleWriter
{
	// Métodos de escritura básicos
	void Write(string value);
	void WriteError(string value); // <-- NUEVO: Para mensajes de error resaltados

	/// <summary>
	/// Imprime un reporte de validación en formato de tabla usando Spectre.Console.
	/// </summary>
	void WriteValidationTable(IEnumerable<string> headers, IEnumerable<List<string>> rows, string title); // <-- NUEVO

	// Métodos de progreso existentes
	void ProgressStart(string name, int? totalCount = null);
	void InProgressItemComplete(string name);
	void ProgressFinish(string name, string? additionalInformation = "");
}
