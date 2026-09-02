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

        public void BeginShutdown() { }

        public Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void ShowBackgroundNotice(string title, string body)
        {
            if (Throws)
            {
                throw new InvalidOperationException("notifications unavailable");
            }

            BackgroundNotices.Add((title, body));
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
    /// Spent on ATTEMPT, not on delivery: notifications the user disabled must not turn the single notice
    /// into a nag on every close.
    /// </summary>
    [Fact]
    public void A_notice_that_cannot_be_delivered_is_still_spent()
    {
        var settings = new FakeBackgroundMonitoringSettingsService();
        var notifications = new RecordingNotificationService { Throws = true };
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
        new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "OpenDashboard; DROP" }
    };
}
