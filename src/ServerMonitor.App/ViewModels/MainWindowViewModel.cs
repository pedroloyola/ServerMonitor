namespace ServerMonitor.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private bool _isPaneOpen;

    public bool IsPaneOpen
    {
        get => _isPaneOpen;
        set => SetProperty(ref _isPaneOpen, value);
    }
}
