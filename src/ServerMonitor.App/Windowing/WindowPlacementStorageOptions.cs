namespace ServerMonitor.App.Windowing;

public sealed record WindowPlacementStorageOptions
{
    public required string FilePath { get; init; }

    public static WindowPlacementStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new WindowPlacementStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "window-placement.json")
        };
    }
}
