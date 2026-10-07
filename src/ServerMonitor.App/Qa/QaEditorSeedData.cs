using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.7 B-23): the synthetic saved servers an Edit starts from. Documentation addresses only (RFC 5737 / RFC 2606),
/// synthetic secrets, a key path inside the QA root (the editor never opens it). Written through the REAL profile service,
/// so the secrets land in the process-local credential store and the JSON on the QA root.
/// </summary>
internal static class QaEditorSeedData
{
    public static async Task WriteAsync(string seed, IServerProfileService profiles, string root, CancellationToken cancellationToken)
    {
        using var secret = seed == "password" ? new SecretValue("qa-synthetic-password") : null;
        using var jumpSecret = seed == "routed" ? new SecretValue("qa-synthetic-jump-password") : null;
        var result = await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = new ServerInput
            {
                Name = seed switch { "routed" => "private-db", "password" => "legacy-box", _ => "prod-web-01" },
                Host = seed == "routed" ? "10.0.0.5" : "192.0.2.10",
                Port = 22,
                Username = seed == "routed" ? "deploy" : "monitor",
                OperatingSystem = ServerOperatingSystem.Linux,
                AuthenticationMethod = seed == "password" ? AuthenticationMethod.Password : AuthenticationMethod.SshKey,
                PrivateKeyPath = seed == "password" ? null : Path.Combine(root, "profile", ".ssh", "id_ed25519"),
                Route = seed == "routed"
                    ? new ServerRoute
                    {
                        Jump = new JumpHop
                        {
                            Host = "bastion.example.com",
                            Port = 22,
                            Username = "admin",
                            AuthenticationMethod = AuthenticationMethod.Password
                        }
                    }
                    : null
            },
            CredentialChange = secret is null ? CredentialChange.Clear : CredentialChange.Replace(secret),
            JumpCredentialChange = jumpSecret is null ? null : CredentialChange.Replace(jumpSecret)
        }, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("The QA editor seed could not be written.");
        }
    }
}
