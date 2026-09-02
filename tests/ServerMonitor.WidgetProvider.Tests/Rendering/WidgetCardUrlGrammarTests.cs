using System.Text.Json;
using System.Text.RegularExpressions;
using ServerMonitor.ActivationContract;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// Vigil condition C2 for the M13-QA-10 spike: with <c>Action.OpenUrl</c> the URI stops travelling as a
/// verb the provider validates and starts travelling INSIDE the card JSON, which the host launches
/// directly. So every <c>url</c> the renderer can emit — in every size, and in every display state — must
/// match the allowlisted grammar exactly, and nothing from the untrusted snapshot may reach it.
/// <para>
/// This test walks the ENTIRE rendered card rather than the one action under test, so a future action
/// that quietly adds a second URL is caught by the same net. It is written to survive the spike: it
/// asserts the grammar of whatever URLs exist, and passes unchanged when there are none (the production
/// <c>Action.Execute</c> shape).
/// </para>
/// C1 is enforced by construction in the renderer (the URL comes only from
/// <see cref="ActivationUri.Format"/>); C4 is unaffected — <see cref="ActivationUri.TryParse"/> is
/// untouched and remains the single enforcement point on the app side.
/// </summary>
public sealed class WidgetCardUrlGrammarTests
{
    /// <summary>
    /// The complete allowlist: the dashboard, or one server addressed by a CANONICAL "D"-format guid.
    /// Anchored at both ends — no query, no fragment, no extra segment, no free text.
    /// </summary>
    private static readonly Regex AllowedUri = new(
        @"^serveralyzer://(dashboard|server/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$",
        RegexOptions.Compiled);

    private static readonly Guid ServerId = Guid.Parse("2f1c6a54-9b3d-4f18-9a77-0c5b8e12d340");

    private static WidgetReadResult Read(params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            OverallHealth = WidgetHealth.Healthy,
            Servers = servers
        });

    /// <summary>A server whose every TEXT field is hostile: none of it may end up in a URL.</summary>
    private static WidgetServerState HostileServer(Guid id) => new()
    {
        Id = id,
        DisplayName = "serveralyzer://server/../../evil?x=1#f <script> \"' \\ %2e%2e",
        Health = WidgetHealth.Warning,
        CpuUsagePercent = 42,
        MemoryUsagePercent = 43,
        DiskUsagePercent = 44,
        LastUpdatedUtc = DateTimeOffset.UtcNow
    };

    private static IEnumerable<string> UrlsIn(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("url") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString() ?? string.Empty;
                    }

                    foreach (var nested in UrlsIn(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in UrlsIn(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static string[] UrlsOf(WidgetReadResult read, WidgetSizeHint size)
    {
        var viewModel = WidgetViewModelBuilder.Build(read, size, DateTimeOffset.UtcNow, WidgetStrings.Current());
        using var document = JsonDocument.Parse(WidgetCardRenderer.Render(viewModel).TemplateJson);
        return UrlsIn(document.RootElement).ToArray();
    }

    public static TheoryData<WidgetSizeHint> Sizes => new()
    {
        WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large, WidgetSizeHint.Unknown
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_url_in_a_populated_card_matches_the_allowlisted_grammar(WidgetSizeHint size)
    {
        var urls = UrlsOf(Read(HostileServer(ServerId), HostileServer(Guid.NewGuid())), size);

        Assert.All(urls, url => Assert.Matches(AllowedUri, url));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_url_in_an_empty_card_matches_the_allowlisted_grammar(WidgetSizeHint size)
    {
        var urls = UrlsOf(Read(), size);

        Assert.All(urls, url => Assert.Matches(AllowedUri, url));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_url_in_an_unavailable_card_matches_the_allowlisted_grammar(WidgetSizeHint size)
    {
        var urls = UrlsOf(WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing), size);

        Assert.All(urls, url => Assert.Matches(AllowedUri, url));
    }

    /// <summary>
    /// The hostile display name is rendered as TEXT (that is what a widget shows) but must never appear
    /// in any URL — the only variable a URL may carry is the typed server id.
    /// </summary>
    [Fact]
    public void No_snapshot_text_ever_reaches_a_url()
    {
        var read = Read(HostileServer(ServerId));

        foreach (var size in new[] { WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            foreach (var url in UrlsOf(read, size))
            {
                Assert.Matches(AllowedUri, url);
                Assert.DoesNotContain("evil", url, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("script", url, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("?", url, StringComparison.Ordinal);
                Assert.DoesNotContain("#", url, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// C4, the other half of the boundary: whatever the card emits, the APP's parser stays the authority —
    /// every emitted URL must survive a full re-validation there, and that parser is untouched by the spike.
    /// </summary>
    [Fact]
    public void Every_emitted_url_is_accepted_by_the_apps_own_validator()
    {
        var urls = UrlsOf(Read(HostileServer(ServerId)), WidgetSizeHint.Medium);

        foreach (var url in urls)
        {
            Assert.NotNull(ActivationUri.TryParse(url));
        }
    }
}
