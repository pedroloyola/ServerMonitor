namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.12 C-L8 (Cortex UI.12A): the last line for UI work nobody awaits - <c>async void</c> event handlers and
/// <see cref="AsyncRelayCommand.Execute"/>. An exception escaping one of those is re-raised on the UI dispatcher and ends
/// the process. Here it is reported (operation name and exception TYPE only - messages can carry host names or paths)
/// and the caller's error state is applied instead. Cancellation is the normal end of superseded work and is not a failure.
/// Pure and WinUI-free.
/// </summary>
internal static class UiFireAndForget
{
    /// <summary>Where failures are reported; the composition root sets it once to its logger. Null: not reported.</summary>
    internal static Action<string, string>? Report { get; set; }

    public static async Task RunAsync(string operation, Func<Task> work, Action? onFailed = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report?.Invoke(operation, exception.GetType().Name);
            onFailed?.Invoke();
        }
    }
}
