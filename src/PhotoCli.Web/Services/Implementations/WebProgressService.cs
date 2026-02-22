using PhotoCli.Core.Services.Contracts.SpectreConsole;

namespace PhotoCli.Web.Services.Implementations;

public class WebProgressService : IProgressService
{
	// Evento para notificar a la UI de Blazor que algo ha cambiado
	public event Action<string, double>? OnProgressChanged;

	public T ExecuteProgress<T>(string title, Func<IProgressContextWrapper, T> action)
	{
		// En la Web, el 'contexto' simplemente envuelve nuestra lógica de notificación
		var wrapper = new WebProgressContextWrapper(this);

		// Ejecutamos la acción (la lógica del Core)
		var result = action(wrapper);

		return result;
	}

	internal void NotifyChange(string description, double value)
	{
		OnProgressChanged?.Invoke(description, value);
	}
}

public class WebProgressContextWrapper(WebProgressService parent) : IProgressContextWrapper
{
	public IProgressTask AddTask(string title, double maxValue = 1.0)
	{
		return new WebProgressTask(title, maxValue, parent);
	}
}

public class WebProgressTask : IProgressTask
{
	private readonly WebProgressService _parent;
	private readonly string _title;
	private double _maxValue;

	public WebProgressTask(string title, double maxValue, WebProgressService parent)
	{
		_title = title;
		_maxValue = maxValue;
		_parent = parent;
	}

	public double MaxValue
	{
		get => _maxValue;
		set { _maxValue = value; }
	}

	public void Increment(double value)
	{
		// Calculamos el porcentaje para enviarlo a la UI
		_parent.NotifyChange(_title, value);
	}

	public void UpdateDescription(string description)
	{
		_parent.NotifyChange(description, 0); // Notificamos el cambio de texto
	}

	public void Stop()
	{
		// Opcional: Notificar que la tarea ha terminado
		_parent.NotifyChange($"{_title} (Completado)", _maxValue);
	}
}
