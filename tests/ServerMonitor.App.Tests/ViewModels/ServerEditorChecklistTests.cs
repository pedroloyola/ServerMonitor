using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using static ServerMonitor.App.Tests.ViewModels.OnboardingEditorTestKit;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.5 B2 — "Test connection" drives the checklist: the request carries a progress sink, live stages move
/// the rows, the result finalizes them, and the existing host-key trust flow is untouched.
/// </summary>
public sealed class ServerEditorChecklistTests
{
    private static ConnectionStepState[] States(ServerEditorViewModel editor) =>
        editor.ConnectionChecklist.Steps.Select(step => step.State).ToArray();

    private static readonly SshConnectionStage[] AllStages =
    [
        SshConnectionStage.PortReachable,
        SshConnectionStage.HostKeyVerified,
        SshConnectionStage.Authenticated,
        SshConnectionStage.OperatingSystemIdentified
    ];

    private static SshConnectionResult ConnectedLinux() => new()
    {
        State = ServerConnectionState.Connected,
        ReachedStage = SshConnectionStage.OperatingSystemIdentified,
        DetectedOperatingSystem = ServerOperatingSystem.Linux
    };

    [Fact]
    public async Task TestConnection_PassesAProgressSinkAndShowsLiveStagesBeforeTheResult()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = new Attempt
        {
            Reports = [SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified],
            Gate = gate,
            Result = ConnectedLinux()
        };
        var ssh = new StagedSsh().Then(attempt);
        using var editor = FilledAddEditor(ssh);

        var test = editor.TestConnectionAsync();
        await attempt.Reported.Task;

        // Mid-flight: two stages reported, the login is the step being checked.
        Assert.NotNull(Assert.Single(ssh.Requests).StageProgress);
        Assert.True(editor.IsTestingConnection);
        Assert.True(editor.HasConnectionStatus);
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Running,
                ConnectionStepState.Pending
            ],
            States(editor));

        gate.SetResult();
        await test;

        Assert.False(editor.IsTestingConnection);
        Assert.All(editor.ConnectionChecklist.Steps, step => Assert.Equal(ConnectionStepState.Passed, step.State));
        Assert.Equal("Linux", editor.ConnectionChecklist.Steps[3].StateText);
        Assert.Equal("ConnectionStateConnected", editor.ConnectionStatusMessage);
    }

    [Fact]
    public async Task TestConnection_WithoutAnyProgress_IsFinalizedFromTheResultAlone()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.AuthenticationFailed,
                ErrorCode = SshConnectionErrorCode.AuthenticationFailed,
                ReachedStage = SshConnectionStage.HostKeyVerified
            }
        });
        using var editor = FilledAddEditor(ssh);

        await editor.TestConnectionAsync();

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Failed,
                ConnectionStepState.NotReached
            ],
            States(editor));
        var failed = editor.ConnectionChecklist.Steps[2];
        Assert.Equal("ConnectionHintAuthenticationFailed", failed.Detail);
        Assert.True(failed.OffersPrepHelp);
        Assert.Equal("cat ~/.ssh/authorized_keys", failed.Command);
        // The summary line keeps today's wording.
        Assert.Equal("ConnectionErrorAuthenticationFailed", editor.ConnectionStatusMessage);
    }

    [Fact]
    public async Task ResultLowerThanTheReportedProgress_Wins()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Reports = AllStages,
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.ConnectionRefused,
                ReachedStage = SshConnectionStage.None
            }
        });
        using var editor = FilledAddEditor(ssh);

        await editor.TestConnectionAsync();

        Assert.Equal(
            [
                ConnectionStepState.Failed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(editor));
    }

    [Fact]
    public async Task UnknownHostKey_ShowsConfirmIdentityKeepsTheTrustPanelAndRetestsAfterTrust()
    {
        var key = HostKey();
        var endpoint = SshEndpoint.Create("10.0.0.5", 22);
        var ssh = new StagedSsh()
            .Then(new Attempt
            {
                Reports = [SshConnectionStage.PortReachable],
                Result = new SshConnectionResult
                {
                    State = ServerConnectionState.HostKeyUnknown,
                    ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
                    ReachedStage = SshConnectionStage.PortReachable,
                    PresentedHostKey = key,
                    HostKeyEndpoint = endpoint
                }
            })
            .Then(new Attempt { Reports = AllStages, Result = ConnectedLinux() });
        var trust = new RecordingTrustStore();
        using var editor = FilledAddEditor(ssh, trust);

        await editor.TestConnectionAsync();

        // Step 2 asks for the confirmation; the existing trust panel is what is shown, and nothing was trusted.
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.ActionRequired,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(editor));
        Assert.True(editor.HasUnknownHostKey);
        Assert.Equal(key.Sha256Fingerprint, editor.PresentedHostKeyFingerprint);
        Assert.Empty(trust.Writes);
        Assert.Single(ssh.Requests);

        await editor.TrustAndConnectAsync();

        Assert.Equal((endpoint, key), Assert.Single(trust.Writes));
        Assert.Equal(2, ssh.Requests.Count);
        Assert.NotSame(ssh.Requests[0].StageProgress, ssh.Requests[1].StageProgress);
        Assert.False(editor.HasUnknownHostKey);
        Assert.All(editor.ConnectionChecklist.Steps, step => Assert.Equal(ConnectionStepState.Passed, step.State));
    }

    [Fact]
    public async Task TrustStoreFailure_KeepsThePortStepAndFailsTheIdentityStep()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.HostKeyUnknown,
                ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
                ReachedStage = SshConnectionStage.PortReachable,
                PresentedHostKey = HostKey(),
                HostKeyEndpoint = SshEndpoint.Create("10.0.0.5", 22)
            }
        });
        var trust = new RecordingTrustStore { TrustThrows = new IOException("synthetic") };
        using var editor = FilledAddEditor(ssh, trust);
        await editor.TestConnectionAsync();

        await editor.TrustAndConnectAsync();

        Assert.Single(ssh.Requests);
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Failed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(editor));
        Assert.Equal("ConnectionHintUnexpected", editor.ConnectionChecklist.Steps[1].Detail);
    }

    [Fact]
    public async Task UnknownOperatingSystemAfterLogin_IsAWarningAndStillASuccess()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Reports = [SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified, SshConnectionStage.Authenticated],
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.Connected,
                ReachedStage = SshConnectionStage.Authenticated,
                DetectedOperatingSystem = ServerOperatingSystem.Unknown
            }
        });
        using var editor = FilledAddEditor(ssh);

        await editor.TestConnectionAsync();

        Assert.Equal(ConnectionStepState.Warning, editor.ConnectionChecklist.Steps[3].State);
        Assert.Equal("ConnectionStepStateOperatingSystemUnknown", editor.ConnectionChecklist.Steps[3].StateText);
        Assert.DoesNotContain(editor.ConnectionChecklist.Steps, step => step.IsFailed);
        Assert.Equal("ConnectionStateConnected", editor.ConnectionStatusMessage);
        // The unknown system is not written into the form, and the verified result is still what gets saved.
        Assert.Equal((int)ServerOperatingSystem.Auto, editor.SelectedOperatingSystemIndex);
        Assert.True(editor.TryCreateResult(out var result));
        using (result)
        {
            Assert.True(result!.ConnectionResult!.IsSuccess);
        }
    }

    [Fact]
    public async Task Cancel_StopsTheTestKeepsReportedStepsAndFailsNone()
    {
        var attempt = new Attempt
        {
            Reports = [SshConnectionStage.PortReachable],
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var ssh = new StagedSsh().Then(attempt);
        using var editor = FilledAddEditor(ssh);

        var test = editor.TestConnectionAsync();
        await attempt.Reported.Task;
        editor.CancelTest();
        await test;

        Assert.False(editor.IsTestingConnection);
        Assert.Equal("ConnectionErrorCancelled", editor.ConnectionStatusMessage);
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(editor));
        Assert.DoesNotContain(editor.ConnectionChecklist.Steps, step => step.IsFailed || step.IsRunning);
    }

    [Fact]
    public async Task CancelledResultFromTheService_UsesItsReachedStage()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Reports = [SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified],
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.Cancelled,
                ErrorCode = SshConnectionErrorCode.Cancelled,
                ReachedStage = SshConnectionStage.HostKeyVerified
            }
        });
        using var editor = FilledAddEditor(ssh);

        await editor.TestConnectionAsync();

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(editor));
    }

    [Fact]
    public async Task UnexpectedException_FailsTheStepAfterTheLastReportedStage()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Reports = [SshConnectionStage.PortReachable],
            Throws = new InvalidOperationException("synthetic")
        });
        using var editor = FilledAddEditor(ssh);

        await editor.TestConnectionAsync();

        Assert.Equal(ConnectionStepState.Passed, editor.ConnectionChecklist.Steps[0].State);
        Assert.Equal(ConnectionStepState.Failed, editor.ConnectionChecklist.Steps[1].State);
        Assert.Equal("ConnectionHintUnexpected", editor.ConnectionChecklist.Steps[1].Detail);
    }

    [Fact]
    public async Task ProgressReportedAfterTheResult_ChangesNothing()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.ConnectionRefused,
                ReachedStage = SshConnectionStage.None
            }
        });
        using var editor = FilledAddEditor(ssh);
        await editor.TestConnectionAsync();
        var before = States(editor);

        ssh.Requests[0].StageProgress!.Report(SshConnectionStage.Authenticated);

        Assert.Equal(before, States(editor));
        Assert.Equal(ConnectionStepState.Failed, editor.ConnectionChecklist.Steps[0].State);
    }

    [Fact]
    public async Task ProgressFromAnEarlierTest_CannotMoveTheCurrentOne()
    {
        var second = new Attempt
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Result = ConnectedLinux()
        };
        var ssh = new StagedSsh()
            .Then(new Attempt { Result = ConnectedLinux() })
            .Then(second);
        using var editor = FilledAddEditor(ssh);
        await editor.TestConnectionAsync();

        var test = editor.TestConnectionAsync();
        await second.Reported.Task;
        ssh.Requests[0].StageProgress!.Report(SshConnectionStage.Authenticated);

        Assert.Equal(ConnectionStepState.Running, editor.ConnectionChecklist.Steps[0].State);

        second.Gate!.SetResult();
        await test;
    }

    // Synchronous on purpose: with the queueing context installed, an await in the test body would post its
    // own continuation to a queue only this method pumps.
    [Fact]
    public void ProgressFromAWorkerThread_IsAppliedWhereTheEditorLives()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueueingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var attempt = new Attempt
            {
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Result = ConnectedLinux()
            };
            var ssh = new StagedSsh().Then(attempt);
            using var editor = FilledAddEditor(ssh);
            var test = editor.TestConnectionAsync();
            // Checked here, on the test thread: a null sink dereferenced on the worker would take the test host down.
            var progress = ssh.Requests[0].StageProgress;
            Assert.NotNull(progress);

            // A worker thread (no context of its own) reports: nothing may change until the owner runs it.
            var worker = new Thread(() => progress.Report(SshConnectionStage.PortReachable));
            worker.Start();
            worker.Join();

            Assert.Equal(1, context.Pending);
            Assert.Equal(ConnectionStepState.Running, editor.ConnectionChecklist.Steps[0].State);

            context.RunPending();
            Assert.Equal(ConnectionStepState.Passed, editor.ConnectionChecklist.Steps[0].State);
            Assert.Equal(ConnectionStepState.Running, editor.ConnectionChecklist.Steps[1].State);

            attempt.Gate!.SetResult();
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    context.RunPending();
                    return test.IsCompleted;
                },
                TimeSpan.FromSeconds(30)));
            Assert.All(editor.ConnectionChecklist.Steps, step => Assert.Equal(ConnectionStepState.Passed, step.State));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task RoutedServer_NamesTheJumpOnThePortStepAndDiagnosesJumpFailuresAsJumpFailures()
    {
        var ssh = new StagedSsh().Then(new Attempt
        {
            Result = new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.JumpConnectionFailed,
                ReachedStage = SshConnectionStage.None
            }
        });
        using var editor = FilledAddEditor(ssh);
        editor.UseJumpHost = true;
        editor.JumpHost = " bastion.example.test ";
        editor.JumpPort = "2222";
        editor.JumpUsername = "jumper";
        editor.JumpPrivateKeyPath = SshDirectory + @"\jump_key";

        await editor.TestConnectionAsync();

        var port = editor.ConnectionChecklist.Steps[0];
        Assert.Equal("ConnectionStepPortTitle (via bastion.example.test:2222)", port.Title);
        Assert.Equal(ConnectionStepState.Failed, port.State);
        Assert.Equal("ConnectionHintJumpConnectionFailed", port.Detail);
        Assert.Equal("ConnectionErrorJumpConnectionFailed", editor.ConnectionStatusMessage);
        Assert.All(
            editor.ConnectionChecklist.Steps.Skip(1),
            step => Assert.Equal(ConnectionStepState.NotReached, step.State));
    }

    [Fact]
    public async Task DirectServer_DoesNotMentionAJumpHost()
    {
        using var editor = FilledAddEditor(new StagedSsh());

        await editor.TestConnectionAsync();

        Assert.Equal("ConnectionStepPortTitle", editor.ConnectionChecklist.Steps[0].Title);
    }

    [Fact]
    public async Task EditingTheFormAfterATest_ClearsTheChecklist()
    {
        using var editor = FilledAddEditor(new StagedSsh().Then(new Attempt { Reports = AllStages, Result = ConnectedLinux() }));
        await editor.TestConnectionAsync();

        editor.Host = "10.0.0.6";

        Assert.False(editor.HasConnectionStatus);
        Assert.All(editor.ConnectionChecklist.Steps, step => Assert.Equal(ConnectionStepState.Pending, step.State));
    }

    [Fact]
    public async Task InvalidForm_StartsNoTestAndNoChecklist()
    {
        var ssh = new StagedSsh();
        using var editor = Editor(null, ssh);

        await editor.TestConnectionAsync();

        Assert.Empty(ssh.Requests);
        Assert.True(editor.HasValidationErrors);
        Assert.False(editor.HasConnectionStatus);
        Assert.All(editor.ConnectionChecklist.Steps, step => Assert.Equal(ConnectionStepState.Pending, step.State));
    }

    /// <summary>A context that only queues: the test decides when "the owner thread" runs what was posted.</summary>
    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int Pending
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count;
                }
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
            }
        }

        public void RunPending()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }

                    item = _queue.Dequeue();
                }

                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    item.Callback(item.State);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }
        }
    }
}
