using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// CV-17 and CV-18. The notice is raised through the REAL <see cref="AppLifecycleController"/>, because
/// the whole condition is about which branch of its CAS ran — a test that called
/// <see cref="FailSafeExitNotice.OnExitCommitted"/> directly would be asserting about a method, not
/// about the situation.
/// </summary>
public sealed class FailSafeExitNoticeTests
{
    // ------------------------------------------------------------------ the Prism condition

    [Fact]
    public void The_notice_is_raised_when_the_fail_safe_exit_commits_the_process()
    {
        var harness = new Harness();

        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.True(harness.Notice.Raised);
        Assert.Single(harness.Notifications.FailSafeNotices);
    }

    [Fact]
    public void No_notice_when_the_user_had_already_asked_to_quit_and_the_cleanup_then_failed()
    {
        // THE Prism condition. The user chose "Sair do ServerAlyzer"; the compensation then fails during
        // that exit and the fail-safe path asks for an exit that is already under way. Telling them to
        // open the app again to keep monitoring would contradict what they just did.
        var harness = new Harness();

        harness.Controller.RequestExit(ExitReason.TrayExit);
        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.False(harness.Notice.Raised);
        Assert.Empty(harness.Notifications.FailSafeNotices);
    }

    [Fact]
    public void No_notice_when_the_window_close_committed_the_exit_first()
    {
        // The same situation reached by the other user-initiated route: X with background off.
        var harness = new Harness();

        harness.Controller.RequestExit(ExitReason.UserClosedWindow);
        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.False(harness.Notice.Raised);
    }

    [Fact]
    public void No_notice_for_any_exit_reason_other_than_the_unverified_cleanup()
    {
        // Every reason gets its own controller, so each one is the WINNER of its own CAS. This isolates
        // "the reason is wrong" from "the CAS was lost" — a conjunctive test would pass with the reason
        // check removed, because losing the CAS already suppresses the notice.
        foreach (var reason in Enum.GetValues<ExitReason>())
        {
            if (reason == ExitReason.TrayCleanupUnverified)
            {
                continue;
            }

            var harness = new Harness();
            harness.Controller.RequestExit(reason);

            Assert.False(harness.Notice.Raised, $"{reason} must not raise the notice");
        }
    }

    [Fact]
    public void The_notice_is_raised_at_most_once()
    {
        var harness = new Harness();

        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);
        harness.Notice.OnExitCommitted(ExitReason.TrayCleanupUnverified);

        Assert.Single(harness.Notifications.FailSafeNotices);
    }

    // ------------------------------------------------------------------ it must never hold up the exit

    [Fact]
    public void A_notice_that_throws_does_not_prevent_the_true_exit()
    {
        // Fire-and-forget is not a comment: the process closing is the safe outcome, and the notice only
        // explains it. If explaining it could stop it, the notice would be the more dangerous half.
        var harness = new Harness(notificationsThrow: true);

        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.Equal(1, harness.ApplicationExits);
        Assert.True(harness.Sequence.DrainHostCalled);
    }

    [Fact]
    public void A_notice_that_throws_does_not_change_the_shutdown_steps_or_their_order()
    {
        var thrown = new Harness(notificationsThrow: true);
        var clean = new Harness();

        thrown.Controller.RequestExit(ExitReason.TrayCleanupUnverified);
        clean.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.Equal(clean.Sequence.Steps, thrown.Sequence.Steps);
        Assert.NotEmpty(clean.Sequence.Steps);
    }

    /// <summary>
    /// Layer one, on its own: the notice swallows a platform failure rather than handing it to the exit.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the controller's guard on purpose. The two layers are defence in depth,
    /// and depth is exactly what makes a single-layer mutation invisible: with only the end-to-end test,
    /// removing EITHER guard stayed green because the other still caught it. A property defended twice
    /// still has to be proven twice.
    /// </remarks>
    [Fact]
    public void The_notice_itself_never_lets_a_platform_failure_escape()
    {
        var notice = new FailSafeExitNotice(
            () => throw new InvalidOperationException("the notification service is gone"),
            new EchoLocalization(),
            NullLogger<FailSafeExitNotice>.Instance);

        notice.OnExitCommitted(ExitReason.TrayCleanupUnverified);

        // It also counts as raised: the single shot is spent on the attempt, not on the success, so a
        // broken platform cannot turn into a retry loop inside a committed exit.
        Assert.True(notice.Raised);
    }

    /// <summary>
    /// Layer two, on its own: the exit path survives an <c>onExitCommitted</c> that throws, whatever it
    /// is. Uses a raw throwing callback rather than the notice, so the notice's own guard cannot stand in
    /// for the controller's.
    /// </summary>
    [Fact]
    public void The_exit_path_survives_a_committed_hook_that_throws()
    {
        var sequence = new RecordingSequence();
        var exits = 0;
        var controller = new AppLifecycleController(
            () => sequence,
            () => exits++,
            new TerminationWatchdog(new ManualWatchdogScheduler(), NullLogger<TerminationWatchdog>.Instance),
            new NullTerminator(),
            NullLogger<AppLifecycleController>.Instance,
            onExitCommitted: _ => throw new InvalidOperationException("hook exploded"));

        controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.Equal(1, exits);
        Assert.True(sequence.DrainHostCalled);
        Assert.Equal(
            [nameof(IExitSequence.StopAcceptingForegroundWork), nameof(IExitSequence.RemoveTrayIcon),
             nameof(IExitSequence.HideUserInterface), nameof(IExitSequence.DrainHost)],
            sequence.Steps);
    }

    [Fact]
    public void The_notice_is_raised_before_the_shutdown_starts_refusing_new_work()
    {
        // StopAcceptingForegroundWork is what closes the notification service. Raised after it, the
        // notice could only ever be shown before the exit it is about — that is, never.
        var harness = new Harness();

        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        Assert.Equal("Notice", harness.Sequence.Steps[0]);
        Assert.Contains("StopAcceptingForegroundWork", harness.Sequence.Steps);
        Assert.True(
            harness.Sequence.Steps.IndexOf("Notice")
            < harness.Sequence.Steps.IndexOf("StopAcceptingForegroundWork"));
    }

    [Fact]
    public void The_exit_never_learns_whether_the_notice_was_delivered()
    {
        // Fire-and-forget, expressed as a type rule: the boundary returns void, so there is no result the
        // exit path could wait on or branch on.
        var show = typeof(IUserNotificationService).GetMethod(nameof(IUserNotificationService.ShowFailSafeExitNotice));

        Assert.NotNull(show);
        Assert.Equal(typeof(void), show!.ReturnType);
    }

    // ------------------------------------------------------------------ CV-18: the payload

    [Fact]
    public void The_notice_carries_the_closed_pair_and_nothing_else()
    {
        var arguments = NotificationActivationContract.ForFailSafeExit();

        Assert.Equal(2, arguments.Count);
        Assert.Equal("FailSafeExit", arguments[NotificationActivationContract.KindKey]);
        Assert.Equal("OpenDashboard", arguments[NotificationActivationContract.ActionKey]);
    }

    [Fact]
    public void A_late_click_resolves_to_the_launch_that_was_already_on_the_allowlist()
    {
        // Minutes later, the process is gone. Activation starts the app on the Dashboard: no capability,
        // no reason code, no resumption of whatever failed, no way back into the path that produced it.
        var action = NotificationActivationContract.ResolveAction(
            NotificationActivationContract.ForFailSafeExit());

        Assert.Equal(NotificationAction.OpenDashboard, action);
    }

    [Fact]
    public void The_fail_safe_kind_only_pairs_with_the_one_action_it_was_approved_with()
    {
        // CI-1b, inherited fail-closed. Each case varies exactly ONE field and leaves the other valid, so
        // each isolates a single filter: a conjunctive case would pass with half the check removed.
        Assert.Equal(NotificationAction.None, Resolve("FailSafeExit", "OpenBackgroundSettings"));
        Assert.Equal(NotificationAction.None, Resolve("ServerHealth", "OpenBackgroundSettings"));
        Assert.Equal(NotificationAction.OpenDashboard, Resolve("FailSafeExit", "OpenDashboard"));
    }

    private static NotificationAction Resolve(string kind, string action) =>
        NotificationActivationContract.ResolveAction(new Dictionary<string, string>
        {
            [NotificationActivationContract.KindKey] = kind,
            [NotificationActivationContract.ActionKey] = action
        });

    [Fact]
    public void The_activation_vocabulary_is_exactly_three_pairs()
    {
        // Widening it is how a closed vocabulary stops being one. Numeric enum spellings stay refused —
        // that is CI-1b, inherited from S2 and not weakened here.
        (string Kind, string Action, NotificationAction Expected)[] cases =
        [
            ("ServerHealth", "OpenDashboard", NotificationAction.OpenDashboard),
            ("BackgroundCloseNotice", "OpenBackgroundSettings", NotificationAction.OpenBackgroundSettings),
            ("FailSafeExit", "OpenDashboard", NotificationAction.OpenDashboard),
            ("FailSafeExit", "None", NotificationAction.None),
            ("FailSafeExit", "OpenBackgroundSettings", NotificationAction.None),
            ("FailSafeExit", "1", NotificationAction.None),
            ("2", "1", NotificationAction.None),
            ("TrayCleanupUnverified", "OpenDashboard", NotificationAction.None),
            ("failsafeexit", "OpenDashboard", NotificationAction.None)
        ];

        foreach (var (kind, action, expected) in cases)
        {
            var resolved = NotificationActivationContract.ResolveAction(new Dictionary<string, string>
            {
                [NotificationActivationContract.KindKey] = kind,
                [NotificationActivationContract.ActionKey] = action
            });

            Assert.Equal(expected, resolved);
        }
    }

    // ------------------------------------------------------------------ CV-17: expiry and copy

    [Fact]
    public void The_notice_expires_soon_and_on_reboot()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), WindowsAppNotificationService.FailSafeExitNoticeLifetime);

        // Shorter than the background notice, which is the point: this one reports something that
        // happened a moment ago, and hours later it would be a puzzle about a process long gone.
        Assert.True(
            WindowsAppNotificationService.FailSafeExitNoticeLifetime
            <= TimeSpan.FromHours(1),
            "the fail-safe notice must be short-lived");
    }

    [Fact]
    public void The_copy_comes_from_the_resources_and_says_nothing_technical()
    {
        var harness = new Harness();

        harness.Controller.RequestExit(ExitReason.TrayCleanupUnverified);

        var (title, body) = harness.Notifications.FailSafeNotices.Single();
        Assert.Equal($"[{FailSafeExitNotice.TitleResourceKey}]", title);
        Assert.Equal($"[{FailSafeExitNotice.BodyResourceKey}]", body);
    }

    [Fact]
    public void Both_strings_are_defined_in_every_localization()
    {
        var resources = Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Resources"),
            "Resources.resw",
            SearchOption.AllDirectories);

        Assert.Equal(3, resources.Length);

        foreach (var file in resources)
        {
            var content = File.ReadAllText(file);
            Assert.Contains($"name=\"{FailSafeExitNotice.TitleResourceKey}\"", content, StringComparison.Ordinal);
            Assert.Contains($"name=\"{FailSafeExitNotice.BodyResourceKey}\"", content, StringComparison.Ordinal);

            // No fleet data and no Shell vocabulary in the copy the user reads.
            foreach (var forbidden in new[] { "NIM_", "Shell_NotifyIcon", "HWND", "HRESULT" })
            {
                Assert.DoesNotContain(forbidden, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>
    /// The PRODUCTION controller and the PRODUCTION notice, with only the platform faked. The claim under
    /// test is about which branch of the controller's CAS runs, so substituting the controller would test
    /// nothing.
    /// </summary>
    private sealed class Harness
    {
        public RecordingSequence Sequence { get; }

        public RecordingNotifications Notifications { get; }

        public FailSafeExitNotice Notice { get; }

        public AppLifecycleController Controller { get; }

        public int ApplicationExits { get; private set; }

        public Harness(bool notificationsThrow = false)
        {
            Sequence = new RecordingSequence();
            Notifications = new RecordingNotifications(Sequence, notificationsThrow);
            Notice = new FailSafeExitNotice(
                () => Notifications,
                new EchoLocalization(),
                NullLogger<FailSafeExitNotice>.Instance);

            Controller = new AppLifecycleController(
                () => Sequence,
                () => ApplicationExits++,
                new TerminationWatchdog(new ManualWatchdogScheduler(), NullLogger<TerminationWatchdog>.Instance),
                new NullTerminator(),
                NullLogger<AppLifecycleController>.Instance,
                onExitCommitted: Notice.OnExitCommitted);
        }
    }

    private sealed class NullTerminator : IProcessTerminator
    {
        public void Terminate(int exitCode)
        {
        }
    }

    /// <summary>Records the shutdown steps in order, on the same list the notice writes to.</summary>
    private sealed class RecordingSequence : IExitSequence
    {
        public List<string> Steps { get; } = [];

        public bool DrainHostCalled { get; private set; }

        public void StopAcceptingForegroundWork() => Steps.Add(nameof(StopAcceptingForegroundWork));

        public void RemoveTrayIcon() => Steps.Add(nameof(RemoveTrayIcon));

        public void HideUserInterface() => Steps.Add(nameof(HideUserInterface));

        public bool DrainHost()
        {
            Steps.Add(nameof(DrainHost));
            DrainHostCalled = true;
            return true;
        }
    }

    private sealed class RecordingNotifications(RecordingSequence sequence, bool shouldThrow) : IUserNotificationService
    {
        public List<(string Title, string Body)> FailSafeNotices { get; } = [];

        public Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void ShowFailSafeExitNotice(string title, string body)
        {
            sequence.Steps.Add("Notice");

            if (shouldThrow)
            {
                throw new InvalidOperationException("the notification platform is unavailable");
            }

            FailSafeNotices.Add((title, body));
        }
    }

    /// <summary>Echoes the key, so a test can prove the copy came from resources without pinning prose.</summary>
    private sealed class EchoLocalization : ILocalizationService
    {
        public string? CurrentLanguageOverride => null;

        public string GetString(string resourceKey) => $"[{resourceKey}]";

        public void InitializeFromSystem()
        {
        }

        public void SetLanguage(string? languageTag)
        {
        }
    }
}
