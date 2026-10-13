using System.Windows.Input;

namespace ServerMonitor.App.ViewModels;

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    // UI.12 C-L8: nothing awaits Execute, so an exception from the command would end the process; it is reported instead.
    // ExecuteAsync still propagates to callers that await it.
    public async void Execute(object? parameter) =>
        await UiFireAndForget.RunAsync(nameof(AsyncRelayCommand), () => ExecuteAsync(parameter));

    /// <summary>The same execution as <see cref="Execute"/>, awaitable (UI.5: the Detail page sequences after it).</summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
