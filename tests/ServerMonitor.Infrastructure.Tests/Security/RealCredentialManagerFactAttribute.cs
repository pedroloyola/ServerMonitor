namespace ServerMonitor.Infrastructure.Tests.Security;

/// <summary>
/// A test that touches the REAL Windows Credential Manager. It runs only when
/// <c>SERVERMONITOR_RUN_REAL_CREDMAN_TESTS=1</c> is set (CI's ephemeral debug-test runner); everywhere else it is
/// SKIPPED with the reason shown, never silently passed. TEST-REALDATA-AUDIT R-1 (UI.3 gate 1C):
/// <see cref="RealCredentialManagerFenceTests"/> fails if the real store is constructed in any test without it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealCredentialManagerFactAttribute : FactAttribute
{
    public const string OptInVariable = "SERVERMONITOR_RUN_REAL_CREDMAN_TESTS";

    public RealCredentialManagerFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Real Credential Manager test: Windows only.";
        }
        else if (!IsOptedIn(Environment.GetEnvironmentVariable(OptInVariable)))
        {
            Skip = $"Real Credential Manager test: opt-in only, set {OptInVariable}=1 (CI debug-test runner only).";
        }
    }

    /// <summary>Exactly "1" opts in; anything else (unset, "true", "0", whitespace) does not.</summary>
    internal static bool IsOptedIn(string? value) => value == "1";
}
