using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// A single managed service prepared for the compact, virtualized list (§46/§48). Immutable projection of
/// a <see cref="ServiceInfo"/>: Name is primary, the description is shown only when the manager provides a
/// useful one (systemd), and the startup configuration is shown only when known (§48/§60/§61) — never
/// invented. Status is carried as both colour (<see cref="Severity"/>) and text (<see cref="StateText"/>)
/// so it is never conveyed by colour alone (§53).
/// </summary>
public sealed class ServiceRowViewModel
{
    public ServiceRowViewModel(ServiceInfo service, ILocalizationService localization)
    {
        Name = service.Name;
        State = service.State;
        Severity = WorkloadPresentation.SeverityFor(service.State);
        StateText = localization.GetString($"WorkloadServiceState{service.State}");

        // The description only adds signal when it differs from the name the operator already reads.
        Description = !string.IsNullOrWhiteSpace(service.DisplayName)
            && !string.Equals(service.DisplayName, service.Name, StringComparison.Ordinal)
                ? service.DisplayName
                : null;
        HasDescription = Description is not null;

        StartupText = service.StartupState is { } startup && startup != ServiceStartupState.Unknown
            ? localization.GetString($"WorkloadServiceStartup{startup}")
            : null;
        HasStartup = StartupText is not null;

        AutomationName = string.Format(
            CultureInfo.CurrentUICulture,
            localization.GetString("WorkloadServiceAccessibleFormat"),
            Name,
            StateText);

        // UI.3 columns (D-UI3-6): "Ativo"/"Inativo" for services; other states keep their text.
        StateDisplay = localization.GetString(WorkloadPresentation.ServiceStateDisplayKey(service.State));
        var startupKey = WorkloadPresentation.ServiceStartupDisplayKey(service.StartupState);
        StartupDisplay = localization.GetString(startupKey ?? "WorkloadValueUnknown");
        IsProblem = Severity == WorkloadSeverity.Negative;
        DisplayAutomationName = string.Format(
            CultureInfo.CurrentUICulture,
            localization.GetString("WorkloadServiceDisplayAccessibleFormat"),
            Name,
            StateDisplay,
            startupKey is null ? localization.GetString("WorkloadValueUnknownAccessible") : StartupDisplay);
    }

    /// <summary>UI.3 state column: Running → "Ativo", Stopped → "Inativo", others as <see cref="StateText"/>.</summary>
    public string StateDisplay { get; } = string.Empty;

    /// <summary>UI.3 startup column: "Automático"/"Estático"/"Manual"/"Bloqueado", or "—" when unknown.</summary>
    public string StartupDisplay { get; } = string.Empty;

    /// <summary>Counts toward "Com problemas" (D-UI3-3): Negative severity only.</summary>
    public bool IsProblem { get; }

    /// <summary>Spoken summary matching the UI.3 columns (name, state, startup).</summary>
    public string DisplayAutomationName { get; } = string.Empty;

    public string Name { get; }

    public ServiceState State { get; }

    public WorkloadSeverity Severity { get; }

    public string StateText { get; }

    public string? Description { get; }

    public bool HasDescription { get; }

    public string? StartupText { get; }

    public bool HasStartup { get; }

    /// <summary>Full spoken summary, e.g. "nginx.service, falhou" (§53).</summary>
    public string AutomationName { get; }
}
