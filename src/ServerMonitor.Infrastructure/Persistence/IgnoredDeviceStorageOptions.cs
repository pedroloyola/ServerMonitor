namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Location of the ignored-devices file. This is kept separate from <c>servers.json</c> and
/// from <c>known-hosts.json</c>: it holds only non-sensitive identity hashes of discovery
/// suggestions the user chose not to see, never hostnames, addresses, credentials or trust.
/// </summary>
public sealed record IgnoredDeviceStorageOptions
{
    public required string FilePath { get; init; }

    public static IgnoredDeviceStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return new IgnoredDeviceStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "ignored-devices.json")
        };
    }
}
