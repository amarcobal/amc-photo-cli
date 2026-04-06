using PhotoCli.Core.Services.Contracts;
using PhotoCli.Core.Models.SpectreConsole;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace PhotoCli.Web.Services.Implementations;

public class WebConsoleWriter : IConsoleWriter
{
	private readonly ILogger<WebConsoleWriter> _logger;

	public WebConsoleWriter(ILogger<WebConsoleWriter> logger)
	{
		_logger = logger;
	}

	public void Write(string value) => _logger.LogInformation(value);
	public void WriteMarkup(string value) => _logger.LogInformation(value);
	public void WriteError(string value) => _logger.LogError(value);

	public void WriteCommandSummary(string commandName, string sourceFolder, string templateName)
	{
		_logger.LogInformation("Comando: {Command} | Origen: {Source} | Plantilla: {Template}", commandName, sourceFolder, templateName);
	}

	public void WriteTable(IEnumerable<TableColumnConfig> columns, IEnumerable<List<string>> rows, string title)
	{
		var columnConfigs = columns.ToList();
		_logger.LogInformation("--- TABLA: {Title} ---", title);

		// Corregido: Usamos .ColumnName en lugar de .Header
		var colNames = string.Join(" | ", columnConfigs.Select(c => c.HeaderText));
		_logger.LogInformation(colNames);

		foreach (var row in rows)
		{
			_logger.LogInformation(string.Join(" | ", row));
		}
	}

	public void WriteFileTree(string title, string rootPath, IReadOnlyCollection<string> filePaths)
	{
		_logger.LogInformation("--- ÁRBOL DE ARCHIVOS: {Title} ---", title);
		_logger.LogInformation("Root: {Root}", rootPath);
		foreach (var path in filePaths)
			_logger.LogInformation("  - {Path}", path);
	}

	public void ProgressStart(string name, int? totalCount = null) => _logger.LogInformation("Progreso iniciado: {Name}", name);
	public void InProgressItemComplete(string name) { }
	public void ProgressFinish(string name, string? additionalInformation = "") => _logger.LogInformation("Progreso finalizado: {Name}. {Info}", name, additionalInformation);

	public void WriteStatusLine(string statusMessage) => _logger.LogInformation("Status: {Status}", statusMessage);
	public void ClearStatusLine() { }

	public bool IsInteractive() => false; // En la Web no hay consola interactiva
}
