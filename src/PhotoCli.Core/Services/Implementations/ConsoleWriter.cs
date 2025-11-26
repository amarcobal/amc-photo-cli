using PhotoCli.Core.Services.Contracts;
using Spectre.Console; // Añadido para usar los componentes avanzados de CLI
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models.SpectreConsole;

namespace PhotoCli.Core.Services.Implementations;

public class ConsoleWriter : IConsoleWriter
{
	private static readonly object PhotoInprogressLock = new();
	private readonly TextWriter _textWriter;
	private readonly ILogger<ConsoleWriter> _logger;
	private string? _previousProgressName;
	private int _progressCompletedCount;
	private int _progressTotalCount;

	public ConsoleWriter(TextWriter textWriter, ILogger<ConsoleWriter> logger)
	{
		_textWriter = textWriter;
		_logger = logger;
		_logger.LogInformation("User interactive console: {IsUserInteractive}", UserInteractive());
	}

	#region Métodos Básicos y de Escritura Avanzada (Spectre.Console)

	public void Write(string value)
	{
		// Para la escritura simple, usamos Spectre.Console para renderizar el marcado.
		AnsiConsole.MarkupLine(value.EscapeMarkup());
	}

	public void WriteMarkup(string markup)
	{
		AnsiConsole.MarkupLine(markup);
	}

	public void WriteError(string value)
	{
		// Implementación para mensajes de error estilizados
		AnsiConsole.MarkupLine($"[red bold]ERROR:[/] {value.EscapeMarkup()}");
	}

	/// <summary>
	/// Imprime un resumen del comando ejecutado al inicio.
	/// Es dinámico y solo muestra el template si está presente.
	/// </summary>
	public void WriteCommandSummary(string commandName, string sourceFolder, string templateName)
	{
		// Limpiamos y estandarizamos el nombre del comando
		var formattedCommand = commandName.ToUpper().Replace("-", " ");

		// Usamos la ruta simplificada (solo el nombre de la carpeta) para un look más limpio
		var sourceFolderName = new DirectoryInfo(sourceFolder).Name;

		AnsiConsole.MarkupLine($"[bold yellow]---[/] [bold]COMMAND SUMMARY[/] [bold yellow]----------------------------------------------------------------[/]");
		AnsiConsole.MarkupLine($"[bold]Command:[/]\t [green]{formattedCommand.EscapeMarkup()}[/]");
		AnsiConsole.MarkupLine($"[bold]Source Dir:[/]\t [blue]{sourceFolderName.EscapeMarkup()}[/]");

		// El template es opcional, solo se muestra si se proporciona
		if (!string.IsNullOrWhiteSpace(templateName))
		{
			AnsiConsole.MarkupLine($"[bold]Template:[/]\t [cyan]{templateName.EscapeMarkup()}[/]");
		}

		AnsiConsole.MarkupLine($"[bold yellow]-----------------------------------------------------------------[/]\n");
	}

	public void WriteTable(IEnumerable<TableColumnConfig> columns, IEnumerable<List<string>> rows, string title)
	{
		var table = new Table()
			.Title($"[bold underline yellow]{title}[/]")
			.Border(TableBorder.Square)
			.BorderColor(Color.Grey)
			.ShowRowSeparators()
			.Expand(); // Expande la tabla para usar todo el ancho disponible

		// --- LÓGICA DE CONFIGURACIÓN DE COLUMNAS ---

		foreach (var config in columns)
		{
			// 1. Crear el objeto de columna con el texto del encabezado
			var column = new TableColumn($"[bold blue]{config.HeaderText}[/]");

			// 2. Aplicar NoWrap si está configurado
			if (config.NoWrap)
			{
				column.NoWrap();
			}

			// 3. Aplicar Width si está configurado
			if (config.Width.HasValue)
			{
				column.Width(config.Width.Value);
			}

			// 4. Añadir la columna configurada a la tabla
			table.AddColumn(column);
		}

		// Añadir las filas
		foreach (var rowData in rows)
		{
			table.AddRow(rowData.ToArray());
		}

		AnsiConsole.Write(table);
	}

	/// <summary>
	/// Muestra una estructura jerárquica de archivos usando un Tree.
	/// </summary>
	// ... (Resto del método WriteFileTree se mantiene igual) ...
	public void WriteFileTree(string title, string rootPath, IReadOnlyCollection<string> filePaths)
	{
		// El contenedor principal es de tipo Tree.
		var tree = new Tree($"[bold brightwhite]{title} ({rootPath})[/]");

		// El mapa DEBE contener instancias de TreeNode
		var nodeMap = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

		foreach (var fullPath in filePaths)
		{
			var relativePath = Path.GetRelativePath(rootPath, fullPath);
			var parts = relativePath.Split(Path.DirectorySeparatorChar);

			// currentContainer puede ser o bien el Tree (para la primera carpeta) o un TreeNode
			// Reiniciamos el contenedor para cada archivo
			object currentContainer = tree;

			var currentRelativePath = string.Empty;

			for (int i = 0; i < parts.Length; i++)
			{
				var part = parts[i];
				var isFile = (i == parts.Length - 1) && !string.IsNullOrEmpty(Path.GetExtension(part));

				// Construcción de la clave de la carpeta
				if (i > 0)
				{
					currentRelativePath = Path.Combine(currentRelativePath, part);
				}
				else
				{
					currentRelativePath = part;
				}

				if (isFile)
				{
					// Es un archivo, lo añadimos al contenedor actual (que será un TreeNode)
					if (currentContainer is TreeNode node)
					{
						node.AddNode($"[green]{part}[/]");
					}
					else
					{
						// Esto solo ocurriría si el archivo está directamente en la raíz y rootPath es ".", pero es seguro
						tree.AddNode($"[green]{part}[/]");
					}
				}
				else
				{
					// Es una carpeta
					if (nodeMap.TryGetValue(currentRelativePath, out var existingNode))
					{
						// La carpeta ya existe, moverse a ella
						currentContainer = existingNode;
					}
					else
					{
						// La carpeta no existe, crearla y añadirla al contenedor padre
						TreeNode folderNode;

						if (currentContainer is TreeNode parentNode)
						{
							folderNode = parentNode.AddNode($"[yellow]{part}/[/]");
						}
						else // Si currentContainer es el Tree (raíz)
						{
							// Esta es la primera carpeta de la ruta.
							folderNode = tree.AddNode($"[yellow]{part}/[/]");
						}

						// Añadir el nuevo nodo al mapa para su posterior búsqueda
						nodeMap.Add(currentRelativePath, folderNode);

						// Moverse al nodo de la carpeta recién creada
						currentContainer = folderNode;
					}
				}
			}
		}

		AnsiConsole.Write(tree);
	}


	#endregion

	#region Métodos de Progreso Originales (Implementados usando TextWriter/Logger y Spectre.Console)

	public void ProgressStart(string name, int? totalCount = null)
	{
		_textWriter.WriteLine($"{name}: started.");
		_logger.LogInformation("Progress {ProgressName} started", name);
		_previousProgressName = name;
		if (totalCount != null)
			_progressTotalCount = totalCount.Value;
	}

	public void InProgressItemComplete(string name)
	{
		Interlocked.Increment(ref _progressCompletedCount);
		_logger.LogTrace("Progress name {ProgressName} count: {Current}/{Total}", name, _progressCompletedCount, _progressTotalCount);
		lock (PhotoInprogressLock)
		{
			TryToClearConsoleLastLine(name);
			// Usamos AnsiConsole para asegurar que el porcentaje se escriba en el stream correcto si es interactivo
			AnsiConsole.MarkupLine($"[grey]{name}: {(float)_progressCompletedCount / _progressTotalCount:0%} - ({_progressCompletedCount}/{_progressTotalCount})[/]");
			_previousProgressName = name;
		}
	}

	public void ProgressFinish(string name, string? additionalInformation = "")
	{
		_progressCompletedCount = 0;
		TryToClearConsoleLastLine(name);
		// Usamos SC para asegurar la escritura completa y el color de finalización
		AnsiConsole.MarkupLine($"[bold green]{name}: finished. {additionalInformation.EscapeMarkup()}[/]");
		_logger.LogInformation("Progress {ProgressName} finished", name);
		_previousProgressName = name;
	}

	#endregion

	#region Lógica de Limpieza de Consola Original (Métodos Auxiliares)

	private void CoverAllLine(string toWrite)
	{
		if (!UserInteractive())
		{
			_logger.LogTrace("Console is not user interactive, directly writing to console");
			_textWriter.WriteLine(toWrite);
			return;
		}

		if (Console.WindowWidth > 0)
			_textWriter.WriteLine(toWrite + new string(' ', Console.WindowWidth - toWrite.Length));
		else
			_textWriter.WriteLine(toWrite);
	}

	private void TryToClearConsoleLastLine(string name)
	{
		if (!UserInteractive())
		{
			_logger.LogTrace("Console is not user interactive, skip clearing console");
			return;
		}

		if (_previousProgressName != name)
			return;

		// Lógica de manipulación del cursor
		if (Console.CursorTop > 0)
			Console.SetCursorPosition(0, Console.CursorTop);
		if (Console.WindowWidth > 0)
			_textWriter.Write(new string(' ', Console.WindowWidth));
		if (Console.CursorTop > 0)
			Console.SetCursorPosition(0, Console.CursorTop - 1);
	}

	private bool UserInteractive()
	{
		// Lógica para determinar si la consola es interactiva (y no un entorno de prueba)
		return Environment.UserInteractive && !Environment.CurrentDirectory.Contains("tests", StringComparison.OrdinalIgnoreCase);
	}

	#endregion
}
