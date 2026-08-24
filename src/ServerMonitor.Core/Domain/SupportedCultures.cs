namespace ServerMonitor.Core.Domain;

public static class SupportedCultures
{
    public const string Default = "pt-BR";

    public static IReadOnlyList<string> All { get; } = [Default, "en-US", "pt-PT"];

    public static bool IsSupported(string? cultureName) =>
        All.Contains(cultureName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? cultureName) =>
        All.FirstOrDefault(
            supported => string.Equals(supported, cultureName, StringComparison.OrdinalIgnoreCase))
        ?? Default;
}
