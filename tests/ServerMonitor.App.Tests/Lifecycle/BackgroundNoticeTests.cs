using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// The one-time background notice and the closed activation contract behind it (M13 S2 §D.1; Vigil
/// C5/C7/C8). The property that matters most is negative: clicking it must NOT reopen the Dashboard the
/// user just closed — which is exactly what the pre-S2 code did for every notification, because the
/// platform adapter discarded the activation arguments.
/// </summary>
public sealed class BackgroundNoticeTests
{
    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public List<(string Title, string Body)> BackgroundNotices { get; } = new();

        public bool Throws { get; set; }

        /// <summary>
        /// What this stand-in reports about itself. It defaults to Registered because every test written
        /// before M13-QA-12 assumed a working service; the ones that care state it explicitly.
        /// </summary>
        public NotificationRegistrationState RegistrationState { get; set; } =
            NotificationRegistrationState.Registered;

        public void BeginShutdown() { }

        public Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public BackgroundNoticeAttempt ShowBackgroundNotice(string title, string body)
        {
            if (Throws)
            {
                throw new InvalidOperationException("notifications unavailable");
            }

            if (RegistrationState != NotificationRegistrationState.Registered)
            {
                return BackgroundNoticeAttempt.NotAttempted;
            }

            BackgroundNotices.Add((title, body));
            return BackgroundNoticeAttempt.ExercisedThroughRegisteredService;
        }
    }

    private static BackgroundNoticePresenter Create(
        FakeBackgroundMonitoringSettingsService settings,
        RecordingNotificationService notifications,
        FakeAppLifecycleController? lifecycle = null) => new(
        settings,
        notifications,
        new FakeLocalizationService(),
        lifecycle ?? new FakeAppLifecycleController(),
        NullLogger<BackgroundNoticePresenter>.Instance);

    [Fact]
    public void The_notice_is_attempted_exactly_once_ever()
    {
        var settings = new FakeBackgroundMonitoringSettingsService();
        var notifications = new RecordingNotificationService();
        var presenter = Create(settings, notifications);

        Assert.True(presenter.TryShowOnce());
        Assert.False(presenter.TryShowOnce());
        Assert.False(presenter.TryShowOnce());

        Assert.Single(notifications.BackgroundNotices);
        Assert.Equal(1, settings.ClaimsGranted);
    }

    [Fact]
    public void A_notice_already_spent_in_a_previous_session_is_never_shown_again()
    {
        var settings = new FakeBackgroundMonitoringSettingsService(noticeShown: true);
        var notifications = new RecordingNotificationService();

        Assert.False(Create(settings, notifications).TryShowOnce());
        Assert.Empty(notifications.BackgroundNotices);
    }

    /// <summary>
    /// Spent on a legitimate ATTEMPT, not on delivery: notifications the user disabled, or a display that
    /// fails inside a REGISTERED service, must not turn the single notice into a nag on every close.
    /// <para>
    /// The service here reports itself Registered and then throws, which is the M13-QA-12 boundary from
    /// the other side: the opportunity WAS exercised through a working service, so it stays spent.
    /// </para>
    /// </summary>
    [Fact]
    public void A_notice_that_cannot_be_delivered_by_a_registered_service_is_still_spent()
    {
        var settings = new FakeBackgroundMonitoringSettingsService();
        var notifications = new RecordingNotificationService
        {
            Throws = true,
            RegistrationState = NotificationRegistrationState.Registered
        };
        var presenter = Create(settings, notifications);

        Assert.True(presenter.TryShowOnce());
        Assert.False(presenter.TryShowOnce());
        Assert.True(settings.BackgroundNoticeShown);
    }

    [Fact]
    public void No_notice_is_attempted_while_exiting()
    {
        var settings = new FakeBackgroundMonitoringSettingsService();
        var notifications = new RecordingNotificationService();
        var presenter = Create(
            settings, notifications, new FakeAppLifecycleController(AppLifecycleState.Exiting));

        Assert.False(presenter.TryShowOnce());
        Assert.Empty(notifications.BackgroundNotices);
        Assert.Equal(0, settings.ClaimAttempts);
    }

    /// <summary>Vigil C5: no fleet data of any kind reaches the notice.</summary>
    [Fact]
    public void The_notice_carries_no_fleet_data()
    {
        var settings = new FakeBackgroundMonitoringSettingsService();
        var notifications = new RecordingNotificationService();
        Create(settings, notifications).TryShowOnce();

        var (title, body) = Assert.Single(notifications.BackgroundNotices);

        // The strings come from static resources, and the payload has no field for anything else: the
        // activation contract carries two enum-valued keys and nothing more (asserted below).
        foreach (var text in new[] { title, body })
        {
            Assert.DoesNotContain("@", text, StringComparison.Ordinal);
            Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- M13-QA-12

    /// <summary>
    /// The whole defect, end to end, against the PRODUCTION service and the PRODUCTION presenter: the
    /// platform refuses the registration, the service swallows nothing into a fake success, and the one
    /// warning the user ever gets is NOT spent. Only the platform is a double.
    /// </summary>
    /// <summary>The window is irrelevant here; these tests are about registration and the marker.</summary>
    private sealed class StubWindowController : IApplicationWindowController
    {
        public bool IsAttached => true;

        public bool IsMaterialized => true;

        public void Attach(Microsoft.UI.Xaml.Window window) { }

        public void AttachWindowFactory(Func<Microsoft.UI.Xaml.Window> factory) { }

        public void HideForMinimize() { }

        public void HideToBackground() { }

        public void RestoreAndActivate() { }

        public void OpenSettings() { }

        public void OpenBackgroundSettings() { }

        public void ToggleCompactMode() { }

        public void RequestClose() { }

        public void BeginShutdown() { }
    }

    private sealed class RealServiceHarness
    {
        public RealServiceHarness(FakeNotificationPlatform platform)
        {
            Platform = platform;
            Service = new WindowsAppNotificationService(
                platform,
                new StubWindowController(),
                Lifecycle,
                NullLogger<WindowsAppNotificationService>.Instance);
            Presenter = new BackgroundNoticePresenter(
                Settings,
                Service,
                new FakeLocalizationService(),
                Lifecycle,
                NullLogger<BackgroundNoticePresenter>.Instance);
        }

        public FakeNotificationPlatform Platform { get; }

        public FakeBackgroundMonitoringSettingsService Settings { get; } = new();

        public FakeAppLifecycleController Lifecycle { get; } = new();

        public WindowsAppNotificationService Service { get; }

        public BackgroundNoticePresenter Presenter { get; }

        public Task Start() => Service.StartAsync(default);
    }

    /// <summary>QA-12 #1: a registration that succeeded is reported as such, and behaves so.</summary>
    [Fact]
    public async Task A_successful_registration_is_reported_as_registered()
    {
        var h = new RealServiceHarness(new FakeNotificationPlatform());

        await h.Start();

        Assert.Equal(NotificationRegistrationState.Registered, h.Service.RegistrationState);
        Assert.Equal(1, h.Platform.RegisterCount);
    }

    /// <summary>
    /// QA-12 #2: THE defect. The registration throws, the top-level catch keeps startup alive — and the
    /// service must NOT come out of it looking registered. It used to, and every later Show went nowhere.
    /// </summary>
    [Fact]
    public async Task A_failed_registration_never_claims_success()
    {
        var h = new RealServiceHarness(new FakeNotificationPlatform { FailRegistration = true });

        await h.Start();

        Assert.Equal(NotificationRegistrationState.NotRegistered, h.Service.RegistrationState);
        Assert.False(
            h.Platform.HasHandler,
            "a failed registration must not leave the activation handler attached");
    }

    /// <summary>QA-12 #2b: a platform that cannot be used at all is Unavailable, not a silent success.</summary>
    [Fact]
    public async Task An_unusable_platform_is_reported_unavailable()
    {
        var unsupported = new RealServiceHarness(new FakeNotificationPlatform { Supported = false });

        await unsupported.Start();

        Assert.Equal(NotificationRegistrationState.Unavailable, unsupported.Service.RegistrationState);
        Assert.Equal(0, unsupported.Platform.RegisterCount);
    }

    /// <summary>
    /// <b>Availability is what the PLATFORM answered, never what a local file suggests.</b> A gate used to
    /// stand between the two: if <c>Assets\ServerMonitorNotification.png</c> was missing, the service went
    /// straight to Unavailable and never called Register at all. That asset fed the unpackaged
    /// <c>Register(displayName, iconUri)</c> overload, which is gone; neither manifest references it (see
    /// <c>ManifestAssetIntegrityTests</c>), and no code but the gate ever read it. Its absence must
    /// therefore be incapable of manufacturing a failure the platform would never have reported, and
    /// nothing speculative may take its place.
    /// <para>
    /// The asset is hidden for the duration and put back afterwards, because absent is the only state in
    /// which the removed gate would have bitten: asserting against a file that is present would let the
    /// gate come back unnoticed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_missing_obsolete_icon_asset_cannot_make_notifications_unavailable()
    {
        var obsoleteAsset = Path.Combine(AppContext.BaseDirectory, "Assets", "ServerMonitorNotification.png");
        var hidden = obsoleteAsset + $".hidden-{Guid.NewGuid():N}";
        var wasPresent = File.Exists(obsoleteAsset);
        if (wasPresent)
        {
            File.Move(obsoleteAsset, hidden);
        }

        try
        {
            var h = new RealServiceHarness(new FakeNotificationPlatform());

            await h.Start();

            Assert.False(File.Exists(obsoleteAsset));
            Assert.Equal(NotificationRegistrationState.Registered, h.Service.RegistrationState);
            Assert.Equal(1, h.Platform.RegisterCount);
        }
        finally
        {
            if (wasPresent)
            {
                File.Move(hidden, obsoleteAsset, overwrite: true);
            }
        }
    }

    /// <summary>
    /// QA-12 #4: the marker survives a failed registration. This is the user-visible half of the defect —
    /// the single explanation was being burned against a service that could not deliver it, so the next
    /// session stayed silent too.
    /// </summary>
    [Fact]
    public async Task A_failed_registration_leaves_the_single_opportunity_unspent()
    {
        var h = new RealServiceHarness(new FakeNotificationPlatform { FailRegistration = true });
        await h.Start();

        Assert.False(h.Presenter.TryShowOnce());

        Assert.False(h.Settings.BackgroundNoticeShown);
        Assert.Equal(0, h.Settings.ClaimAttempts);
        Assert.Equal(0, h.Platform.ShowCount);
    }

    /// <summary>QA-12 #5: registered and closed for the first time — one Show, and the marker is spent.</summary>
    [Fact]
    public async Task A_registered_service_shows_the_notice_once_and_spends_the_marker()
    {
        var h = new RealServiceHarness(new FakeNotificationPlatform());
        await h.Start();

        Assert.True(h.Presenter.TryShowOnce());

        Assert.Equal(1, h.Platform.ShowCount);
        Assert.True(h.Settings.BackgroundNoticeShown);
        Assert.Equal(1, h.Settings.ClaimsGranted);
    }

    /// <summary>QA-12 #6: every later close is silent.</summary>
    [Fact]
    public async Task Later_closes_never_duplicate_the_notice()
    {
        var h = new RealServiceHarness(new FakeNotificationPlatform());
        await h.Start();

        Assert.True(h.Presenter.TryShowOnce());
        Assert.False(h.Presenter.TryShowOnce());
        Assert.False(h.Presenter.TryShowOnce());

        Assert.Equal(1, h.Platform.ShowCount);
        Assert.Equal(1, h.Settings.ClaimsGranted);
    }

    /// <summary>
    /// QA-12 #7: a display failure INSIDE a registered service changes nothing about the lifecycle and
    /// does not reopen the opportunity. Delivery is best effort, and no acknowledgement is sought.
    /// </summary>
    [Fact]
    public async Task A_display_failure_after_a_valid_registration_changes_nothing()
    {
        var h = new RealServiceHarness(new FakeNotificationPlatform { FailShow = true });
        await h.Start();

        Assert.True(h.Presenter.TryShowOnce());

        Assert.Equal(1, h.Platform.ShowCount);
        Assert.True(h.Settings.BackgroundNoticeShown);
        Assert.Equal(NotificationRegistrationState.Registered, h.Service.RegistrationState);
        Assert.Equal(0, h.Lifecycle.ExitRequests);
        Assert.False(h.Lifecycle.IsExiting);
    }

    // ---------------------------------------------------------------- the activation contract

    [Fact]
    public void The_health_contract_maps_to_the_dashboard()
    {
        var arguments = NotificationActivationContract.ForServerHealth();

        Assert.Equal(NotificationAction.OpenDashboard, NotificationActivationContract.ResolveAction(arguments));
    }

    /// <summary>
    /// THE rule: the notice opens Settings → Background, never the Dashboard. Anything else would undo
    /// the hide the user just asked for.
    /// </summary>
    [Fact]
    public void The_notice_contract_maps_to_background_settings_and_never_to_the_dashboard()
    {
        var arguments = NotificationActivationContract.ForBackgroundCloseNotice();

        Assert.Equal(
            NotificationAction.OpenBackgroundSettings,
            NotificationActivationContract.ResolveAction(arguments));
        Assert.NotEqual(
            NotificationAction.OpenDashboard,
            NotificationActivationContract.ResolveAction(arguments));
    }

    /// <summary>Zero action parameters: exactly two keys, both enum-valued.</summary>
    [Fact]
    public void The_payload_carries_two_keys_and_no_parameters()
    {
        foreach (var arguments in new[]
                 {
                     NotificationActivationContract.ForServerHealth(),
                     NotificationActivationContract.ForBackgroundCloseNotice()
                 })
        {
            Assert.Equal(2, arguments.Count);
            Assert.True(arguments.ContainsKey(NotificationActivationContract.KindKey));
            Assert.True(arguments.ContainsKey(NotificationActivationContract.ActionKey));
            Assert.True(Enum.TryParse<NotificationKind>(
                arguments[NotificationActivationContract.KindKey], out _));
            Assert.True(Enum.TryParse<NotificationAction>(
                arguments[NotificationActivationContract.ActionKey], out _));
        }
    }

    /// <summary>Fail closed: anything unrecognized does nothing at all.</summary>
    [Theory]
    [MemberData(nameof(HostileArguments))]
    public void Unrecognized_payloads_fail_closed(Dictionary<string, string>? arguments) =>
        Assert.Equal(NotificationAction.None, NotificationActivationContract.ResolveAction(arguments));

    public static TheoryData<Dictionary<string, string>?> HostileArguments => new()
    {
        null,
        new Dictionary<string, string>(),
        new Dictionary<string, string> { ["kind"] = "ServerHealth" },                     // no action
        new Dictionary<string, string> { ["action"] = "OpenDashboard" },                  // no kind
        new Dictionary<string, string> { ["kind"] = "Nope", ["action"] = "OpenDashboard" },
        new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "Nope" },
        new Dictionary<string, string> { ["kind"] = "serverhealth", ["action"] = "OpenDashboard" }, // case
        // A valid action under the wrong kind is not a valid activation:
        new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "OpenBackgroundSettings" },
        new Dictionary<string, string> { ["kind"] = "BackgroundCloseNotice", ["action"] = "OpenDashboard" },
        new Dictionary<string, string> { ["kind"] = "BackgroundCloseNotice", ["action"] = "None" },
        new Dictionary<string, string> { ["serverId"] = "11111111-1111-1111-1111-111111111111" },
        // Vigil CI-1's specific concern: Enum.TryParse accepts an enum's NUMERIC representation, so a
        // payload of "1"/"2" would have resolved to a real action. The exact allowlist rejects it.
        new Dictionary<string, string> { ["kind"] = "0", ["action"] = "1" },
        new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "1" },
        new Dictionary<string, string> { ["kind"] = "1", ["action"] = "OpenDashboard" },
        // ...and comma-separated combinations, which Enum.TryParse also accepts for any enum.
        new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "OpenDashboard, None" },
        new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "OpenDashboard; DROP" }
    };
}
