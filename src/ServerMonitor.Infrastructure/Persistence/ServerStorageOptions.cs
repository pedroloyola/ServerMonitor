namespace ServerMonitor.Infrastructure.Persistence;

public sealed record ServerStorageOptions
{
    /// <summary>The routed-servers file name, always a sibling of <see cref="FilePath"/> (M14.4b-1 F3).</summary>
    public const string RoutedFileName = "routed-servers.json";

    /// <summary>The legacy <c>servers.json</c>: a bare array holding only DIRECT servers.</summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// <c>routed-servers.json</c> next to <see cref="FilePath"/>: a versioned envelope holding only servers
    /// with a route. A released build never reads it, so it never dials a routed server direct.
    /// </summary>
    public string RoutedFilePath => Path.Combine(
        Path.GetDirectoryName(FilePath) ?? string.Empty,
        RoutedFileName);

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
