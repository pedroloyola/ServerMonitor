using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>Fakes shared by the M14.5 editor tests (key auto-select, checklist, helper).</summary>
internal static class OnboardingEditorTestKit
{
    public const string SshDirectory = @"C:\Users\tester\.ssh";

    public static LocalSshKey Ed25519(bool recommended = true) =>
        new(SshDirectory + @"\id_ed25519", "id_ed25519", LocalSshKeyKind.Ed25519, recommended);

    public static LocalSshKey Ecdsa(bool recommended = false) =>
        new(SshDirectory + @"\id_ecdsa", "id_ecdsa", LocalSshKeyKind.Ecdsa, recommended);

    public static LocalSshKey Rsa(bool recommended = false) =>
        new(SshDirectory + @"\id_rsa", "id_rsa", LocalSshKeyKind.Rsa, recommended);

    public static HostKeyIdentity HostKey(byte value = 7) =>
        HostKeyIdentity.Create("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()));

    public static Server SavedKeyServer(string? keyPath) => TestData.LinuxServer() with
    {
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = keyPath
    };

    /// <summary>An add-mode editor with the target filled in, ready for "Test connection".</summary>
    public static ServerEditorViewModel FilledAddEditor(
        ISshConnectionService ssh,
        IHostKeyTrustStore? trust = null,
        ILocalSshKeyDiscovery? discovery = null,
        ISshConfigImportSource? importSource = null,
        IPrivateKeyFilePicker? picker = null)
    {
        var editor = Editor(null, ssh, trust, discovery, importSource, picker);
        editor.Name = "web-01";
        editor.Host = "10.0.0.5";
        editor.Username = "deploy";
        editor.PrivateKeyPath = SshDirectory + @"\work_key";
        return editor;
    }

    public static ServerEditorViewModel Editor(
        Server? server = null,
        ISshConnectionService? ssh = null,
        IHostKeyTrustStore? trust = null,
        ILocalSshKeyDiscovery? discovery = null,
        ISshConfigImportSource? importSource = null,
        IPrivateKeyFilePicker? picker = null,
        ServerDiscoveryPrefill? prefill = null) =>
        new(
            new ServerValidator(),
            ssh ?? new StagedSsh(),
            trust ?? new RecordingTrustStore(),
            new FakeConnectionStateStore(),
            picker ?? new ScriptedPicker(),
            new FakeLocalizationService(),
            server,
            prefill,
            importSource,
            routedHostKeyTrustStore: null,
            localSshKeyDiscovery: discovery);

    /// <summary>Returns a fixed key list, optionally only once released, and counts/observes its calls.</summary>
    public sealed class FakeLocalSshKeyDiscovery(params LocalSshKey[] keys) : ILocalSshKeyDiscovery
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Blocked { get; init; }

        public Exception? Throws { get; init; }

        public int DiscoverCount { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<LocalSshKey>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            LastToken = cancellationToken;
            if (Throws is not null)
            {
                throw Throws;
            }

            if (Blocked)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return keys;
        }
    }

    public sealed class ScriptedPicker : IPrivateKeyFilePicker
    {
        public string? PickedPath { get; set; }

        public int PickCount { get; private set; }

        public Task<string?> PickAsync(CancellationToken cancellationToken = default)
        {
            PickCount++;
            return Task.FromResult(PickedPath);
        }
    }

    public sealed class RecordingTrustStore : IHostKeyTrustStore
    {
        public List<(SshEndpoint Endpoint, HostKeyIdentity Key)> Writes { get; } = [];

        public Exception? TrustThrows { get; set; }

        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult<TrustedHostKey?>(null);

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            if (TrustThrows is not null)
            {
                throw TrustThrows;
            }

            Writes.Add((endpoint, identity));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    /// <summary>One scripted "Test connection": the stages it reports, then how it ends.</summary>
    public sealed class Attempt
    {
        public SshConnectionStage[] Reports { get; init; } = [];

        public SshConnectionResult? Result { get; init; }

        public Exception? Throws { get; init; }

        /// <summary>When set, the attempt reports its stages and then waits here (or for cancellation).</summary>
        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Signalled once the stages were reported and the attempt is waiting on <see cref="Gate"/>.</summary>
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Connection service that plays <see cref="Attempt"/>s in order and keeps each request.</summary>
    public sealed class StagedSsh : ISshConnectionService
    {
        public Queue<Attempt> Attempts { get; } = new();

        public List<SshConnectionRequest> Requests { get; } = [];

        public StagedSsh Then(Attempt attempt)
        {
            Attempts.Enqueue(attempt);
            return this;
        }

        public Task<SshConnectionResult> ConnectAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the editor only tests connections");

        public Task<SshConnectionResult> DetectOperatingSystemAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the editor only tests connections");

        public async Task<SshConnectionResult> TestConnectionAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var attempt = Attempts.Count > 0 ? Attempts.Dequeue() : new Attempt { Result = TestData.Connected() };
            foreach (var stage in attempt.Reports)
            {
                request.StageProgress?.Report(stage);
            }

            if (attempt.Gate is not null)
            {
                attempt.Reported.TrySetResult();
                await attempt.Gate.Task.WaitAsync(cancellationToken);
            }

            if (attempt.Throws is not null)
            {
                throw attempt.Throws;
            }

            return attempt.Result ?? TestData.Connected();
        }
    }
}
