namespace ServerMonitor.Core.Enums;

/// <summary>
/// M14.5 — the algorithm of a locally discovered default key, inferred from its OpenSSH default FILE NAME only.
/// The key file is never opened. Order is preference order (most recommended first).
/// </summary>
public enum LocalSshKeyKind
{
    Ed25519,
    Ecdsa,
    Rsa
}
