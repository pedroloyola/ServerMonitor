using System.Globalization;

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
    /// <summary>The culture these strings belong to; numbers on the card ("0,6/11,6 GB") use it (RC-10).</summary>
    public required CultureInfo Culture { get; init; }

    public required string NeutralServerName { get; init; } // §15: shown when a name sanitizes to empty
    public required string Cpu { get; init; }
    public required string Memory { get; init; }
    public required string Disk { get; init; }
    public required string MetricUnknown { get; init; } // shown for a null metric — never "0%"

    // Count labels have singular (…One) and plural (…Other) forms for correct agreement.
    // Prism C1 N-1: every space INSIDE a count ("{0}\u00A0sem\u00A0ligação") is a no-break space, so a
    // wrapping summary only breaks at the " · " separator, never inside one category.

    /// <summary>Picks the singular or plural form for a count.</summary>
    public static string Plural(int count, string one, string many) =>
        count == 1
            ? string.Format(CultureInfo.InvariantCulture, one, count)
            : string.Format(CultureInfo.InvariantCulture, many, count);

    // Freshness.
    public required string UpdatedJustNow { get; init; }
    public required string UpdatedMinutesAgo { get; init; }  // "Updated {0} min ago"
    public required string UpdatedHoursAgo { get; init; }    // "Updated {0} hr ago"

    // ---- UI.9 V3 copy (Prism SPEC review §B, BINDING; pt-PT "tu", pt-BR "você", en neutral) ----------
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

    /// <summary>
    /// Resolves the strings for the user's language, defaulting to English. F-1 (REL.0D, Cortex A-lite): the PROFILE
    /// language - the first Windows preferred language in English or Portuguese, the source the app's text, decisions and
    /// number format follow - not the display language, which can differ (display pt-BR with [en-US, …] showed an English
    /// app next to a Portuguese card). Read per render, like the culture was.
    /// </summary>
    public static WidgetStrings Current() => ForLanguages(PreferredLanguages(), CultureInfo.CurrentUICulture.Name);

    /// <summary>The strings for a machine: its preferred-language list and display language (testable without Windows).</summary>
    public static WidgetStrings ForLanguages(IReadOnlyList<string>? preferredLanguages, string? displayCulture) =>
        ForCultureName(ProfileLanguage(preferredLanguages, displayCulture));

    /// <summary>
    /// The app's <c>SupportedCultures.ProfileLanguage</c> (a parity test keeps them equal): the first preferred language
    /// whose base is en / pt (full tag), else the first entry, else <paramref name="displayCulture"/>.
    /// </summary>
    public static string ProfileLanguage(IReadOnlyList<string>? profileLanguages, string? displayCulture)
    {
        if (profileLanguages is null || profileLanguages.Count == 0)
        {
            return displayCulture ?? string.Empty;
        }

        foreach (var language in profileLanguages)
        {
            var subtags = (language ?? string.Empty).Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
            if (subtags.Length > 0
                && (subtags[0].Equals("en", StringComparison.OrdinalIgnoreCase) || subtags[0].Equals("pt", StringComparison.OrdinalIgnoreCase)))
            {
                return language!;
            }
        }

        return profileLanguages[0] ?? string.Empty;
    }

    /// <summary>
    /// The app's table (REL.0C, <c>SupportedCultures.Resolve</c>; a parity test keeps them equal): pt-BR → PtBr,
    /// any other Portuguese → PtPt, everything else → En. It follows the Windows language, not the app's
    /// in-app language choice (the provider is a separate process).
    /// </summary>
    public static WidgetStrings ForCulture(CultureInfo? culture) => ForCultureName(culture?.Name);

    /// <summary><see cref="ForCulture"/> by name (a profile tag needs no <see cref="CultureInfo"/> to be mapped).</summary>
    public static WidgetStrings ForCultureName(string? cultureName)
    {
        var subtags = (cultureName ?? string.Empty).Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (subtags.Length == 0 || !subtags[0].Equals("pt", StringComparison.OrdinalIgnoreCase))
        {
            return En;
        }

        return subtags.Skip(1).Any(subtag => subtag.Equals("BR", StringComparison.OrdinalIgnoreCase)) ? PtBr : PtPt;
    }

    /// <summary>The user's ordered preferred languages; null when Windows cannot say (the display language then).</summary>
    private static IReadOnlyList<string>? PreferredLanguages()
    {
        try
        {
            return [.. Windows.System.UserProfile.GlobalizationPreferences.Languages];
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
                                              or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly WidgetStrings En = new()
    {
        Culture = CultureInfo.GetCultureInfo("en-US"),
        NeutralServerName = "Server",
        Cpu = "CPU",
        Memory = "RAM",
        Disk = "Disk",
        MetricUnknown = "—",
        UpdatedJustNow = "Updated just now",
        UpdatedMinutesAgo = "Updated {0} min ago",
        UpdatedHoursAgo = "Updated {0} hr ago",
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
        FleetUnknownOnly = "{0}\u00A0without\u00A0recent\u00A0data",
        CountHealthyOne = "{0}\u00A0healthy",
        CountHealthyOther = "{0}\u00A0healthy",
        CountWarning = "{0}\u00A0attention",
        CountCriticalOne = "{0}\u00A0critical",
        CountCriticalOther = "{0}\u00A0critical",
        CountOffline = "{0}\u00A0no\u00A0connection",
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
        Culture = CultureInfo.GetCultureInfo("pt-BR"),
        NeutralServerName = "Servidor",
        Cpu = "CPU",
        Memory = "RAM",
        Disk = "Disco",
        MetricUnknown = "—",
        UpdatedJustNow = "Atualizado agora",
        UpdatedMinutesAgo = "Atualizado há {0} min",
        UpdatedHoursAgo = "Atualizado há {0} h",
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
        FleetUnknownOnly = "{0}\u00A0sem\u00A0dados\u00A0recentes",
        CountHealthyOne = "{0}\u00A0saudável",
        CountHealthyOther = "{0}\u00A0saudáveis",
        CountWarning = "{0}\u00A0atenção",
        CountCriticalOne = "{0}\u00A0crítico",
        CountCriticalOther = "{0}\u00A0críticos",
        CountOffline = "{0}\u00A0sem\u00A0conexão",
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
        Culture = CultureInfo.GetCultureInfo("pt-PT"),
        NeutralServerName = "Servidor",
        Cpu = "CPU",
        Memory = "RAM",
        Disk = "Disco",
        MetricUnknown = "—",
        UpdatedJustNow = "Atualizado agora",
        UpdatedMinutesAgo = "Atualizado há {0} min",
        UpdatedHoursAgo = "Atualizado há {0} h",
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
        FleetUnknownOnly = "{0}\u00A0sem\u00A0dados\u00A0recentes",
        CountHealthyOne = "{0}\u00A0saudável",
        CountHealthyOther = "{0}\u00A0saudáveis",
        CountWarning = "{0}\u00A0atenção",
        CountCriticalOne = "{0}\u00A0crítico",
        CountCriticalOther = "{0}\u00A0críticos",
        CountOffline = "{0}\u00A0sem\u00A0ligação",
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
