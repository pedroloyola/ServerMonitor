using System.Runtime.CompilerServices;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// UI.5 fix round 5 (Boss, Atlas C4): the PROOF that no App test runs a notice / toast countdown on the system clock. On
/// load of this test assembly - before any test - <see cref="TransientNoticeTimer.RejectSystemTimeProvider"/> is armed,
/// so <see cref="TransientNoticeTimer.Start"/> throws whenever its provider is <see cref="TimeProvider.System"/>, however
/// the clock was obtained (named, interpolation hole, <c>using static</c>, alias, reflection). The lexical
/// SystemClockGuardTests is only an advisory lint.
/// </summary>
internal static class SystemClockTestGuard
{
    [ModuleInitializer]
    internal static void Arm() => TransientNoticeTimer.RejectSystemTimeProvider = true;
}
