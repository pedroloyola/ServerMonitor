using System.Globalization;
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
/// Renders a <see cref="WidgetViewModel"/> to an Adaptive Card 1.6 in the M13 "instrument panel" visual
/// language (Fable design, refined against the real host): a caps accent kicker, an oversized numeric hero
/// with a small trailing unit, and full-bleed segmented tick meters — colour used only for meaning, all
/// top-aligned. Uses ONLY widget-supported elements (<c>TextBlock</c>, <c>ColumnSet</c>/<c>Column</c>,
/// <c>Container</c>) plus container <c>style</c> backgrounds (with fixed-pixel gap columns) for the meters —
/// no images/HTML/SVG. <c>"header": null</c> lets the composition own the top region (that strip is not
/// clickable, so the body carries the <c>selectAction</c> deep-links). Health is always a text label AND a
/// colour (§18); the accent role is used ONLY for the fleet kicker (never for health, §4) - and note that
/// Adaptive Cards "accent" is an ENUM the host resolves to the SYSTEM accent, not our brand #1846E1, so the
/// kicker is brand-positioned but not brand-coloured; the
/// meter fill is magnitude-neutral accent (magnitude read by filled-tick COUNT). Layouts genuinely differ
/// per size: Small = fleet verdict, Medium/Large = telemetry blocks.
/// </summary>
public static class WidgetCardRenderer
{
    // Emit human-readable Unicode (localized text keeps its accents) while STILL escaping the JSON/HTML
    // structural characters (< > & ' "), so the card is compact and readable but never injectable.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private const string Dot = "●";           // status glyph
    private const int MeterSegments = 10;      // ticks per metric meter

    // M13-QA-6. The meters used Container BACKGROUND styles ("accent" filled, "emphasis" empty). Container
    // styles are the one part of the palette the host does not resolve usefully per theme: in its light
    // config both land on near-white, measured at 1.16:1 filled-vs-empty and 1.25:1 filled-vs-background,
    // so the instrument that carries magnitude read as an empty bar for every light-theme user. The fleet
    // bar had the same failure for the same reason.
    //
    // Text FOREGROUND colours DO resolve per theme - that is why every label on this card stays legible in
    // both. So the ticks are glyphs in a TextBlock now, buying correct per-theme contrast from the host
    // instead of fighting it. Filled and empty differ by BOTH luminance and shape (solid vs outlined), so
    // the distinction does not rest on colour alone and survives High Contrast.
    private const string TickFilled = "▮";      // black vertical rectangle
    private const string TickEmpty = "▯";       // white (outlined) vertical rectangle
    private const string MeterSize = "Default";     // metric meter glyph size
    private const string FleetSize = "Default";     // fleet-bar glyph size

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
            ["version"] = "1.6",
            // Our UX owns the top region (removes the host's duplicated brand header, Prism M1) and
            // top-aligns the body (kills the empty band above content, Prism M2).
            ["header"] = null,
            ["verticalContentAlignment"] = "Top",
            ["body"] = body
        };

        // The whole card opens the Dashboard; server blocks override this with openServer.
        if (vm.DisplayState != WidgetDisplayState.Unavailable)
        {
            card["selectAction"] = Execute(ActivationVerbs.OpenDashboard);
        }

        return new WidgetCard(card.ToJsonString(SerializerOptions), "{}");
    }

    // ---- Small: the fleet verdict. Kicker -> giant fraction -> label -> per-server fleet bar -> freshness.
    private static JsonArray SmallBody(WidgetViewModel vm)
    {
        var (number, unit) = SplitFraction(vm.HeroValue);
        return new JsonArray
        {
            Text(vm.FleetKicker, size: "Small", weight: "Bolder", color: "accent"),
            NumberUnit(number, unit, "ExtraLarge", "Medium", vm.OverallHealthColor, unitSubtle: false),
            Text(vm.HeroLabel.ToUpperInvariant(), size: "Small", weight: "Bolder", color: vm.OverallHealthColor,
                spacingNone: true),
            FleetBar(vm, FleetSize),
            Freshness(vm)
        };
    }

    // ---- Medium / Large: hero header (kicker + freshness, then fraction + label + fleet bar) then blocks.
    private static JsonArray ListBody(WidgetViewModel vm)
    {
        var large = vm.Size == WidgetSizeHint.Large;
        var body = new JsonArray
        {
            // Row 1: FROTA kicker (left) + freshness (right).
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "None",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray
                    {
                        Text(vm.FleetKicker, size: "Small", weight: "Bolder", color: "accent", spacingNone: true)
                    }),
                    Column("auto", new JsonArray { FreshnessRight(vm) }, verticalAlignment: "Bottom")
                }
            },
            // Row 2: giant fraction + label + fleet bar (Large adds an overall-health chip + a taller gauge).
            HeroLine(vm, large)
        };

        foreach (var row in vm.Rows)
        {
            body.Add(ServerBlock(row, vm, large));
        }

        if (vm.OverflowText.Length > 0)
        {
            // NOT subtle (Prism M1): this line is the only thing telling the user that servers exist which
            // the card is not showing. De-emphasising the one affordance that carries that fact works
            // against the very honesty the cap fix restored.
            body.Add(Text(vm.OverflowText, size: "Small", weight: "Bolder", spacingNone: true));
        }

        // Large fills the bottom band with a non-interactive fleet-summary footer (Fable).
        if (large)
        {
            body.Add(FleetSummary(vm));
        }

        return body;
    }

    // Hero line: big fraction, then label (+ overall chip on Large) and the fleet gauge filling the rest.
    private static JsonObject HeroLine(WidgetViewModel vm, bool large)
    {
        var (number, unit) = SplitFraction(vm.HeroValue);

        JsonObject labelRow = large
            ? new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "None",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray
                    {
                        Text(vm.HeroLabel.ToUpperInvariant(), size: "Small", weight: "Bolder",
                            color: vm.OverallHealthColor, spacingNone: true)
                    }),
                    Column("auto", new JsonArray
                    {
                        Text($"{Dot} {vm.OverallHealthLabel}", size: "Small", weight: "Bolder",
                            color: vm.OverallHealthColor, spacingNone: true)
                    }, verticalAlignment: "Bottom")
                }
            }
            : Text(vm.HeroLabel.ToUpperInvariant(), size: "Small", weight: "Bolder", color: vm.OverallHealthColor,
                spacingNone: true);

        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "None",
            ["columns"] = new JsonArray
            {
                Column("auto", new JsonArray
                {
                    Text(number, size: "ExtraLarge", weight: "Bolder", color: vm.OverallHealthColor, spacingNone: true)
                }, spacingNone: true),
                Column("auto", new JsonArray
                {
                    Text(unit, size: "Medium", weight: "Bolder", color: vm.OverallHealthColor, spacingNone: true)
                }, verticalAlignment: "Bottom", spacingNone: true),
                Column("stretch", new JsonArray
                {
                    labelRow,
                    FleetBar(vm, large ? "Medium" : FleetSize)
                }, verticalAlignment: "Center", spacing: "Medium")
            }
        };
    }

    // Large-only fleet-summary footer: four stat tiles from the health counts (only non-zero severities
    // carry colour, so a calm fleet shows no alarm). Non-interactive; anchors the bottom band (Fable).
    private static JsonObject FleetSummary(WidgetViewModel vm) => new()
    {
        ["type"] = "Container",
        ["spacing"] = "Medium",
        ["separator"] = true,
        ["items"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "None",
                ["columns"] = new JsonArray
                {
                    StatTile(vm.HealthyCount, vm.HealthyLabel, vm.HealthyCount > 0 ? "good" : null),
                    StatTile(vm.WarningCount, vm.WarningLabel, vm.WarningCount > 0 ? "warning" : null),
                    StatTile(vm.CriticalCount, vm.CriticalLabel, vm.CriticalCount > 0 ? "attention" : null),
                    StatTile(vm.OfflineCount, vm.OfflineLabel, vm.OfflineCount > 0 ? "attention" : null)
                }
            }
        }
    };

    private static JsonObject StatTile(int count, string label, string? color) => new()
    {
        ["type"] = "Column",
        ["width"] = "stretch",
        ["spacing"] = "None",
        ["items"] = new JsonArray
        {
            Text(count.ToString(CultureInfo.InvariantCulture), size: "Medium", weight: "Bolder", color: color,
                subtle: color is null, spacingNone: true),
            Text(label.ToUpperInvariant(), size: "Small", subtle: true, spacingNone: true)
        }
    };

    // A server telemetry block: name + "● Health" chip, then CPU/MEM/DISK number+unit with segmented meters.
    private static JsonObject ServerBlock(WidgetServerRow row, WidgetViewModel vm, bool large) => new()
    {
        ["type"] = "Container",
        ["spacing"] = "Medium",
        ["separator"] = true,
        ["selectAction"] = Execute(ActivationVerbs.OpenServer, row.ServerId),
        ["items"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    Column("stretch", new JsonArray
                    {
                        Text(row.DisplayName, size: large ? "Medium" : "Default", weight: "Bolder", wrap: false)
                    }),
                    Column("auto", new JsonArray
                    {
                        Text($"{Dot} {row.HealthLabel}", size: "Small", weight: "Bolder", color: row.HealthColor)
                    }, verticalAlignment: "Center")
                }
            },
            new JsonObject
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "Small",
                ["columns"] = new JsonArray
                {
                    MetricColumn(vm.CpuLabel, row.CpuText, row.CpuFraction, detail: large ? row.CpuDetail : string.Empty, large: large),
                    MetricColumn(vm.MemoryLabel, row.MemoryText, row.MemoryFraction, detail: large ? row.MemoryDetail : string.Empty, large: large),
                    MetricColumn(vm.DiskLabel, row.DiskText, row.DiskFraction, detail: large ? row.DiskDetail : string.Empty, large: large)
                }
            }
        }
    };

    // One metric cell: big number + small trailing unit, a segmented meter, a small caps label, and — on
    // Large — an absolute "used / total GB" detail line under the label (empty/omitted otherwise).
    private static JsonObject MetricColumn(string label, string valueText, double fraction, string detail, bool large)
    {
        var (number, unit) = SplitPercent(valueText);
        var items = new JsonArray
        {
            NumberUnit(number, unit, "Large", "Small", color: null, unitSubtle: true),
            Meter(fraction, large ? "Medium" : MeterSize),
            Text(label.ToUpperInvariant(), size: "Small", subtle: true, spacingNone: true)
        };

        if (detail.Length > 0)
        {
            items.Add(Text(detail, size: "Small", subtle: true, spacingNone: true));
        }

        return new JsonObject
        {
            ["type"] = "Column",
            ["width"] = "stretch",
            ["spacing"] = "None",
            ["items"] = items
        };
    }

    // Big number (auto) + small bottom-aligned unit (auto) on one baseline — the refs' "95 bpm" / "62 %".
    private static JsonObject NumberUnit(string number, string unit, string numberSize, string unitSize,
        string? color, bool unitSubtle)
    {
        var columns = new JsonArray
        {
            Column("auto", new JsonArray
            {
                Text(number, size: numberSize, weight: "Bolder", color: color, spacingNone: true)
            }, spacingNone: true)
        };

        if (unit.Length > 0)
        {
            columns.Add(Column("auto", new JsonArray
            {
                Text(unit, size: unitSize, weight: unitSubtle ? null : "Bolder", color: color, subtle: unitSubtle,
                    spacingNone: true)
            }, verticalAlignment: "Bottom", spacingNone: true));
        }

        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "None",
            ["columns"] = columns
        };
    }

    // The instrument. Magnitude is the FILLED-TICK COUNT; the fill stays magnitude-neutral (health lives
    // only on the chip). Fill rule: ceil(pct/step), min 1 lit tick when pct > 0. Unknown (fraction < 0) =
    // all-empty track (§19). See the TickFilled/TickEmpty note for why these are glyphs and not styled
    // container backgrounds (M13-QA-6).
    private static JsonObject Meter(double fraction, string size)
    {
        var filled = FilledSegments(fraction, MeterSegments);
        return GlyphBar(
            new[]
            {
                (Repeat(TickFilled, filled), "accent", false),
                (Repeat(TickEmpty, MeterSegments - filled), (string?)null, true)
            },
            size);
    }

    // Fleet bar: one tick per server, worst-first, in health FOREGROUND colours. The fleet bar IS
    // health-coloured by design, unlike the metric meters - but it had the same light-theme failure for the
    // same reason, so it moves to glyphs too. Unknown has no health colour and uses the subtle foreground.
    private static JsonObject FleetBar(WidgetViewModel vm, string size) => GlyphBar(
        new[]
        {
            (Repeat(TickFilled, vm.CriticalCount + vm.OfflineCount), "attention", false),
            (Repeat(TickFilled, vm.WarningCount), "warning", false),
            (Repeat(TickFilled, vm.UnknownCount), (string?)null, true),
            (Repeat(TickFilled, vm.HealthyCount), "good", false)
        },
        size);

    // A row of tick glyphs built from coloured runs. Each run is its own TextBlock in an auto column with
    // no spacing, so the runs read as one continuous bar while each keeps its own foreground colour - which
    // is the whole point: foreground colours are the part of the palette the host resolves correctly in
    // both themes.
    private static JsonObject GlyphBar(IEnumerable<(string Text, string? Color, bool Subtle)> runs, string size)
    {
        var columns = new JsonArray();
        foreach (var (text, color, subtle) in runs)
        {
            if (text.Length == 0)
            {
                continue;
            }

            columns.Add(Column("auto", new JsonArray
            {
                Text(text, size: size, weight: "Bolder", color: color, subtle: subtle, spacingNone: true)
            }, spacingNone: true));
        }

        // Trailing stretch keeps the bar left-aligned in its cell.
        columns.Add(Column("stretch", new JsonArray(), spacingNone: true));

        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = columns
        };
    }

    private static string Repeat(string glyph, int count) =>
        count <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(glyph, count));

    private static int FilledSegments(double fraction, int segments)
    {
        if (fraction < 0)
        {
            return 0;
        }

        var filled = (int)Math.Ceiling(fraction * segments);
        if (filled == 0 && fraction > 0)
        {
            filled = 1;
        }

        return Math.Clamp(filled, 0, segments);
    }


    // "39%" -> ("39","%"); "—" (or any non-percent) -> (text,"") so unknown shows no unit (§19).
    private static (string Number, string Unit) SplitPercent(string text) =>
        text.EndsWith('%') ? (text[..^1], "%") : (text, string.Empty);

    // "2/2" -> ("2","/2").
    private static (string Number, string Unit) SplitFraction(string text)
    {
        var slash = text.IndexOf('/');
        return slash < 0 ? (text, string.Empty) : (text[..slash], text[slash..]);
    }

    private static JsonArray EmptyBody(WidgetViewModel vm) => new()
    {
        Text(vm.FleetKicker, size: "Small", weight: "Bolder", color: "accent"),
        Text(vm.NoServersText, weight: "Bolder", wrap: true, spacingNone: true),
        Freshness(vm)
    };

    private static JsonArray UnavailableBody(WidgetViewModel vm) => new()
    {
        Text(vm.BrandName, weight: "Bolder", color: "accent"),
        Text(vm.NoDataTitle, weight: "Bolder", wrap: true, spacingNone: true),
        Text(vm.NoDataBody, subtle: true, wrap: true, spacingNone: true)
    };

    private static JsonObject Freshness(WidgetViewModel vm) =>
        Text(vm.FreshnessText, subtle: true, size: "Small", spacingNone: true);

    private static JsonObject FreshnessRight(WidgetViewModel vm)
    {
        var node = Freshness(vm);
        node["horizontalAlignment"] = "Right";
        return node;
    }

    // ---- Adaptive Card Action.Execute (verb + optional opaque serverId in data). ----
    private static JsonObject Execute(string verb, Guid? serverId = null)
    {
        var action = new JsonObject
        {
            ["type"] = "Action.Execute",
            ["verb"] = verb
        };

        if (serverId is { } id)
        {
            action["data"] = new JsonObject
            {
                [ActivationVerbs.ServerIdDataKey] = id.ToString("D", CultureInfo.InvariantCulture)
            };
        }

        return action;
    }

    private static JsonObject Column(string width, JsonArray items, string? verticalAlignment = null,
        bool spacingNone = false, string? spacing = null)
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

        if (spacingNone)
        {
            column["spacing"] = "None";
        }
        else if (spacing is not null)
        {
            column["spacing"] = spacing;
        }

        return column;
    }

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
