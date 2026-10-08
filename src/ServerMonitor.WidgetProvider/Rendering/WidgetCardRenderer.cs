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
    /// <summary>
    /// Separator between a metric label and its value inside ONE text block ("CPU 41%"). A no-break space,
    /// so label and value never wrap apart. It is data, not template text (no literal copy in templates).
    /// </summary>
    private const string LabelValueGap = "\u00A0";

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
            ["name"] = row.DisplayName.ForCard(),
            ["status"] = row.StatusText,
            ["statusColor"] = row.StatusColor,
            ["gap"] = LabelValueGap
        };

        AddMetric(data, "cpu", row.Cpu, row.IsStale);
        AddMetric(data, "mem", row.Memory, row.IsStale);
        AddMetric(data, "disk", row.Disk, row.IsStale);
        return data;
    }

    private static void AddMetric(JsonObject data, string key, WidgetMetric metric, bool stale)
    {
        var filled = metric.Percent is { } percent ? WidgetLayout.FilledSegments(percent) : 0;
        var hasMeter = metric.Percent is not null;

        data[key + "Label"] = metric.Label;
        data[key + "Value"] = metric.ValueText;
        data[key + "Detail"] = metric.Detail;
        data[key + "Fill"] = Glyphs(WidgetLayout.MeterFill, filled);
        data[key + "Track"] = Glyphs(WidgetLayout.MeterTrack, WidgetLayout.MeterSegments - filled);
        // 0 % has no fill run and 100 % no track run: never an empty TextRun on the card.
        data[key + "HasFill"] = filled > 0;
        data[key + "HasTrack"] = filled < WidgetLayout.MeterSegments;
        data[key + "MeterFresh"] = hasMeter && !stale;
        data[key + "MeterStale"] = hasMeter && stale;

        // Prism C0 §1 bars: two weighted columns. HasFill/HasTrack above hold for both meter styles (fill iff
        // > 0 %, track iff < 100 %), so the bar never draws a zero-weight column.
        var weight = metric.Percent is { } p ? WidgetLayout.BarFillWeight(p) : 0;
        data[key + "FillW"] = weight;
        data[key + "TrackW"] = 100 - weight;
    }

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
            UnavailableState(size)
        };

        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["version"] = "1.6",
            ["verticalContentAlignment"] = "Top"
        };

        // Medium/Large: the whole card opens the Dashboard in every state (SPEC §3); rows override with
        // openServer. Small allows ONE touch target (Cortex L-1): its single action lives on each state
        // container instead, so the Empty state's only target is its CTA button.
        if (size != WidgetSizeHint.Small)
        {
            card["selectAction"] = Execute(ActivationVerbs.OpenDashboard);
        }

        card["body"] = body;
        return card;
    }

    // ---- Small: the fleet verdict. P-RC-2 fallback (amended by Prism P-C1-3): "3/3" ExtraLarge Bolder +
    // title + subtitle + the freshness line — no fleet bar, which pushed the freshness out of 146 px. ----
    private static JsonObject SmallFleet() => When("isFleet", new JsonObject
    {
        ["type"] = "Container",
        ["selectAction"] = Execute(ActivationVerbs.OpenDashboard),
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
                    // Prism C0 §3/§4: the Small title is Medium Bolder WITHOUT style:heading, and nothing in the
                    // Small card wraps. The board leaves ~85 px under the header and clips SILENTLY, and this
                    // layout needs 64 px only if no line wraps; an elided title still leads with the count.
                    Column("stretch", new JsonArray
                    {
                        Text("title", size: "Medium", weight: "Bolder"),
                        When("hasSubtitle", Text("subtitle", size: "Small", subtle: true, spacingNone: true))
                    }, verticalAlignment: "Center")
                }
            },
            Text("footer", size: "Small", subtle: true, spacing: "Small")
        }
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
                    // Prism P-C1-2: the heading never truncates (auto); the summary takes the rest and WRAPS
                    // to a second line rather than eliding a problem count.
                    Column("auto", new JsonArray { Text("serversHeading", weight: "Bolder") }, verticalAlignment: "Center"),
                    Column("stretch", new JsonArray
                    {
                        Text("summary", size: "Small", color: "${stateColor}", wrap: true, horizontalAlignment: "Right")
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
            // Prism P-C1-1: label and value are ONE text block ("CPU 41%"), never a label/value ColumnSet —
            // the host squeezes an `auto` column and elides the value ("4…"), which hides the % that must
            // always be text. A text block wraps instead of being cut by column layout.
            new JsonObject
            {
                ["type"] = "RichTextBlock",
                ["inlines"] = new JsonArray
                {
                    Run(key + "Label", size: "Small", subtle: true),
                    Run("gap", size: "Small"),
                    Run(key + "Value", size: "Small", weight: "Bolder")
                }
            },
        };

        // Unknown / hidden (Offline, Unknown): no meter at all, never a 0 % track (D-17).
        if (WidgetLayout.Meter == WidgetLayout.MeterStyle.Bars)
        {
            // Fresh: the METRIC colour (never a state colour, RC-7). Stale: muted fill. Theme by $host.
            items.Add(When(key + "MeterFresh", Bar(key, large, stale: false)));
            items.Add(When(key + "MeterStale", Bar(key, large, stale: true)));
        }
        else
        {
            // Fallback (c): neutral accent fill + subtle track; stale = both subtle.
            items.Add(When(key + "MeterFresh", Meter(key, fillColor: WidgetLayout.MeterFillColor, fillSubtle: false)));
            items.Add(When(key + "MeterStale", Meter(key, fillColor: null, fillSubtle: true)));
        }

        if (large)
        {
            // RC-10 detail: "Ativo 43d 18h" under CPU, "0,6/11,6 GB" under RAM/Disk, "—" when unknown. It
            // WRAPS rather than eliding: the proxy render showed "Ativo 43d 1…" in an ~80 px column (C1-fix
            // DV-14; RC-10's fallback "43d 18h" stays Prism's call after C0 measures).
            items.Add(Text(key + "Detail", size: "Small", subtle: true, wrap: true, spacingNone: true));
        }

        return Column("stretch", items);
    }

    // ---- Continuous bar (Prism C0 §1): ColumnSet of a weighted fill column + a weighted track column, each
    // a Container of minHeight with a 1x1 constant data-URI backgroundImage repeated. The URL is a LITERAL
    // WidgetImages constant chosen by $when on $host.hostTheme — never bound from data (Vigil C0 §2).
    private static JsonObject Bar(string key, bool large, bool stale)
    {
        var (fillDark, fillLight) = stale
            ? (WidgetImages.BarStaleDark, WidgetImages.BarStaleLight)
            : key switch
            {
                "cpu" => (WidgetImages.BarCpuDark, WidgetImages.BarCpuLight),
                "mem" => (WidgetImages.BarRamDark, WidgetImages.BarRamLight),
                _ => (WidgetImages.BarDiskDark, WidgetImages.BarDiskLight)
            };

        return new JsonObject
        {
            ["type"] = "Container",
            ["spacing"] = "Small",
            ["items"] = new JsonArray
            {
                WhenTheme(dark: true, BarColumns(key, large, fillDark, WidgetImages.BarTrackDark)),
                WhenTheme(dark: false, BarColumns(key, large, fillLight, WidgetImages.BarTrackLight))
            }
        };
    }

    private static JsonObject BarColumns(string key, bool large, string fill, string track) => new()
    {
        ["type"] = "ColumnSet",
        ["spacing"] = "None",
        ["columns"] = new JsonArray
        {
            When(key + "HasFill", BarColumn(key + "FillW", large, fill)),
            When(key + "HasTrack", BarColumn(key + "TrackW", large, track))
        }
    };

    private static JsonObject BarColumn(string weightKey, bool large, string url) => new()
    {
        ["type"] = "Column",
        ["width"] = Bind(weightKey),
        ["spacing"] = "None",
        ["items"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "Container",
                ["minHeight"] = WidgetLayout.BarHeight(large),
                ["backgroundImage"] = new JsonObject { ["url"] = url, ["fillMode"] = "repeat" },
                ["items"] = new JsonArray()
            }
        }
    };

    /// <summary>
    /// Host-theme selection (proven on the board, P-theme / P-D): dark when the host says "dark", the light
    /// set for anything else, so a future host theme still gets a legible bar.
    /// </summary>
    private static JsonObject WhenTheme(bool dark, JsonObject element)
    {
        element["$when"] = dark ? "${$host.hostTheme == 'dark'}" : "${$host.hostTheme != 'dark'}";
        return element;
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
        var items = new JsonArray();
        if (size != WidgetSizeHint.Small)
        {
            // Prism C0 §5: ServerStack01 40x40 above the title, M/L only. Decorative; its altText is the
            // empty-state title (from WidgetStrings), so a screen reader hears no new or invented text.
            items.Add(WhenTheme(dark: true, EmptyIcon(WidgetImages.EmptyIconDark)));
            items.Add(WhenTheme(dark: false, EmptyIcon(WidgetImages.EmptyIconLight)));
            items.Add(Heading("title", center: true));
            items.Add(Text("body", size: "Small", subtle: true, wrap: true, horizontalAlignment: "Center"));
        }
        else
        {
            items.Add(Text("title", size: "Medium", weight: "Bolder", horizontalAlignment: "Center")); // C0 §4: no heading on S
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
        if (size != WidgetSizeHint.Small)
        {
            // Prism P-C1-6: the Small empty card has no freshness line (Figma 112:11594); it would be cut.
            items.Add(When("hasFooter", Text("footer", size: "Small", subtle: true, horizontalAlignment: "Center")));
        }

        return When("isEmpty", new JsonObject { ["type"] = "Container", ["items"] = items });
    }

    private static JsonObject EmptyIcon(string url) => new()
    {
        ["type"] = "Image",
        ["url"] = url,
        ["width"] = "40px",
        ["height"] = "40px",
        ["horizontalAlignment"] = "Center",
        ["altText"] = Bind("title")
    };

    // ---- Unavailable: neutral title + body, no CTA (SPEC §3); the card still opens the Dashboard. ----
    private static JsonObject UnavailableState(WidgetSizeHint size)
    {
        var container = new JsonObject
        {
            ["type"] = "Container",
            ["items"] = new JsonArray
            {
                // C0 §4: style:heading on M/L only; the Small title is Medium Bolder and does not wrap.
                size == WidgetSizeHint.Small ? Text("title", size: "Medium", weight: "Bolder") : Heading("title"),
                Text("body", size: "Small", subtle: true, wrap: true)
            }
        };

        if (size == WidgetSizeHint.Small)
        {
            container["selectAction"] = Execute(ActivationVerbs.OpenDashboard); // Small: no card-level action
        }

        return When("isUnavailable", container);
    }

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
        string? style = null,
        string? spacing = null)
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
        else if (spacing is not null)
        {
            node["spacing"] = spacing;
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
