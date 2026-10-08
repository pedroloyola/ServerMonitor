using System.Globalization;
using System.Text.Json.Nodes;
using AdaptiveCards.Templating;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 C3 — M-3 closure (Vigil C0 debrief §1). The board proved that a TextRun keeps markdown literal, but
/// its date/time pre-processor rewrites <c>{{DATE(…)}}</c> / <c>{{TIME(…)}}</c>. So every emitted untrusted
/// name has a U+200B between consecutive <c>{</c>: the card never carries <c>{{</c>.
/// <list type="bullet">
/// <item>Checked in the data JSON and in the expanded card, both with the test harness and with the
/// .NET templating oracle.</item>
/// <item>Round-trips to the truncated name.</item>
/// <item>Leaves names without <c>{{</c> byte-identical.</item>
/// </list>
/// </summary>
public sealed class UntrustedNameNeutralizationTests
{
    private const char Zwsp = '​';
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in WidgetCardRendererTests.HostileNames.Concat(new[]
                 {
                     "{{TIME(2017-02-14T06:08:00Z)}}", "{{{DATE(2017-02-14T06:08:00Z)}}}", "{{date(2017-02-14T06:08:00Z)}}",
                     "server{{", new string('{', 60)
                 }))
        {
            data.Add(name);
        }

        return data;
    }

    private static (WidgetViewModel Vm, WidgetCard Card) Render(string name, WidgetSizeHint size)
    {
        var read = WidgetCardRendererTests.Read(Now, WidgetCardRendererTests.Server(1, WidgetHealth.Warning, name, metric: "disk"));
        var vm = WidgetViewModelBuilder.Build(read, size, Now, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US")));
        return (vm, WidgetCardRenderer.Render(vm));
    }

    private static IEnumerable<string> NameRuns(JsonNode expanded) =>
        CardTemplateHarness.Objects(expanded)
            .Where(o => (string?)o.Node["type"] == "TextRun" && o.OwnerKey == "inlines" && (string?)o.Node["weight"] == "Bolder"
                && (string?)o.Node["size"] == "Small" && ((string?)o.Node["text"])?.Contains('%') == false)
            .Select(o => (string)o.Node["text"]!);

    [Theory]
    [MemberData(nameof(Names))]
    public void An_emitted_name_never_contains_a_double_brace_anywhere_on_the_card(string name)
    {
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var (vm, card) = Render(name, size);
            var truncated = vm.Rows[0].DisplayName.Value;
            var emitted = (string)JsonNode.Parse(card.DataJson)!["rows"]![0]!["name"]!;

            // (a) data JSON
            Assert.DoesNotContain("{{", emitted, StringComparison.Ordinal);
            // (b) round-trip: removing the U+200B gives back exactly the truncated name
            Assert.Equal(truncated, emitted.Replace(Zwsp.ToString(), string.Empty, StringComparison.Ordinal));

            // (a) expanded card — test harness and the .NET templating oracle (host engine family)
            var harness = CardTemplateHarness.Expand(card.TemplateJson, card.DataJson);
            var oracle = JsonNode.Parse(new AdaptiveCardTemplate(card.TemplateJson)
                .Expand(new EvaluationContext(card.DataJson, "{\"hostTheme\":\"dark\"}")))!;
            foreach (var expanded in new[] { harness, oracle })
            {
                Assert.Contains(emitted, NameRuns(expanded)); // (c) still only in the name TextRun
                Assert.DoesNotContain(CardTemplateHarness.Strings(expanded), s => s.Value.Contains("{{", StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData("Web server")]
    [InlineData("srv{1}")]
    [InlineData("a{b}c{d}")]
    [InlineData("}}")]
    [InlineData("Café 日本語")]
    public void A_name_without_double_braces_is_emitted_byte_identical(string name)
    {
        var text = new UntrustedText(name);
        Assert.Same(name, text.ForCard()); // not even a copy: no gratuitous U+200B
        Assert.Equal(name, (string)JsonNode.Parse(Render(name, WidgetSizeHint.Medium).Card.DataJson)!["rows"]![0]!["name"]!);
    }

    [Fact]
    public void Neutralization_is_exact_and_applies_after_truncation()
    {
        Assert.Equal("{​{", new UntrustedText("{{").ForCard());
        Assert.Equal("{​{​{", new UntrustedText("{{{").ForCard());
        Assert.Equal("a{​{b}}", new UntrustedText("a{{b}}").ForCard());

        // 60 x "{" is truncated to 21 + "…" FIRST, then neutralised: 20 U+200B, never a split character.
        var (vm, card) = Render(new string('{', 60), WidgetSizeHint.Medium);
        Assert.Equal(new string('{', 21) + "…", vm.Rows[0].DisplayName.Value);
        var emitted = (string)JsonNode.Parse(card.DataJson)!["rows"]![0]!["name"]!;
        Assert.Equal(20, emitted.Count(c => c == Zwsp));
    }
}
