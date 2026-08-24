using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Security;

public sealed record CredentialChange
{
    public required CredentialChangeMode Mode { get; init; }

    public SecretValue? Secret { get; init; }

    public static CredentialChange Keep { get; } = new() { Mode = CredentialChangeMode.Keep };

    public static CredentialChange Clear { get; } = new() { Mode = CredentialChangeMode.Clear };

    public static CredentialChange Replace(SecretValue secret) => new()
    {
        Mode = CredentialChangeMode.Replace,
        Secret = secret ?? throw new ArgumentNullException(nameof(secret))
    };

    public override string ToString() => $"CredentialChange {{ Mode = {Mode}, Secret = [REDACTED] }}";
}
