using System.Text.RegularExpressions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 C-L8 (Cortex UI.12A). An exception escaping an <c>async void</c> (an event handler, or
/// <see cref="AsyncRelayCommand.Execute"/>) is posted to the captured synchronization context - on the UI thread that ends
/// the process. The representative path is the command: it is driven under a capturing context that records whatever an
/// <c>async void</c> would re-raise there. The editor's handlers use the same guard; their wiring is pinned by source.
/// </summary>
[Collection(nameof(UiFireAndForgetReportCollection))]
public sealed partial class Ui12FireAndForgetGuardTests
{
    [Fact]
    public void ACommandWhoseWorkThrows_DoesNotRaiseOnTheUiContext_AndIsReportedByTypeOnly()
    {
        var reports = new List<(string Operation, string Type)>();
        UiFireAndForget.Report = (operation, type) => reports.Add((operation, type));
        try
        {
            var command = new AsyncRelayCommand(() => Task.FromException(new IOException("secret-host.example.test unreachable")));

            var raised = CapturingContext.Run(() => command.Execute(null));

            Assert.Empty(raised);
            Assert.Equal([(nameof(AsyncRelayCommand), nameof(IOException))], reports);
            Assert.True(command.CanExecute(null)); // the executing flag was released
        }
        finally
        {
            UiFireAndForget.Report = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_StillPropagates_ToCallersThatAwaitIt()
    {
        var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync());
    }

    [Fact]
    public async Task TheGuard_AppliesTheErrorState_AndTreatsCancellationAsNoFailure()
    {
        var failed = 0;
        await UiFireAndForget.RunAsync("op", () => Task.FromException(new InvalidOperationException()), () => failed++);
        Assert.Equal(1, failed);

        await UiFireAndForget.RunAsync("op", () => Task.FromCanceled(new CancellationToken(true)), () => failed++);
        await UiFireAndForget.RunAsync("op", () => Task.CompletedTask, () => failed++);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void TheEditorsFireAndForgetHandlers_AllGoThroughTheGuard()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");
        Assert.Contains("TestPanel.RetryRequested += async (_, _) => await UiFireAndForget.RunAsync(\"editor retest\", StartTestAsync);", page, StringComparison.Ordinal);
        Assert.Contains("await UiFireAndForget.RunAsync(\"editor trust\", () => _controller.AcceptTrustAsync(shown));", page, StringComparison.Ordinal);
        Assert.Contains("await UiFireAndForget.RunAsync(\"editor test\", StartTestAsync);", page, StringComparison.Ordinal);
        Assert.Contains("await UiFireAndForget.RunAsync(\"editor import\", _controller.OpenImportAsync);", page, StringComparison.Ordinal);
        Assert.Matches(SubmitIsGuarded(), page);

        // No other awaited call is left bare in an async void of the page.
        Assert.DoesNotMatch(BareAwaitInAsyncVoidLambda(), page);

        var form = AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs");
        Assert.Contains("browse.Click += async (_, _) => await UiFireAndForget.RunAsync(", form, StringComparison.Ordinal);

        var command = AppSourceTree.CodeWithoutComments("ViewModels/AsyncRelayCommand.cs");
        Assert.Contains("await UiFireAndForget.RunAsync(nameof(AsyncRelayCommand), () => ExecuteAsync(parameter));", command, StringComparison.Ordinal);

        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        Assert.Contains("UiFireAndForget.Report = (operation, exceptionType) =>", app, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"await UiFireAndForget\.RunAsync\(\s*""editor save"",\s*async \(\) => outcome = await _controller\.SubmitAsync\(ServerForm\.CaptureSecret\),\s*onFailed: \(\) => outcome = new ServerEditorSubmitOutcome\(ServerEditorSaveStatus\.Failed\)\);")]
    private static partial Regex SubmitIsGuarded();

    [GeneratedRegex(@"\+= async \(_, _\) => await (?!UiFireAndForget)")]
    private static partial Regex BareAwaitInAsyncVoidLambda();

    /// <summary>Records what an async void would re-raise on the context it captured (instead of crashing the runner).</summary>
    private sealed class CapturingContext : SynchronizationContext
    {
        private readonly List<Exception> _raised = [];

        public static IReadOnlyList<Exception> Run(Action action)
        {
            var previous = Current;
            var context = new CapturingContext();
            SetSynchronizationContext(context);
            try
            {
                action();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }

            return context._raised;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                d(state);
            }
            catch (Exception exception)
            {
                _raised.Add(exception);
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => Post(d, state);
    }
}

/// <summary><see cref="UiFireAndForget.Report"/> is process-wide: the tests that set it do not run in parallel.</summary>
[CollectionDefinition(nameof(UiFireAndForgetReportCollection), DisableParallelization = true)]
public sealed class UiFireAndForgetReportCollection;
