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

    public WindowFrame Frame { get; set; } = WindowFrame.None;

    public int ApplyBoundsCount { get; private set; }

    public bool IsAttached => true;

    public WindowPlacement? GetPlacement() => new(CurrentBounds, CurrentDpi);

    public IReadOnlyList<DisplayWorkArea> GetDisplays() => Displays;

    /// <summary>
    /// c4 B-7, as measured on WinAppSDK: a MoveAndResize of a MAXIMIZED window moves it but keeps it Maximized and leaves
    /// the restored rectangle untouched; on a restored window the restored rectangle follows the bounds.
    /// </summary>
    public void ApplyBounds(WindowBounds bounds)
    {
        ApplyBoundsCount++;
        Calls.Add($"ApplyBounds {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
        CurrentBounds = bounds;
        if (!IsMaximized)
        {
            RestoredBounds = bounds;
        }
    }

    public void ConfigurePresenter(WindowMode mode, WindowSizeConstraints constraints)
    {
        Calls.Add($"ConfigurePresenter {mode}");
        LastPresenterMode = mode;
    }

    public WindowFrame GetFrame() => Frame;

    public void SetAlwaysOnTop(bool enabled) => TopmostMutations.Add(enabled);

    /// <summary>c4 B-7: every presenter/bounds call, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>UI.8 c2 B-2: what the presenter reports; set by a test to simulate a native maximize.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>The bounds the presenter returns to on <see cref="Restore"/> (Windows keeps the restored rectangle).</summary>
    public WindowBounds? RestoredBounds { get; set; }

    /// <summary>Where a maximized window sits (the work area plus the hidden frame, as measured: −8,−8 1936×1096).</summary>
    public WindowBounds MaximizedBounds { get; set; } = new(-8, -8, 1936, 1096);

    public int RestoreCount { get; private set; }

    public int MaximizeCount { get; private set; }

    public void Restore()
    {
        RestoreCount++;
        Calls.Add("Restore");
        IsMaximized = false;
        if (RestoredBounds is { } restored)
        {
            CurrentBounds = restored;
        }
    }

    /// <summary>The app's own maximize, and the user's (the caption button) in a test.</summary>
    public void Maximize()
    {
        MaximizeCount++;
        Calls.Add("Maximize");
        if (!IsMaximized)
        {
            RestoredBounds = CurrentBounds;
        }

        IsMaximized = true;
        CurrentBounds = MaximizedBounds;
    }
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
