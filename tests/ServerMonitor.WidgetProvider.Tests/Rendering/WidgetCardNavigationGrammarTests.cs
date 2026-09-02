using System.Text.Json;
using ServerMonitor.ActivationContract;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// The card's navigation grammar, asserted POSITIVELY (M13-QA-10 close-out).
/// <para>
/// Its predecessor asserted that every <c>url</c> the card emits matched an allowlist. That was true and
/// useful while the QA-10 spike put an <c>Action.OpenUrl</c> on the card; the moment production went back
/// to <c>Action.Execute</c> it became VACUOUS — there are no <c>url</c> properties left, so "all of them
/// match" passes over an empty set and proves nothing. Vigil caught it. This replacement states what the
/// production card MUST contain, so it fails when navigation is wrong, not merely when it is absent:
/// </para>
/// <list type="bullet">
/// <item>zero <c>url</c> properties and zero <c>Action.OpenUrl</c>, anywhere, in any size or state;</item>
/// <item>every navigation action is <c>Action.Execute</c>;</item>
/// <item>every <c>verb</c> is in the explicit navigation allowlist, pinned to its wire value;</item>
/// <item>the dashboard action carries the verb and NOTHING else — no data;</item>
/// <item>a server action's data holds exactly one key, <c>serverId</c>, whose value parses as a
/// non-empty canonical guid that came from the snapshot;</item>
/// <item>no display name, host, address or other snapshot text can reach an action;</item>
/// <item>no query, fragment or free-text navigation grammar exists at all;</item>
/// <item>and the EXPECTED NUMBER of actions is asserted per size and state, so the suite can never again
/// go green over an empty collection.</item>
/// </list>
/// The proof that it bites: reintroducing <c>Action.OpenUrl</c>, emitting an unknown verb, or adding an
/// arbitrary string to the action data each make it fail.
/// </summary>
public sealed class WidgetCardNavigationGrammarTests
{
    /// <summary>
    /// The closed navigation allowlist, written as WIRE VALUES rather than as the constants alone: the
    /// verb strings are a contract with the Widgets host, so a rename of the constant's value has to fail
    /// here too, not silently follow along.
    /// </summary>
    private static readonly string[] AllowedVerbs = ["openDashboard", "openServer"];

    private const string DashboardVerb = "openDashboard";
    private const string ServerVerb = "openServer";
    private const string ServerIdKey = "serverId";

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings Strings = WidgetStrings.Current();

    /// <summary>Text fields that a hostile or merely unlucky snapshot could carry.</summary>
    private static readonly string[] HostileFragments =
    [
        "evil", "script", "10.0.0.7", "example.local", "..", "%2e", "serveralyzer://"
    ];

    private static WidgetServerState HostileServer(Guid id) => new()
    {
        Id = id,
        // Everything textual is hostile: a URI-shaped name, an address, a host, traversal, markup.
        DisplayName = "serveralyzer://server/../../evil?x=1#f 10.0.0.7 example.local <script> %2e%2e",
        Health = WidgetHealth.Warning,
        CpuUsagePercent = 42,
        MemoryUsagePercent = 43,
        DiskUsagePercent = 44,
        LastUpdatedUtc = Now
    };

    private static WidgetReadResult Available(params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = WidgetHealth.Warning,
            Servers = servers
        });

    private sealed record CardActions(
        List<JsonElement> All, List<JsonElement> Dashboard, List<JsonElement> Server, List<string> Urls);

    /// <summary>
    /// Every action in the card, however it is attached (card-level <c>selectAction</c>, a container's
    /// <c>selectAction</c>, or an <c>actions</c> array), plus every <c>url</c> property found anywhere —
    /// so a URL smuggled outside an action is caught by the same walk.
    /// </summary>
    private static CardActions ActionsOf(WidgetReadResult read, WidgetSizeHint size)
    {
        var viewModel = WidgetViewModelBuilder.Build(read, size, Now, Strings);
        using var document = JsonDocument.Parse(WidgetCardRenderer.Render(viewModel).TemplateJson);

        var all = new List<JsonElement>();
        var urls = new List<string>();
        Walk(document.RootElement.Clone(), all, urls);

        return new CardActions(
            all,
            all.Where(a => Verb(a) == DashboardVerb).ToList(),
            all.Where(a => Verb(a) == ServerVerb).ToList(),
            urls);
    }

    private static void Walk(JsonElement element, List<JsonElement> actions, List<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    (type.GetString() ?? string.Empty).StartsWith("Action.", StringComparison.Ordinal))
                {
                    actions.Add(element);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("url") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        urls.Add(property.Value.GetString() ?? string.Empty);
                    }

                    Walk(property.Value, actions, urls);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, actions, urls);
                }

                break;
        }
    }

    private static string? Verb(JsonElement action) =>
        action.TryGetProperty("verb", out var verb) && verb.ValueKind == JsonValueKind.String
            ? verb.GetString()
            : null;

    private static string[] Keys(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public static TheoryData<WidgetSizeHint> AllSizes => new()
    {
        WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large, WidgetSizeHint.Unknown
    };

    /// <summary>Every state and size the card can be rendered in, with the actions each must produce.</summary>
    public static TheoryData<WidgetSizeHint, int> SizesAndServerActionCounts => new()
    {
        // Small and Unknown show no server rows (MaxRowsFor), so only the card-level dashboard action.
        { WidgetSizeHint.Small, 0 },
        { WidgetSizeHint.Unknown, 0 },
        { WidgetSizeHint.Medium, 2 },
        { WidgetSizeHint.Large, 3 }
    };

    // ---------------------------------------------------------------- no OpenUrl, anywhere, ever

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void No_size_or_state_emits_a_url_or_an_open_url_action(WidgetSizeHint size)
    {
        var states = new[]
        {
            Available(HostileServer(Guid.NewGuid()), HostileServer(Guid.NewGuid()), HostileServer(Guid.NewGuid())),
            Available(),
            WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing)
        };

        foreach (var state in states)
        {
            var actions = ActionsOf(state, size);

            Assert.Empty(actions.Urls);
            Assert.DoesNotContain(actions.All, a => a.GetProperty("type").GetString() == "Action.OpenUrl");
        }
    }

    // ---------------------------------------------------------------- the positive shape

    [Theory]
    [MemberData(nameof(SizesAndServerActionCounts))]
    public void A_populated_card_emits_exactly_the_expected_navigation_actions(
        WidgetSizeHint size, int expectedServerActions)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var actions = ActionsOf(Available(ids.Select(HostileServer).ToArray()), size);

        // Anti-vacuity: the card must really carry these, and exactly these.
        Assert.Single(actions.Dashboard);
        Assert.Equal(expectedServerActions, actions.Server.Count);
        Assert.Equal(1 + expectedServerActions, actions.All.Count);

        foreach (var action in actions.All)
        {
            Assert.Equal("Action.Execute", action.GetProperty("type").GetString());
            Assert.Contains(Verb(action), AllowedVerbs);
        }

        // The dashboard action carries the verb and nothing else — no data, no url, no extras.
        Assert.Equal(["type", "verb"], Keys(actions.Dashboard[0]));

        foreach (var action in actions.Server)
        {
            Assert.Equal(["data", "type", "verb"], Keys(action));

            var data = action.GetProperty("data");
            Assert.Equal([ServerIdKey], Keys(data)); // ONLY the opaque id

            var raw = data.GetProperty(ServerIdKey);
            Assert.Equal(JsonValueKind.String, raw.ValueKind);
            Assert.True(Guid.TryParseExact(raw.GetString(), "D", out var id), "serverId is not a canonical guid");
            Assert.NotEqual(Guid.Empty, id);
            Assert.Contains(id, ids); // it came from the snapshot, it was not invented
        }
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void An_empty_card_emits_only_the_dashboard_action(WidgetSizeHint size)
    {
        var actions = ActionsOf(Available(), size);

        Assert.Single(actions.All);
        Assert.Single(actions.Dashboard);
        Assert.Equal("Action.Execute", actions.Dashboard[0].GetProperty("type").GetString());
        Assert.Equal(["type", "verb"], Keys(actions.Dashboard[0]));
    }

    /// <summary>
    /// The neutral state deliberately carries NO navigation at all (§13/§14): there is nothing to open.
    /// Asserting it here means "zero actions" is a checked property of that state, not an accident that
    /// would make the other assertions vacuous.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSizes))]
    public void An_unavailable_card_emits_no_navigation_at_all(WidgetSizeHint size)
    {
        var actions = ActionsOf(WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing), size);

        Assert.Empty(actions.All);
        Assert.Empty(actions.Urls);
    }

    // ---------------------------------------------------------------- nothing untrusted gets in

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void No_snapshot_text_reaches_any_action(WidgetSizeHint size)
    {
        var actions = ActionsOf(Available(HostileServer(Guid.NewGuid()), HostileServer(Guid.NewGuid())), size);

        foreach (var action in actions.All)
        {
            var json = action.GetRawText();
            foreach (var fragment in HostileFragments)
            {
                Assert.DoesNotContain(fragment, json, StringComparison.OrdinalIgnoreCase);
            }

            // No query/fragment/free-text navigation grammar exists to smuggle anything through.
            Assert.DoesNotContain('?', json);
            Assert.DoesNotContain('#', json);
        }
    }

    /// <summary>
    /// The other half of the boundary: whatever the card emits, the APP's parser is the authority. Each
    /// emitted action must map back to exactly the intent it claims, through the real contract.
    /// </summary>
    [Fact]
    public void Every_emitted_action_maps_back_to_a_valid_intent_through_the_real_contract()
    {
        var id = Guid.NewGuid();
        var actions = ActionsOf(Available(HostileServer(id)), WidgetSizeHint.Medium);

        var dashboard = ActivationVerbs.TryToIntent(Verb(actions.Dashboard[0]), null);
        Assert.NotNull(dashboard);
        Assert.Equal(ActivationIntentKind.OpenDashboard, dashboard.Kind);

        var server = actions.Server.Single();
        var serverId = Guid.ParseExact(server.GetProperty("data").GetProperty(ServerIdKey).GetString()!, "D");
        var intent = ActivationVerbs.TryToIntent(Verb(server), serverId);
        Assert.NotNull(intent);
        Assert.Equal(ActivationIntentKind.OpenServer, intent.Kind);
        Assert.Equal(id, intent.ServerId);

        // And the URI those intents produce is still exactly the allowlisted grammar the app re-validates.
        Assert.Equal("serveralyzer://dashboard", ActivationUri.Format(dashboard));
        Assert.Equal($"serveralyzer://server/{id:D}", ActivationUri.Format(intent));
        Assert.NotNull(ActivationUri.TryParse(ActivationUri.Format(intent)));
    }

    /// <summary>The wire values are the contract with the host; pin them.</summary>
    [Fact]
    public void The_navigation_allowlist_is_exactly_the_two_contract_verbs()
    {
        Assert.Equal(DashboardVerb, ActivationVerbs.OpenDashboard);
        Assert.Equal(ServerVerb, ActivationVerbs.OpenServer);
        Assert.Equal(ServerIdKey, ActivationVerbs.ServerIdDataKey);
        Assert.Equal([DashboardVerb, ServerVerb], AllowedVerbs);
    }
}
