using System.ComponentModel;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

public sealed class WindowModeViewModelTests
{
    [Fact]
    public void EnterAndExitCommands_DriveTheCoordinator()
    {
        var coordinator = new FakeCoordinator();
        var viewModel = new WindowModeViewModel(coordinator);

        viewModel.EnterCompactCommand.Execute(null);
        Assert.Equal(WindowMode.Compact, coordinator.LastSwitch);

        viewModel.ExitCompactCommand.Execute(null);
        Assert.Equal(WindowMode.Standard, coordinator.LastSwitch);

        viewModel.ToggleCommand.Execute(null);
        Assert.Equal(1, coordinator.ToggleCount);
    }

    [Fact]
    public void IsCompact_TracksTheCoordinatorMode()
    {
        var coordinator = new FakeCoordinator();
        var viewModel = new WindowModeViewModel(coordinator);
        Assert.False(viewModel.IsCompact);

        coordinator.RaiseModeChanged(WindowMode.Compact);

        Assert.True(viewModel.IsCompact);
    }

    [Fact]
    public void SettingAlwaysOnTop_DelegatesAndRaisesChange()
    {
        var coordinator = new FakeCoordinator();
        var viewModel = new WindowModeViewModel(coordinator);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.CompactAlwaysOnTop = true;

        Assert.True(coordinator.CompactAlwaysOnTop);
        Assert.Contains(nameof(WindowModeViewModel.CompactAlwaysOnTop), raised);
    }

    [Fact]
    public void ModeChanged_RaisesPropertyNotifications()
    {
        var coordinator = new FakeCoordinator();
        var viewModel = new WindowModeViewModel(coordinator);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        coordinator.RaiseModeChanged(WindowMode.Compact);

        Assert.Contains(nameof(WindowModeViewModel.IsCompact), raised);
    }

    private sealed class FakeCoordinator : IWindowModeCoordinator
    {
        public WindowMode CurrentMode { get; private set; } = WindowMode.Standard;

        public bool CompactAlwaysOnTop { get; private set; }

        public bool IsApplyingBounds => false;

        public WindowMode LastSwitch { get; private set; } = WindowMode.Standard;

        public int ToggleCount { get; private set; }

        public event EventHandler<WindowMode>? ModeChanged;

        public void Initialize() { }

        public void SwitchTo(WindowMode mode)
        {
            LastSwitch = mode;
            CurrentMode = mode;
            ModeChanged?.Invoke(this, mode);
        }

        public void Toggle() => ToggleCount++;

        public void SetCompactAlwaysOnTop(bool enabled) => CompactAlwaysOnTop = enabled;

        public void CaptureCurrentBounds() { }

        public void PersistCurrentBounds() { }

        public void RaiseModeChanged(WindowMode mode)
        {
            CurrentMode = mode;
            ModeChanged?.Invoke(this, mode);
        }
    }
}
