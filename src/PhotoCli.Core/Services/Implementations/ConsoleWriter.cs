using PhotoCli.Core.Services.Contracts;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using PhotoCli.Core.Models.SpectreConsole;
using System.IO.Abstractions;

namespace PhotoCli.Core.Services.Implementations;

public class ConsoleWriter : IConsoleWriter
{
	private static readonly object PhotoInprogressLock = new();
	private readonly TextWriter _textWriter;
	private readonly ILogger<ConsoleWriter> _logger;
	private readonly IFileSystem _fileSystem; // Se asume que usas IFileSystem para WriteFileTree

	private string? _previousProgressName;
	private int _progressCompletedCount;
	private int _progressTotalCount;
	private string? _lastStatusMessage;

	public ConsoleWriter(TextWriter textWriter, ILogger<ConsoleWriter> logger)
	{
		_textWriter = textWriter;
		_logger = logger;
		_logger.LogInformation("User interactive console: {IsUserInteractive}", UserInteractive());
	}

	#region Métodos Básicos y de Escritura Avanzada (Spectre.Console)

	public void Write(string value)
	{
		AnsiConsole.MarkupLine(value.EscapeMarkup());
	}

	public void WriteMarkup(string markup)
	{
		AnsiConsole.MarkupLine(markup);
	}

	public void WriteError(string value)
	{
		AnsiConsole.MarkupLine($"[red bold]ERROR:[/] {value.EscapeMarkup()}");
	}

	public void WriteCommandSummary(string commandName, string sourceFolder, string templateName)
	{
		var formattedCommand = commandName.ToUpper().Replace("-", " ");
		var sourceFolderName = sourceFolder;

		var rule = new Rule("[bold]COMMAND SUMMARY[/]");
		rule.Justification = Justify.Center;
		rule.Style = new Style(foreground: Color.Yellow);
		AnsiConsole.Write(rule);

		AnsiConsole.MarkupLine($"[bold]Command:[/]\t[green]{formattedCommand.EscapeMarkup()}[/]");

		var path = new TextPath(sourceFolderName);
		path.RootStyle = new Style(foreground: Color.Red);
		path.SeparatorStyle = new Style(foreground: Color.Green);
		path.StemStyle = new Style(foreground: Color.Blue);
		path.LeafStyle = new Style(foreground: Color.Yellow);

		AnsiConsole.Markup($"[bold]Source Dir:[/]\t");
		AnsiConsole.Write(path);
		AnsiConsole.WriteLine();

		if (!string.IsNullOrWhiteSpace(templateName))
		{
			AnsiConsole.MarkupLine($"[bold]Template:[/]\t[cyan]{templateName.EscapeMarkup()}[/]");
		}

		AnsiConsole.Write(new Rule().RuleStyle(new Style(foreground: Color.Yellow)));
	}

	public void WriteTable(IEnumerable<TableColumnConfig> columns, IEnumerable<List<string>> rows, string title)
	{
		var table = new Table()
			.Title($"[bold underline yellow]{title}[/]")
			.Border(TableBorder.Square)
			.BorderColor(Color.Grey)
			.ShowRowSeparators()
			.Expand();

		foreach (var config in columns)
		{
			var column = new TableColumn($"[bold blue]{config.HeaderText}[/]");

			if (config.NoWrap)
			{
				column.NoWrap();
			}

			if (config.Width.HasValue)
			{
				column.Width(config.Width.Value);
			}

			table.AddColumn(column);
		}

		foreach (var rowData in rows)
		{
			table.AddRow(rowData.ToArray());
		}

		AnsiConsole.Write(table);
	}

	public void WriteFileTree(string title, string rootPath, IReadOnlyCollection<string> filePaths)
	{
		var tree = new Tree($"[bold brightwhite]{title} ({rootPath})[/]");
		var nodeMap = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

		foreach (var fullPath in filePaths)
		{
			var relativePath = Path.GetRelativePath(rootPath, fullPath);
			var parts = relativePath.Split(Path.DirectorySeparatorChar);
			object currentContainer = tree;
			var currentRelativePath = string.Empty;

			for (int i = 0; i < parts.Length; i++)
			{
				var part = parts[i];
				var isFile = (i == parts.Length - 1) && !string.IsNullOrEmpty(Path.GetExtension(part));

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
					if (currentContainer is TreeNode node)
					{
						node.AddNode($"[green]{part}[/]");
					}
					else
					{
						tree.AddNode($"[green]{part}[/]");
					}
				}
				else
				{
					if (nodeMap.TryGetValue(currentRelativePath, out var existingNode))
					{
						currentContainer = existingNode;
					}
					else
					{
						TreeNode folderNode;

						if (currentContainer is TreeNode parentNode)
						{
							folderNode = parentNode.AddNode($"[yellow]{part}/[/]");
						}
						else
						{
							folderNode = tree.AddNode($"[yellow]{part}/[/]");
						}

						nodeMap.Add(currentRelativePath, folderNode);
						currentContainer = folderNode;
					}
				}
			}
		}

		AnsiConsole.Write(tree);
	}


	#endregion

	#region Métodos de Progreso Originales

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
			AnsiConsole.MarkupLine($"[grey]{name}: {(float)_progressCompletedCount / _progressTotalCount:0%} - ({_progressCompletedCount}/{_progressTotalCount})[/]");
			_previousProgressName = name;
		}
	}

	public void ProgressFinish(string name, string? additionalInformation = "")
	{
		_progressCompletedCount = 0;
		TryToClearConsoleLastLine(name);
		AnsiConsole.MarkupLine($"[bold green]{name}: finished. {additionalInformation.EscapeMarkup()}[/]");
		_logger.LogInformation("Progress {ProgressName} finished", name);
		_previousProgressName = name;
	}

	#endregion

	#region Métodos de Status Line (Para Paralelismo)

	public void WriteStatusLine(string statusMessage)
	{
		lock (PhotoInprogressLock)
		{
			// 1. Limpiamos cualquier cosa anterior (progreso o status anterior)
			TryToClearConsoleLastLine(null);

			if (UserInteractive())
			{
				// 2. Usar AnsiConsole.Markup para asegurar el parsing de colores y la escritura sin salto de línea
				AnsiConsole.Markup(statusMessage.EscapeMarkup());
			}
			else
			{
				_logger.LogTrace("Status: {Status}", statusMessage);
			}

			// 3. Guardar el nuevo mensaje.
			_lastStatusMessage = statusMessage;
			_previousProgressName = null;
		}
	}

	public void ClearStatusLine()
	{
		lock (PhotoInprogressLock)
		{
			if (_lastStatusMessage != null || _previousProgressName != null)
			{
				// 1. Limpiamos la línea actual (donde está el status)
				TryToClearConsoleLastLine(null);

				// 2. Escribimos una línea vacía para garantizar que la siguiente escritura estática 
				//    comience en una nueva línea limpia y no sobre el log inicial.
				AnsiConsole.MarkupLine(string.Empty);

				_lastStatusMessage = null;
				_previousProgressName = null;
			}
		}
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

	private void TryToClearConsoleLastLine(string? name)
	{
		if (!UserInteractive())
		{
			_logger.LogTrace("Console is not user interactive, skip clearing console");
			return;
		}

		if (name != null)
		{
			if (_previousProgressName != name) return;
		}
		else
		{
			if (_lastStatusMessage == null && _previousProgressName == null) return;
		}

		if (Console.CursorTop > 0)
			Console.SetCursorPosition(0, Console.CursorTop);

		if (Console.WindowWidth > 0)
			_textWriter.Write(new string(' ', Console.WindowWidth));

		if (Console.CursorTop > 0)
			Console.SetCursorPosition(0, Console.CursorTop - 1);
	}

	private bool UserInteractive()
	{
		return Environment.UserInteractive && !Environment.CurrentDirectory.Contains("tests", StringComparison.OrdinalIgnoreCase);
	}

	public bool IsInteractive() // <-- NUEVO MÉTODO IMPLEMENTADO
	{
		return UserInteractive();
	}

	#endregion
}
