using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 SPEC test 10 (strings half) / Prism §B: every <see cref="WidgetStrings"/> key exists, is non-empty
/// and carries the same placeholders in en-US, pt-PT and pt-BR; and the binding decisions of the copy table
/// are pinned so a regression to the legacy wording ("Alerta", "Offline", "Memória") is caught.
/// </summary>
public sealed partial class WidgetStringsParityTests
{
    private static readonly string[] Cultures = { "en-US", "pt-PT", "pt-BR" };

    private static readonly PropertyInfo[] Keys = typeof(WidgetStrings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string))
        .ToArray();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void Every_key_is_non_empty_and_has_the_same_placeholders_in_every_culture()
    {
        Assert.True(Keys.Length > 60, $"only {Keys.Length} keys found"); // the reflection walk is real

        var reference = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));
        foreach (var culture in Cultures)
        {
            var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
            foreach (var key in Keys)
            {
                var value = (string?)key.GetValue(strings);
                Assert.False(string.IsNullOrEmpty(value), $"{culture}.{key.Name} is empty");

                var expected = Placeholders((string)key.GetValue(reference)!);
                Assert.True(expected.SetEquals(Placeholders(value!)),
                    $"{culture}.{key.Name} placeholders differ from en-US");
            }
        }
    }

    private static HashSet<string> Placeholders(string value) =>
        Placeholder().Matches(value).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("en-US", "Attention", "No connection", "RAM", "Open ServerAlyzer", "High disk", "Critical disk")]
    [InlineData("pt-PT", "Atenção", "Sem ligação", "RAM", "Abrir o ServerAlyzer", "Disco alto", "Disco crítico")]
    [InlineData("pt-BR", "Atenção", "Sem conexão", "RAM", "Abrir o ServerAlyzer", "Disco alto", "Disco crítico")]
    public void Binding_copy_decisions_are_pinned(
        string culture, string warning, string offline, string memory, string cta, string diskWarning, string diskCritical)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        Assert.Equal(warning, strings.StatusWarning);
        Assert.Equal(offline, strings.StatusOffline);
        Assert.Equal(memory, strings.Memory);
        Assert.Equal(cta, strings.EmptyCta);
        Assert.Equal(diskWarning, strings.ReasonDiskWarning);
        Assert.Equal(diskCritical, strings.ReasonDiskCritical);
    }

    [Fact]
    public void Portuguese_voices_differ_tu_vs_voce()
    {
        var pt = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-PT"));
        var br = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-BR"));

        Assert.StartsWith("Adiciona ", pt.EmptyBody);
        Assert.StartsWith("Adicione ", br.EmptyBody);
        Assert.Equal("O teu primeiro servidor", pt.EmptyTitle);
        Assert.Equal("Seu primeiro servidor", br.EmptyTitle);
    }
}
