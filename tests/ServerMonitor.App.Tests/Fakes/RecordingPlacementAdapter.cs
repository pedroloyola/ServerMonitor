using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// A placement adapter that records every topmost MUTATION, in order. Coverage O needs the behavioural
/// half of the proof: not "the API is not referenced" — a rename would satisfy that — but "the lifecycle
/// and navigation paths never change the flag", with a control showing Compact still does.
/// </summary>
internal sealed class RecordingPlacementAdapter : IWindowPlacementAdapter
{
    public List<bool> TopmostMutations { get; } = new();

    public WindowBounds CurrentBounds { get; set; } = new(100, 100, 780, 760);

    public int CurrentDpi { get; set; } = 100;

    public IReadOnlyList<DisplayWorkArea> Displays { get; set; } = [new(0, 0, 1920, 1040, 100)];

    public WindowMode LastPresenterMode { get; private set; }

    public int ApplyBoundsCount { get; private set; }

    public bool IsAttached => true;

    public WindowPlacement? GetPlacement() => new(CurrentBounds, CurrentDpi);

    public IReadOnlyList<DisplayWorkArea> GetDisplays() => Displays;

    public void ApplyBounds(WindowBounds bounds)
    {
        ApplyBoundsCount++;
        CurrentBounds = bounds;
    }

    public void ConfigurePresenter(WindowMode mode, WindowSizeConstraints constraints) =>
        LastPresenterMode = mode;

    public void SetAlwaysOnTop(bool enabled) => TopmostMutations.Add(enabled);
}

/// <summary>Placement store over a fixed settings value.</summary>
internal sealed class FakeWindowPlacementStore(WindowPlacementSettings? initial = null) : IWindowPlacementStore
{
    public WindowPlacementSettings Saved { get; private set; } = initial ?? WindowPlacementSettings.Default;

    public int SaveCount { get; private set; }

    public WindowPlacementSettings Load() => Saved;

    public void Save(WindowPlacementSettings settings)
    {
        SaveCount++;
        Saved = settings;
    }
}
