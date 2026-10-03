using System.Reflection;
using System.Runtime.CompilerServices;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using Xunit.Sdk;

[assembly: SystemClockTestGuard.FailOnSystemClock]

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// UI.5 fix round 5 (Boss, Atlas C4): the PROOF that no App test runs a notice / toast countdown on the system clock. On
/// load of this test assembly - before any test - <see cref="TransientNoticeTimer.RejectSystemTimeProvider"/> is armed,
/// so <see cref="TransientNoticeTimer.Start"/> refuses a <see cref="TimeProvider.System"/> provider, however the clock
/// was obtained (named, interpolation hole, <c>using static</c>, alias, reflection). The refusal throws AND is recorded
/// for the running test; <see cref="FailOnSystemClockAttribute"/> (assembly-wide) fails that test afterwards - so a view
/// model that swallows the exception (the history commands catch everything) cannot hide it. The lexical
/// SystemClockGuardTests is only an advisory lint.
/// </summary>
internal static class SystemClockTestGuard
{
    private static readonly AsyncLocal<List<string>?> Current = new();

    [ModuleInitializer]
    internal static void Arm()
    {
        TransientNoticeTimer.SystemClockRejected = Record;
        TransientNoticeTimer.RejectSystemTimeProvider = true;
    }

    /// <summary>The refusals recorded for the running test so far, cleared (for the tests that provoke one on purpose).</summary>
    internal static IReadOnlyList<string> TakeViolations()
    {
        var violations = Current.Value;
        if (violations is null)
        {
            return [];
        }

        lock (violations)
        {
            var taken = violations.ToList();
            violations.Clear();
            return taken;
        }
    }

    private static void Record(string message)
    {
        if (Current.Value is { } violations)
        {
            lock (violations)
            {
                violations.Add(message);
            }
        }
    }

    /// <summary>Opens a per-test record before every App test and fails the test if anything was refused.</summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class FailOnSystemClockAttribute : BeforeAfterTestAttribute
    {
        public override void Before(MethodInfo methodUnderTest) => Current.Value = [];

        public override void After(MethodInfo methodUnderTest)
        {
            var violations = TakeViolations();
            Current.Value = null;
            if (violations.Count > 0)
            {
                throw new XunitException(
                    $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}: {violations.Count} notice countdown(s) on the SYSTEM clock. {violations[0]}");
            }
        }
    }
}
