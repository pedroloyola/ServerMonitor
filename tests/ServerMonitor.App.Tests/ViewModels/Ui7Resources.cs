using System.Xml.Linq;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.7B: the real .resw of one culture as name → value (copy assertions on the shipped text).</summary>
internal static class Ui7Resources
{
    public static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);

    /// <summary>UI.7C (B-18): every key exists and is non-empty in pt-PT, pt-BR and en-US.</summary>
    public static void AssertPresentInEveryCulture(IEnumerable<string> keys)
    {
        var wanted = keys.ToList();
        var failures = new List<string>();
        foreach (var culture in new[] { "pt-PT", "pt-BR", "en-US" })
        {
            var resources = Load(culture);
            failures.AddRange(wanted
                .Where(key => !resources.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                .Select(key => $"{culture}: {key}"));
        }

        Assert.True(failures.Count == 0, "Missing or empty: " + string.Join(", ", failures));
    }
}
