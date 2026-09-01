namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>
/// Drives repainting while at least one widget is on screen. The coordinator owns the arm/disarm decision
/// — it is the only thing that knows which widgets the host has shown interest in — and knows nothing
/// about HOW the pump detects change.
/// <para>
/// This exists because the Windows Widgets host is not an update pump: it calls the provider on
/// Create/Activate/ContextChanged and then goes quiet. Without a change source of our own, a widget the
/// user is looking at on an open board never repaints, however fresh <c>widget-state.json</c> becomes
/// (M13 QA-9).
/// </para>
/// </summary>
public interface IWidgetRefreshPump : IDisposable
{
    /// <summary>Start watching for snapshot changes. Idempotent; called when the first widget goes on screen.</summary>
    void Arm();

    /// <summary>Stop watching and cancel pending work. Idempotent; called when the last widget leaves the screen.</summary>
    void Disarm();
}
