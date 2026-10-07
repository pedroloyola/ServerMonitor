using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7 B-7 (F-2, Cortex R-1/R-2, Vigil CP-6) and B-8 (F-3) on the REAL <see cref="ServerEditorViewModel"/>: nothing that
/// resumes after Dispose starts a connection, writes a store or changes a bound property; a key whose write was already
/// in flight may complete, but nothing after it runs; and a test of a draft never writes the saved server's state.
/// </summary>
public sealed class Ui7EditorLifetimeTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task R1_DisposeDuringATest_WritesNoStateAndChangesNothingAfterwards()
    {
        var viewModel = EditViewModel();
        var late = Ui7EditorSessionTests.RecordLateChanges(viewModel, out var markDisposed);
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var test = viewModel.TestConnectionAsync();
        await arrived;

        markDisposed();
        viewModel.Dispose();
        await test;

        Assert.Empty(late);
        Assert.Equal(0, _world.ConnectionStates.SetCount);
        Assert.True(viewModel.IsTestingConnection); // frozen as it was: no property changes after Dispose
    }

    /// <summary>A connection that ignores cancellation and answers after Dispose: the late result changes nothing.</summary>
    [Fact]
    public async Task R1_ALateResultThatIgnoredCancellation_IsDroppedAfterDispose()
    {
        var ssh = new LateSsh();
        var viewModel = new ServerEditorViewModel(
            new ServerValidator(), ssh, _world.DirectTrust, _world.ConnectionStates, new NullPicker(), new FakeLocalizationService(),
            Server(), routedHostKeyTrustStore: _world.RoutedTrust, localSshKeyDiscovery: new NoKeys());
        var late = Ui7EditorSessionTests.RecordLateChanges(viewModel, out var markDisposed);
        var test = viewModel.TestConnectionAsync();

        markDisposed();
        viewModel.Dispose();
        ssh.Answer.SetResult(TestData.Connected());
        await test;

        Assert.Empty(late);
        Assert.Equal(0, _world.ConnectionStates.SetCount);
        Assert.Equal("ConnectionStateConnecting", viewModel.ConnectionStatusMessage); // still the in-flight state
    }

    /// <summary>
    /// CP-6 / R-2 / F-2: Dispose while an accepted key is being written. The write already in flight completes (the user
    /// accepted it), but no retest starts (no new authenticated connection), no state is written, nothing changes.
    /// </summary>
    [Fact]
    public async Task CP6_DisposeWhileTheAcceptedKeyIsWritten_NeverRetests_NorWritesState()
    {
        var viewModel = AddViewModel();
        Ui7EditorSessionTests.FillDirect(viewModel);
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(SshEndpoint.Create("10.0.0.9", 22));
        await viewModel.TestConnectionAsync();
        _world.Ssh.Result = TestData.Connected();
        _world.DirectTrust.Barrier = new TaskCompletionSource();
        var late = Ui7EditorSessionTests.RecordLateChanges(viewModel, out var markDisposed);
        var trust = viewModel.TrustAndConnectAsync();

        markDisposed();
        viewModel.Dispose();
        _world.DirectTrust.Barrier.SetResult();
        await trust;

        Assert.Equal(1, _world.Ssh.TestConnectionCount); // only the first test: no retest after Dispose
        Assert.Equal(1, _world.DirectTrust.Trusts); // the accepted write completed (B-6)
        Assert.Equal(0, _world.ConnectionStates.SetCount);
        Assert.Empty(late);
    }

    [Fact]
    public async Task ADisposedEditor_StartsNoTest_AndTrustsNothing()
    {
        var viewModel = AddViewModel();
        Ui7EditorSessionTests.FillDirect(viewModel);
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(SshEndpoint.Create("10.0.0.9", 22));
        await viewModel.TestConnectionAsync();

        viewModel.Dispose();
        await viewModel.TestConnectionAsync();
        await viewModel.TrustAndConnectAsync();

        Assert.Equal(1, _world.Ssh.TestConnectionCount);
        Assert.Equal(0, _world.DirectTrust.Trusts);
    }

    /// <summary>B-8 (F-3): Edit tests of a DRAFT never write the saved server's connection state, whatever the outcome.</summary>
    [Theory]
    [InlineData(ServerConnectionState.Connected)]
    [InlineData(ServerConnectionState.Error)]
    public async Task F3_TestingAnEditDraft_NeverWritesTheSavedServersConnectionState(ServerConnectionState state)
    {
        var viewModel = EditViewModel();
        viewModel.Host = "10.0.0.77";
        _world.Ssh.Result = new SshConnectionResult
        {
            State = state,
            ErrorCode = state == ServerConnectionState.Connected ? SshConnectionErrorCode.None : SshConnectionErrorCode.ConnectionRefused
        };

        await viewModel.TestConnectionAsync();
        viewModel.CancelTest();

        Assert.True(viewModel.HasConnectionStatus);
        Assert.Equal(0, _world.ConnectionStates.SetCount);
    }

    [Fact]
    public async Task TheTrustWindow_CountsAsConnectionWork_AndIsRaised()
    {
        var viewModel = AddViewModel();
        Ui7EditorSessionTests.FillDirect(viewModel);
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(SshEndpoint.Create("10.0.0.9", 22));
        await viewModel.TestConnectionAsync();
        _world.Ssh.Result = TestData.Connected();
        _world.DirectTrust.Barrier = new TaskCompletionSource();
        var raised = new List<bool>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerEditorViewModel.IsConnectionWorkInProgress))
            {
                raised.Add(viewModel.IsConnectionWorkInProgress);
            }
        };

        var trust = viewModel.TrustAndConnectAsync();
        Assert.True(viewModel.IsConnectionWorkInProgress);
        _world.DirectTrust.Barrier.SetResult();
        await trust;

        Assert.False(viewModel.IsConnectionWorkInProgress);
        Assert.Equal(true, raised.First());
        Assert.Equal(false, raised.Last());
    }

    private ServerEditorViewModel AddViewModel() => new(
        new ServerValidator(), _world.Ssh, _world.DirectTrust, _world.ConnectionStates, new NullPicker(), new FakeLocalizationService(),
        server: null, routedHostKeyTrustStore: _world.RoutedTrust, localSshKeyDiscovery: new NoKeys());

    private ServerEditorViewModel EditViewModel() => new(
        new ServerValidator(), _world.Ssh, _world.DirectTrust, _world.ConnectionStates, new NullPicker(), new FakeLocalizationService(),
        Server(), routedHostKeyTrustStore: _world.RoutedTrust, localSshKeyDiscovery: new NoKeys());

    private Server Server() => new()
    {
        Id = Guid.NewGuid(),
        Name = "web-01",
        Host = "10.0.0.5",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = Path.Combine(_world.Directory, "id_ed25519"),
        CreatedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)
    };

    /// <summary>An SSH service that ignores cancellation and answers only when told.</summary>
    private sealed class LateSsh : Core.Interfaces.ISshConnectionService
    {
        public TaskCompletionSource<SshConnectionResult> Answer { get; } = new();

        public Task<SshConnectionResult> ConnectAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SshConnectionResult> DetectOperatingSystemAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SshConnectionResult> TestConnectionAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            Answer.Task;
    }
}
