using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>Where the notification registration outcome is written so a human can read it later.</summary>
public sealed record NotificationDiagnosticsOptions
{
    public required string FilePath { get; init; }

    public static NotificationDiagnosticsOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new NotificationDiagnosticsOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "notification-registration.log")
        };
    }
}

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
/// been taken. One file, rewritten at each start and appended to only by the few events of this one
/// question, so it cannot grow without bound.
/// </para>
/// </summary>
public interface INotificationRegistrationEvidence
{
    /// <summary>Starts the record for this process, replacing the previous one.</summary>
    void Record(string report);

    /// <summary>Adds a later fact to the record for this process.</summary>
    void Append(string line);
}

/// <inheritdoc />
public sealed class FileNotificationRegistrationEvidence(
    NotificationDiagnosticsOptions options,
    ILogger<FileNotificationRegistrationEvidence> logger) : INotificationRegistrationEvidence
{
    private readonly object _sync = new();

    public void Record(string report) => Write(report, append: false);

    public void Append(string line) => Write(line + Environment.NewLine, append: true);

    /// <summary>Never throws: evidence must not become a second failure.</summary>
    private void Write(string text, bool append)
    {
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(options.FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (append)
                {
                    File.AppendAllText(options.FilePath, text);
                }
                else
                {
                    File.WriteAllText(options.FilePath, text);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "The notification registration evidence could not be written ({Type}).",
                    exception.GetType().Name);
            }
        }
    }
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
