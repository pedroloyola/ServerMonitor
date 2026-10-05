using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.ViewModels;

/// <summary>Derived first run; dismissal and activation suppression last for this process only.</summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly IServerService _servers;
    private readonly INavigationService _navigation;
    private bool _dismissed;
    private volatile bool _activation;
    private bool _visible;
    private int _step = 1;

    public OnboardingViewModel(IServerService servers, INavigationService navigation, DashboardViewModel dashboard)
    {
        _servers = servers;
        _navigation = navigation;
        AddServerCommand = new AsyncRelayCommand(() => FinishAsync(dashboard.AddServerCommand));
        ImportFromSshCommand = new AsyncRelayCommand(() => FinishAsync(dashboard.ImportFromSshCommand));
    }

    public bool IsVisible { get => _visible; private set => SetProperty(ref _visible, value); }
    public int Step { get => _step; private set => SetProperty(ref _step, value); }
    public ServerLoadStatus? LoadStatus { get; private set; }
    public bool IsConfigurationUnavailable => LoadStatus == ServerLoadStatus.Unavailable;
    public AsyncRelayCommand AddServerCommand { get; }
    public AsyncRelayCommand ImportFromSshCommand { get; }

    public async Task OnMainWindowShownAsync(bool normalStart)
    {
        try
        {
            LoadStatus = _servers is IServerLoadStatusSource source
                ? await source.GetLoadStatusAsync() : ServerLoadStatus.Loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LoadStatus = ServerLoadStatus.Unavailable;
        }
        OnPropertyChanged(nameof(LoadStatus));
        OnPropertyChanged(nameof(IsConfigurationUnavailable));
        IsVisible = normalStart && !_activation && !_dismissed && LoadStatus == ServerLoadStatus.NotFound;
    }

    public void RecordActivation() => _activation = true;

    public void SuppressForActivation() { _activation = true; IsVisible = false; }
    public void Next() { if (IsVisible && Step < 3) Step++; }
    public void Back() { if (IsVisible && Step > 1) Step--; }
    public void Dismiss() { _dismissed = true; IsVisible = false; }

    private async Task FinishAsync(System.Windows.Input.ICommand command)
    {
        if (!IsVisible || Step != 3) return;
        Dismiss();
        _navigation.GoToDashboard();
        if (command is AsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync();
        else if (command.CanExecute(null)) command.Execute(null);
    }
}
