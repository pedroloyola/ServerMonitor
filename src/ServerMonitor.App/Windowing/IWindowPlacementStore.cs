namespace ServerMonitor.App.Windowing;

/// <summary>
/// Loads and saves the window-placement preference. Implementations must be resilient to a missing,
/// malformed, oversized or partially-written file: reads always return usable settings and writes
/// must never throw into the caller's control flow.
/// </summary>
public interface IWindowPlacementStore
{
    WindowPlacementSettings Load();

    void Save(WindowPlacementSettings settings);
}
