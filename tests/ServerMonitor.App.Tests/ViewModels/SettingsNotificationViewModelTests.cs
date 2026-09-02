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

    // ------------------------------------------------------ M13 S2 §11: the notice reaches the section

    /// <summary>
    /// The returned review found ConsumeBackgroundSettingsFocus() with ZERO callers — the same class of
    /// defect as RefreshAll having none: the method existed, the tests were green, and the navigation the
    /// human decided on never happened. This fails if the consumption is removed again.
    /// </summary>
    [Fact]
    public void A_requested_background_focus_is_consumed_when_settings_is_navigated_to()
    {
        var navigation = new FakeNavigationService();
        var viewModel = CreateWithNavigation(navigation);
        navigation.RequestBackgroundSettingsFocus();

        viewModel.NotifyNavigatedTo();

        Assert.True(viewModel.IsBackgroundSectionRequested);
        Assert.Equal(0, navigation.BackgroundSettingsFocusRequests); // consumed, not merely read
    }

    [Fact]
    public void An_ordinary_navigation_does_not_focus_the_background_section()
    {
        var navigation = new FakeNavigationService();
        var viewModel = CreateWithNavigation(navigation);

        viewModel.NotifyNavigatedTo();

        Assert.False(viewModel.IsBackgroundSectionRequested);
    }

    /// <summary>One request, one focus: later visits to Settings must not scroll on their own.</summary>
    [Fact]
    public void The_background_focus_request_is_spent_by_the_first_navigation()
    {
        var navigation = new FakeNavigationService();
        var viewModel = CreateWithNavigation(navigation);
        navigation.RequestBackgroundSettingsFocus();

        viewModel.NotifyNavigatedTo();
        Assert.True(viewModel.IsBackgroundSectionRequested);

        viewModel.NotifyNavigatedTo();
        Assert.False(viewModel.IsBackgroundSectionRequested);
    }

    /// <summary>
    /// Both halves of the path the notice's activation takes: the producer navigates to Settings and
    /// records the request, the consumer picks it up. Only the XAML bring-into-view is left, and that is
    /// NOT_RUN pending a real window.
    /// </summary>
    [Fact]
    public void The_request_and_the_consumption_are_two_halves_of_one_path()
    {
        var navigation = new FakeNavigationService();
        var viewModel = CreateWithNavigation(navigation);

        // exactly what ApplicationWindowController.OpenBackgroundSettings does before showing the window
        navigation.GoToSettings();
        navigation.RequestBackgroundSettingsFocus();

        viewModel.NotifyNavigatedTo();

        Assert.Equal(1, navigation.SettingsCount);
        Assert.True(viewModel.IsBackgroundSectionRequested);
    }

    /// <summary>§13: the degradation notice is visible in Settings the moment the tray reports it.</summary>
    [Fact]
    public void The_degradation_notice_opens_the_settings_info_bar()
    {
        var degradation = new BackgroundDegradationNotice();
        var viewModel = CreateWithDegradation(degradation);
        Assert.False(viewModel.IsBackgroundDegradedNoticeOpen);

        degradation.Raise();

        Assert.True(viewModel.IsBackgroundDegradedNoticeOpen);
    }

    /// <summary>
    /// TWO surfaces, not one: the InfoBar is the EVENT and can be dismissed, the caption is the STATE and
    /// must survive that dismissal — otherwise the user who closes the bar and comes back later asking
    /// "why does X quit the app?" finds no answer (scope control §1).
    /// </summary>
    [Fact]
    public void The_state_caption_survives_the_info_bar_being_dismissed()
    {
        var degradation = new BackgroundDegradationNotice();
        var viewModel = CreateWithDegradation(degradation);
        Assert.Equal(Visibility.Collapsed, viewModel.IsBackgroundDegraded);

        degradation.Raise();
        Assert.Equal(Visibility.Visible, viewModel.IsBackgroundDegraded);
        Assert.True(viewModel.IsBackgroundDegradedNoticeOpen);

        viewModel.IsBackgroundDegradedNoticeOpen = false; // the user closes the InfoBar

        Assert.Equal(
            Visibility.Visible,
            viewModel.IsBackgroundDegraded);
    }

    /// <summary>
    /// A degraded session does NOT rewrite the preference: the toggle keeps the persisted value and stays
    /// usable, because nothing about the user's choice changed — only this session cannot honour it.
    /// </summary>
    [Fact]
    public void A_degraded_session_leaves_the_persisted_preference_alone()
    {
        var degradation = new BackgroundDegradationNotice();
        var background = new FakeBackgroundMonitoringSettingsService(enabled: true);
        var viewModel = new SettingsViewModel(
            new FakeThemeService(),
            new FakeLocalizationService(),
            new FakeNavigationService(),
            new FakeServerService(),
            new EmptyDiscoveryService(),
            new FakeNotificationSettingsService(true),
            background,
            degradation,
            new NullHistoryMaintenanceService(),
            new AppVersionProvider(),
            NullLogger<SettingsViewModel>.Instance);

        degradation.Raise();

        Assert.True(background.BackgroundMonitoringEnabled);
        Assert.True(viewModel.BackgroundMonitoringEnabled);
    }

    private static SettingsViewModel CreateWithNavigation(FakeNavigationService navigation) => new(
        new FakeThemeService(),
        new FakeLocalizationService(),
        navigation,
        new FakeServerService(),
        new EmptyDiscoveryService(),
        new FakeNotificationSettingsService(true),
        new FakeBackgroundMonitoringSettingsService(),
        new BackgroundDegradationNotice(),
        new NullHistoryMaintenanceService(),
        new AppVersionProvider(),
        NullLogger<SettingsViewModel>.Instance);

    private static SettingsViewModel CreateWithDegradation(IBackgroundDegradationNotice degradation) => new(
        new FakeThemeService(),
        new FakeLocalizationService(),
        new FakeNavigationService(),
        new FakeServerService(),
        new EmptyDiscoveryService(),
        new FakeNotificationSettingsService(true),
        new FakeBackgroundMonitoringSettingsService(),
        degradation,
        new NullHistoryMaintenanceService(),
        new AppVersionProvider(),
        NullLogger<SettingsViewModel>.Instance);

    private static SettingsViewModel Create(
        INotificationSettingsService settings,
        IBackgroundMonitoringSettingsService? background = null) => new(
        new FakeThemeService(),
        new FakeLocalizationService(),
        new FakeNavigationService(),
        new FakeServerService(),
        new EmptyDiscoveryService(),
        settings,
        background ?? new FakeBackgroundMonitoringSettingsService(),
        new BackgroundDegradationNotice(),
        new NullHistoryMaintenanceService(),
        new AppVersionProvider(),
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
        public int SettingsCount { get; private set; }

        public void Initialize(Frame frame) => throw new NotSupportedException();

        public void NavigateTo<TPage>() where TPage : Page => throw new NotSupportedException();

        public void GoToDashboard()
        {
        }

        public void RequestBackgroundSettingsFocus() => BackgroundSettingsFocusRequests++;

        public int BackgroundSettingsFocusRequests { get; private set; }

        public bool ConsumeBackgroundSettingsFocus()
        {
            if (BackgroundSettingsFocusRequests == 0)
            {
                return false;
            }

            BackgroundSettingsFocusRequests--;
            return true;
        }

        public void GoToSettings() => SettingsCount++;

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
