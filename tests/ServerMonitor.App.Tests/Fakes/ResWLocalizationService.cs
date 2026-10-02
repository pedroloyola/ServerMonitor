using System.Xml.Linq;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// <see cref="ILocalizationService"/> backed by the real <c>Resources.resw</c> of one culture, so tests can
/// assert the exact visible copy (pt-PT/pt-BR/en-US) without the WinRT resource loader. A missing key throws,
/// which turns a typo in a dynamically built key into a test failure instead of a silent fallback. The
/// culture is reported as the explicit UI language, which is what the VMs format dates with.
/// </summary>
internal sealed class ResWLocalizationService : ILocalizationService
{
    public static readonly string[] Cultures = ["pt-PT", "pt-BR", "en-US"];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.Ordinal);
    private static readonly Lock CacheLock = new();

    private readonly IReadOnlyDictionary<string, string> _resources;

    public ResWLocalizationService(string culture)
    {
        Culture = culture;
        _resources = Load(culture);
    }

    public string Culture { get; }

    public string? CurrentLanguageOverride => Culture;

    public string GetString(string resourceKey) =>
        _resources.TryGetValue(resourceKey, out var value)
            ? value
            : throw new KeyNotFoundException($"{Culture} resw has no '{resourceKey}'.");

    public bool Contains(string resourceKey) => _resources.ContainsKey(resourceKey);

    public void InitializeFromSystem()
    {
    }

    public void SetLanguage(string? languageTag)
    {
    }

    public static IReadOnlyDictionary<string, string> Load(string culture)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(culture, out var resources))
            {
                var path = Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App", "Resources", culture, "Resources.resw");
                resources = XDocument.Load(path)
                    .Root!
                    .Elements("data")
                    .ToDictionary(
                        element => element.Attribute("name")!.Value,
                        element => element.Element("value")?.Value ?? string.Empty,
                        StringComparer.Ordinal);
                Cache[culture] = resources;
            }

            return resources;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ServerMonitor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
