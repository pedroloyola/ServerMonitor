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
    public required string MoreCount { get; init; }           // "{0} more" / "mais {0}"

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

    // ---- UI.9 V3 copy (Prism SPEC review §B, BINDING; pt-PT "tu", pt-BR "você", en neutral) ----------
    // The legacy keys above stay only while the M13 renderer draws the card; C1 removes them with it.
    public required string ServersHeading { get; init; }
    public required string StatusHealthy { get; init; }
    public required string StatusWarning { get; init; }
    public required string StatusCritical { get; init; }
    public required string StatusOffline { get; init; }
    public required string StatusUnknown { get; init; }
    public required string StatusRowStale { get; init; }
    public required string ReasonCpuWarning { get; init; }
    public required string ReasonCpuCritical { get; init; }
    public required string ReasonMemoryWarning { get; init; }
    public required string ReasonMemoryCritical { get; init; }
    public required string ReasonDiskWarning { get; init; }
    public required string ReasonDiskCritical { get; init; }
    public required string FleetAllHealthy { get; init; }
    public required string FleetConnectedOne { get; init; }
    public required string FleetConnectedOther { get; init; }
    public required string FleetProblemsOne { get; init; }
    public required string FleetProblemsOther { get; init; }
    public required string FleetUnknownOnly { get; init; }
    public required string CountHealthyOne { get; init; }
    public required string CountHealthyOther { get; init; }
    public required string CountWarning { get; init; }
    public required string CountCriticalOne { get; init; }
    public required string CountCriticalOther { get; init; }
    public required string CountOffline { get; init; }
    public required string CountUnknown { get; init; }
    public required string CountSeparator { get; init; }
    public required string StaleTitle { get; init; }
    public required string StaleLastStateFormat { get; init; }
    public required string LastReadingMinutesAgo { get; init; }
    public required string LastReadingHoursAgo { get; init; }
    public required string OverflowFormat { get; init; }
    public required string UptimeDetailFormat { get; init; }
    public required string EmptyTitleSmall { get; init; }
    public required string EmptyTitle { get; init; }
    public required string EmptyBody { get; init; }
    public required string EmptyCta { get; init; }
    public required string UnavailableTitle { get; init; }
    public required string UnavailableBody { get; init; }
    public required string RingAltFormat { get; init; }

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
        Memory = "RAM",
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
        MoreCount = "{0} more",
        UpdatedJustNow = "Updated just now",
        UpdatedMinutesAgo = "Updated {0} min ago",
        UpdatedHoursAgo = "Updated {0} hr ago",
        NoServers = "No servers monitored",
        NoDataTitle = "No monitoring data yet",
        NoDataBody = "Open ServerAlyzer to start monitoring.",
        ServersHeading = "Servers",
        StatusHealthy = "Healthy",
        StatusWarning = "Attention",
        StatusCritical = "Critical",
        StatusOffline = "No connection",
        StatusUnknown = "No data",
        StatusRowStale = "Not updated",
        ReasonCpuWarning = "High CPU",
        ReasonCpuCritical = "Critical CPU",
        ReasonMemoryWarning = "High RAM",
        ReasonMemoryCritical = "Critical RAM",
        ReasonDiskWarning = "High disk",
        ReasonDiskCritical = "Critical disk",
        FleetAllHealthy = "All healthy",
        FleetConnectedOne = "{0} server connected",
        FleetConnectedOther = "{0} servers connected",
        FleetProblemsOne = "{0} issue",
        FleetProblemsOther = "{0} issues",
        FleetUnknownOnly = "{0} no data",
        CountHealthyOne = "{0} healthy",
        CountHealthyOther = "{0} healthy",
        CountWarning = "{0} attention",
        CountCriticalOne = "{0} critical",
        CountCriticalOther = "{0} critical",
        CountOffline = "{0} no connection",
        CountUnknown = "{0} no data",
        CountSeparator = " · ",
        StaleTitle = "No recent data",
        StaleLastStateFormat = "Last state: {0} of {1} healthy",
        LastReadingMinutesAgo = "Last reading {0} min ago",
        LastReadingHoursAgo = "Last reading {0} hr ago",
        OverflowFormat = "{0} of {1} servers",
        UptimeDetailFormat = "Up {0}",
        EmptyTitleSmall = "No servers yet",
        EmptyTitle = "Your first server",
        EmptyBody = "Add a server in ServerAlyzer to see CPU, RAM and disk here.",
        EmptyCta = "Open ServerAlyzer",
        UnavailableTitle = "No monitoring data",
        UnavailableBody = "Open ServerAlyzer to refresh.",
        RingAltFormat = "{0} of {1} healthy"
    };

    private static readonly WidgetStrings PtBr = new()
    {
        BrandName = "ServerAlyzer",
        NeutralServerName = "Servidor",
        Cpu = "CPU",
        Memory = "RAM",
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
        MoreCount = "mais {0}",
        UpdatedJustNow = "Atualizado agora",
        UpdatedMinutesAgo = "Atualizado há {0} min",
        UpdatedHoursAgo = "Atualizado há {0} h",
        NoServers = "Nenhum servidor monitorado",
        NoDataTitle = "Ainda sem dados de monitoramento",
        NoDataBody = "Abra o ServerAlyzer para iniciar o monitoramento.",
        ServersHeading = "Servidores",
        StatusHealthy = "Saudável",
        StatusWarning = "Atenção",
        StatusCritical = "Crítico",
        StatusOffline = "Sem conexão",
        StatusUnknown = "Sem dados",
        StatusRowStale = "Sem atualização",
        ReasonCpuWarning = "CPU alta",
        ReasonCpuCritical = "CPU crítica",
        ReasonMemoryWarning = "RAM alta",
        ReasonMemoryCritical = "RAM crítica",
        ReasonDiskWarning = "Disco alto",
        ReasonDiskCritical = "Disco crítico",
        FleetAllHealthy = "Tudo saudável",
        FleetConnectedOne = "{0} servidor conectado",
        FleetConnectedOther = "{0} servidores conectados",
        FleetProblemsOne = "{0} problema",
        FleetProblemsOther = "{0} problemas",
        FleetUnknownOnly = "{0} sem dados",
        CountHealthyOne = "{0} saudável",
        CountHealthyOther = "{0} saudáveis",
        CountWarning = "{0} atenção",
        CountCriticalOne = "{0} crítico",
        CountCriticalOther = "{0} críticos",
        CountOffline = "{0} sem conexão",
        CountUnknown = "{0} sem dados",
        CountSeparator = " · ",
        StaleTitle = "Sem dados recentes",
        StaleLastStateFormat = "Último estado: {0} de {1} saudáveis",
        LastReadingMinutesAgo = "Última leitura há {0} min",
        LastReadingHoursAgo = "Última leitura há {0} h",
        OverflowFormat = "{0} de {1} servidores",
        UptimeDetailFormat = "Ativo {0}",
        EmptyTitleSmall = "Ainda sem servidores",
        EmptyTitle = "Seu primeiro servidor",
        EmptyBody = "Adicione um servidor no ServerAlyzer para ver aqui CPU, RAM e disco.",
        EmptyCta = "Abrir o ServerAlyzer",
        UnavailableTitle = "Sem dados de monitoramento",
        UnavailableBody = "Abra o ServerAlyzer para atualizar.",
        RingAltFormat = "{0} de {1} saudáveis"
    };

    private static readonly WidgetStrings PtPt = new()
    {
        BrandName = "ServerAlyzer",
        NeutralServerName = "Servidor",
        Cpu = "CPU",
        Memory = "RAM",
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
        MoreCount = "mais {0}",
        UpdatedJustNow = "Atualizado agora",
        UpdatedMinutesAgo = "Atualizado há {0} min",
        UpdatedHoursAgo = "Atualizado há {0} h",
        NoServers = "Nenhum servidor monitorizado",
        NoDataTitle = "Ainda sem dados de monitorização",
        NoDataBody = "Abra o ServerAlyzer para iniciar a monitorização.",
        ServersHeading = "Servidores",
        StatusHealthy = "Saudável",
        StatusWarning = "Atenção",
        StatusCritical = "Crítico",
        StatusOffline = "Sem ligação",
        StatusUnknown = "Sem dados",
        StatusRowStale = "Sem atualização",
        ReasonCpuWarning = "CPU alta",
        ReasonCpuCritical = "CPU crítica",
        ReasonMemoryWarning = "RAM alta",
        ReasonMemoryCritical = "RAM crítica",
        ReasonDiskWarning = "Disco alto",
        ReasonDiskCritical = "Disco crítico",
        FleetAllHealthy = "Tudo saudável",
        FleetConnectedOne = "{0} servidor ligado",
        FleetConnectedOther = "{0} servidores ligados",
        FleetProblemsOne = "{0} problema",
        FleetProblemsOther = "{0} problemas",
        FleetUnknownOnly = "{0} sem dados",
        CountHealthyOne = "{0} saudável",
        CountHealthyOther = "{0} saudáveis",
        CountWarning = "{0} atenção",
        CountCriticalOne = "{0} crítico",
        CountCriticalOther = "{0} críticos",
        CountOffline = "{0} sem ligação",
        CountUnknown = "{0} sem dados",
        CountSeparator = " · ",
        StaleTitle = "Sem dados recentes",
        StaleLastStateFormat = "Último estado: {0} de {1} saudáveis",
        LastReadingMinutesAgo = "Última leitura há {0} min",
        LastReadingHoursAgo = "Última leitura há {0} h",
        OverflowFormat = "{0} de {1} servidores",
        UptimeDetailFormat = "Ativo {0}",
        EmptyTitleSmall = "Ainda sem servidores",
        EmptyTitle = "O teu primeiro servidor",
        EmptyBody = "Adiciona um servidor no ServerAlyzer para veres aqui a CPU, a RAM e o disco.",
        EmptyCta = "Abrir o ServerAlyzer",
        UnavailableTitle = "Sem dados de monitorização",
        UnavailableBody = "Abre o ServerAlyzer para atualizar.",
        RingAltFormat = "{0} de {1} saudáveis"
    };
}
