using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S2, Cortex §2.4). The real HighContrast theme cannot be requested by an app (ElementTheme is
/// Default/Light/Dark only); it is a system setting the gallery never touches. This is a RESOURCE SIMULATION,
/// labelled as such everywhere:
/// <list type="number">
/// <item>Resolution: read every token's <c>HighContrast</c> theme-dictionary entry in code (reliable).</item>
/// <item>Preview (empirical): copy those entries into a host element's Dark and Light theme dictionaries, so
/// content created under that host resolves its ThemeResources to the HC entries - structure only, with the
/// current system palette, never a real contrast theme.</item>
/// </list>
/// </summary>
internal static class QaHighContrastSimulation
{
    private static readonly string[] HostThemes = ["Dark", "Light"];

    /// <summary>
    /// Every entry of the given theme dictionary across Application.Resources, in lookup order: a dictionary's
    /// own theme entries first, then its merged dictionaries from last to first (the first found wins).
    /// </summary>
    public static IReadOnlyDictionary<string, object> ThemeEntries(string theme)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        Visit(Application.Current.Resources, theme, entries);
        return entries;
    }

    /// <summary>Turns the preview on (copies the HighContrast token entries into <paramref name="host"/>) or off.</summary>
    public static void Apply(FrameworkElement host, bool simulate)
    {
        foreach (var theme in HostThemes)
        {
            host.Resources.ThemeDictionaries.Remove(theme);
        }

        if (!simulate)
        {
            return;
        }

        var highContrast = ThemeEntries("HighContrast")
            .Where(entry => entry.Key.StartsWith("Sa", StringComparison.Ordinal))
            .ToList();
        foreach (var theme in HostThemes)
        {
            var dictionary = new ResourceDictionary();
            foreach (var (key, value) in highContrast)
            {
                dictionary[key] = value;
            }

            host.Resources.ThemeDictionaries[theme] = dictionary;
        }
    }

    private static void Visit(ResourceDictionary dictionary, string theme, Dictionary<string, object> entries)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) && themed is ResourceDictionary themeDictionary)
        {
            foreach (var (key, value) in themeDictionary)
            {
                if (key is string name)
                {
                    entries.TryAdd(name, value);
                }
            }
        }

        for (var index = dictionary.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            Visit(dictionary.MergedDictionaries[index], theme, entries);
        }
    }
}
