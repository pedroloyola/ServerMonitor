using System.Windows.Input;
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

    public WindowModeViewModel(IWindowModeCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _coordinator.ModeChanged += OnModeChanged;
        EnterCompactCommand = new RelayCommand(() => _coordinator.SwitchTo(WindowMode.Compact));
        ExitCompactCommand = new RelayCommand(() => _coordinator.SwitchTo(WindowMode.Standard));
        ToggleCommand = new RelayCommand(() => _coordinator.Toggle());
    }

    public ICommand EnterCompactCommand { get; }

    public ICommand ExitCompactCommand { get; }

    public ICommand ToggleCommand { get; }

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

    private void OnModeChanged(object? sender, WindowMode mode)
    {
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(CompactAlwaysOnTop));
    }
}
