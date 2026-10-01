namespace ServerMonitor.App.ViewModels;

/// <summary>
/// One entry of the History server selector (UI.3): the server's name plus a subtitle built only from
/// data the app already has — the detected OS (omitted when unknown) and the live monitoring status
/// (omitted when the server was never reached). Nothing is invented to fill the second line.
/// </summary>
public sealed class HistoryServerOptionViewModel
{
    public HistoryServerOptionViewModel(Guid id, string name, string subtitle)
    {
        Id = id;
        Name = name ?? string.Empty;
        Subtitle = subtitle ?? string.Empty;
        AutomationName = HasSubtitle ? $"{Name}, {Subtitle}" : Name;
    }

    public Guid Id { get; }

    public string Name { get; }

    /// <summary>e.g. "Ubuntu 24.04 · Ligado"; empty when nothing is known.</summary>
    public string Subtitle { get; }

    public bool HasSubtitle => Subtitle.Length > 0;

    public string AutomationName { get; }

    public override string ToString() => Name;
}
