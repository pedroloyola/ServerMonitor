namespace ServerMonitor.App.Qa;

/// <summary>QA-ONLY: a clock frozen at one instant, so "Atualizado há …" renders identically on every run.</summary>
internal sealed class QaFixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
