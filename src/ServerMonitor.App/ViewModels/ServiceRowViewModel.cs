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
    }

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
