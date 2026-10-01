using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.2 G-5). A fixed, in-memory window placement: every launch starts from the same placement and nothing
/// is ever persisted, so screenshots are deterministic run after run. Excluded from Release.
/// </summary>
internal sealed class QaInMemoryPlacementStore(WindowPlacementSettings initial) : IWindowPlacementStore
{
    public WindowPlacementSettings Load() => initial;

    public void Save(WindowPlacementSettings settings)
    {
        // No-op: the harness never persists placement.
    }
}
