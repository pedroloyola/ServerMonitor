using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ServerMonitor.App.Controls.Primitives;
using Windows.UI;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>One probe comparison: what the theme dictionary (or app resources) holds vs what the element got.</summary>
public sealed record QaTokenResult(string Theme, string Key, string Kind, string Expected, string Effective, string Status, string? Note = null);

/// <summary>Empirical result of the HC preview host (Cortex §2.4 (2)).</summary>
public sealed record QaHcPreviewResult(
    bool Works,
    string Criterion,
    string SystemWindowTextColor,
    string DarkEntry,
    string HighContrastEntry,
    string PreviewEffective,
    int BrushProbesTakingTheHighContrastEntry,
    int BrushProbes);

/// <summary>The --qa-tokens report (tokens-selfcheck.json).</summary>
public sealed record QaTokenSelfCheckReport(
    string Schema,
    string GeneratedAtUtc,
    int ProcessId,
    string RealSystemHighContrast,
    int ManifestKeys,
    IReadOnlyDictionary<string, string> SystemColors,
    IReadOnlyList<QaTokenResult> Results,
    QaHcPreviewResult? HcPreview,
    int Passed,
    int Failed,
    int ExitCode,
    string? Error)
{
    public const string SchemaId = "serveralyzer.qa.tokens-selfcheck/1";

    public static QaTokenSelfCheckReport HarnessError(Exception exception) => new(
        SchemaId, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), Environment.ProcessId, "unknown",
        QaTokenManifest.Entries.Count, new Dictionary<string, string>(), [], null, 0, 0,
        QaGalleryComposition.HarnessErrorExitCode, exception.ToString());
}

/// <summary>
/// QA-ONLY (UI.2 S2, gate F-1). Resolves every manifest token at runtime, fail-closed: a missing key, a wrong
/// type, a default colour or an exception is a FAIL.
/// <list type="bullet">
/// <item>Dark and Light: the probe page stays loaded while GalleryRoot.RequestedTheme flips, so each brush probe
/// proves RUNTIME re-resolution; every probe is compared with the entry read in code from that theme's
/// dictionary (brushes) or from Application.Resources (everything else).</item>
/// <item>HighContrast-sim: each HighContrast brush entry is instantiated in code and must be a brush with a
/// non-default colour - this proves the entries exist and their SystemColor* references resolve. It is a
/// resource simulation, not the real contrast theme.</item>
/// <item>The S1 empirical question - does a default style delivered through Application.Resources template a
/// <c>Sa*</c> control? - is answered by the SaStatusIndicator probe in each theme.</item>
/// <item>The HC preview host is reported as an empirical result and never fails the run.</item>
/// </list>
/// </summary>
internal sealed class QaTokenSelfCheck(QaGalleryWindow window)
{
    private const string HighContrastSimTheme = "HighContrast-sim";
    private const string DefaultStyleProbeKey = "SaStatusIndicator default style (via Application.Resources)";

    private static readonly string[] SystemColorKeys =
    [
        "SystemColorWindowColor", "SystemColorWindowTextColor", "SystemColorHighlightColor",
        "SystemColorHighlightTextColor", "SystemColorGrayTextColor"
    ];

    public async Task<QaTokenSelfCheckReport> RunAsync()
    {
        var results = new List<QaTokenResult>();
        var systemColors = SystemColorKeys.ToDictionary(
            key => key,
            key => Application.Current.Resources.TryGetValue(key, out var value) ? Describe(value) : "<missing>",
            StringComparer.Ordinal);

        window.ApplyTheme("dark");
        window.SelectPage("tokens");
        var page = await LoadedProbePageAsync();

        foreach (var (theme, option) in new[] { ("Dark", "dark"), ("Light", "light") })
        {
            window.ApplyTheme(option);
            await SettleAsync();
            if (!ReferenceEquals(page, window.CurrentPage))
            {
                throw new InvalidOperationException("The probe page was recreated by a Dark/Light switch; runtime re-resolution would be unproven.");
            }

            var themeEntries = QaHighContrastSimulation.ThemeEntries(theme);
            var probes = Probes(page);
            results.AddRange(QaTokenManifest.Entries.Select(entry => Check(theme, entry, probes, themeEntries)));
            results.Add(CheckDefaultStyle(theme, page.DefaultStyleProbe, themeEntries));
        }

        var highContrast = QaHighContrastSimulation.ThemeEntries("HighContrast");
        results.AddRange(QaTokenManifest.Entries
            .Where(entry => entry.Kind == QaTokenKind.Brush)
            .Select(entry => CheckHighContrastEntry(entry, highContrast, systemColors)));

        var preview = await ProbeHighContrastPreviewAsync(highContrast);

        window.ApplyTheme("dark");
        await SettleAsync();

        var failed = results.Count(result => result.Status != "PASS");
        return new QaTokenSelfCheckReport(
            QaTokenSelfCheckReport.SchemaId,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.ProcessId,
            RealHighContrast(),
            QaTokenManifest.Entries.Count,
            systemColors,
            results,
            preview,
            results.Count - failed,
            failed,
            failed == 0 ? 0 : 2,
            null);
    }

    private static QaTokenResult Check(
        string theme,
        QaTokenEntry entry,
        IReadOnlyDictionary<string, FrameworkElement> probes,
        IReadOnlyDictionary<string, object> themeEntries)
    {
        var kind = entry.Kind.ToString();
        try
        {
            if (entry.Kind == QaTokenKind.Color)
            {
                // Raw colours are private to Styles/Tokens/**: read in code, never through a XAML probe.
                if (!Application.Current.Resources.TryGetValue(entry.Key, out var raw))
                {
                    return Fail("<missing>", "<n/a>", "key not found in Application.Resources");
                }

                return raw is Color colour && colour != default
                    ? new QaTokenResult(theme, entry.Key, kind, Describe(raw), Describe(raw), "PASS", "code lookup (no XAML probe by design)")
                    : Fail(Describe(raw), Describe(raw), "not a non-default Color");
            }

            if (!probes.TryGetValue(entry.Key, out var probe))
            {
                return Fail("<n/a>", "<no probe>", "the probe page has no element with this Tag");
            }

            object? expected;
            if (entry.Kind == QaTokenKind.Brush)
            {
                if (!themeEntries.TryGetValue(entry.Key, out expected))
                {
                    return Fail("<missing>", "<n/a>", $"no {theme} theme-dictionary entry");
                }
            }
            else if (!Application.Current.Resources.TryGetValue(entry.Key, out expected))
            {
                return Fail("<missing>", "<n/a>", "key not found in Application.Resources");
            }

            var effective = Effective(entry.Kind, probe);
            var expectedText = Describe(expected);
            var effectiveText = Describe(effective);

            if (effective is SolidColorBrush { Color: var solid } && solid == default)
            {
                return Fail(expectedText, effectiveText, "default colour");
            }

            if (ReferenceEquals(expected, effective))
            {
                return new QaTokenResult(theme, entry.Key, kind, expectedText, effectiveText, "PASS", "same instance");
            }

            return expected is not null && effective is not null && expected.GetType() == effective.GetType() && Equivalent(expected, effective)
                ? new QaTokenResult(theme, entry.Key, kind, expectedText, effectiveText, "PASS", "equivalent value")
                : Fail(expectedText, effectiveText, "mismatch");
        }
        catch (Exception exception)
        {
            return Fail("<n/a>", "<exception>", exception.GetType().Name + ": " + exception.Message);
        }

        QaTokenResult Fail(string expected, string effective, string note) =>
            new(theme, entry.Key, kind, expected, effective, "FAIL", note);
    }

    private static QaTokenResult CheckDefaultStyle(string theme, SaStatusIndicator probe, IReadOnlyDictionary<string, object> themeEntries)
    {
        try
        {
            var dot = Descendants(probe).OfType<Ellipse>().FirstOrDefault(e => e.Name == "PART_Dot");
            var expected = themeEntries.TryGetValue("SaHealthyBrush", out var healthy) ? healthy : null;
            var templated = VisualTreeHelper.GetChildrenCount(probe) > 0 && dot is not null;
            var automationName = AutomationProperties.GetName(probe);
            var effective = dot?.Fill;
            var pass = templated
                && expected is not null && effective is not null
                && (ReferenceEquals(expected, effective) || Equivalent(expected, effective))
                && automationName == probe.Label;
            return new QaTokenResult(theme, DefaultStyleProbeKey, "Primitive",
                "templated; PART_Dot.Fill = SaHealthyBrush " + Describe(expected) + "; automation name = Label",
                $"templated={templated}; PART_Dot.Fill={Describe(effective)}; automationName='{automationName}'",
                pass ? "PASS" : "FAIL",
                pass ? "the implicit style in Application.Resources templates the control; the Healthy visual state re-resolves per theme" : null);
        }
        catch (Exception exception)
        {
            return new QaTokenResult(theme, DefaultStyleProbeKey, "Primitive", "templated", "<exception>", "FAIL", exception.Message);
        }
    }

    private static QaTokenResult CheckHighContrastEntry(
        QaTokenEntry entry,
        IReadOnlyDictionary<string, object> highContrast,
        IReadOnlyDictionary<string, string> systemColors)
    {
        try
        {
            if (!highContrast.TryGetValue(entry.Key, out var value))
            {
                return new QaTokenResult(HighContrastSimTheme, entry.Key, "Brush", "<entry>", "<missing>", "FAIL", "no HighContrast entry");
            }

            var text = Describe(value);
            var ok = value is SolidColorBrush { Color: var colour } && colour != default;
            var matches = systemColors.Where(pair => text.Contains(pair.Value, StringComparison.Ordinal)).Select(pair => pair.Key).ToList();
            return new QaTokenResult(HighContrastSimTheme, entry.Key, "Brush", "SolidColorBrush, non-default colour", text,
                ok ? "PASS" : "FAIL",
                "resource simulation" + (matches.Count > 0 ? "; equals " + string.Join("/", matches) : string.Empty));
        }
        catch (Exception exception)
        {
            return new QaTokenResult(HighContrastSimTheme, entry.Key, "Brush", "<entry>", "<exception>", "FAIL", exception.Message);
        }
    }

    /// <summary>Cortex §2.4 (2), empirical: does content created under the injected host take the HC entries?
    /// Criterion: the SaGlassBorderBrush probe shows SystemColorWindowTextColor.</summary>
    private async Task<QaHcPreviewResult> ProbeHighContrastPreviewAsync(IReadOnlyDictionary<string, object> highContrast)
    {
        window.ApplyTheme("dark");
        await SettleAsync();
        window.ApplyTheme("hc-sim");
        var page = await LoadedProbePageAsync();

        var probes = Probes(page);
        var dark = QaHighContrastSimulation.ThemeEntries("Dark");
        var windowText = Application.Current.Resources.TryGetValue("SystemColorWindowTextColor", out var text) ? Describe(text) : "<missing>";
        const string criterionKey = "SaGlassBorderBrush";
        var effective = probes.TryGetValue(criterionKey, out var probe) ? Describe(Effective(QaTokenKind.Brush, probe)) : "<no probe>";
        var hcEntry = highContrast.TryGetValue(criterionKey, out var hc) ? Describe(hc) : "<missing>";
        var darkEntry = dark.TryGetValue(criterionKey, out var d) ? Describe(d) : "<missing>";

        var brushProbes = QaTokenManifest.Entries.Where(entry => entry.Kind == QaTokenKind.Brush).ToList();
        var taking = brushProbes.Count(entry =>
            probes.TryGetValue(entry.Key, out var element)
            && highContrast.TryGetValue(entry.Key, out var expected)
            && Describe(Effective(QaTokenKind.Brush, element)) == Describe(expected));

        var works = effective.Contains(windowText, StringComparison.Ordinal) && effective != darkEntry;
        return new QaHcPreviewResult(works, $"{criterionKey} probe == SystemColorWindowTextColor", windowText, darkEntry, hcEntry, effective, taking, brushProbes.Count);
    }

    private async Task<QaTokenProbePage> LoadedProbePageAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await SettleAsync();
            if (window.CurrentPage is QaTokenProbePage { IsLoaded: true } page)
            {
                return page;
            }
        }

        throw new TimeoutException("The token probe page never loaded.");
    }

    private async Task SettleAsync()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var tick = new TaskCompletionSource();
            if (!window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => tick.SetResult()))
            {
                throw new InvalidOperationException("The UI dispatcher is shutting down.");
            }

            await tick.Task;
            window.ThemeRoot.UpdateLayout();
        }

        await Task.Delay(50);
    }

    private static IReadOnlyDictionary<string, FrameworkElement> Probes(QaTokenProbePage page)
    {
        var probes = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        foreach (var element in Descendants(page).OfType<FrameworkElement>())
        {
            if (element.Tag is string key && key.StartsWith("Sa", StringComparison.Ordinal) && !probes.TryAdd(key, element))
            {
                throw new InvalidOperationException($"Duplicate probe for {key}.");
            }
        }

        return probes;
    }

    private static object? Effective(QaTokenKind kind, FrameworkElement probe) => kind switch
    {
        QaTokenKind.Brush => ((Border)probe).Background,
        QaTokenKind.Shadow => probe.Shadow,
        QaTokenKind.FontFamily => ((TextBlock)probe).FontFamily,
        QaTokenKind.Double => probe.Width,
        QaTokenKind.Style => probe.Style,
        QaTokenKind.Thickness => ((Border)probe).BorderThickness,
        QaTokenKind.CornerRadius => ((Border)probe).CornerRadius,
        QaTokenKind.MotionString => ((TextBlock)probe).Text,
        QaTokenKind.Color => throw new InvalidOperationException("Colours have no XAML probe."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static bool Equivalent(object expected, object effective) => (expected, effective) switch
    {
        (Style a, Style b) => StyleEquivalent(a, b),
        _ => Describe(expected) == Describe(effective)
    };

    private static bool StyleEquivalent(Style a, Style b)
    {
        if (a.TargetType != b.TargetType || a.Setters.Count != b.Setters.Count)
        {
            return false;
        }

        if ((a.BasedOn is null) != (b.BasedOn is null) || (a.BasedOn is not null && !StyleEquivalent(a.BasedOn, b.BasedOn!)))
        {
            return false;
        }

        for (var index = 0; index < a.Setters.Count; index++)
        {
            if (a.Setters[index] is not Setter left || b.Setters[index] is not Setter right
                || !ReferenceEquals(left.Property, right.Property)
                || Describe(left.Value) != Describe(right.Value))
            {
                return false;
            }
        }

        return true;
    }

    internal static string Describe(object? value) => value switch
    {
        null => "<null>",
        SolidColorBrush solid => $"Solid {Hex(solid.Color)} opacity={Number(solid.Opacity)}",
        AcrylicBrush acrylic => $"Acrylic tint={Hex(acrylic.TintColor)} tintOpacity={Number(acrylic.TintOpacity)} luminosity={Number(acrylic.TintLuminosityOpacity ?? double.NaN)} fallback={Hex(acrylic.FallbackColor)}",
        Color colour => Hex(colour),
        double number => Number(number),
        Thickness t => $"{Number(t.Left)},{Number(t.Top)},{Number(t.Right)},{Number(t.Bottom)}",
        CornerRadius r => $"{Number(r.TopLeft)},{Number(r.TopRight)},{Number(r.BottomRight)},{Number(r.BottomLeft)}",
        FontFamily family => "FontFamily " + family.Source,
        string text => text,
        Style style => $"Style<{style.TargetType?.Name}> setters={style.Setters.Count} basedOn={(style.BasedOn is null ? "none" : "yes")}",
        ThemeShadow => "ThemeShadow",
        _ => value.GetType().Name
    };

    private static string Hex(Color colour) =>
        string.Create(CultureInfo.InvariantCulture, $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}");

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private string RealHighContrast()
    {
        var settings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(window.AppWindow.Id);
        return settings.HighContrast ? "on (" + settings.HighContrastScheme + ")" : "off";
    }
}
