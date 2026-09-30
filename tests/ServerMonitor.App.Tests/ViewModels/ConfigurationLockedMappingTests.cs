using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.6 contract §7: while a restore holds the configuration every ordinary write throws
/// <see cref="ConfigurationLockedException"/>. The user-initiated write paths report "a restore is in
/// progress" instead of the generic "try again" error, and never crash.
/// </summary>
public sealed class ConfigurationLockedMappingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)] // control: any other failure keeps the existing "could not save" error
    public void Settings_notification_toggle(bool locked)
    {
        var viewModel = CreateSettings(
            notifications: new ThrowingNotificationSettings(Failure(locked)),
            background: new FakeBackgroundMonitoringSettingsService());

        viewModel.NotificationsEnabled = false;

        Assert.True(viewModel.NotificationsEnabled); // reverted to the committed value either way
        Assert.Equal(locked, viewModel.IsConfigurationLockedOpen);
        Assert.Equal(!locked, viewModel.IsNotificationSettingsErrorOpen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Settings_background_toggle(bool locked)
    {
        var viewModel = CreateSettings(
            notifications: new ThrowingNotificationSettings(null),
            background: new ThrowingBackgroundSettings(Failure(locked)));

        viewModel.BackgroundMonitoringEnabled = false;

        Assert.True(viewModel.BackgroundMonitoringEnabled);
        Assert.Equal(locked, viewModel.IsConfigurationLockedOpen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Settings_restore_of_a_hidden_server(bool locked)
    {
        var servers = new LockedServerService(Failure(locked));
        var viewModel = CreateSettings(
            notifications: new ThrowingNotificationSettings(null),
            background: new FakeBackgroundMonitoringSettingsService(),
            servers: servers);
        await viewModel.LoadAsync();

        var hidden = Assert.Single(viewModel.HiddenServers);
        hidden.RestoreCommand.Execute(null);

        Assert.Equal(1, servers.RestoreCount);
        Assert.Equal(locked, viewModel.IsConfigurationLockedOpen);
        Assert.Equal(!locked, viewModel.IsServerOperationErrorOpen);
    }

    [Fact]
    public void Settings_message_is_the_configuration_locked_resource()
    {
        var viewModel = CreateSettings(
            notifications: new ThrowingNotificationSettings(null),
            background: new FakeBackgroundMonitoringSettingsService());

        Assert.Equal("ConfigurationLocked", viewModel.ConfigurationLockedMessage);
    }

    /// <summary>Every dashboard write (add, edit, remove, hide) funnels its failure through HandleError.
    /// Built uninitialized like the other dashboard tests, so the new state must be null-safe.</summary>
    [Fact]
    public void Dashboard_write_failures()
    {
        var viewModel = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));

        viewModel.HandleError(new ConfigurationLockedException(), "hide server");

        Assert.True(viewModel.IsConfigurationLockedOpen);
        Assert.False(viewModel.IsOperationErrorOpen);
        Assert.Equal(string.Empty, viewModel.ConfigurationLockedMessage);
    }

    private static Exception Failure(bool locked) =>
        locked ? new ConfigurationLockedException() : new IOException("disk unavailable");

    private static SettingsViewModel CreateSettings(
        INotificationSettingsService notifications,
        IBackgroundMonitoringSettingsService background,
        IServerService? servers = null) => new(
        new InertThemeService(),
        new FakeLocalizationService(),
        new FakeNavigationService(),
        servers ?? new FakeServerService(),
        new NoDiscoveryService(),
        notifications,
        background,
        new BackgroundDegradationNotice(),
        new NullHistoryMaintenanceService(),
        new AppVersionProvider(),
        NullLogger<SettingsViewModel>.Instance);

    private sealed class ThrowingNotificationSettings(Exception? failure) : INotificationSettingsService
    {
        public bool NotificationsEnabled => true;

        public event EventHandler? NotificationsEnabledChanged
        {
            add { }
            remove { }
        }

        public void SetNotificationsEnabled(bool enabled)
        {
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private sealed class ThrowingBackgroundSettings(Exception failure) : IBackgroundMonitoringSettingsService
    {
        public event EventHandler? BackgroundMonitoringEnabledChanged
        {
            add { }
            remove { }
        }

        public bool BackgroundMonitoringEnabled => true;

        public bool BackgroundNoticeShown => false;

        public void SetBackgroundMonitoringEnabled(bool enabled) => throw failure;

        public bool TryClaimBackgroundNotice() => false;
    }

    private sealed class LockedServerService(Exception failure) : IServerService
    {
        private readonly Server _hidden = TestData.LinuxServer() with { IsHidden = true };

        public int RestoreCount { get; private set; }

        public event EventHandler? ServersChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Server>>([_hidden]);

        public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
            throw failure;

        public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw failure;

        public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw failure;

        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw failure;

        public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => throw failure;

        public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            return Task.FromException<bool>(failure);
        }
    }

    private sealed class InertThemeService : IThemeService
    {
        public AppThemePreference Current => AppThemePreference.System;

        public void Attach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Detach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Apply(AppThemePreference preference)
        {
        }
    }

    private sealed class NoDiscoveryService : IServerDiscoveryService
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
