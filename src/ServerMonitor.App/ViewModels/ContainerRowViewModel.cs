using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// A single Docker container prepared for the compact, virtualized list (§46/§47). Everything shown is
/// precomputed and immutable: the row is a lightweight projection of a <see cref="ContainerInfo"/>, never
/// a live-updating card. Name is primary; the full container id is never surfaced as the primary text
/// (§47). CPU/RAM are null in M11 and are simply not projected here — hidden gracefully, never shown as 0
/// (unknown ≠ zero). The status is carried both as a colour (<see cref="Severity"/>) and as text
/// (<see cref="StateText"/>/<see cref="HealthText"/>) so state is never conveyed by colour alone (§53).
/// </summary>
public sealed class ContainerRowViewModel
{
    public ContainerRowViewModel(ContainerInfo container, ILocalizationService localization)
    {
        Name = container.Name;
        Image = container.Image;
        State = container.State;
        Health = container.Health;
        Severity = WorkloadPresentation.SeverityFor(container);
        StateSeverity = WorkloadPresentation.StateSeverityFor(container.State);
        HealthSeverity = WorkloadPresentation.HealthSeverityFor(container.Health);
        StateText = localization.GetString($"WorkloadContainerState{container.State}");

        // "None" (no health check) and "Unknown" (undetermined) are not the same, but neither is worth a
        // chip: only real health verdicts (Healthy/Unhealthy/Starting) are shown.
        HasHealth = container.Health is ContainerHealth.Healthy
            or ContainerHealth.Unhealthy
            or ContainerHealth.Starting;
        HealthText = HasHealth ? localization.GetString($"WorkloadContainerHealth{container.Health}") : null;

        AutomationName = HasHealth
            ? string.Format(
                CultureInfo.CurrentUICulture,
                localization.GetString("WorkloadContainerAccessibleWithHealthFormat"),
                Name,
                StateText,
                HealthText)
            : string.Format(
                CultureInfo.CurrentUICulture,
                localization.GetString("WorkloadContainerAccessibleFormat"),
                Name,
                StateText);
    }

    public string Name { get; }

    public string Image { get; }

    public ContainerState State { get; }

    public ContainerHealth Health { get; }

    public WorkloadSeverity Severity { get; }

    /// <summary>Severity of the lifecycle text alone (colours "Em execução"/"Parado"/"Falhou"). M-01.</summary>
    public WorkloadSeverity StateSeverity { get; }

    /// <summary>Severity of the health text alone (colours "Não saudável"/"A iniciar"/"Saudável"). M-01.</summary>
    public WorkloadSeverity HealthSeverity { get; }

    public string StateText { get; }

    public bool HasHealth { get; }

    public string? HealthText { get; }

    /// <summary>Full spoken summary, e.g. "postgres, container em execução, saudável" (§53).</summary>
    public string AutomationName { get; }
}
