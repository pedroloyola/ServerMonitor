using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.4b-2 §4 editor: the route section builds the draft's route, the jump secret travels only as the jump
/// override / jump credential change, and "Trust and connect" writes each hop's key to ITS store only.
/// </summary>
public sealed class ServerEditorRouteTests
{
    private static readonly SshEndpoint JumpEndpoint = SshEndpoint.Create("bastion.example.test", 2222);
    private static readonly SshEndpoint TargetEndpoint = SshEndpoint.Create("10.0.0.5", 22);
    private static readonly SshRoute Route = SshRoute.Create(JumpEndpoint, TargetEndpoint);
    private static readonly HostKeyIdentity JumpKey = Key(1);
    private static readonly HostKeyIdentity TargetKey = Key(2);

    [Fact]
    public async Task Two_step_trust_writes_the_jump_key_to_the_direct_store_then_the_target_key_to_the_routed_store()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(JumpUnknown());
        f.Ssh.Results.Enqueue(TargetUnknown());
        f.Ssh.Results.Enqueue(new SshConnectionResult { State = ServerConnectionState.Connected });
        var vm = f.RoutedEditor();

        await vm.TestConnectionAsync();
        Assert.True(vm.HasUnknownHostKey);
        Assert.Equal("jump host bastion.example.test:2222", vm.HostKeySubjectDisplay);

        await vm.TrustAndConnectAsync(); // step 1 → retest
        Assert.Equal([(JumpEndpoint, JumpKey)], f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
        Assert.True(vm.HasUnknownHostKey);
        Assert.Equal("target 10.0.0.5:22 via bastion.example.test:2222", vm.HostKeySubjectDisplay);

        await vm.TrustAndConnectAsync(); // step 2 → retest
        Assert.Equal([(JumpEndpoint, JumpKey)], f.Direct.Writes);
        Assert.Equal([(Route, TargetKey)], f.Routed.Writes);
        Assert.False(vm.HasUnknownHostKey);
        Assert.Equal(3, f.Ssh.Requests.Count);
        Assert.All(f.Ssh.Requests, r => Assert.NotNull(r.Server.Route));
    }

    [Fact]
    public async Task A_direct_servers_key_goes_to_the_direct_store_under_its_own_endpoint_only()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyUnknown,
            ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
            PresentedHostKey = TargetKey,
            HostKeyHop = SshHostKeyHop.Direct,
            HostKeyEndpoint = TargetEndpoint
        });
        var vm = f.Editor(TestData.LinuxServer() with { CredentialReferenceId = Guid.NewGuid() });

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.Equal([(TargetEndpoint, TargetKey)], f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Theory]
    [InlineData(SshHostKeyHop.Target)] // target key while the form is direct: never into the direct store
    [InlineData(SshHostKeyHop.Jump)]
    public async Task A_routed_hop_key_is_never_trusted_for_a_direct_form(SshHostKeyHop hop)
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(hop == SshHostKeyHop.Jump ? JumpUnknown() : TargetUnknown());
        var vm = f.Editor(TestData.LinuxServer() with { CredentialReferenceId = Guid.NewGuid() });

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task A_direct_hop_key_is_never_trusted_for_a_routed_form()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyUnknown,
            ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
            PresentedHostKey = TargetKey,
            HostKeyHop = SshHostKeyHop.Direct,
            HostKeyEndpoint = TargetEndpoint
        });
        var vm = f.RoutedEditor();

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task A_target_key_is_not_trusted_without_the_routed_store()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(TargetUnknown());
        var vm = f.RoutedEditor(withRoutedStore: false);

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task A_key_for_a_route_the_form_no_longer_describes_is_not_trusted()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(TargetUnknown());
        f.Ssh.Results.Enqueue(JumpUnknown());
        var vm = f.RoutedEditor();

        await vm.TestConnectionAsync();
        vm.JumpPort = "22"; // the route changed: the pending key is dropped with the result
        await vm.TrustAndConnectAsync();

        // Even a result naming an endpoint that differs from the form is refused.
        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.Empty(f.Routed.Writes);
        Assert.Empty(f.Direct.Writes);
    }

    [Fact]
    public async Task A_jump_key_that_changed_during_the_tunnel_open_names_the_jump_and_writes_nothing()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyMismatch,
            ErrorCode = SshConnectionErrorCode.JumpHostKeyMismatch,
            PresentedHostKey = Key(8),
            HostKeyHop = SshHostKeyHop.Jump,
            HostKeyEndpoint = JumpEndpoint,
            TrustedHostKey = new TrustedHostKey { Endpoint = JumpEndpoint, Identity = JumpKey }
        });
        var vm = f.RoutedEditor();

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.True(vm.HasHostKeyMismatch);
        Assert.Equal("jump host bastion.example.test:2222", vm.HostKeySubjectDisplay);
        Assert.Equal(JumpKey.Sha256Fingerprint, vm.TrustedHostKeyFingerprint);
        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task A_direct_prompt_without_an_endpoint_is_dismissed_without_writing()
    {
        var f = new Fixture();
        f.Ssh.Results.Enqueue(new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyUnknown,
            ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
            PresentedHostKey = TargetKey,
            HostKeyHop = SshHostKeyHop.Direct,
            HostKeyEndpoint = null
        });
        var vm = f.Editor(TestData.LinuxServer() with { CredentialReferenceId = Guid.NewGuid() });

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
        Assert.False(vm.HasUnknownHostKey);
    }

    [Fact]
    public async Task A_conflicting_routed_trust_shows_the_routed_mismatch_and_never_overwrites()
    {
        var f = new Fixture();
        f.Routed.Conflict = true;
        f.Routed.Entries[Route] = new TrustedRoutedHostKey { Route = Route, Identity = Key(9) };
        f.Ssh.Results.Enqueue(TargetUnknown());
        var vm = f.RoutedEditor();

        await vm.TestConnectionAsync();
        await vm.TrustAndConnectAsync();

        Assert.True(vm.HasHostKeyMismatch);
        Assert.Equal(Key(9).Sha256Fingerprint, vm.TrustedHostKeyFingerprint);
        Assert.Empty(f.Direct.Writes);
        Assert.Equal(Key(9), f.Routed.Entries[Route].Identity);
    }

    [Fact]
    public async Task The_route_section_builds_the_draft_route_and_the_jump_secret_is_only_the_jump_override()
    {
        var f = new Fixture();
        var vm = f.Editor(null);
        vm.Name = "web";
        vm.Host = "10.0.0.5";
        vm.Username = "deploy";
        vm.SelectedAuthenticationIndex = 1;
        vm.CaptureSecret("target-pw");
        vm.UseJumpHost = true;
        vm.JumpHost = "bastion.example.test";
        vm.JumpPort = "2222";
        vm.JumpUsername = "jumper";
        vm.SelectedJumpAuthenticationIndex = 1;
        vm.CaptureJumpSecret("jump-pw");

        await vm.TestConnectionAsync();

        var request = Assert.Single(f.Ssh.Requests);
        Assert.Equal("bastion.example.test", request.Server.Route!.Jump!.Host);
        Assert.Equal(2222, request.Server.Route.Jump.Port);
        Assert.Equal(AuthenticationMethod.Password, request.Server.Route.Jump.AuthenticationMethod);
        Assert.Equal("jump-pw", request.JumpCredentialOverride!.RevealAsString());
        Assert.Equal("target-pw", request.CredentialOverride!.RevealAsString());

        Assert.True(vm.TryCreateResult(out var result));
        Assert.Equal(CredentialChangeMode.Replace, result!.Profile.JumpCredentialChange!.Mode);
        Assert.Equal("jump-pw", result.Profile.JumpCredentialChange.Secret!.RevealAsString());
        Assert.Equal(CredentialChangeMode.Replace, result.Profile.CredentialChange.Mode);
        Assert.Equal("target-pw", result.Profile.CredentialChange.Secret!.RevealAsString());
        result.Dispose();
    }

    [Fact]
    public void A_jump_password_is_required_and_a_changed_jump_login_drops_the_saved_jump_secret()
    {
        var f = new Fixture();
        var vm = f.RoutedEditor();
        Assert.True(vm.HasSavedJumpSecret);

        vm.JumpUsername = "someone-else";

        Assert.False(vm.HasSavedJumpSecret);
        Assert.False(vm.TryCreateResult(out _)); // password jump with neither a saved nor a typed secret
        vm.CaptureJumpSecret("new-jump-pw");
        Assert.True(vm.TryCreateResult(out var result));
        Assert.Equal(CredentialChangeMode.Replace, result!.Profile.JumpCredentialChange!.Mode);
        result.Dispose();
    }

    [Fact]
    public void Unchecking_the_jump_host_is_the_only_way_to_make_the_server_direct()
    {
        var f = new Fixture();
        var vm = f.RoutedEditor();

        Assert.True(vm.TryCreateResult(out var kept));
        Assert.NotNull(kept!.Profile.Configuration.Route);
        Assert.Null(kept.Profile.JumpCredentialChange); // unchanged jump login keeps its secret

        vm.UseJumpHost = false;
        Assert.True(vm.TryCreateResult(out var direct));
        Assert.Null(direct!.Profile.Configuration.Route);
    }

    private static SshConnectionResult JumpUnknown() => new()
    {
        State = ServerConnectionState.HostKeyUnknown,
        ErrorCode = SshConnectionErrorCode.JumpHostKeyUnknown,
        PresentedHostKey = JumpKey,
        HostKeyHop = SshHostKeyHop.Jump,
        HostKeyEndpoint = JumpEndpoint
    };

    private static SshConnectionResult TargetUnknown() => new()
    {
        State = ServerConnectionState.HostKeyUnknown,
        ErrorCode = SshConnectionErrorCode.RoutedHostKeyUnknown,
        PresentedHostKey = TargetKey,
        HostKeyHop = SshHostKeyHop.Target,
        HostKeyRoute = Route
    };

    private static HostKeyIdentity Key(byte value) =>
        HostKeyIdentity.Create("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()));

    private sealed class Fixture
    {
        public ScriptedSsh Ssh { get; } = new();

        public DirectStore Direct { get; } = new();

        public RoutedStore Routed { get; } = new();

        public ServerEditorViewModel Editor(Server? server, bool withRoutedStore = true) => new(
            new ServerValidator(),
            Ssh,
            Direct,
            new FakeConnectionStateStore(),
            new NoPicker(),
            new FakeLocalizationService(),
            server,
            prefill: null,
            sshConfigImportSource: null,
            routedHostKeyTrustStore: withRoutedStore ? Routed : null);

        public ServerEditorViewModel RoutedEditor(bool withRoutedStore = true) => Editor(
            TestData.LinuxServer() with
            {
                CredentialReferenceId = Guid.NewGuid(),
                Route = new ServerRoute
                {
                    Jump = new JumpHop
                    {
                        Host = "bastion.example.test",
                        Port = 2222,
                        Username = "jumper",
                        AuthenticationMethod = AuthenticationMethod.Password,
                        CredentialReferenceId = Guid.NewGuid()
                    }
                }
            },
            withRoutedStore);
    }

    private sealed class ScriptedSsh : ISshConnectionService
    {
        public Queue<SshConnectionResult> Results { get; } = new();

        public List<SshConnectionRequest> Requests { get; } = [];

        public Task<SshConnectionResult> ConnectAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            Next(request);

        public Task<SshConnectionResult> TestConnectionAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            Next(request);

        public Task<SshConnectionResult> DetectOperatingSystemAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            Next(request);

        private Task<SshConnectionResult> Next(SshConnectionRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : TestData.Connected());
        }
    }

    private sealed class DirectStore : IHostKeyTrustStore
    {
        public List<(SshEndpoint, HostKeyIdentity)> Writes { get; } = [];

        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult<TrustedHostKey?>(null);

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            Writes.Add((endpoint, identity));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RoutedStore : IRoutedHostKeyTrustStore
    {
        public Dictionary<SshRoute, TrustedRoutedHostKey> Entries { get; } = [];

        public List<(SshRoute, HostKeyIdentity)> Writes { get; } = [];

        public bool Conflict { get; set; }

        public Task<TrustedRoutedHostKey?> GetAsync(SshRoute route, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.GetValueOrDefault(route));

        public Task TrustAsync(SshRoute route, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            if (Conflict)
            {
                throw new HostKeyTrustConflictException();
            }

            Writes.Add((route, identity));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshRoute route, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NoPicker : IPrivateKeyFilePicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
