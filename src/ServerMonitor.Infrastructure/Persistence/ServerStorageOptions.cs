namespace ServerMonitor.Infrastructure.Persistence;

public sealed record ServerStorageOptions
{
    public required string FilePath { get; init; }

    public static ServerStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return new ServerStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "servers.json")
        };
    }
}
