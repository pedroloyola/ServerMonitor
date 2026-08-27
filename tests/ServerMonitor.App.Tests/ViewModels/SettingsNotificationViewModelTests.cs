using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class SettingsNotificationViewModelTests
{
    [Fact]
    public void Constructor_ReflectsPersistedGlobalSetting()
    {
        var settings = new FakeNotificationSettingsService(false);
        using var viewModel = Create(settings);

        Assert.False(viewModel.NotificationsEnabled);
        Assert.False(viewModel.IsNotificationSettingsErrorOpen);
    }

    [Fact]
    public void Toggle_PersistsGlobalSettingAndTracksExternalChanges()
    {
        var settings = new FakeNotificationSettingsService(true);
        using var viewModel = Create(settings);

        viewModel.NotificationsEnabled = false;

        Assert.False(settings.NotificationsEnabled);
        Assert.Equal(1, settings.SetCount);
        settings.SetNotificationsEnabled(true);
        Assert.True(viewModel.NotificationsEnabled);
    }

    [Fact]
    public void PersistenceFailure_RevertsToggleAndSurfacesError()
    {
        var settings = new FakeNotificationSettingsService(true)
        {
            SetException = new IOException("disk unavailable")
        };
        using var viewModel = Create(settings);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.NotificationsEnabled = false;

        Assert.True(viewModel.NotificationsEnabled);
        Assert.True(viewModel.IsNotificationSettingsErrorOpen);
        Assert.Contains(nameof(viewModel.NotificationsEnabled), changed);
    }

    [Fact]
    public void Dispose_UnsubscribesFromNotificationSettingChanges()
    {
        var settings = new FakeNotificationSettingsService(true);
        var viewModel = Create(settings);

        Assert.Equal(1, settings.SubscriberCount);

        viewModel.Dispose();

        Assert.Equal(0, settings.SubscriberCount);
    }

    private static SettingsViewModel Create(INotificationSettingsService settings) => new(
        new FakeThemeService(),
        new FakeLocalizationService(),
        new FakeNavigationService(),
        new FakeServerService(),
        new EmptyDiscoveryService(),
        settings,
        new NullHistoryMaintenanceService(),
        NullLogger<SettingsViewModel>.Instance);

    private sealed class FakeNotificationSettingsService(bool enabled) : INotificationSettingsService
    {
        private EventHandler? _changed;

        public Exception? SetException { get; init; }

        public int SetCount { get; private set; }

        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;

        public bool NotificationsEnabled { get; private set; } = enabled;

        public event EventHandler? NotificationsEnabledChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public void SetNotificationsEnabled(bool value)
        {
            SetCount++;
            if (SetException is not null)
            {
                throw SetException;
            }

            if (NotificationsEnabled == value)
            {
                return;
            }

            NotificationsEnabled = value;
            _changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeThemeService : IThemeService
    {
        public AppThemePreference Current => AppThemePreference.System;

        public void Attach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Apply(AppThemePreference preference)
        {
        }
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public void Initialize(Frame frame) => throw new NotSupportedException();

        public void NavigateTo<TPage>() where TPage : Page => throw new NotSupportedException();

        public void GoToDashboard()
        {
        }

        public void GoToSettings()
        {
        }

        public void GoToHistory(Guid serverId, string serverName)
        {
        }

        public void GoToWorkloads(Guid serverId, string serverName)
        {
        }
    }

    private sealed class EmptyDiscoveryService : IServerDiscoveryService
    {
        public event EventHandler DiscoveredChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<DiscoveredService> GetDiscovered() => [];

        public Task IgnoreAsync(
            ServiceInstanceIdentity identity,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
