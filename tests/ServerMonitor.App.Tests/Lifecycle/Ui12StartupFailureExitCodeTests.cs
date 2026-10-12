using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// UI.12 APP-STARTUP-FAILURE-EXIT0 (Cortex UI.12A C-S1a). A startup exception caught by <c>OnLaunched</c> ended the
/// process with exit code 0 (measured in UI.10 A-1: six harnesses exited 0 with no window). The catch body is now the
/// invocable <see cref="App.HandleStartupFailure"/>, which records <see cref="StartupExitCode.StartupFailure"/> before
/// anything that can throw; <c>Main</c> returns that code. <c>OnLaunched</c> and <c>Main</c> need a XAML runtime, so their
/// two call sites are pinned by source; everything else is behavioural. Runtime exit code: NOT_RUN (Boss: no new QA
/// fault-injection flag).
/// </summary>
public sealed partial class Ui12StartupFailureExitCodeTests
{
    [Fact]
    public void TheExitCode_IsZeroUntilAFailureIsRecorded_ThenTheDistinctStartupCode_SetOnce()
    {
        var code = new StartupExitCode();
        Assert.Equal(0, code.Value);

        code.RecordStartupFailure();
        Assert.Equal(StartupExitCode.StartupFailure, code.Value);
        Assert.Equal(0x5353, code.Value);

        code.RecordStartupFailure();
        Assert.Equal(StartupExitCode.StartupFailure, code.Value);

        // Distinct from every other code the process can end with.
        Assert.NotEqual(ProcessTerminator.WatchdogExitCode, StartupExitCode.StartupFailure);
        Assert.NotEqual(3, StartupExitCode.StartupFailure); // QaStartupIsolation.RefusedExitCode (Debug-only type)
    }

    [Fact]
    public void AStartupFailure_IsRecorded_BeforeTheExitIsRequested_AndTheExitRunsOnce()
    {
        var code = new StartupExitCode();
        var lifecycle = new RecordingLifecycle(onExit: () => Assert.Equal(StartupExitCode.StartupFailure, code.Value));
        var fallbackExits = 0;

        App.HandleStartupFailure(Services(lifecycle), new InvalidOperationException("boom"), code, () => fallbackExits++);

        Assert.Equal(StartupExitCode.StartupFailure, code.Value);
        Assert.Equal([ExitReason.StartupFailure], lifecycle.Requests);
        Assert.Equal(0, fallbackExits);
    }

    [Fact]
    public void WhenTheExitRequestThrows_TheCodeIsStillRecorded_AndTheFallbackExitRuns()
    {
        var code = new StartupExitCode();
        var lifecycle = new RecordingLifecycle(onExit: () => throw new InvalidOperationException("drain failed"));
        var fallbackExits = 0;

        App.HandleStartupFailure(Services(lifecycle), new InvalidOperationException("boom"), code, () => fallbackExits++);

        Assert.Equal(StartupExitCode.StartupFailure, code.Value);
        Assert.Equal(1, fallbackExits);
    }

    [Fact]
    public void WhenEvenTheLoggerCannotBeResolved_TheCodeIsAlreadyRecorded()
    {
        var code = new StartupExitCode();
        var services = new ServiceCollection().BuildServiceProvider(); // no logger, no lifecycle

        Assert.ThrowsAny<Exception>(() =>
            App.HandleStartupFailure(services, new InvalidOperationException("boom"), code, () => { }));

        Assert.Equal(StartupExitCode.StartupFailure, code.Value);
    }

    // ---- the wiring (XAML runtime: pinned by source) ----

    [Fact]
    public void OnLaunchedCatch_DelegatesToHandleStartupFailure_AndMainReturnsTheRecordedCode()
    {
        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        Assert.Matches(OnLaunchedCatchDelegates(), app);
        Assert.Single(HandleStartupFailureCalls().Matches(app));

        var program = AppSourceTree.CodeWithoutComments("Program.cs");
        Assert.Matches(MainReturnsTheRecordedCode(), program);
        Assert.Matches(RedirectStillReturnsZero(), program);
    }

    [GeneratedRegex(@"catch \(Exception exception\)\s*\{\s*HandleStartupFailure\(ServicesHost\.Services, exception, Program\.StartupExitCode, Exit\);\s*\}")]
    private static partial Regex OnLaunchedCatchDelegates();

    [GeneratedRegex(@"HandleStartupFailure\(ServicesHost")]
    private static partial Regex HandleStartupFailureCalls();

    [GeneratedRegex(@"Application\.Start\([\s\S]*?\}\);\s*return StartupExitCode\.Value;\s*\}")]
    private static partial Regex MainReturnsTheRecordedCode();

    [GeneratedRegex(@"if \(ShouldRedirectToExistingInstance\(\)\)\s*\{\s*return 0;\s*\}")]
    private static partial Regex RedirectStillReturnsZero();

    private static ServiceProvider Services(IAppLifecycleController lifecycle) =>
        new ServiceCollection()
            .AddSingleton<ILogger<App>>(NullLogger<App>.Instance)
            .AddSingleton(lifecycle)
            .BuildServiceProvider();

    private sealed class RecordingLifecycle(Action onExit) : IAppLifecycleController
    {
        public List<ExitReason> Requests { get; } = [];

        public AppLifecycleState State => AppLifecycleState.Foreground;

        public bool StartedInBackground => false;

        public bool IsExiting => Requests.Count > 0;

        public void EnterForeground() { }

        public void EnterBackground() { }

        public void RequestExit(ExitReason reason)
        {
            Requests.Add(reason);
            onExit();
        }
    }
}
