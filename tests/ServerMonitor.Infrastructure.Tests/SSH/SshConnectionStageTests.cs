using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Workloads;
using ServerMonitor.Infrastructure.SSH;
using static ServerMonitor.Infrastructure.Tests.SSH.SshRoutedTestDoubles;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.5 A2: the PRODUCTION <see cref="SshConnectionService"/> sets <see cref="SshConnectionResult.ReachedStage"/>
/// from what it observed, and reports <see cref="SshConnectionRequest.StageProgress"/> once per step, in order,
/// never skipping and never beyond the result. Only the network (sessions, tunnel) is a double.
/// </summary>
public sealed class SshConnectionStageTests
{
    private static readonly SshEndpoint DirectEndpoint = SshEndpoint.Create("server.example", 22);
    private static readonly SshEndpoint JumpEndpoint = SshEndpoint.Create("bastion.example", 2222);
    private static readonly SshEndpoint TargetEndpoint = SshEndpoint.Create("10.0.0.5", 22);
    private static readonly SshRoute Route = SshRoute.Create(JumpEndpoint, TargetEndpoint);
    private static readonly HostKeyIdentity ServerKey = Key(1);
    private static readonly HostKeyIdentity OtherKey = Key(9);

    private static readonly SshConnectionStage[] AllStages =
    [
        SshConnectionStage.PortReachable,
        SshConnectionStage.HostKeyVerified,
        SshConnectionStage.Authenticated,
        SshConnectionStage.OperatingSystemIdentified
    ];

    // ---------------------------------------------------------------- direct

    [Theory]
    [InlineData(SshConnectionErrorCode.DnsResolutionFailed)]
    [InlineData(SshConnectionErrorCode.ConnectionRefused)]
    [InlineData(SshConnectionErrorCode.HostUnreachable)]
    [InlineData(SshConnectionErrorCode.NetworkUnavailable)]
    [InlineData(SshConnectionErrorCode.ConnectionTimedOut)]
    [InlineData(SshConnectionErrorCode.ProtocolError)]
    [InlineData(SshConnectionErrorCode.UnsupportedAlgorithm)]
    [InlineData(SshConnectionErrorCode.RemoteDisconnected)]
    [InlineData(SshConnectionErrorCode.Unexpected)]
    public async Task Direct_probe_failing_before_any_banner_or_key_reached_nothing(SshConnectionErrorCode code)
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Fails(code, identificationReceived: false));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    [Theory]
    [InlineData(SshConnectionErrorCode.ProtocolError)]
    [InlineData(SshConnectionErrorCode.UnsupportedAlgorithm)]
    [InlineData(SshConnectionErrorCode.RemoteDisconnected)]
    public async Task Direct_probe_failing_after_the_banner_reached_the_port(SshConnectionErrorCode code)
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Fails(code, identificationReceived: true));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(SshConnectionStage.PortReachable, result.ReachedStage);
        Assert.Equal([SshConnectionStage.PortReachable], f.Progress.Reports);
    }

    [Fact]
    public async Task Invalid_configuration_reached_nothing_and_dials_nothing()
    {
        var f = new Fixture();

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(server => server with { Host = " " }));

        Assert.Equal(SshConnectionErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
        Assert.Empty(f.Sessions.Probes.Dequeued);
    }

    [Fact]
    public async Task First_sight_of_a_host_key_is_never_verified()
    {
        var f = new Fixture(); // nothing trusted
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(SshConnectionErrorCode.HostKeyUnknown, result.ErrorCode);
        Assert.Equal(SshConnectionStage.PortReachable, result.ReachedStage);
        Assert.Equal([SshConnectionStage.PortReachable], f.Progress.Reports);
        Assert.Empty(f.Credentials.Reads);
        Assert.Empty(f.Direct.Writes); // still no auto-TOFU
    }

    [Fact]
    public async Task Changed_host_key_stops_at_the_port()
    {
        var f = new Fixture().TrustDirect(OtherKey);
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(SshConnectionErrorCode.HostKeyMismatch, result.ErrorCode);
        Assert.Equal(SshConnectionStage.PortReachable, result.ReachedStage);
        Assert.Equal([SshConnectionStage.PortReachable], f.Progress.Reports);
    }

    [Fact]
    public async Task Authentication_failure_stops_at_the_verified_key()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task Missing_password_reference_stops_at_the_verified_key()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(server => server with { CredentialReferenceId = null }));

        Assert.Equal(SshConnectionErrorCode.CredentialNotConfigured, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task Unavailable_stored_password_stops_at_the_verified_key()
    {
        var f = new Fixture().TrustDirect();
        f.Credentials.Secrets.Clear();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(SshConnectionErrorCode.CredentialUnavailable, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task Missing_private_key_path_stops_at_the_verified_key()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(server => server with
        {
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = null,
            CredentialReferenceId = null
        }));

        Assert.Equal(SshConnectionErrorCode.PrivateKeyUnavailable, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Theory]
    [InlineData(true, SshConnectionErrorCode.PrivateKeyInvalid)]
    [InlineData(false, SshConnectionErrorCode.PrivateKeyUnavailable)]
    public async Task Unloadable_private_key_stops_at_the_verified_key(bool invalid, SshConnectionErrorCode expected)
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.PrivateKeyException = invalid
            ? new SshPrivateKeyLoadException(new FormatException())
            : new IOException("locked");
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(server => server with
        {
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = @"C:\keys\id_ed25519",
            CredentialReferenceId = null
        }));

        Assert.Equal(expected, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public async Task Identified_operating_system_completes_all_four_steps(ServerOperatingSystem os)
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None, os));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(os, result.DetectedOperatingSystem);
        AssertStages(result, f.Progress, SshConnectionStage.OperatingSystemIdentified);
    }

    [Fact]
    public async Task Unknown_operating_system_is_a_success_that_stops_at_authenticated()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None, ServerOperatingSystem.Unknown));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(ServerOperatingSystem.Unknown, result.DetectedOperatingSystem);
        AssertStages(result, f.Progress, SshConnectionStage.Authenticated);
    }

    [Fact]
    public async Task Connect_without_os_detection_stops_at_authenticated()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None));

        var result = await f.Service.ConnectAsync(f.DirectRequest());

        Assert.True(result.IsSuccess);
        AssertStages(result, f.Progress, SshConnectionStage.Authenticated);
    }

    [Fact]
    public async Task Caller_cancel_during_the_probe_keeps_its_code_and_reached_nothing()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.WaitsForCancellation(key: null, authenticationCompleted: false));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(), cancel.Token);

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    [Fact]
    public async Task Deadline_during_the_probe_keeps_its_code_and_reached_nothing()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.WaitsForCancellation(key: null, authenticationCompleted: false));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
    }

    [Fact]
    public async Task Caller_cancel_during_authentication_keeps_the_verified_key()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.WaitsForCancellation(ServerKey, authenticationCompleted: false));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(), cancel.Token);

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task Caller_cancel_after_authentication_during_uname_keeps_authenticated()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.WaitsForCancellation(ServerKey, authenticationCompleted: true));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(), cancel.Token);

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.Authenticated);
    }

    [Fact]
    public async Task Key_changing_between_probe_and_authentication_never_moves_the_result_below_what_was_reported()
    {
        // The probe matched the trusted key (step 2 was genuinely observed and reported); the authenticated
        // connection then presented another key and was refused before any credential was sent. The code is the
        // mismatch; the result never claims less than the progress already showed.
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(OtherKey, SshConnectionErrorCode.None));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest());

        Assert.Equal(SshConnectionErrorCode.HostKeyMismatch, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task A_throwing_progress_never_changes_the_outcome_and_only_its_type_is_logged()
    {
        var quiet = new Fixture().TrustDirect();
        quiet.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        quiet.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None, ServerOperatingSystem.Linux));
        var expected = await quiet.Service.TestConnectionAsync(quiet.DirectRequest());

        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None, ServerOperatingSystem.Linux));
        var throwing = new ThrowingProgress();

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(progress: throwing));

        Assert.Equal(expected with { Duration = result.Duration }, result);
        Assert.True(result.IsSuccess);
        Assert.Equal(SshConnectionStage.OperatingSystemIdentified, result.ReachedStage);
        Assert.Equal(AllStages, throwing.Attempts); // every step still offered once, in order
        Assert.Equal(4, f.Logger.Messages.Count(m => m.Contains(nameof(InvalidTimeZoneException), StringComparison.Ordinal)));
        Assert.DoesNotContain(f.Logger.Messages, m => m.Contains(ThrowingProgress.SecretLookingMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_progress_sink_still_sets_the_reached_stage()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.DirectRequest(progress: null, useProgress: false));

        Assert.Equal(SshConnectionStage.HostKeyVerified, result.ReachedStage);
    }

    // ---------------------------------------------------- monitoring / collection

    [Fact]
    public async Task Linux_collection_is_unchanged_and_carries_its_stage_without_any_progress()
    {
        var f = new Fixture().TrustDirect();
        var raw = new Collectors.Linux.LinuxMetricsRawData { MemInfo = "MemTotal: 1 kB" };
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None, linuxMetrics: raw));

        var result = await f.Service.CollectAsync(f.DirectRequest().Server, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

        Assert.True(result.ConnectionResult.IsSuccess);
        Assert.Same(raw, result.Data);
        Assert.Equal(ServerConnectionState.Connected, result.ConnectionResult.State);
        Assert.Equal(SshConnectionStage.Authenticated, result.ConnectionResult.ReachedStage);
        Assert.Empty(f.Progress.Reports);
        Assert.DoesNotContain(f.Logger.Messages, m => m.Contains("progress", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Workload_collection_failure_is_unchanged_and_carries_its_stage()
    {
        var f = new Fixture().TrustDirect();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Sessions.Authenticated.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.CollectAsync(
            f.DirectRequest().Server,
            new WorkloadRemoteRequest { Timeout = TimeSpan.FromSeconds(2) });

        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, result.ConnectionResult.ErrorCode);
        Assert.Equal(ServerConnectionState.AuthenticationFailed, result.ConnectionResult.State);
        Assert.Null(result.Data);
        Assert.Equal(SshConnectionStage.HostKeyVerified, result.ConnectionResult.ReachedStage);
    }

    // ---------------------------------------------------------------- routed

    [Fact]
    public async Task Routed_jump_probe_failure_reached_nothing()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Fails(SshConnectionErrorCode.ConnectionRefused, identificationReceived: false));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    [Fact]
    public async Task Routed_unknown_jump_key_reached_nothing()
    {
        var f = new Fixture().TrustTarget(); // jump not trusted
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.JumpHostKeyUnknown, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    [Fact]
    public async Task Routed_changed_jump_key_reached_nothing()
    {
        var f = new Fixture().TrustJump(OtherKey).TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.JumpHostKeyMismatch, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
    }

    [Fact]
    public async Task Routed_jump_credential_unavailable_reached_nothing()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Credentials.Secrets.Remove(ServerCredentialKind.JumpPassword);
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.JumpCredentialUnavailable, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
    }

    [Fact]
    public async Task Routed_jump_authentication_failure_reached_nothing()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.OpenResult = new JumpTunnelOpenResult(
            JumpTunnelOpenStage.Jump,
            new SshSessionResult { ErrorCode = SshConnectionErrorCode.AuthenticationFailed, IdentificationReceived = true, PresentedHostKey = Key(1) });

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.JumpAuthenticationFailed, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
    }

    [Fact]
    public async Task Routed_local_tunnel_failure_reached_nothing()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.OpenResult = new JumpTunnelOpenResult(JumpTunnelOpenStage.TunnelListen, null, "SocketException");

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.LocalTunnelFailed, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
    }

    [Fact]
    public async Task Routed_target_unreachable_through_the_jump_reached_nothing()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Fails(SshConnectionErrorCode.RemoteDisconnected, identificationReceived: false));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    [Fact]
    public async Task Routed_jump_dropping_while_the_target_answers_is_a_jump_failure_that_reached_nothing()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.JumpConnected = false;
        f.Tunnels.Targets.Enqueue(ScriptedSession.Fails(SshConnectionErrorCode.ProtocolError, identificationReceived: true));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    [Fact]
    public async Task Routed_first_sight_of_the_target_key_is_never_verified()
    {
        var f = new Fixture().TrustJump(); // target not trusted
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyUnknown, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.PortReachable);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task Routed_changed_target_key_stops_at_the_port()
    {
        var f = new Fixture().TrustJump().TrustTarget(OtherKey);
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyMismatch, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.PortReachable);
    }

    [Fact]
    public async Task Routed_target_verified_by_the_ROUTED_store_only()
    {
        // The direct store trusts the target's bare endpoint with the presented key; only the routed store
        // (which does not know it) may verify a routed target.
        var f = new Fixture().TrustJump().TrustDirect();
        f.Direct.Entries[TargetEndpoint] = new TrustedHostKey { Endpoint = TargetEndpoint, Identity = ServerKey };
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyUnknown, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.PortReachable);
    }

    [Fact]
    public async Task Routed_target_authentication_failure_stops_at_the_verified_key()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task Routed_target_credential_unavailable_stops_at_the_verified_key()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Credentials.Secrets.Remove(ServerCredentialKind.Password);
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.Equal(SshConnectionErrorCode.CredentialUnavailable, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux, SshConnectionStage.OperatingSystemIdentified)]
    [InlineData(ServerOperatingSystem.MacOS, SshConnectionStage.OperatingSystemIdentified)]
    [InlineData(ServerOperatingSystem.Unknown, SshConnectionStage.Authenticated)]
    public async Task Routed_success_describes_the_target(ServerOperatingSystem os, SshConnectionStage expected)
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.None, os));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest());

        Assert.True(result.IsSuccess);
        AssertStages(result, f.Progress, expected);
    }

    [Fact]
    public async Task Routed_cancel_during_target_authentication_keeps_the_verified_key()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(ScriptedSession.Presents(Key(1), SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.Presents(ServerKey, SshConnectionErrorCode.AuthenticationFailed));
        f.Tunnels.Targets.Enqueue(ScriptedSession.WaitsForCancellation(ServerKey, authenticationCompleted: false));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest(), cancel.Token);

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
        AssertStages(result, f.Progress, SshConnectionStage.HostKeyVerified);
    }

    [Fact]
    public async Task Routed_invalid_route_reached_nothing()
    {
        var f = new Fixture();

        var result = await f.Service.TestConnectionAsync(f.RoutedRequest(jump => jump with { Host = "" }));

        Assert.Equal(SshConnectionErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(f.Progress.Reports);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The result's stage is <paramref name="expected"/> and the progress saw exactly every step up to it, in order, once.</summary>
    private static void AssertStages(SshConnectionResult result, RecordingProgress progress, SshConnectionStage expected)
    {
        Assert.Equal(expected, result.ReachedStage);
        Assert.Equal(AllStages.Where(stage => stage <= expected), progress.Reports);
    }

    private static HostKeyIdentity Key(byte value) => SshRoutedTestDoubles.Key(value);

    private sealed class Fixture
    {
        public Fixture()
        {
            Direct = new DirectTrustStore();
            Routed = new RoutedTrustStore();
            Credentials = new CredentialStore();
            Credentials.Secrets[ServerCredentialKind.Password] = "target-secret";
            Credentials.Secrets[ServerCredentialKind.JumpPassword] = "jump-secret";
            Service = new SshConnectionService(Direct, Routed, Credentials, Logger, Sessions, Tunnels);
        }

        public DirectTrustStore Direct { get; }

        public RoutedTrustStore Routed { get; }

        public CredentialStore Credentials { get; }

        public ScriptedSessionFactory Sessions { get; } = new();

        public ScriptedTunnelFactory Tunnels { get; } = new();

        public RecordingProgress Progress { get; } = new();

        public ListLogger Logger { get; } = new();

        public SshConnectionService Service { get; }

        public Fixture TrustDirect(HostKeyIdentity? identity = null)
        {
            Direct.Entries[DirectEndpoint] = new TrustedHostKey { Endpoint = DirectEndpoint, Identity = identity ?? ServerKey };
            return this;
        }

        public Fixture TrustJump(HostKeyIdentity? identity = null)
        {
            Direct.Entries[JumpEndpoint] = new TrustedHostKey { Endpoint = JumpEndpoint, Identity = identity ?? Key(1) };
            return this;
        }

        public Fixture TrustTarget(HostKeyIdentity? identity = null)
        {
            Routed.Entries[Route] = new TrustedRoutedHostKey { Route = Route, Identity = identity ?? ServerKey };
            return this;
        }

        public SshConnectionRequest DirectRequest(
            Func<Server, Server>? configure = null,
            TimeSpan? timeout = null,
            IProgress<SshConnectionStage>? progress = null,
            bool useProgress = true)
        {
            var server = new Server
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Direct",
                Host = "server.example",
                Port = 22,
                Username = "tester",
                AuthenticationMethod = AuthenticationMethod.Password,
                CredentialReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            };
            return new SshConnectionRequest
            {
                Server = configure?.Invoke(server) ?? server,
                Timeout = timeout ?? TimeSpan.FromSeconds(5),
                StageProgress = useProgress ? progress ?? Progress : null
            };
        }

        public SshConnectionRequest RoutedRequest(Func<JumpHop, JumpHop>? configureJump = null)
        {
            var jump = new JumpHop
            {
                Host = "bastion.example",
                Port = 2222,
                Username = "jumper",
                AuthenticationMethod = AuthenticationMethod.Password,
                CredentialReferenceId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            };
            return new SshConnectionRequest
            {
                Server = new Server
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Name = "Routed",
                    Host = "10.0.0.5",
                    Port = 22,
                    Username = "tester",
                    AuthenticationMethod = AuthenticationMethod.Password,
                    CredentialReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Route = new ServerRoute { Jump = configureJump?.Invoke(jump) ?? jump }
                },
                Timeout = TimeSpan.FromSeconds(5),
                StageProgress = Progress
            };
        }
    }

    /// <summary>Records synchronously (unlike <see cref="Progress{T}"/>, which posts), so the order is exact.</summary>
    private sealed class RecordingProgress : IProgress<SshConnectionStage>
    {
        public List<SshConnectionStage> Reports { get; } = [];

        public void Report(SshConnectionStage value) => Reports.Add(value);
    }

    private sealed class ThrowingProgress : IProgress<SshConnectionStage>
    {
        public const string SecretLookingMessage = "observer-detail-that-must-not-be-logged";

        public List<SshConnectionStage> Attempts { get; } = [];

        public void Report(SshConnectionStage value)
        {
            Attempts.Add(value);
            throw new InvalidTimeZoneException(SecretLookingMessage);
        }
    }

    /// <summary>A queue that remembers what was taken, so a test can prove nothing was dialled.</summary>
    private sealed class SessionQueue
    {
        private readonly Queue<ISshSession> _queue = new();

        public List<ISshSession> Dequeued { get; } = [];

        public void Enqueue(ISshSession session) => _queue.Enqueue(session);

        public ISshSession Take()
        {
            var session = _queue.Dequeue();
            Dequeued.Add(session);
            return session;
        }
    }

    private sealed class ScriptedSessionFactory : ISshSessionFactory
    {
        /// <summary>Direct host-key probes and jump host-key probes.</summary>
        public SessionQueue Probes { get; } = new();

        public SessionQueue Authenticated { get; } = new();

        public Exception? PrivateKeyException { get; set; }

        public ISshSession CreateHostKeyProbe(Server server, TimeSpan timeout) => Probes.Take();

        public ISshSession CreateJumpHostKeyProbe(SshDialTarget jump, TimeSpan timeout) => Probes.Take();

        public ISshSession CreatePasswordSession(Server server, string password, TimeSpan timeout) => Authenticated.Take();

        public ISshSession CreatePrivateKeySession(Server server, string privateKeyPath, string? passphrase, TimeSpan timeout) =>
            PrivateKeyException is { } exception ? throw exception : Authenticated.Take();
    }

    private sealed class ScriptedTunnelFactory : IJumpTunnelFactory
    {
        public SessionQueue Targets { get; } = new();

        public JumpTunnelOpenResult? OpenResult { get; set; }

        public bool JumpConnected { get; set; } = true;

        public IJumpTunnel Create(SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target, TimeSpan timeout) =>
            new ScriptedTunnel(this);

        private sealed class ScriptedTunnel(ScriptedTunnelFactory owner) : IJumpTunnel
        {
            public bool IsJumpConnected => owner.JumpConnected;

            public Task<JumpTunnelOpenResult> OpenAsync(Func<HostKeyIdentity, bool> jumpHostKeyVerifier, CancellationToken cancellationToken) =>
                Task.FromResult(owner.OpenResult ?? new JumpTunnelOpenResult(
                    JumpTunnelOpenStage.Opened,
                    new SshSessionResult { ErrorCode = SshConnectionErrorCode.None, IdentificationReceived = true }));

            public ISshSession CreateTargetSession(string username, SshLogin login, TimeSpan timeout) => owner.Targets.Take();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>One scripted connection attempt; every operation runs the same script.</summary>
    private sealed class ScriptedSession(Func<Func<HostKeyIdentity, bool>, CancellationToken, Task<SshSessionResult>> run) : ISshSession
    {
        /// <summary>Presents <paramref name="key"/>: <paramref name="whenTrusted"/> if the verifier accepts it, else refused before auth.</summary>
        public static ScriptedSession Presents(
            HostKeyIdentity key,
            SshConnectionErrorCode whenTrusted,
            ServerOperatingSystem os = ServerOperatingSystem.Unknown,
            Collectors.Linux.LinuxMetricsRawData? linuxMetrics = null) =>
            new((verifier, _) => Task.FromResult(verifier(key)
                ? new SshSessionResult
                {
                    ErrorCode = whenTrusted,
                    PresentedHostKey = key,
                    IdentificationReceived = true,
                    DetectedOperatingSystem = os,
                    LinuxMetrics = linuxMetrics
                }
                : new SshSessionResult
                {
                    ErrorCode = SshConnectionErrorCode.HostKeyMismatch,
                    HostKeyRejected = true,
                    PresentedHostKey = key,
                    IdentificationReceived = true
                }));

        public static ScriptedSession Fails(SshConnectionErrorCode code, bool identificationReceived) =>
            new((_, _) => Task.FromResult(new SshSessionResult { ErrorCode = code, IdentificationReceived = identificationReceived }));

        /// <summary>As SshNetSession: a cancelled connect is caught and returned as <see cref="SshConnectionErrorCode.Cancelled"/>.</summary>
        public static ScriptedSession WaitsForCancellation(HostKeyIdentity? key, bool authenticationCompleted) =>
            new(async (verifier, token) =>
            {
                if (key is not null && !verifier(key))
                {
                    throw new InvalidOperationException("The scripted key must be trusted here.");
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                }

                return new SshSessionResult
                {
                    ErrorCode = SshConnectionErrorCode.Cancelled,
                    PresentedHostKey = key,
                    IdentificationReceived = key is not null,
                    AuthenticationCompleted = authenticationCompleted
                };
            });

        public Task<SshSessionResult> ConnectAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
            run(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> DetectOperatingSystemAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
            run(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> CollectLinuxMetricsAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, TimeSpan cpuSampleInterval, CancellationToken cancellationToken) =>
            run(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> CollectMacOsMetricsAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
            run(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> CollectWorkloadsAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, WorkloadCollectionPlan plan, CancellationToken cancellationToken) =>
            run(hostKeyVerifier, cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger : ILogger<SshConnectionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
