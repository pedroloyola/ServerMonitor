using ServerMonitor.Core.Enums;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.4b-2 §3: the classifier is STRUCTURAL — its inputs are booleans and a type-mapped code, never exception
/// text. The state space below is enumerated exhaustively (every flag combination × every mapped code), so the
/// separation invariants are checked over the whole domain, not over a few samples.
/// </summary>
public sealed class ProxyJumpFailureClassifierTests
{
    private static readonly SshConnectionErrorCode[] JumpCodes =
    [
        SshConnectionErrorCode.JumpConnectionFailed,
        SshConnectionErrorCode.JumpAuthenticationFailed,
        SshConnectionErrorCode.JumpHostKeyUnknown,
        SshConnectionErrorCode.JumpHostKeyMismatch,
        SshConnectionErrorCode.JumpCredentialUnavailable
    ];

    private static readonly SshConnectionErrorCode[] DirectNetworkCodes =
    [
        SshConnectionErrorCode.DnsResolutionFailed,
        SshConnectionErrorCode.ConnectionRefused,
        SshConnectionErrorCode.HostUnreachable,
        SshConnectionErrorCode.NetworkUnavailable
    ];

    [Fact]
    public void Jump_stage_only_ever_produces_jump_codes_or_the_stage_neutral_timeout()
    {
        foreach (var failure in AllStates(ProxyJumpStage.Jump).Where(f => !f.Cancelled))
        {
            var code = ProxyJumpFailureClassifier.Classify(failure);
            if (failure.TimedOut)
            {
                Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, code);
            }
            else
            {
                Assert.Contains(code, JumpCodes);
            }
        }
    }

    [Fact]
    public void Deadline_is_stage_neutral_after_cancellation()
    {
        foreach (var stage in new[] { ProxyJumpStage.Jump, ProxyJumpStage.Target })
        {
            foreach (var failure in AllStates(stage).Where(f => f.TimedOut))
            {
                Assert.Equal(
                    failure.Cancelled ? SshConnectionErrorCode.Cancelled : SshConnectionErrorCode.ConnectionTimedOut,
                    ProxyJumpFailureClassifier.Classify(failure));
            }
        }
    }

    [Fact]
    public void Target_stage_never_produces_a_jump_code_except_the_jump_dropping()
    {
        var sawDrop = false;
        foreach (var failure in AllStates(ProxyJumpStage.Target).Where(f => !f.Cancelled))
        {
            var code = ProxyJumpFailureClassifier.Classify(failure);
            if (code == SshConnectionErrorCode.JumpConnectionFailed)
            {
                Assert.False(failure.JumpConnected);
                sawDrop = true;
                continue;
            }

            Assert.DoesNotContain(code, JumpCodes);
            Assert.NotEqual(SshConnectionErrorCode.None, code);
            Assert.NotEqual(SshConnectionErrorCode.LocalTunnelFailed, code);
        }

        Assert.True(sawDrop);
    }

    [Fact]
    public void Nothing_through_a_jump_enters_the_direct_trust_flow_or_a_direct_network_diagnosis()
    {
        foreach (var stage in Enum.GetValues<ProxyJumpStage>())
        {
            foreach (var failure in AllStates(stage))
            {
                var code = ProxyJumpFailureClassifier.Classify(failure);
                Assert.NotEqual(SshConnectionErrorCode.HostKeyUnknown, code);
                Assert.NotEqual(SshConnectionErrorCode.HostKeyMismatch, code);
                Assert.DoesNotContain(code, DirectNetworkCodes);
            }
        }
    }

    [Fact]
    public void Cancellation_wins_on_every_stage()
    {
        foreach (var stage in Enum.GetValues<ProxyJumpStage>())
        {
            foreach (var failure in AllStates(stage).Where(f => f.Cancelled))
            {
                Assert.Equal(SshConnectionErrorCode.Cancelled, ProxyJumpFailureClassifier.Classify(failure));
            }
        }
    }

    [Fact]
    public void Tunnel_listen_is_always_local()
    {
        foreach (var failure in AllStates(ProxyJumpStage.TunnelListen).Where(f => !f.Cancelled))
        {
            Assert.Equal(SshConnectionErrorCode.LocalTunnelFailed, ProxyJumpFailureClassifier.Classify(failure));
        }
    }

    [Fact]
    public void A_target_that_never_identified_is_unreachable_via_the_jump_regardless_of_the_exception()
    {
        // Channel refused (prohibited), jump-side connect failure, target closed pre-banner: all look like this.
        foreach (var mapped in Enum.GetValues<SshConnectionErrorCode>().Where(c => c is not (SshConnectionErrorCode.ConnectionTimedOut)))
        {
            var failure = Target(mapped) with { IdentificationReceived = false };
            Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, ProxyJumpFailureClassifier.Classify(failure));
        }
    }

    [Fact]
    public void Order_is_cancelled_then_timeout_then_jump_drop_then_identification()
    {
        var worst = Target(SshConnectionErrorCode.RemoteDisconnected) with
        {
            Cancelled = true,
            TimedOut = true,
            JumpConnected = false,
            IdentificationReceived = false
        };

        Assert.Equal(SshConnectionErrorCode.Cancelled, ProxyJumpFailureClassifier.Classify(worst));
        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, ProxyJumpFailureClassifier.Classify(worst with { Cancelled = false }));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, ProxyJumpFailureClassifier.Classify(worst with { Cancelled = false, TimedOut = false }));
        Assert.Equal(
            SshConnectionErrorCode.TargetUnreachableViaJump,
            ProxyJumpFailureClassifier.Classify(worst with { Cancelled = false, TimedOut = false, JumpConnected = true }));
    }

    [Fact]
    public void Identified_target_shapes_keep_their_target_codes()
    {
        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyUnknown, ProxyJumpFailureClassifier.Classify(
            Target(SshConnectionErrorCode.HostKeyMismatch) with { HostKeyRejected = true, HostKeyUnknown = true }));
        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyMismatch, ProxyJumpFailureClassifier.Classify(
            Target(SshConnectionErrorCode.HostKeyMismatch) with { HostKeyRejected = true }));
        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.AuthenticationFailed)));
        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.ConnectionTimedOut)));
        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.Cancelled)));
        Assert.Equal(SshConnectionErrorCode.RemoteDisconnected, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.RemoteDisconnected)));
        Assert.Equal(SshConnectionErrorCode.ProtocolError, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.ProtocolError)));
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.ConnectionRefused)));
        Assert.Equal(SshConnectionErrorCode.Unexpected, ProxyJumpFailureClassifier.Classify(Target(SshConnectionErrorCode.None)));
    }

    [Fact]
    public void Measured_jump_shapes_map_to_their_jump_codes()
    {
        Assert.Equal(SshConnectionErrorCode.JumpAuthenticationFailed, Classify(Jump(SshConnectionErrorCode.AuthenticationFailed)));
        Assert.Equal(SshConnectionErrorCode.JumpHostKeyUnknown, Classify(Jump(SshConnectionErrorCode.HostKeyMismatch) with { HostKeyRejected = true, HostKeyUnknown = true }));
        Assert.Equal(SshConnectionErrorCode.JumpHostKeyMismatch, Classify(Jump(SshConnectionErrorCode.HostKeyMismatch) with { HostKeyRejected = true }));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, Classify(Jump(SshConnectionErrorCode.ConnectionRefused)));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, Classify(Jump(SshConnectionErrorCode.DnsResolutionFailed)));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, Classify(Jump(SshConnectionErrorCode.ConnectionTimedOut)));
        Assert.Equal(SshConnectionErrorCode.JumpCredentialUnavailable, Classify(Jump(SshConnectionErrorCode.PrivateKeyInvalid)));
        Assert.Equal(SshConnectionErrorCode.JumpCredentialUnavailable, Classify(Jump(SshConnectionErrorCode.PrivateKeyUnavailable)));
    }

    [Fact]
    public void Undefined_stage_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProxyJumpFailureClassifier.Classify(new ProxyJumpFailure { Stage = (ProxyJumpStage)99 }));
    }

    [Fact]
    public void The_classifier_has_no_exception_input()
    {
        // §3: the SSH.NET message-text dependency is gone; the only public entry point takes the state record.
        var method = Assert.Single(typeof(ProxyJumpFailureClassifier).GetMethods(), m => m.Name == "Classify");
        Assert.Equal([typeof(ProxyJumpFailure).MakeByRefType()], method.GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(
            typeof(ProxyJumpFailure).GetProperties(),
            p => typeof(Exception).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string));
    }

    private static SshConnectionErrorCode Classify(ProxyJumpFailure failure) => ProxyJumpFailureClassifier.Classify(failure);

    private static ProxyJumpFailure Jump(SshConnectionErrorCode mapped) => new()
    {
        Stage = ProxyJumpStage.Jump,
        MappedCode = mapped,
        IdentificationReceived = true
    };

    private static ProxyJumpFailure Target(SshConnectionErrorCode mapped) => new()
    {
        Stage = ProxyJumpStage.Target,
        MappedCode = mapped,
        JumpConnected = true,
        IdentificationReceived = true
    };

    private static IEnumerable<ProxyJumpFailure> AllStates(ProxyJumpStage stage)
    {
        bool[] bits = [false, true];
        foreach (var mapped in Enum.GetValues<SshConnectionErrorCode>())
        foreach (var cancelled in bits)
        foreach (var timedOut in bits)
        foreach (var jumpConnected in bits)
        foreach (var identified in bits)
        foreach (var rejected in bits)
        foreach (var unknown in bits)
        {
            yield return new ProxyJumpFailure
            {
                Stage = stage,
                MappedCode = mapped,
                Cancelled = cancelled,
                TimedOut = timedOut,
                JumpConnected = jumpConnected,
                IdentificationReceived = identified,
                HostKeyRejected = rejected,
                HostKeyUnknown = unknown
            };
        }
    }
}
