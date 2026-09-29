namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Location of <c>known-hosts.routes.json</c> (M14.4b-1 T2): trust for targets reached through a jump
/// host. Always a sibling of the direct <c>known-hosts.json</c> and never the same file — a released build
/// ignores this file, and the direct store never reads it.
/// </summary>
public sealed record RoutedHostKeyTrustStorageOptions
{
    public const string FileName = "known-hosts.routes.json";

    public required string FilePath { get; init; }

    public static RoutedHostKeyTrustStorageOptions From(HostKeyTrustStorageOptions directOptions)
    {
        ArgumentNullException.ThrowIfNull(directOptions);

        return new RoutedHostKeyTrustStorageOptions
        {
            FilePath = Path.Combine(
                Path.GetDirectoryName(directOptions.FilePath) ?? string.Empty,
                FileName)
        };
    }
}
