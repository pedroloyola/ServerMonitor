using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M14.4a SSH config import. Launch with
/// <c>--qa-ssh-config &lt;dir&gt;</c> to make "Import from SSH config" read
/// <c>&lt;dir&gt;\.ssh\config</c> (and its includes) instead of the real <c>~/.ssh</c>. It swaps only
/// the import source: the real, read-only <see cref="SshConfigFileImportSource"/> runs unchanged,
/// just rooted at the fixture profile. Fixtures: <c>tools/qa/ssh-config-fixtures.ps1</c>.
/// Excluded from Release (see ServerMonitor.App.csproj); the flag is ignored there.
/// </summary>
internal static class QaSshConfigComposition
{
    public static string? RequestedProfile() =>
        QaSshConfigProfilePolicy.ResolveProfile(Environment.GetCommandLineArgs(), isDebugBuild: true);

    /// <summary>Registered last so it wins over the real registration for every resolve.</summary>
    public static void Apply(IServiceCollection services, string profile) =>
        services.AddSingleton<ISshConfigImportSource>(new SshConfigFileImportSource(profile));
}
