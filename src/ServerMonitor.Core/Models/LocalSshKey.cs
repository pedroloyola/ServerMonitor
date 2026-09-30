using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

/// <summary>
/// M14.5 — a private key found at an OpenSSH default path directly inside <c>%USERPROFILE%\.ssh</c>.
/// Carries a PATH and metadata only: discovery never reads, copies or parses key contents.
/// </summary>
/// <param name="Path">Full path of the private key file.</param>
/// <param name="FileName">File name as shown to the user (e.g. <c>id_ed25519</c>).</param>
/// <param name="Kind">Algorithm implied by the default file name.</param>
/// <param name="IsRecommended">True for exactly one key: the most preferred kind found.</param>
public sealed record LocalSshKey(string Path, string FileName, LocalSshKeyKind Kind, bool IsRecommended);
