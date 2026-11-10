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

    /// <summary>The visible hint after the label, led by a small gap; empty without a hint.</summary>
    public string HintText { get; private set; } = string.Empty;

    internal void SetText(string label, string? hint)
    {
        Label = label;
        HintText = HintTextOf(hint);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HintText)));
    }

    /// <summary>Pure (unit-tested): the hint shown after the label, an en space ahead of it; nothing without a hint.</summary>
    internal static string HintTextOf(string? hint) => string.IsNullOrWhiteSpace(hint) ? string.Empty : " " + hint;
}
