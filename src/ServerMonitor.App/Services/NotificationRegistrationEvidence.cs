namespace ServerMonitor.App.Services;

/// <summary>
/// Recoverable evidence for one thing only: what the notification registration actually did, and what the
/// single first-close notice then did with it (M13-QA-12).
/// <para>
/// <b>Why this exists at all.</b> The registration failure was invisible for the whole of M13. The host
/// logs through <c>AddDebug()</c> and nothing else, and Debug output goes nowhere in a packaged run — so
/// the exception that explains the missing notice was written to a stream no one can read, on the exact
/// build where it matters. A failure whose evidence cannot be retrieved is, in practice, a failure that
/// did not happen. This records facts only: it names no cause and draws no conclusion.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> This is not a logging framework and must not grow into one: a general
/// recoverable log sink is a product decision — location, retention, what may be written — that has not
/// been taken. One file, replaced by EVERY terminal registration outcome and appended to only by the few
/// events of this one question, so it cannot grow without bound and cannot describe a previous run.
/// </para>
/// </summary>
public interface INotificationRegistrationEvidence
{
    /// <summary>
    /// Starts the record for this process, REPLACING whatever was there. Every terminal registration
    /// outcome calls it, including the ones that never reach the platform: a run that wrote nothing would
    /// leave the previous run's file in place, and it would be read as current.
    /// </summary>
    void Record(string report);

    /// <summary>Adds a later fact to the record for this process.</summary>
    void Append(string line);
}

/// <summary>Used where the outcome is not being collected — tests that are about something else.</summary>
public sealed class NullNotificationRegistrationEvidence : INotificationRegistrationEvidence
{
    public static NullNotificationRegistrationEvidence Instance { get; } = new();

    public void Record(string report)
    {
    }

    public void Append(string line)
    {
    }
}
