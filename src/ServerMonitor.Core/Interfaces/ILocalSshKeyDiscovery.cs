using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// M14.5 — read-only discovery of default private keys in <c>%USERPROFILE%\.ssh</c> (metadata only; never opens,
/// reads, copies or modifies a key, and never writes to <c>~/.ssh</c>). Never throws for I/O problems: an
/// unreadable or missing directory yields an empty list.
/// </summary>
public interface ILocalSshKeyDiscovery
{
    /// <summary>Default keys found, most recommended first; empty when none.</summary>
    Task<IReadOnlyList<LocalSshKey>> DiscoverAsync(CancellationToken cancellationToken = default);
}
