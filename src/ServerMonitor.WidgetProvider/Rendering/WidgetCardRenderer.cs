using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using ServerMonitor.ActivationContract;
using ServerMonitor.WidgetProvider.Hosting;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>A widget update: an Adaptive Card template plus its (empty) data binding.</summary>
public sealed record WidgetCard(string TemplateJson, string DataJson);

/// <summary>
/// Renders a <see cref="WidgetViewModel"/> to an Adaptive Card (self-contained template, empty data —
/// the card is small and re-rendered per update, §25). Uses ONLY widget-supported Adaptive Cards 1.5
/// elements: <c>TextBlock</c>, <c>ColumnSet</c>/<c>Column</c>, <c>Container</c> and whole-card/whole-row
/// <c>Action.Execute</c> select actions (openDashboard / openServer, the read-only deep-links of Slice 4).
/// No images, progress bars, FactSets, or visible buttons. Health is always conveyed by a text
/// label AND a colour (never colour alone, §18); the brand accent is used only for the ServerAlyzer name,
/// never for health (§4). Distinct layouts per size (§6): Small = summary, Medium/Large = server rows.
/// </summary>
public static class WidgetCardRenderer
{
    // Emit human-readable Unicode (localized text keeps its accents) while STILL escaping the JSON/HTML
    // structural characters (< > & ' "), so the card is compact and readable but never injectable.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static WidgetCard Render(WidgetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        var body = vm.DisplayState switch
        {
            WidgetDisplayState.Unavailable => UnavailableBody(vm),
            WidgetDisplayState.Empty => EmptyBody(vm),
            _ => vm.Size == WidgetSizeHint.Small ? SmallBody(vm) : ListBody(vm)
        };

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = body,
            // Tapping the card background (anything not a server row) opens the dashboard. The row's own
            // selectAction (openServer) takes precedence when a row is tapped. Deep-link is Slice 4.
            ["selectAction"] = Execute(ActivationVerbs.OpenDashboard, data: null)
        };

        return new WidgetCard(card.ToJsonString(SerializerOptions), "{}");
    }

    private static JsonArray SmallBody(WidgetViewModel vm) => new()
    {
        Brand(vm),
        Text(vm.PrimarySummary, size: "Large", weight: "Bolder", color: vm.OverallHealthColor, wrap: true),
        Text(vm.CountsSummary, subtle: true, wrap: true, spacingNone: true),
        Freshness(vm)
    };

    private static JsonArray ListBody(WidgetViewModel vm)
    {
        var body = new JsonArray
        {
            HeaderRow(vm)
        };

        // The counts line goes under the header on BOTH Medium and Large: the per-size row cap can hide a
        // severity (e.g. an Unknown server past the cap), so the counts keep every severity visible and
        // stop the worst-status hero from mis-reading as "the app is offline" instead of "1 of N" (§21,
        // Prism S3 M3/M4).
        if (vm.CountsSummary.Length > 0)
        {
            body.Add(Text(vm.CountsSummary, subtle: true, size: "Small", wrap: true, spacingNone: true));
        }

        body.Add(Freshness(vm));

        foreach (var row in vm.Rows)
        {
            body.Add(ServerRow(row));
        }

        if (vm.OverflowText.Length > 0)
        {
            body.Add(Text(vm.OverflowText, subtle: true, size: "Small"));
        }

        return body;
    }

    private static JsonArray EmptyBody(WidgetViewModel vm) => new()
    {
        Brand(vm),
        Text(vm.NoServersText, subtle: true, wrap: true),
        Freshness(vm)
    };

    private static JsonArray UnavailableBody(WidgetViewModel vm) => new()
    {
        Brand(vm),
        Text(vm.NoDataTitle, weight: "Bolder", wrap: true),
        Text(vm.NoDataBody, subtle: true, wrap: true, spacingNone: true)
    };

    // Header: brand (accent) on the left, overall health label (coloured) on the right.
    private static JsonObject HeaderRow(WidgetViewModel vm) => new()
    {
        ["type"] = "ColumnSet",
        ["columns"] = new JsonArray
        {
            Column("stretch", Brand(vm)),
            Column("auto", Text(vm.OverallHealthLabel, weight: "Bolder", color: vm.OverallHealthColor))
        }
    };

    private static JsonObject ServerRow(WidgetServerRow row)
    {
        return new JsonObject
        {
            ["type"] = "Container",
            ["spacing"] = "Small",
            // Tapping this row opens the dashboard focused on this server (opaque id only, §13).
            ["selectAction"] = Execute(
                ActivationVerbs.OpenServer,
                new JsonObject { [ActivationVerbs.ServerIdDataKey] = row.ServerId.ToString("D") }),
            ["items"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "ColumnSet",
                    ["columns"] = new JsonArray
                    {
                        Column("stretch", Text(row.DisplayName, weight: "Bolder", wrap: false)),
                        Column("auto", Text(row.HealthLabel, color: row.HealthColor, weight: "Bolder"))
                    }
                },
                Text(row.MetricsText, subtle: true, size: "Small", spacingNone: true, wrap: false)
            }
        };
    }

    private static JsonObject Brand(WidgetViewModel vm) =>
        Text(vm.BrandName, weight: "Bolder", color: "accent");

    private static JsonObject Freshness(WidgetViewModel vm) =>
        Text(vm.FreshnessText, subtle: true, size: "Small", spacingNone: true);

    // An allowlisted Adaptive Card Action.Execute. The Widgets host routes it to the provider's
    // OnActionInvoked as a (verb, data) pair — it launches the app, never runs anything itself.
    private static JsonObject Execute(string verb, JsonObject? data)
    {
        var action = new JsonObject
        {
            ["type"] = "Action.Execute",
            ["verb"] = verb
        };

        if (data is not null)
        {
            action["data"] = data;
        }

        return action;
    }

    private static JsonObject Column(string width, JsonObject item) => new()
    {
        ["type"] = "Column",
        ["width"] = width,
        ["verticalContentAlignment"] = "Center",
        ["items"] = new JsonArray { item }
    };

    private static JsonObject Text(
        string text,
        string? size = null,
        string? weight = null,
        string? color = null,
        bool subtle = false,
        bool wrap = false,
        bool spacingNone = false)
    {
        var node = new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = text ?? string.Empty
        };

        if (size is not null)
        {
            node["size"] = size;
        }

        if (weight is not null)
        {
            node["weight"] = weight;
        }

        if (color is not null)
        {
            node["color"] = color;
        }

        if (subtle)
        {
            node["isSubtle"] = true;
        }

        if (wrap)
        {
            node["wrap"] = true;
        }

        if (spacingNone)
        {
            node["spacing"] = "None";
        }

        return node;
    }
}
