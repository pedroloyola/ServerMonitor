using System.Globalization;
using System.Text.Json.Nodes;
using AdaptiveCards.Templating;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 C1-fix S-1 (Cortex). A near-host ORACLE in CI: the .NET <c>AdaptiveCards.Templating</c> engine,
/// which is the same family as the live widget host's <c>TemplateExpander</c> (AdaptiveCards.Templating on
/// Microsoft.Bot.AdaptiveExpressions.Core 4.22.9, Relay C0-offline). It expands every size × state × culture
/// template + data pair, and the result must equal <see cref="CardTemplateHarness"/>'s expansion. So the
/// strict test expander cannot quietly diverge from the real engine on the patterns we use:
/// <list type="bullet">
/// <item><c>$when</c> on inline TextRuns;</item>
/// <item><c>$data</c> with an empty array;</item>
/// <item>typed bool and string substitution;</item>
/// <item>empty-string values.</item>
/// </list>
/// TEST-ONLY dependency, pinned exactly; the provider never references it (<c>ProviderBoundaryTests</c>).
/// The board (C0-online) remains the only real oracle.
/// </summary>
public sealed class TemplatingOracleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static JsonNode OracleExpand(string templateJson, string dataJson)
    {
        var expanded = new AdaptiveCardTemplate(templateJson).Expand(new EvaluationContext { Root = dataJson });
        return JsonNode.Parse(expanded)!;
    }

    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => System.Text.Json.JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString()
    };

    [Fact]
    public void The_dotnet_templating_engine_expands_every_card_exactly_like_the_test_harness()
    {
        var compared = 0;
        foreach (var size in new[] { WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            foreach (var (state, read) in WidgetCardRendererTests.States())
            {
                foreach (var culture in new[] { "en-US", "pt-PT", "pt-BR" })
                {
                    var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
                    var card = WidgetCardRenderer.Render(WidgetViewModelBuilder.Build(read, size, Now, strings));

                    var oracle = Canonical(OracleExpand(card.TemplateJson, card.DataJson));
                    var harness = Canonical(CardTemplateHarness.Expand(card.TemplateJson, card.DataJson));

                    Assert.True(oracle == harness, $"{size}/{state}/{culture}: oracle and harness differ\noracle:  {oracle}\nharness: {harness}");
                    compared++;
                }
            }
        }

        Assert.Equal(72, compared);
    }

    [Theory]
    [MemberData(nameof(WidgetCardRendererTests.HostileNameData), MemberType = typeof(WidgetCardRendererTests))]
    public void The_dotnet_templating_engine_never_evaluates_a_hostile_name(string hostile)
    {
        var read = WidgetCardRendererTests.Read(Now, WidgetCardRendererTests.Server(1, name: hostile), WidgetCardRendererTests.Server(2, name: hostile));
        var vm = WidgetViewModelBuilder.Build(read, WidgetSizeHint.Large, Now, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US")));
        var card = WidgetCardRenderer.Render(vm);
        var shown = vm.Rows[0].DisplayName.Value;

        var expanded = OracleExpand(card.TemplateJson, card.DataJson);

        var carriers = CardTemplateHarness.Objects(expanded).Where(o => (string?)o.Node["text"] == shown).ToList();
        Assert.Equal(2, carriers.Count); // one literal TextRun per row, nothing evaluated or dropped
        Assert.All(carriers, c =>
        {
            Assert.Equal("TextRun", (string?)c.Node["type"]);
            Assert.Equal("inlines", c.OwnerKey);
        });
        Assert.Equal(Canonical(CardTemplateHarness.Expand(card.TemplateJson, card.DataJson)), Canonical(expanded));
    }
}
