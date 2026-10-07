using System.Windows.Input;

namespace ServerMonitor.App.ViewModels;

/// <summary>A command whose parameter is a <typeparamref name="T"/>; any other parameter can neither run nor execute.</summary>
public sealed class ParameterCommand<T>(Action<T> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => parameter is T;

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            execute(value);
        }
    }
}
