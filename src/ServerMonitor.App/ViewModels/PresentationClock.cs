namespace ServerMonitor.App.ViewModels;

/// <summary>
/// The clock the overview reads for "Atualizado há …" (UI.4 §2, same rule as D-UI3-9: recomputed only on a new
/// snapshot / Atualizar / load, never on a timer). A dedicated type rather than a container-wide
/// <see cref="TimeProvider"/>, so a Debug QA harness can fix the overview's clock without freezing the tray, the alert
/// coordinator or any other service that takes an optional TimeProvider. Not registered in production (system clock).
/// </summary>
public sealed class PresentationClock(TimeProvider timeProvider)
{
    public static PresentationClock System { get; } = new(TimeProvider.System);

    public TimeProvider TimeProvider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public DateTimeOffset UtcNow => TimeProvider.GetUtcNow();
}
