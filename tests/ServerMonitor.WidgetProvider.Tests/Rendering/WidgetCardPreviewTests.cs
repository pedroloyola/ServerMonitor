using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Rendering;
using Contract = ServerMonitor.WidgetContract.WidgetServerState;
using Health = ServerMonitor.WidgetContract.WidgetHealth;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// Writes every size × state × culture as a preview for Prism (ADR-018 §47, SPEC §8). It ASSERTS that each
/// expands to a valid card, so it never reports PASS for work that did not happen. For each case it writes
/// three files under <c>%TEMP%\sm-widget-preview\v3\</c>:
/// <list type="bullet">
/// <item><c>.template.json</c> + <c>.data.json</c>, the exact pair the host receives (paste both into the
/// Adaptive Cards Designer);</item>
/// <item><c>.expanded.json</c>, the card as the test harness expands it.</item>
/// </list>
/// The templates carry no theme-dependent content (no images until C0), so one file per case serves both
/// themes (DV-12). The real board stays NOT_RUN.
/// </summary>
public sealed class WidgetCardPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static TheoryData<string, WidgetSizeHint, string> Cases()
    {
        var data = new TheoryData<string, WidgetSizeHint, string>();
        foreach (var size in new[] { WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            foreach (var (state, _) in WidgetCardRendererTests.States())
            {
                foreach (var culture in new[] { "en-US", "pt-PT", "pt-BR" })
                {
                    data.Add(state, size, culture);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_expands_to_a_valid_card_and_is_written_for_preview(string state, WidgetSizeHint size, string culture)
    {
        var read = WidgetCardRendererTests.States().Single(s => s.State == state).Read;
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        var card = WidgetCardRenderer.Render(WidgetViewModelBuilder.Build(read, size, Now, strings));
        var expanded = CardTemplateHarness.Expand(card.TemplateJson, card.DataJson);

        Assert.Empty(CardTemplateHarness.ShapeErrors(expanded));

        var dir = Path.Combine(Path.GetTempPath(), "sm-widget-preview", "v3");
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, $"{size.ToString().ToLowerInvariant()}-{state}-{culture}");
        File.WriteAllText(stem + ".template.json", Indent(card.TemplateJson));
        File.WriteAllText(stem + ".data.json", Indent(card.DataJson));
        File.WriteAllText(stem + ".expanded.json", expanded.ToJsonString(Pretty));
    }

    /// <summary>
    /// UI.9 C2: the SYNTHETIC fleet behind the widget-picker screenshots (Medium, per culture, all healthy and
    /// fresh). Invented names, no host/IP/user — never real data. Written to
    /// <c>%TEMP%\sm-widget-preview\picker\</c> for the offline proxy render that produces
    /// <c>Public\ServerAlyzerWidgetScreenshot*.png</c> (command in the C2 report).
    /// </summary>
    [Theory]
    [InlineData("en-US", "Web server", "Database", "Backup")]
    [InlineData("pt-PT", "Servidor web", "Base de dados", "Cópias")]
    [InlineData("pt-BR", "Servidor web", "Banco de dados", "Backup")]
    public void Picker_fleet_is_synthetic_and_written_for_the_store_screenshot(string culture, string web, string db, string backup)
    {
        static Contract Server(int n, string name, double cpu, double mem, double disk) => new()
        {
            Id = new Guid($"00000000-0000-0000-0000-{n:D12}"),
            DisplayName = name,
            Health = Health.Healthy,
            CpuUsagePercent = cpu,
            MemoryUsagePercent = mem,
            DiskUsagePercent = disk,
            LastUpdatedUtc = Now.AddSeconds(-5),
            StaleAfterSeconds = 60
        };

        var servers = new[] { Server(1, web, 24, 61, 43), Server(2, db, 39, 72, 55), Server(3, backup, 8, 22, 55) };
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        var read = WidgetReadResult.Available(new ServerMonitor.WidgetContract.WidgetStateSnapshot
        {
            SchemaVersion = ServerMonitor.WidgetContract.WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = Health.Healthy,
            Servers = servers
        });
        var card = WidgetCardRenderer.Render(WidgetViewModelBuilder.Build(read, WidgetSizeHint.Medium, Now, strings));
        var expanded = CardTemplateHarness.Expand(card.TemplateJson, card.DataJson);

        Assert.Empty(CardTemplateHarness.ShapeErrors(expanded));
        Assert.Equal(strings.FleetAllHealthy, WidgetViewModelBuilder.Build(read, WidgetSizeHint.Small, Now, strings).Title);

        var dir = Path.Combine(Path.GetTempPath(), "sm-widget-preview", "picker");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"medium-picker-{culture}.expanded.json"), expanded.ToJsonString(Pretty));
    }

    private static string Indent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, Pretty);
    }
}
