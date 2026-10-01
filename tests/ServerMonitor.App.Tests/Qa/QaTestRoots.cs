using System.Runtime.CompilerServices;
using ServerMonitor.App.Qa;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// Vigil F-10: every test in this assembly uses its own QA root. Set once, before any test runs, so no test ever
/// reads, writes or deletes under the live %TEMP%\ServerMonitor-QA root that running harnesses share.
/// </summary>
internal static class QaTestRoots
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "ServerMonitor-QA-tests", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void IsolateQaRoots() => QaWindowPlacementIsolation.RootOverride = Root;
}
