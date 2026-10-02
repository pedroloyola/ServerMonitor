namespace ServerMonitor.App.ViewModels;

/// <summary>
/// One entry of the History server selector (UI.3): the server's name plus a subtitle built only from
/// data the app already has — the detected OS (omitted when unknown) and the live monitoring status
/// (omitted when the server was never reached). Nothing is invented to fill the second line.
/// While the page loads, the selected entry shows a transient line instead ("A carregar histórico…",
/// Figma 112:16322), because the closed selector renders the item itself.
/// </summary>
public sealed class HistoryServerOptionViewModel : ObservableObject
{
    private string? _transientSubtitle;

    public HistoryServerOptionViewModel(Guid id, string name, string subtitle)
    {
        Id = id;
        Name = name ?? string.Empty;
        BaseSubtitle = subtitle ?? string.Empty;
    }

    public Guid Id { get; }

    public string Name { get; }

    /// <summary>The data-derived line, e.g. "Ubuntu 24.04 · Ligado"; empty when nothing is known.</summary>
    public string BaseSubtitle { get; }

    /// <summary>What the selector shows: the transient (loading) line when set, otherwise <see cref="BaseSubtitle"/>.</summary>
    public string Subtitle => _transientSubtitle ?? BaseSubtitle;

    public bool HasSubtitle => Subtitle.Length > 0;

    public string AutomationName => HasSubtitle ? $"{Name}, {Subtitle}" : Name;

    /// <summary>Sets (or clears with <c>null</c>) the transient second line.</summary>
    public void SetTransientSubtitle(string? text)
    {
        if (string.Equals(_transientSubtitle, text, StringComparison.Ordinal))
        {
            return;
        }

        _transientSubtitle = text;
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasSubtitle));
        OnPropertyChanged(nameof(AutomationName));
    }

    public override string ToString() => Name;
}
