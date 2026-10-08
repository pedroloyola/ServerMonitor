using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using ServerMonitor.ActivationContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>A widget update: a constant Adaptive Card template plus the data it binds.</summary>
public sealed record WidgetCard(string TemplateJson, string DataJson);

/// <summary>
/// UI.9 V3 renderer (SPEC §6, D-UI9-8). It returns a CONSTANT template per size and a data object built
/// from the view model.
/// <para>
/// <b>Template (V-RC-3, UI9-SEC-2).</b>
/// <list type="bullet">
/// <item>One of three templates (Small/Medium/Large), built once from code and never from data, so its
/// bytes are identical for every snapshot, hostile or not.</item>
/// <item>States are selected with <c>$when</c> on precomputed boolean data keys. Server rows repeat with
/// <c>$data</c> over an array.</item>
/// <item>Every visible text, alt text and button title is a <c>${key}</c> binding: the template carries no
/// literal copy, and all of it comes from <see cref="WidgetStrings"/> through the data.</item>
/// <item>Verbs and the <see cref="ActivationVerbs.ServerIdDataKey"/> key are literals in the template; only
/// the opaque id is bound.</item>
/// </list>
/// </para>
/// <para>
/// <b>Untrusted text (R-1).</b> The one user/server-derived string, the display name, is emitted ONLY as a
/// <c>TextRun</c> inside a <c>RichTextBlock</c>, which the host never parses as markdown. Markdown is a real
/// vector in <c>TextBlock</c>: links, emphasis, lists, <c>{{DATE()}}</c>.
/// </para>
/// <para>
/// <b>Visual defaults that do not depend on the board spike (C0).</b>
/// <list type="bullet">
/// <item>No images, no URLs of any kind, no <c>$schema</c>.</item>
/// <item>No <c>"header"</c> key: the host draws its attribution header.</item>
/// <item>Meters use strategy (c), a foreground glyph meter (R-2). Fill <c>▰</c> and track <c>▱</c> differ by
/// SHAPE; the track is <c>default</c> subtle; the fill is the neutral <c>accent</c>, never a health colour;
/// stale means all subtle. The % is always text beside the meter.</item>
/// <item>Typography follows Prism RC-8 with host sizes R-4. State is always text, never colour only.</item>
/// </list>
/// </para>
/// </summary>
public static class WidgetCardRenderer
{
    /// <summary>Glyph meter segments. MEASURED pitch: a Default-size block glyph is ~13 px; a V3 metric
    /// column is ~76–80 px, so 5 fit with margin (legacy M13-QA-6 measurement). C0 re-measures ▰/▱.</summary>
    public const int MeterSegments = 5;

    /// <summary>Filled and empty meter glyphs — told apart by shape, not colour (R-2 / P-RC-1).</summary>
    public const string MeterFill = "▰";
    public const string MeterTrack = "▱";

    /// <summary>Above this many servers the Small fleet bar is omitted rather than truncated (P-RC-2).</summary>
    public const int MaxFleetTicks = 8;

    // Human-readable Unicode, while still escaping JSON/HTML structural characters (< > & ' ").
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly string SmallTemplate = Serialize(BuildTemplate(WidgetSizeHint.Small));
    private static readonly string MediumTemplate = Serialize(BuildTemplate(WidgetSizeHint.Medium));
    private static readonly string LargeTemplate = Serialize(BuildTemplate(WidgetSizeHint.Large));

    /// <summary>The constant template for a size (V-RC-3: never derived from data).</summary>
    public static string TemplateFor(WidgetSizeHint size) => size switch
    {
        WidgetSizeHint.Small => SmallTemplate,
        WidgetSizeHint.Large => LargeTemplate,
        _ => MediumTemplate
    };

    public static WidgetCard Render(WidgetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return new WidgetCard(TemplateFor(vm.Size), Serialize(BuildData(vm)));
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(SerializerOptions);

    // =====================================================================================================
    // Data: fixed keys whatever the state; rows as an array of objects with fixed keys (V-RC-3 b).
    // =====================================================================================================

    private static JsonObject BuildData(WidgetViewModel vm)
    {
        var fleet = vm.CardState is WidgetCardState.Healthy or WidgetCardState.Attention
            or WidgetCardState.NoCurrentData or WidgetCardState.Stale;
        var stale = vm.Freshness == WidgetFreshnessState.Stale;
        var showFleetBar = fleet && vm.TotalServers > 0 && vm.TotalServers <= MaxFleetTicks;

        var rows = new JsonArray();
        foreach (var row in vm.Rows)
        {
            rows.Add(RowData(row));
        }

        return new JsonObject
        {
            ["isFleet"] = fleet,
            ["isHealthy"] = vm.CardState == WidgetCardState.Healthy,
            ["isAttention"] = vm.CardState == WidgetCardState.Attention,
            ["isNoCurrentData"] = vm.CardState == WidgetCardState.NoCurrentData,
            ["isStale"] = vm.CardState == WidgetCardState.Stale,
            ["isEmpty"] = vm.CardState == WidgetCardState.Empty,
            ["isUnavailable"] = vm.CardState == WidgetCardState.Unavailable,
            ["title"] = vm.Title,
            ["subtitle"] = vm.Subtitle,
            ["hasSubtitle"] = vm.Subtitle.Length > 0,
            ["summary"] = vm.Summary,
            ["stateColor"] = vm.StateColor,
            ["body"] = vm.Body,
            ["cta"] = vm.CtaText,
            ["footer"] = vm.FooterText,
            ["hasFooter"] = vm.FooterText.Length > 0,
            ["serversHeading"] = vm.ServersHeading,
            ["rowsOfTotal"] = vm.RowsOfTotalText,
            ["fraction"] = vm.FractionText,
            ["fleetBarFresh"] = showFleetBar && !stale,
            ["fleetBarStale"] = showFleetBar && stale,
            ["fleetCritical"] = Glyphs(MeterFill, vm.CriticalOrOfflineCount),
            ["hasFleetCritical"] = vm.CriticalOrOfflineCount > 0,
            ["fleetWarning"] = Glyphs(MeterFill, vm.WarningCount),
            ["hasFleetWarning"] = vm.WarningCount > 0,
            ["fleetNoData"] = Glyphs(MeterTrack, vm.NoCurrentDataCount),
            ["hasFleetNoData"] = vm.NoCurrentDataCount > 0,
            ["fleetHealthy"] = Glyphs(MeterFill, vm.HealthyFreshCount),
            ["hasFleetHealthy"] = vm.HealthyFreshCount > 0,
            ["fleetAll"] = Glyphs(MeterFill, vm.TotalServers),
            ["rows"] = rows
        };
    }

    private static JsonObject RowData(WidgetServerRow row)
    {
        var data = new JsonObject
        {
            ["id"] = row.ServerId.ToString("D", CultureInfo.InvariantCulture),
            // The ONLY untrusted string on the card. The template binds ${name} in a TextRun and nowhere
            // else (R-1); see NameRun.
            ["name"] = row.DisplayName.Value,
            ["status"] = row.StatusText,
            ["statusColor"] = row.StatusColor
        };

        AddMetric(data, "cpu", row.Cpu, row.IsStale);
        AddMetric(data, "mem", row.Memory, row.IsStale);
        AddMetric(data, "disk", row.Disk, row.IsStale);
        return data;
    }

    private static void AddMetric(JsonObject data, string key, WidgetMetric metric, bool stale)
    {
        var filled = metric.Percent is { } percent ? FilledSegments(percent) : 0;
        var hasMeter = metric.Percent is not null;

        data[key + "Label"] = metric.Label;
        data[key + "Value"] = metric.ValueText;
        data[key + "Detail"] = metric.Detail;
        data[key + "Fill"] = Glyphs(MeterFill, filled);
        data[key + "Track"] = Glyphs(MeterTrack, MeterSegments - filled);
        // 0 % has no fill run and 100 % no track run: never an empty TextRun on the card.
        data[key + "HasFill"] = filled > 0;
        data[key + "HasTrack"] = filled < MeterSegments;
        data[key + "MeterFresh"] = hasMeter && !stale;
        data[key + "MeterStale"] = hasMeter && stale;
    }

    /// <summary>
    /// Filled segments for a percentage, rounded to the nearest segment (DV-10): 0–9 % → 0, 10–29 % → 1,
    /// …, 90–100 % → 5. The meter is a glance aid; the exact % is always printed beside it.
    /// </summary>
    public static int FilledSegments(int percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) * MeterSegments / 100d, MidpointRounding.AwayFromZero);

    private static string Glyphs(string glyph, int count) =>
        count <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(glyph, count));

    // =====================================================================================================
    // Templates: built once per size from code only. Nothing below may read the view model.
    // =====================================================================================================

    private static JsonObject BuildTemplate(WidgetSizeHint size)
    {
        var body = new JsonArray
        {
            size == WidgetSizeHint.Small ? SmallFleet() : ListFleet(size == WidgetSizeHint.Large),
            EmptyState(size),
            UnavailableState()
        };

        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["version"] = "1.6",
            ["verticalContentAlignment"] = "Top",
            // The whole card opens the Dashboard in every state (SPEC §3); rows override with openServer.
            ["selectAction"] = Execute(ActivationVerbs.OpenDashboard),
            ["body"] = body
        };
    }

    // ---- Small: the fleet verdict. P-RC-2 fallback: "3/3" ExtraLarge Bolder + title + subtitle, a fleet
    // glyph bar only up to MaxFleetTicks servers, then the freshness line. -----------------------------
    private static JsonObject SmallFleet() => When("isFleet", new JsonObject
    {
        ["type"] = "Container",
        ["items"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    Column("auto", new JsonArray
                    {
                        Text("fraction", size: "ExtraLarge", weight: "Bolder", color: "${stateColor}")
                    }, verticalAlignment: "Center"),
                    Column("stretch", new JsonArray
                    {
                        Heading("title"),
                        When("hasSubtitle", Text("subtitle", size: "Small", subtle: true, wrap: true, spacingNone: true))
                    }, verticalAlignment: "Center")
                }
            },
            FleetBarFresh(),
            FleetBarStale(),
            Text("footer", size: "Small", subtle: true)
        }
    });

    // One glyph per server, worst-first, in health FOREGROUND colours (the bar IS health by design, and
    // the title states the same in text). Unknown/not-fresh use the hollow track glyph. Stale: one neutral
    // subtle run — a stale fleet is never painted green.
    private static JsonObject FleetBarFresh() =>
        When("fleetBarFresh", new JsonObject
        {
            ["type"] = "RichTextBlock",
            ["inlines"] = new JsonArray
            {
                When("hasFleetCritical", Run("fleetCritical", color: "attention")),
                When("hasFleetWarning", Run("fleetWarning", color: "warning")),
                When("hasFleetNoData", Run("fleetNoData", subtle: true)),
                When("hasFleetHealthy", Run("fleetHealthy", color: "good"))
            }
        });

    private static JsonObject FleetBarStale() =>
        When("fleetBarStale", new JsonObject
        {
            ["type"] = "RichTextBlock",
            ["inlines"] = new JsonArray { Run("fleetAll", subtle: true) }
        });

    // ---- Medium/Large: "Servers" + summary, rows, footer with "N of M". ---------------------------------
    private static JsonObject ListFleet(bool large) => When("isFleet", new JsonObject
    {
        ["type"] = "Container",
        ["items"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray { Text("serversHeading", weight: "Bolder") }, verticalAlignment: "Center"),
                    Column("auto", new JsonArray
                    {
                        Text("summary", size: "Small", color: "${stateColor}", horizontalAlignment: "Right")
                    }, verticalAlignment: "Center")
                }
            },
            ServerRow(large),
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "Small",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray { Text("footer", size: "Small", subtle: true) }),
                    Column("auto", new JsonArray
                    {
                        Text("rowsOfTotal", size: "Small", subtle: true, horizontalAlignment: "Right")
                    })
                }
            }
        }
    });

    private static JsonObject ServerRow(bool large) => new()
    {
        ["type"] = "Container",
        ["$data"] = "${rows}",
        ["separator"] = true,
        ["spacing"] = "Small",
        ["selectAction"] = Execute(ActivationVerbs.OpenServer, withServerId: true),
        ["items"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray { NameRun() }, verticalAlignment: "Center"),
                    Column("auto", new JsonArray
                    {
                        Text("status", size: "Small", color: "${statusColor}", horizontalAlignment: "Right")
                    }, verticalAlignment: "Center")
                }
            },
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "Small",
                ["columns"] = new JsonArray
                {
                    MetricColumn("cpu", large),
                    MetricColumn("mem", large),
                    MetricColumn("disk", large)
                }
            }
        }
    };

    /// <summary>
    /// THE single emitter of the untrusted display name (R-1, Cortex §7.1): a RichTextBlock whose only
    /// inline is a TextRun — never a markdown-capable TextBlock. Tests pin that <c>${name}</c> appears in
    /// the template exactly here.
    /// </summary>
    private static JsonObject NameRun() => new()
    {
        ["type"] = "RichTextBlock",
        ["inlines"] = new JsonArray { Run("name", size: "Small", weight: "Bolder") }
    };

    private static JsonObject MetricColumn(string key, bool large)
    {
        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray { Text(key + "Label", size: "Small", subtle: true) }),
                    Column("auto", new JsonArray { Text(key + "Value", size: "Small", weight: "Bolder") })
                }
            },
            // Fresh: neutral accent fill + subtle track. Stale: both subtle (muted, Prism RC-7). Unknown /
            // hidden (Offline, Unknown): neither — no meter at all, never a 0 % track.
            When(key + "MeterFresh", Meter(key, fillColor: "accent", fillSubtle: false)),
            When(key + "MeterStale", Meter(key, fillColor: null, fillSubtle: true))
        };

        if (large)
        {
            // RC-10 detail: "Ativo 43d 18h" under CPU, "0,6/11,6 GB" under RAM/Disk, "—" when unknown.
            items.Add(Text(key + "Detail", size: "Small", subtle: true, spacingNone: true));
        }

        return Column("stretch", items);
    }

    private static JsonObject Meter(string key, string? fillColor, bool fillSubtle) => new()
    {
        ["type"] = "RichTextBlock",
        ["spacing"] = "None",
        ["inlines"] = new JsonArray
        {
            When(key + "HasFill", Run(key + "Fill", color: fillColor, subtle: fillSubtle)),
            When(key + "HasTrack", Run(key + "Track", subtle: true))
        }
    };

    // ---- Empty: title (+ body on M/L) + the dashboard CTA (D2 = openDashboard, label "Open ServerAlyzer").
    private static JsonObject EmptyState(WidgetSizeHint size)
    {
        var items = new JsonArray { Heading("title", center: true) };
        if (size != WidgetSizeHint.Small)
        {
            items.Add(Text("body", size: "Small", subtle: true, wrap: true, horizontalAlignment: "Center"));
        }

        items.Add(new JsonObject
        {
            ["type"] = "ActionSet",
            ["horizontalAlignment"] = "Center",
            ["actions"] = new JsonArray
            {
                Execute(ActivationVerbs.OpenDashboard, title: "${cta}")
            }
        });
        items.Add(When("hasFooter", Text("footer", size: "Small", subtle: true, horizontalAlignment: "Center")));

        return When("isEmpty", new JsonObject { ["type"] = "Container", ["items"] = items });
    }

    // ---- Unavailable: neutral title + body, no CTA (SPEC §3). -----------------------------------------
    private static JsonObject UnavailableState() => When("isUnavailable", new JsonObject
    {
        ["type"] = "Container",
        ["items"] = new JsonArray
        {
            Heading("title"),
            Text("body", size: "Small", subtle: true, wrap: true)
        }
    });

    // =====================================================================================================
    // Element helpers. Every text-bearing helper takes a DATA KEY, never text, so no literal copy can
    // enter a template.
    // =====================================================================================================

    private static string Bind(string key) => "${" + key + "}";

    private static JsonObject When(string key, JsonObject element)
    {
        element["$when"] = Bind(key);
        return element;
    }

    private static JsonObject Execute(string verb, bool withServerId = false, string? title = null)
    {
        var action = new JsonObject
        {
            ["type"] = "Action.Execute",
            ["verb"] = verb
        };

        if (title is not null)
        {
            action["title"] = title;
        }

        if (withServerId)
        {
            // The key is a template LITERAL; only the opaque id value is bound (V-RC-3 c).
            action["data"] = new JsonObject { [ActivationVerbs.ServerIdDataKey] = Bind("id") };
        }

        return action;
    }

    private static JsonObject Column(string width, JsonArray items, string? verticalAlignment = null)
    {
        var column = new JsonObject
        {
            ["type"] = "Column",
            ["width"] = width,
            ["items"] = items
        };

        if (verticalAlignment is not null)
        {
            column["verticalContentAlignment"] = verticalAlignment;
        }

        return column;
    }

    // RC-8 / R-4: titles use the host "heading" style (Large Bolder in the board's host config) with
    // Medium Bolder as the explicit fallback size.
    private static JsonObject Heading(string key, bool center = false) =>
        Text(key, size: "Medium", weight: "Bolder", wrap: true, style: "heading",
            horizontalAlignment: center ? "Center" : null);

    private static JsonObject Text(
        string key,
        string? size = null,
        string? weight = null,
        string? color = null,
        bool subtle = false,
        bool wrap = false,
        bool spacingNone = false,
        string? horizontalAlignment = null,
        string? style = null)
    {
        var node = new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = Bind(key)
        };

        if (style is not null)
        {
            node["style"] = style;
        }

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

        node["wrap"] = wrap;

        if (spacingNone)
        {
            node["spacing"] = "None";
        }

        if (horizontalAlignment is not null)
        {
            node["horizontalAlignment"] = horizontalAlignment;
        }

        return node;
    }

    private static JsonObject Run(string key, string? size = null, string? weight = null, string? color = null,
        bool subtle = false)
    {
        var run = new JsonObject
        {
            ["type"] = "TextRun",
            ["text"] = Bind(key)
        };

        if (size is not null)
        {
            run["size"] = size;
        }

        if (weight is not null)
        {
            run["weight"] = weight;
        }

        if (color is not null)
        {
            run["color"] = color;
        }

        if (subtle)
        {
            run["isSubtle"] = true;
        }

        return run;
    }
}
