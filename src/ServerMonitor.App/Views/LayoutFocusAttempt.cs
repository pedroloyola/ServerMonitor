namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7 final c2 (Beacon B-1 / B-3): moving focus to an element that has just become visible. WinUI's <c>Focus()</c> returns
/// false until the element is laid out, so the page tries once at once and then again on the next layout passes - a
/// bounded number of attempts, never a timer or the wall clock. <paramref name="completed"/> runs exactly once: true when
/// the focus landed, false when the attempts ran out (or the attempt was cancelled: never).
/// </summary>
internal sealed class LayoutFocusAttempt(Func<bool> tryFocus, int maxAttempts, Action<bool>? completed)
{
    private int _attempts;

    public bool IsDone { get; private set; }

    public int Attempts => _attempts;

    /// <summary>One attempt; true once the attempt is finished (focused, out of attempts, or cancelled).</summary>
    public bool Step()
    {
        if (IsDone)
        {
            return true;
        }

        _attempts++;
        if (tryFocus())
        {
            Finish(true);
        }
        else if (_attempts >= maxAttempts)
        {
            Finish(false);
        }

        return IsDone;
    }

    /// <summary>A newer focus move replaces this one: nothing more is tried and the completion does not run.</summary>
    public void Cancel() => IsDone = true;

    private void Finish(bool focused)
    {
        IsDone = true;
        completed?.Invoke(focused);
    }
}
