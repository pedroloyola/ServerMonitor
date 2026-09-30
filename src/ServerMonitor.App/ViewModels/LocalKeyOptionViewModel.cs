using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// One entry of the editor's key selector (M14.5): a default key found in <c>.ssh</c>, or the trailing
/// "browse for another file" entry (<see cref="Key"/> is null), which opens the existing file picker.
/// </summary>
public sealed class LocalKeyOptionViewModel
{
    public LocalKeyOptionViewModel(LocalSshKey? key, string label)
    {
        Key = key;
        Label = label;
    }

    public LocalSshKey? Key { get; }

    public string Label { get; }

    public bool IsBrowse => Key is null;

    public override string ToString() => Label;
}
