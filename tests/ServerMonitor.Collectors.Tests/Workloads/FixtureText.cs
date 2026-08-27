namespace ServerMonitor.Collectors.Tests.Workloads;

internal static class FixtureText
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Workloads", "Fixtures", name));
}
