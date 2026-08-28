using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Security;

internal static class CredentialTargetName
{
    // Neutral, product-stable namespace (M12/ADR-017). New writes always use this prefix.
    internal const string ProductionPrefix = "ServerMonitor:v1:ssh";

    // Personal namespace shipped in M3–M11 (ADR-007). Read-only: credentials found under this
    // prefix are migrated forward to ProductionPrefix and never written anew.
    internal const string LegacyPrefix = "pedroloyola.ServerMonitor:v1:ssh";

    public static string Create(CredentialReference reference) =>
        Build(ProductionPrefix, reference);

    public static string CreateLegacy(CredentialReference reference) =>
        Build(LegacyPrefix, reference);

    private static string Build(string prefix, CredentialReference reference)
    {
        if (!reference.IsValid)
        {
            throw new ArgumentException("The credential reference is invalid.", nameof(reference));
        }

        var kind = reference.Kind switch
        {
            ServerCredentialKind.Password => "password",
            ServerCredentialKind.PrivateKeyPassphrase => "key-passphrase",
            _ => throw new ArgumentException("The credential kind is invalid.", nameof(reference))
        };

        return $"{prefix}:{reference.ServerId:N}:{kind}:{reference.ReferenceId:N}";
    }
}
