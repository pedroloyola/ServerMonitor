using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Qa;

/// <summary>Debug-only strict modifiers, resolved entirely against the synthetic overview catalog.</summary>
internal static class QaShellStartup
{
    public const string StartFlag = "--qa-start";
    public const string ActivationFlag = "--qa-activation";

    internal static string? Value(IReadOnlyList<string> args, string flag)
    {
        var matches = args.Where(a => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !matches[0].StartsWith(flag + "=", StringComparison.Ordinal)) return null;
        return matches[0][(flag.Length + 1)..];
    }

    internal static bool Present(IReadOnlyList<string> args, string flag) =>
        args.Any(a => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal));

    internal static int? Index(string value, string prefix, int count)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var digits = value[prefix.Length..];
        return digits.Length > 0 && digits.All(c => c is >= '0' and <= '9')
            && int.TryParse(digits, out var index) && index >= 1 && index <= count ? index : null;
    }

    internal static string? Refusal(IReadOnlyList<string> args)
    {
        var start = Present(args, StartFlag);
        var activation = Present(args, ActivationFlag);
        if (!start && !activation) return null;
        if (!args.Contains(QaOverviewComposition.LaunchFlag, StringComparer.Ordinal) || (start && activation))
            return "Shell modifiers require --qa-overview and are mutually exclusive.";
        var scenario = QaOverviewScenarioPolicy.ResolveScenario(args, true)
            ?? (QaOverviewScenarioPolicy.IsPresent(args) ? null : QaOverviewScenarioPolicy.DefaultScenario);
        if (scenario is null) return "Unknown overview scenario.";
        var count = QaOverviewCatalog.Build(scenario).Servers.Count(s => !s.Server.IsHidden);
        var value = Value(args, start ? StartFlag : ActivationFlag);
        var valid = start
            ? value is "overview" or "servers" or "history" or "settings" or "settings-data"
                || (value is not null && Index(value, "detail:", count) is not null)
            : value == "dashboard" || (value is not null && Index(value, "server:", count) is not null);
        return valid ? null : "Malformed shell modifier or server index outside the visible synthetic catalog.";
    }

    internal static ActivationIntent? Activation(IReadOnlyList<string> args)
    {
        if (!Present(args, ActivationFlag)) return null;
        if (Refusal(args) is { } refusal) throw new ArgumentException(refusal);
        var value = Value(args, ActivationFlag)!;
        if (value == "dashboard") return ActivationIntent.Dashboard;
        var servers = QaOverviewCatalog.Build(QaOverviewComposition.RequestedScenario(args)).Servers.Where(s => !s.Server.IsHidden).ToArray();
        return ActivationIntent.Server(servers[Index(value, "server:", servers.Length)!.Value - 1].Server.Id);
    }

    internal static async Task ApplyStartAsync(IReadOnlyList<string> args, INavigationService navigation, DashboardViewModel dashboard)
    {
        if (!Present(args, StartFlag)) return;
        if (Refusal(args) is { } refusal) throw new ArgumentException(refusal);
        var value = Value(args, StartFlag)!;
        if (value is "history" || value.StartsWith("detail:", StringComparison.Ordinal)) await dashboard.LoadAsync();
        switch (value)
        {
            case "overview": navigation.GoToDashboard(); break;
            case "servers": navigation.GoToServers(); break;
            case "history": navigation.GoToHistory(); break;
            case "settings": navigation.GoToSettings(); break;
            case "settings-data": navigation.GoToSettings(SettingsSection.Data); break;
            default:
                var card = dashboard.VisibleServers[Index(value, "detail:", dashboard.VisibleServers.Count)!.Value - 1];
                navigation.GoToServerDetail(card.Server.Id, ServerDetailOrigin.Servers);
                break;
        }
    }
}
