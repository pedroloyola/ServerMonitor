using System.Reflection.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Alerts;

namespace ServerMonitor.App.Tests.Services;

public sealed class WindowsAppNotificationServiceTests : IDisposable
{
    private readonly string _iconPath = Path.Combine(
        Path.GetTempPath(),
        $"server-monitor-notification-{Guid.NewGuid():N}.png");

    public WindowsAppNotificationServiceTests() => File.WriteAllBytes(_iconPath, [0x89, 0x50, 0x4e, 0x47]);

    // ---------------------------------------------------------------- M13-QA-12: the packaged overload

    /// <summary>
    /// <b>Which overload the shipped binary actually calls</b> — asked of the COMPILED assembly, not of a
    /// double, because the choice of overload is invisible behind any seam we could fake (BOSS.md §10).
    /// <para>
    /// A packaged app must call <c>AppNotificationManager.Register()</c>: its COM server and its assets
    /// are declared in the manifest. The <c>Register(displayName, iconUri)</c> overload registers the
    /// CALLING PROCESS as the COM server and takes assets from the shell, which is the unpackaged
    /// contract, and it rejects a packaged process outright — measured on the installed candidate as
    /// E_ILLEGAL_METHOD_CALL (0x8000000E), "Not applicable for packaged applications". That single wrong
    /// overload is why no notification arrived for the whole of M13.
    /// </para>
    /// <para>
    /// Reading the metadata answers both halves at once: the reference to the parameterless overload is
    /// present, and no reference to the two-argument one exists anywhere in the application assembly.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAppRegistersThroughThePackagedOverloadOnly()
    {
        var parameterCounts = RegisterReferenceParameterCounts();

        Assert.Contains(0, parameterCounts);
        Assert.DoesNotContain(
            2,
            parameterCounts);
    }

    /// <summary>
    /// Every reference the application assembly makes to <c>AppNotificationManager.Register</c>, by
    /// parameter count. Reads the metadata directly: a source-text scan is defeated by a comment, which
    /// this slice already learned the hard way.
    /// </summary>
    private static IReadOnlyList<int> RegisterReferenceParameterCounts()
    {
        var assemblyPath = typeof(WindowsAppNotificationService).Assembly.Location;
        using var stream = File.OpenRead(assemblyPath);
        using var portableExecutable = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = portableExecutable.GetMetadataReader();
        var counts = new List<int>();

        foreach (var handle in metadata.MemberReferences)
        {
            var reference = metadata.GetMemberReference(handle);
            if (!string.Equals(metadata.GetString(reference.Name), "Register", StringComparison.Ordinal))
            {
                continue;
            }

            if (reference.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var parent = metadata.GetTypeReference(
                (TypeReferenceHandle)reference.Parent);
            if (!string.Equals(
                    metadata.GetString(parent.Name), "AppNotificationManager", StringComparison.Ordinal))
            {
                continue;
            }

            // A method signature blob is [calling convention][parameter count][return type][parameters].
            var signature = metadata.GetBlobBytes(reference.Signature);
            counts.Add(signature[1]);
        }

        Assert.NotEmpty(counts);
        return counts;
    }

    // ---------------------------------------------------------------- M13-QA-12: measurable evidence

    /// <summary>
    /// The HRESULTs this API is already known to fail with are NAMED, so a reader recognizes the number
    /// instead of looking it up, and anything else is reported raw rather than guessed at. The raw value
    /// is always recorded too — this is recognition, not a verdict about the cause.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80040154), "REGDB_E_CLASSNOTREG (0x80040154)")]
    [InlineData(unchecked((int)0x80004005), "E_FAIL (0x80004005)")]
    [InlineData(unchecked((int)0x8007007E), "ERROR_MOD_NOT_FOUND (0x8007007E)")]
    [InlineData(unchecked((int)0x80070005), "E_ACCESSDENIED (0x80070005)")]
    [InlineData(unchecked((int)0x8000000E), "E_ILLEGAL_METHOD_CALL (0x8000000E)")]
    [InlineData(unchecked((int)0xDEADBEEF), "0xDEADBEEF")]
    public void KnownRegistrationFailuresAreNamedAndUnknownOnesReportedRaw(int hresult, string expected) =>
        Assert.Equal(expected, WindowsAppNotificationService.DescribeHResult(hresult));

    /// <summary>
    /// THE reason this defect survived M13: the failure was written to Debug output, which goes nowhere in
    /// a packaged run. A failed registration must leave a RETRIEVABLE record carrying the exact exception
    /// type, the exact HRESULT, the call site and the state on both sides of the attempt.
    /// </summary>
    [Fact]
    public async Task FailedRegistration_LeavesRetrievableEvidenceWithTheRealHResult()
    {
        var evidence = new RecordingEvidence();
        var platform = new FakePlatform
        {
            RegistrationFailure = new System.Runtime.InteropServices.COMException(
                "Class not registered", unchecked((int)0x80040154))
        };
        var service = Create(platform, new FakeWindowController(), evidence: evidence);

        await service.StartAsync(default);

        Assert.Equal(NotificationRegistrationState.NotRegistered, service.RegistrationState);
        var report = Assert.Single(evidence.Reports);
        foreach (var expected in new[]
                 {
                     "hresultRaw=0x80040154",
                     "REGDB_E_CLASSNOTREG (0x80040154)",
                     "exceptionType=System.Runtime.InteropServices.COMException",
                     "stateBefore=NotRegistered",
                     "stateAfter=NotRegistered",
                     "registerCallSite=WindowsAppNotificationService.StartAsync",
                     "packageIdentity="
                 })
        {
            Assert.Contains(expected, report, StringComparison.Ordinal);
        }
    }

    /// <summary>A success is recorded too: the absence of a record must not be the only signal.</summary>
    [Fact]
    public async Task SuccessfulRegistration_IsRecordedAsRegistered()
    {
        var evidence = new RecordingEvidence();
        var service = Create(new FakePlatform(), new FakeWindowController(), evidence: evidence);

        await service.StartAsync(default);

        var report = Assert.Single(evidence.Reports);
        Assert.Contains("stateAfter=Registered", report, StringComparison.Ordinal);
        Assert.DoesNotContain("hresult", report, StringComparison.Ordinal);
    }

    private sealed class RecordingEvidence : INotificationRegistrationEvidence
    {
        public List<string> Reports { get; } = new();

        public List<string> Appended { get; } = new();

        public void Record(string report) => Reports.Add(report);

        public void Append(string line) => Appended.Add(line);
    }

    [Fact]
    public void Platform_DoesNotResolveDefaultManagerBeforeCapabilityGatePasses()
    {
        var managerFactoryCalled = false;
        var platform = new WindowsAppNotificationPlatform(
            () => false,
            () =>
            {
                managerFactoryCalled = true;
                throw new InvalidOperationException("The unavailable Singleton must not be resolved.");
            });

        Assert.False(platform.IsSupported());
        Assert.False(managerFactoryCalled);
    }

    /// <summary>
    /// Once, with the handler already attached — the ordering Microsoft requires, and the one the
    /// activation depends on. The display name and icon assertions are gone with the overload that took
    /// them: a packaged app declares both in its manifest (M13-QA-12), so there is nothing to pass and
    /// nothing left to assert here. What the process registers with is proved by
    /// <see cref="TheAppRegistersThroughThePackagedOverloadOnly"/>, against the compiled assembly.
    /// </summary>
    [Fact]
    public async Task Start_RegistersOnceWithHandlerAlreadyAttached()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());

        await service.StartAsync(default);
        await service.StartAsync(default);

        Assert.Equal(1, platform.RegisterCount);
        Assert.True(platform.HandlerWasAttachedAtRegister);
        Assert.Equal(NotificationRegistrationState.Registered, service.RegistrationState);
    }

    [Fact]
    public async Task UnsupportedPlatform_DoesNotRegisterOrThrow()
    {
        var platform = new FakePlatform { Supported = false };
        var service = Create(platform, new FakeWindowController());

        await service.StartAsync(default);
        await service.ShowAsync(Notification());

        Assert.Equal(0, platform.RegisterCount);
        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task DisabledOsSetting_SuppressesNotification()
    {
        var platform = new FakePlatform { Setting = AppNotificationSetting.DisabledForUser };
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        await service.ShowAsync(Notification());

        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task Show_PassesOnlyPreparedTitleAndBody()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        await service.ShowAsync(Notification());

        Assert.Equal(1, platform.ShowCount);
        Assert.Equal("Offline", platform.Title);
        Assert.Equal("Server unavailable", platform.Body);
    }

    [Fact]
    public async Task NotificationClick_RestoresSameWindowUntilShutdown()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        platform.RaiseInvoked();
        await service.StopAsync(default);
        platform.RaiseInvoked();

        Assert.Equal(1, window.RestoreCount);
        Assert.Equal(1, platform.UnregisterCount);
    }

    [Fact]
    public async Task RepeatedStop_UnregistersOnceAndSuppressesCallbacks()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        await service.StopAsync(default);
        await service.StopAsync(default);
        await service.ShowAsync(Notification());

        Assert.Equal(1, platform.UnregisterCount);
        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task BeginShutdown_SynchronouslySuppressesDeliveryAndActivationBeforeUnregister()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        service.BeginShutdown();
        await service.ShowAsync(Notification());
        platform.RaiseInvoked();

        Assert.Equal(0, platform.ShowCount);
        Assert.Equal(0, window.RestoreCount);
        await service.StopAsync(default);
        Assert.Equal(1, platform.UnregisterCount);
    }

    // ---------------------------------------------------------------- M13 S2: typed activation routing

    /// <summary>
    /// THE rule the human called out: the background notice must not reopen the Dashboard by accident.
    /// Before S2 it would have, because the platform adapter discarded the activation arguments and the
    /// service restored the window for ANY click.
    /// </summary>
    [Fact]
    public async Task The_background_notice_opens_settings_and_never_the_dashboard()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        platform.RaiseInvoked(NotificationActivationContract.ForBackgroundCloseNotice());

        Assert.Equal(1, window.OpenBackgroundSettingsCount);
        Assert.Equal(0, window.RestoreCount);
    }

    [Fact]
    public async Task A_health_notification_still_opens_the_dashboard_but_explicitly()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        platform.RaiseInvoked(NotificationActivationContract.ForServerHealth());

        Assert.Equal(1, window.RestoreCount);
        Assert.Equal(0, window.OpenBackgroundSettingsCount);
    }

    /// <summary>Fail closed: an unrecognized payload does nothing, rather than defaulting to a restore.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("empty")]
    [InlineData("unknown-kind")]
    [InlineData("mismatched")]
    public async Task An_unrecognized_activation_does_nothing(string? shape)
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        var arguments = shape switch
        {
            "empty" => new Dictionary<string, string>(),
            "unknown-kind" => new Dictionary<string, string> { ["kind"] = "Nope", ["action"] = "OpenDashboard" },
            "mismatched" => new Dictionary<string, string>
            {
                ["kind"] = "BackgroundCloseNotice",
                ["action"] = "OpenDashboard"
            },
            _ => null
        };

        platform.RaiseInvoked(arguments);

        Assert.Equal(0, window.RestoreCount);
        Assert.Equal(0, window.OpenBackgroundSettingsCount);
    }

    /// <summary>EXIT WINS: an activation during the drain materializes nothing.</summary>
    [Fact]
    public async Task An_activation_while_exiting_is_discarded()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(
            platform, window, new FakeAppLifecycleController(AppLifecycleState.Exiting));
        await service.StartAsync(default);

        platform.RaiseInvoked(NotificationActivationContract.ForServerHealth());
        platform.RaiseInvoked(NotificationActivationContract.ForBackgroundCloseNotice());

        Assert.Equal(0, window.RestoreCount);
        Assert.Equal(0, window.OpenBackgroundSettingsCount);
    }

    /// <summary>
    /// The notice is short-lived and does not persist in the Notification Centre, unlike a health alert
    /// (Prism): it explains a transition that has already happened.
    /// </summary>
    [Fact]
    public async Task The_notice_is_short_lived_and_health_alerts_are_not()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        service.ShowBackgroundNotice("title", "body");
        Assert.True(platform.LastExpiresOnReboot);
        // §12: an explicit lifetime, not just "until reboot" - a machine that never reboots must not keep
        // a one-off educational toast in the Notification Centre forever.
        Assert.Equal(WindowsAppNotificationService.BackgroundNoticeLifetime, platform.LastExpiresAfter);
        Assert.True(platform.LastExpiresAfter > TimeSpan.Zero);
        Assert.Equal(NotificationActivationContract.ForBackgroundCloseNotice(), platform.LastArguments);

        await service.ShowAsync(Notification());
        Assert.False(platform.LastExpiresOnReboot);
        Assert.Null(platform.LastExpiresAfter); // health alerts keep the platform default
        Assert.Equal(NotificationActivationContract.ForServerHealth(), platform.LastArguments);
    }

    /// <summary>
    /// CV-17. The fail-safe notice actually reaches the platform carrying its own closed pair and its own
    /// short lifetime — asserted at the boundary, not on the constant, because a constant nobody passes
    /// is a value with no behaviour.
    /// </summary>
    [Fact]
    public async Task The_fail_safe_notice_carries_its_own_pair_and_expires_soon()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        service.ShowFailSafeExitNotice("title", "body");

        Assert.Equal(1, platform.ShowCount);
        Assert.True(platform.LastExpiresOnReboot);
        Assert.Equal(WindowsAppNotificationService.FailSafeExitNoticeLifetime, platform.LastExpiresAfter);
        Assert.NotNull(platform.LastExpiresAfter);
        Assert.True(platform.LastExpiresAfter <= TimeSpan.FromHours(1));

        // Shorter than the background notice: it reports something that happened a moment ago.
        Assert.True(
            WindowsAppNotificationService.FailSafeExitNoticeLifetime
            > WindowsAppNotificationService.BackgroundNoticeLifetime,
            "the fail-safe notice is longer than the first-close notice by design, but still short");

        Assert.Equal(NotificationActivationContract.ForFailSafeExit(), platform.LastArguments);
    }

    /// <summary>
    /// The fail-safe notice is raised from a committed exit, so unlike the background notice it must NOT
    /// be refused once shutdown begins — refusing it would mean it could only ever appear before the exit
    /// it is about, which is never.
    /// </summary>
    [Fact]
    public async Task The_fail_safe_notice_is_still_shown_after_shutdown_begins()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);
        service.BeginShutdown();

        service.ShowFailSafeExitNotice("title", "body");

        Assert.Equal(1, platform.ShowCount);
    }

    [Fact]
    public async Task The_fail_safe_notice_is_suppressed_when_Windows_notifications_are_disabled()
    {
        var platform = new FakePlatform { Setting = AppNotificationSetting.DisabledForApplication };
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        service.ShowFailSafeExitNotice("title", "body");

        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task The_notice_is_not_shown_after_shutdown_begins()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);
        service.BeginShutdown();

        service.ShowBackgroundNotice("title", "body");

        Assert.Equal(0, platform.ShowCount);
    }

    private WindowsAppNotificationService Create(
        IWindowsAppNotificationPlatform platform,
        IApplicationWindowController window,
        IAppLifecycleController? lifecycle = null,
        INotificationRegistrationEvidence? evidence = null) => new(
            platform,
            window,
            lifecycle ?? new FakeAppLifecycleController(),
            NullLogger<WindowsAppNotificationService>.Instance,
            _iconPath,
            evidence);

    private static UserNotification Notification() => new(
        Guid.NewGuid(),
        ServerAlertCategory.Offline,
        "Offline",
        "Server unavailable");

    public void Dispose()
    {
        if (File.Exists(_iconPath))
        {
            File.Delete(_iconPath);
        }
    }

    private sealed class FakePlatform : IWindowsAppNotificationPlatform
    {
        private EventHandler<NotificationActivationEventArgs>? _invoked;

        public event EventHandler<NotificationActivationEventArgs>? Invoked
        {
            add { _invoked += value; }
            remove { _invoked -= value; }
        }

        public bool Supported { get; init; } = true;
        public AppNotificationSetting Setting { get; init; } = AppNotificationSetting.Enabled;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public int ShowCount { get; private set; }
        public bool HandlerWasAttachedAtRegister { get; private set; }
        public string? Title { get; private set; }
        public string? Body { get; private set; }

        public bool IsSupported() => Supported;

        /// <summary>When set, the platform refuses the registration exactly as the real one can.</summary>
        public Exception? RegistrationFailure { get; init; }

        public void Register()
        {
            RegisterCount++;
            HandlerWasAttachedAtRegister = _invoked is not null;

            if (RegistrationFailure is not null)
            {
                throw RegistrationFailure;
            }
        }

        public void Unregister() => UnregisterCount++;

        public IReadOnlyDictionary<string, string>? LastArguments { get; private set; }

        public bool LastExpiresOnReboot { get; private set; }

        public TimeSpan? LastExpiresAfter { get; private set; }

        public void Show(
            string title,
            string body,
            IReadOnlyDictionary<string, string> arguments,
            bool expiresOnReboot,
            TimeSpan? expiresAfter)
        {
            ShowCount++;
            Title = title;
            Body = body;
            LastArguments = arguments;
            LastExpiresOnReboot = expiresOnReboot;
            LastExpiresAfter = expiresAfter;
        }

        /// <summary>Raises an activation carrying the health contract, which is what these tests exercise.</summary>
        public void RaiseInvoked() =>
            RaiseInvoked(NotificationActivationContract.ForServerHealth());

        public void RaiseInvoked(IReadOnlyDictionary<string, string>? arguments) =>
            _invoked?.Invoke(this, new NotificationActivationEventArgs(arguments));
    }

    private sealed class FakeWindowController : IApplicationWindowController
    {
        public bool IsAttached => true;
        public int RestoreCount { get; private set; }

        public void Attach(Window window) { }

        public bool IsMaterialized => true;

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideToBackground() => HideToBackgroundCount++;

        public int HideToBackgroundCount { get; private set; }

        public void OpenBackgroundSettings() => OpenBackgroundSettingsCount++;

        public int OpenBackgroundSettingsCount { get; private set; }
        public void HideForMinimize() { }
        public void RestoreAndActivate() => RestoreCount++;
        public void OpenSettings() { }
        public void ToggleCompactMode() { }
        public void RequestClose() { }
        public void BeginShutdown() { }
    }
}
