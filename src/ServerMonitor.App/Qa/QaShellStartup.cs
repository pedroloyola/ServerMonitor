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
        // UI.7 B-23: the editor harness starts on the editor (Add, SSH import, or an Edit of a seeded server).
        if (args.Contains(QaEditorComposition.LaunchFlag, StringComparer.Ordinal))
        {
            var editorValue = Value(args, StartFlag);
            return !activation && start && editorValue is not null
                && (editorValue is "editor-add" or "editor-import"
                    || Index(editorValue, "editor-edit:", QaEditorComposition.SeededCount(args)) is not null)
                ? null
                : "With --qa-editor, --qa-start must be editor-add, editor-import or editor-edit:<n> (n within the seed); --qa-activation is not supported.";
        }

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

    // The editor opens over the Visão geral (its origin); an Edit opens from the seeded server's Detail page, the only Edit
    // surface. The commands are not awaited: their task lasts as long as the editor visit.
    private static async Task StartEditorAsync(string value, INavigationService navigation, DashboardViewModel dashboard)
    {
        if (App.ServicesHost.Services.GetService(typeof(QaEditorSeed)) is QaEditorSeed seed)
        {
            await seed.Completed;
        }

        await dashboard.LoadAsync();
        navigation.GoToDashboard();
        switch (value)
        {
            case "editor-add": dashboard.AddServerCommand.Execute(null); break;
            case "editor-import": dashboard.ImportFromSshCommand.Execute(null); break;
            default:
                var card = dashboard.VisibleServers[Index(value, "editor-edit:", dashboard.VisibleServers.Count)!.Value - 1];
                navigation.GoToServerDetail(card.Server.Id, ServerDetailOrigin.Servers);
                card.EditCommand.Execute(null);
                break;
        }
    }

    internal static async Task ApplyStartAsync(IReadOnlyList<string> args, INavigationService navigation, DashboardViewModel dashboard)
    {
        if (!Present(args, StartFlag)) return;
        if (Refusal(args) is { } refusal) throw new ArgumentException(refusal);
        var value = Value(args, StartFlag)!;
        if (value.StartsWith("editor-", StringComparison.Ordinal))
        {
            await StartEditorAsync(value, navigation, dashboard);
            return;
        }

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
