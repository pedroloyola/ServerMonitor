using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// The one ViewModel that legitimately knows about window mode. Server-presentation ViewModels stay
/// mode-agnostic (see OWNERSHIP); this thin wrapper over <see cref="IWindowModeCoordinator"/> backs
/// the Standard "compact mode" entry, the compact widget's expand affordance, and the always-on-top
/// preference, keeping window-lifecycle logic out of the code-behind and the dashboard VM.
/// </summary>
public sealed class WindowModeViewModel : ObservableObject, IDisposable
{
    private readonly IWindowModeCoordinator _coordinator;
    private readonly IApplicationWindowController? _windowController;
    private readonly DashboardViewModel? _dashboard;

    public WindowModeViewModel(
        IWindowModeCoordinator coordinator,
        IApplicationWindowController? windowController = null,
        DashboardViewModel? dashboard = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _windowController = windowController;
        _dashboard = dashboard;
        _coordinator.ModeChanged += OnModeChanged;
        EnterCompactCommand = new RelayCommand(() => _coordinator.SwitchTo(WindowMode.Compact));
        ExitCompactCommand = new RelayCommand(() => _coordinator.SwitchTo(WindowMode.Standard));
        ToggleCommand = new RelayCommand(() => _coordinator.Toggle());
        OpenServerDetailCommand = new ParameterCommand<Guid>(serverId =>
            InStandard(dashboard => dashboard.OpenServerDetail(serverId, ServerDetailOrigin.Overview)));
        AddServerCommand = new RelayCommand(() => InStandard(dashboard => dashboard.AddServerCommand.Execute(null)));
        ManageHiddenServersCommand = new RelayCommand(() => InStandard(dashboard => dashboard.RestoreHiddenServersCommand.Execute(null)));
    }

    public ICommand EnterCompactCommand { get; }

    public ICommand ExitCompactCommand { get; }

    public ICommand ToggleCommand { get; }

    /// <summary>
    /// UI.8 D-UI8-7 / RC-3: a Compact row opens that server's Detail. The window first leaves Compact (the Detail only
    /// exists in Standard), THEN the dashboard's own command runs - so the UI.7 exit guard runs once, in Standard, where its
    /// "Descartar alterações?" dialog fits. Back from the Detail goes to the Visão geral (origin Overview), not to Compact.
    /// </summary>
    public ICommand OpenServerDetailCommand { get; }

    /// <summary>UI.8 D-UI8-8: the empty state's "Adicionar servidor" - Standard first, then the existing Add (guarded).</summary>
    public ICommand AddServerCommand { get; }

    /// <summary>
    /// UI.8 R-8: the all-hidden state's "Gerir servidores ocultos" - Standard first, then the existing action (Definições ›
    /// Dados e servidores, guarded).
    /// </summary>
    public ICommand ManageHiddenServersCommand { get; }

    public bool IsCompact => _coordinator.CurrentMode == WindowMode.Compact;

    public bool CompactAlwaysOnTop
    {
        get => _coordinator.CompactAlwaysOnTop;
        set
        {
            if (_coordinator.CompactAlwaysOnTop == value)
            {
                return;
            }

            _coordinator.SetCompactAlwaysOnTop(value);
            OnPropertyChanged();
        }
    }

    public void Dispose() => _coordinator.ModeChanged -= OnModeChanged;

    // Order is the contract (RC-3): surface in Standard, then run the destination's existing (guarded) command.
    private void InStandard(Action<DashboardViewModel> command)
    {
        if (_windowController is null || _dashboard is null)
        {
            return;
        }

        _windowController.RestoreAndActivateStandard();
        command(_dashboard);
    }

    private void OnModeChanged(object? sender, WindowMode mode)
    {
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(CompactAlwaysOnTop));
    }
}
