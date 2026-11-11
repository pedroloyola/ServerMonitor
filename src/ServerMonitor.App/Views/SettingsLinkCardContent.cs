using System.ComponentModel;

namespace ServerMonitor.App.Views;

/// <summary>
/// M15.4a r4: what a composition-supplied Settings card shows, drawn by <c>SettingsLinkCardTemplate</c> in
/// <c>SettingsPage.xaml</c> (so its icon style follows theme and high-contrast changes like the built-in cards'). The
/// label and hint are read again through the shell (its try/catch, its fallbacks) and pushed here.
/// </summary>
public sealed partial class SettingsLinkCardContent : INotifyPropertyChanged
{
    public SettingsLinkCardContent(string iconPathData) => IconPathData = iconPathData;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IconPathData { get; }

    public string Label { get; private set; } = string.Empty;

    /// <summary>The visible hint after the label, led by the Figma's 12 px gap; empty without a hint.</summary>
    public string HintText { get; private set; } = string.Empty;

    internal void SetText(string label, string? hint)
    {
        Label = label;
        HintText = HintTextOf(hint);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HintText)));
    }

    /// <summary>
    /// Pure (unit-tested): the hint shown after the label, led by an em space; nothing without a hint. The hint's run is
    /// 12 px (<c>SaFontSizeCaption</c>), and an em space is one em wide, so the gap is the Figma's 12 px. A run has no
    /// margin, and label and hint stay in one text block so they trim together with one ellipsis.
    /// </summary>
    internal static string HintTextOf(string? hint) => string.IsNullOrWhiteSpace(hint) ? string.Empty : EmSpace + hint;

    /// <summary>One em wide: 12 px in the hint's 12 px run.</summary>
    internal const string EmSpace = "\u2003";
}
