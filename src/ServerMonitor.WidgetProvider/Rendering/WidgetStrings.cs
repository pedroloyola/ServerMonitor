using System.Globalization;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>
/// Localized strings for the widget, kept deliberately light (no resx / no App localization stack —
/// ADR-018 §17). The provider resolves the culture from <see cref="CultureInfo.CurrentUICulture"/> at
/// render time (it runs in the user's context) and maps it to one of the three supported cultures,
/// defaulting to English. All widget text flows through here so the rendering stays localizable and
/// deterministically testable.
/// </summary>
public sealed class WidgetStrings
{
    public required string BrandName { get; init; }
    public required string NeutralServerName { get; init; } // §15: shown when a name sanitizes to empty
    public required string Cpu { get; init; }
    public required string Memory { get; init; }
    public required string Disk { get; init; }
    public required string MetricUnknown { get; init; } // shown for a null metric — never "0%"

    // Health labels (text, never colour-only — §18).
    public required string Healthy { get; init; }
    public required string HealthyPlural { get; init; } // fleet hero label, e.g. "Saudáveis" / "Healthy"
    public required string FleetKicker { get; init; }    // small caps accent kicker above the hero
    public required string Warning { get; init; }
    public required string Critical { get; init; }
    public required string Offline { get; init; }
    public required string Unknown { get; init; }

    // Overall / summary copy. Count labels have singular (…One) and plural forms for correct agreement.
    public required string AllHealthy { get; init; }         // every server healthy
    public required string NeedAttentionLabel { get; init; }  // plural: "{0} need attention"
    public required string NeedAttentionLabelOne { get; init; } // singular: "1 needs attention"
    public required string HealthyCountLabel { get; init; }   // plural: "{0} healthy"
    public required string HealthyCountLabelOne { get; init; }
    public required string UnknownCountLabel { get; init; }   // plural: "{0} unknown"
    public required string UnknownCountLabelOne { get; init; }
    public required string MoreCount { get; init; }           // "+{0} more"

    /// <summary>Picks the singular or plural form for a count.</summary>
    public static string Plural(int count, string one, string many) =>
        count == 1
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, one, count)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, many, count);

    // Freshness.
    public required string UpdatedJustNow { get; init; }
    public required string UpdatedMinutesAgo { get; init; }  // "Updated {0} min ago"
    public required string UpdatedHoursAgo { get; init; }    // "Updated {0} hr ago"

    // Empty / unavailable states.
    public required string NoServers { get; init; }
    public required string NoDataTitle { get; init; }
    public required string NoDataBody { get; init; }

    /// <summary>Resolves the strings for the current UI culture, defaulting to English.</summary>
    public static WidgetStrings Current() => ForCulture(CultureInfo.CurrentUICulture);

    public static WidgetStrings ForCulture(CultureInfo? culture)
    {
        var name = culture?.Name ?? string.Empty;

        if (name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase))
        {
            return PtBr;
        }

        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            // pt-PT and any other Portuguese variant fall back to European Portuguese.
            return PtPt;
        }

        return En;
    }

    /// <summary>Maps a wire health to its localized label.</summary>
    public string HealthLabel(WidgetHealth health) => health switch
    {
        WidgetHealth.Healthy => Healthy,
        WidgetHealth.Warning => Warning,
        WidgetHealth.Critical => Critical,
        WidgetHealth.Offline => Offline,
        _ => Unknown
    };

    private static readonly WidgetStrings En = new()
    {
        BrandName = "ServerAlyzer",
        NeutralServerName = "Server",
        Cpu = "CPU",
        Memory = "Memory",
        Disk = "Disk",
        MetricUnknown = "—",
        Healthy = "Healthy",
        HealthyPlural = "Healthy",
        FleetKicker = "FLEET",
        Warning = "Warning",
        Critical = "Critical",
        Offline = "Offline",
        Unknown = "Unknown",
        AllHealthy = "All servers healthy",
        NeedAttentionLabel = "{0} need attention",
        NeedAttentionLabelOne = "{0} needs attention",
        HealthyCountLabel = "{0} healthy",
        HealthyCountLabelOne = "{0} healthy",
        UnknownCountLabel = "{0} unknown",
        UnknownCountLabelOne = "{0} unknown",
        MoreCount = "+{0} more",
        UpdatedJustNow = "Updated just now",
        UpdatedMinutesAgo = "Updated {0} min ago",
        UpdatedHoursAgo = "Updated {0} hr ago",
        NoServers = "No servers monitored",
        NoDataTitle = "No monitoring data yet",
        NoDataBody = "Open ServerAlyzer to start monitoring."
    };

    private static readonly WidgetStrings PtBr = new()
    {
        BrandName = "ServerAlyzer",
        NeutralServerName = "Servidor",
        Cpu = "CPU",
        Memory = "Memória",
        Disk = "Disco",
        MetricUnknown = "—",
        Healthy = "Saudável",
        HealthyPlural = "Saudáveis",
        FleetKicker = "FROTA",
        Warning = "Alerta",
        Critical = "Crítico",
        Offline = "Offline",
        Unknown = "Desconhecido",
        AllHealthy = "Todos os servidores saudáveis",
        NeedAttentionLabel = "{0} precisam de atenção",
        NeedAttentionLabelOne = "{0} precisa de atenção",
        HealthyCountLabel = "{0} saudáveis",
        HealthyCountLabelOne = "{0} saudável",
        UnknownCountLabel = "{0} desconhecidos",
        UnknownCountLabelOne = "{0} desconhecido",
        MoreCount = "+{0}",
        UpdatedJustNow = "Atualizado agora",
        UpdatedMinutesAgo = "Atualizado há {0} min",
        UpdatedHoursAgo = "Atualizado há {0} h",
        NoServers = "Nenhum servidor monitorado",
        NoDataTitle = "Ainda sem dados de monitoramento",
        NoDataBody = "Abra o ServerAlyzer para iniciar o monitoramento."
    };

    private static readonly WidgetStrings PtPt = new()
    {
        BrandName = "ServerAlyzer",
        NeutralServerName = "Servidor",
        Cpu = "CPU",
        Memory = "Memória",
        Disk = "Disco",
        MetricUnknown = "—",
        Healthy = "Saudável",
        HealthyPlural = "Saudáveis",
        FleetKicker = "FROTA",
        Warning = "Alerta",
        Critical = "Crítico",
        Offline = "Offline",
        Unknown = "Desconhecido",
        AllHealthy = "Todos os servidores saudáveis",
        NeedAttentionLabel = "{0} precisam de atenção",
        NeedAttentionLabelOne = "{0} precisa de atenção",
        HealthyCountLabel = "{0} saudáveis",
        HealthyCountLabelOne = "{0} saudável",
        UnknownCountLabel = "{0} desconhecidos",
        UnknownCountLabelOne = "{0} desconhecido",
        MoreCount = "+{0}",
        UpdatedJustNow = "Atualizado agora",
        UpdatedMinutesAgo = "Atualizado há {0} min",
        UpdatedHoursAgo = "Atualizado há {0} h",
        NoServers = "Nenhum servidor monitorizado",
        NoDataTitle = "Ainda sem dados de monitorização",
        NoDataBody = "Abra o ServerAlyzer para iniciar a monitorização."
    };
}
