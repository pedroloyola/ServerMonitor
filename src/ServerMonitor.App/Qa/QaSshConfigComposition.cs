using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M14.4a SSH config import. Launch with
/// <c>--qa-ssh-config &lt;dir&gt;</c> to make "Import from SSH config" read
/// <c>&lt;dir&gt;\.ssh\config</c> (and its includes) instead of the real <c>~/.ssh</c>, and (M14.5) key
/// discovery look in <c>&lt;dir&gt;\.ssh</c>, so QA never touches the real <c>~/.ssh</c>. It swaps only
/// those two sources: the real, read-only <see cref="SshConfigFileImportSource"/> and
/// <see cref="LocalSshKeyDiscovery"/> run unchanged, just rooted at the fixture profile; the private-key
/// picker also starts there (M14.5 D-2b).
/// Fixtures: <c>tools/qa/ssh-config-fixtures.ps1</c>.
/// Excluded from Release (see ServerMonitor.App.csproj); the flag is ignored there.
/// </summary>
internal static class QaSshConfigComposition
{
    public static string? RequestedProfile() =>
        QaSshConfigProfilePolicy.ResolveProfile(Environment.GetCommandLineArgs(), isDebugBuild: true);

    /// <summary>Registered last so it wins over the real registration for every resolve.</summary>
    public static void Apply(IServiceCollection services, string profile)
    {
        services.AddSingleton<ISshConfigImportSource>(new SshConfigFileImportSource(profile));
        services.AddSingleton<ILocalSshKeyDiscovery>(new LocalSshKeyDiscovery(profile));
        // D-2b: "Browse..." starts at the fixture's .ssh, never the real one.
        services.AddSingleton<IPrivateKeyFilePicker>(sp =>
            new PrivateKeyFilePicker(sp.GetRequiredService<IWindowContext>(), profile));
    }
}
