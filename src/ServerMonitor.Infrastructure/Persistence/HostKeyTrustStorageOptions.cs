namespace ServerMonitor.Infrastructure.Persistence;

public sealed record HostKeyTrustStorageOptions
{
    public required string FilePath { get; init; }

    public static HostKeyTrustStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return new HostKeyTrustStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "known-hosts.json")
        };
    }
}
