using System.Windows.Input;

namespace ServerMonitor.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private bool _isAddServerNoticeOpen;

    public DashboardViewModel()
    {
        AddServerCommand = new RelayCommand(() => IsAddServerNoticeOpen = true);
    }

    public ICommand AddServerCommand { get; }

    public bool IsAddServerNoticeOpen
    {
        get => _isAddServerNoticeOpen;
        set => SetProperty(ref _isAddServerNoticeOpen, value);
    }
}
