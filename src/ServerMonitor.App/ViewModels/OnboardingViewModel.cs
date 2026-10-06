using Microsoft.Extensions.Logging;
using ServerMonitor.App.Windowing;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.ViewModels;

/// <summary>Derived first run; dismissal and activation suppression last for this process only.</summary>
public sealed class OnboardingViewModel : ObservableObject, IDisposable
{
    private readonly IServerLoadStatusSource _servers;
    private readonly INavigationService _navigation;
    private bool _dismissed;
    private readonly ActivationLatch _activation;
    private readonly ILogger<OnboardingViewModel> _logger;
    private bool _normalStart;
    private bool _windowShown;
    private bool _standard = true;
    private bool _visible;
    private int _step = 1;

    public OnboardingViewModel(IServerLoadStatusSource servers, INavigationService navigation, OnboardingActions actions,
        ActivationLatch activation, ILogger<OnboardingViewModel> logger)
    {
        _servers = servers;
        _navigation = navigation;
        _activation = activation;
        _logger = logger;
        _navigation.Navigated += OnNavigated;
        AddServerCommand = new AsyncRelayCommand(() => FinishAsync(actions.AddServerCommand));
        ImportFromSshCommand = new AsyncRelayCommand(() => FinishAsync(actions.ImportFromSshCommand));
    }

    public event Func<Task>? PreparingEditor;

    public bool IsVisible { get => _visible; private set => SetProperty(ref _visible, value); }
    public int Step { get => _step; private set => SetProperty(ref _step, value); }
    public ServerLoadStatus? LoadStatus { get; private set; }
    public bool IsConfigurationUnavailable => LoadStatus == ServerLoadStatus.Unavailable;
    public AsyncRelayCommand AddServerCommand { get; }
    public AsyncRelayCommand ImportFromSshCommand { get; }

    public async Task OnMainWindowShownAsync(bool normalStart)
    {
        _windowShown = true;
        _normalStart = normalStart;
        try
        {
            LoadStatus = await _servers.GetLoadStatusAsync();
        }
        catch (Exception exception)
        {
            LoadStatus = ServerLoadStatus.Unavailable;
            _logger.LogWarning("Onboarding configuration diagnosis failed. Type: {Type}.", exception.GetType().Name);
        }
        OnPropertyChanged(nameof(LoadStatus));
        OnPropertyChanged(nameof(IsConfigurationUnavailable));
        UpdateVisibility();
    }

    private void UpdateVisibility() => IsVisible = _normalStart && _standard && !_activation.IsRecorded && !_dismissed
        && _navigation.CurrentDestination == NavigationDestination.Overview && LoadStatus == ServerLoadStatus.NotFound;

    public void SetWindowMode(WindowMode mode)
    {
        _standard = mode == WindowMode.Standard;
        UpdateVisibility();
    }

    private void OnNavigated(object? sender, EventArgs args)
    {
        if (_windowShown && _navigation.CurrentDestination != NavigationDestination.Overview) Dismiss();
    }

    public void Dispose()
    {
        _navigation.Navigated -= OnNavigated;
        Dismiss();
    }

    public void RecordActivation() => _activation.Record();
    public void SuppressForActivation() { _activation.Record(); IsVisible = false; }
    public void Next() { if (IsVisible && Step < 3) Step++; }
    public void Back() { if (IsVisible && Step > 1) Step--; }
    public void Dismiss() { _dismissed = true; IsVisible = false; }

    private async Task FinishAsync(System.Windows.Input.ICommand command)
    {
        if (!IsVisible || Step != 3) return;
        Dismiss();
        _navigation.GoToDashboard();
        if (PreparingEditor is { } prepare)
            foreach (Func<Task> handler in prepare.GetInvocationList()) await handler();
        if (command is AsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync();
        else if (command.CanExecute(null)) command.Execute(null);
    }
}
