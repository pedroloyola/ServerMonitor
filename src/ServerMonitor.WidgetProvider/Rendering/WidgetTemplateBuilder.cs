using System.Text.Json.Nodes;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>A widget update: an Adaptive Card template plus its (here empty) data binding.</summary>
public sealed record WidgetCard(string TemplateJson, string DataJson);

/// <summary>
/// Builds a <b>minimal, neutral DEV Adaptive Card</b> to prove the provider→host update plumbing
/// (ADR-018 §19). It shows only non-sensitive aggregates — overall health, server count, and freshness —
/// and NEVER individual server names/metrics; the final small/medium/large art direction is Slice 3.
/// The card is marked "dev template" so it can never be mistaken for release-ready UI. Built with
/// <see cref="JsonNode"/> so the output is always valid JSON against the Adaptive Cards 1.5 schema the
/// Widgets host supports.
/// </summary>
public static class WidgetTemplateBuilder
{
    public const string DevMarker = "dev template - final design in a later update";

    public static WidgetCard Build(WidgetReadResult read, WidgetFreshnessState freshness, WidgetSizeHint size)
    {
        _ = size; // Slice 2 renders the same neutral card for every size; S/M/L is Slice 3.

        var body = new JsonArray
        {
            TextBlock("ServerAlyzer", weight: "Bolder", cardSize: "Medium")
        };

        if (read.IsAvailable && read.Snapshot is { } snapshot)
        {
            body.Add(TextBlock($"Overall: {snapshot.OverallHealth}", spacingNone: true));

            var count = snapshot.Servers.Count;
            var plural = count == 1 ? "server" : "servers";
            var freshnessText = freshness switch
            {
                WidgetFreshnessState.Fresh => "updated recently",
                WidgetFreshnessState.Stale => "updated a while ago",
                _ => "no recent update"
            };
            body.Add(TextBlock($"{count} {plural} - {freshnessText}", subtle: true, wrap: true));
        }
        else
        {
            body.Add(TextBlock("Widget data unavailable", subtle: true, wrap: true));
        }

        body.Add(TextBlock(DevMarker, subtle: true, cardSize: "Small", wrap: true));

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = body
        };

        return new WidgetCard(card.ToJsonString(), "{}");
    }

    private static JsonObject TextBlock(
        string text,
        string? weight = null,
        string? cardSize = null,
        bool subtle = false,
        bool wrap = false,
        bool spacingNone = false)
    {
        var node = new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = text
        };

        if (weight is not null)
        {
            node["weight"] = weight;
        }

        if (cardSize is not null)
        {
            node["size"] = cardSize;
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
