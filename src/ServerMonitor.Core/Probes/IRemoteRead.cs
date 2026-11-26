using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Probes;

/// <summary>What the one argument of an <see cref="IRemoteRead"/> is, and so which rule it must meet.</summary>
public enum RemoteReadArgumentKind
{
    /// <summary>A systemd unit id from the server's latest service list ("ssh.service"). Linux only.</summary>
    SystemdUnit,

    /// <summary>A full container id (64 lower-case hex) from the server's latest container list.</summary>
    ContainerId,
}

/// <summary>
/// One registered read: a constant script that reads exactly one argument as <c>"$1"</c>, run once on request through
/// <see cref="IRemoteReadRunner"/>. Never on a timer, never in the background.
/// <para>
/// Like an <see cref="IRemoteProbe"/>, a read is compiled and registered in the composition, and every property is read
/// ONCE, at start, and kept. A read whose values break the rules is left out. The script is never edited: at run time
/// the runner sends one fixed first line, <c>set -- '&lt;argument&gt;'</c>, then the script byte for byte, on the standard
/// input of the constant <c>sh -s</c>. The argument is checked against <see cref="ArgumentKind"/> and against the
/// server's own latest list before anything runs, so it can never hold a quote, a space or a shell character.
/// </para>
/// </summary>
public interface IRemoteRead
{
    /// <summary>Stable id, with the same rules as <see cref="IRemoteProbe.Id"/>. Unique across probes and reads.</summary>
    string Id { get; }

    /// <summary>The one operating system it runs on: <see cref="ServerOperatingSystem.Linux"/> or <see cref="ServerOperatingSystem.MacOS"/>.</summary>
    ServerOperatingSystem OperatingSystem { get; }

    /// <summary>
    /// The constant POSIX sh script, with the same rules as <see cref="IRemoteProbe.Command"/> (commands that can read
    /// standard input must redirect it with <c>&lt;/dev/null</c>). It uses <c>"$1"</c> at least once, and never
    /// <c>$@</c>, <c>$*</c>, <c>$2</c> to <c>$9</c>, <c>shift</c>, <c>set</c> or <c>eval</c>.
    /// </summary>
    string Command { get; }

    /// <summary>What the argument is. <see cref="RemoteReadArgumentKind.SystemdUnit"/> needs a Linux read.</summary>
    RemoteReadArgumentKind ArgumentKind { get; }

    /// <summary>The most standard output kept, in bytes, at most <see cref="RemoteProbeLimits.MaxOutputLimitBytes"/>.</summary>
    int OutputLimitBytes { get; }

    /// <summary>How long the script may run, at most <see cref="RemoteProbeLimits.MaxTimeout"/>.</summary>
    TimeSpan Timeout { get; }
}

/// <summary>Runs a registered <see cref="IRemoteRead"/> once, on request.</summary>
public interface IRemoteReadRunner
{
    /// <summary>
    /// Runs the read <paramref name="readId"/> on server <paramref name="serverId"/> with <paramref name="argument"/>
    /// as <c>$1</c>. Every check is made before any SSH work, and a failed check is a
    /// <see cref="RemoteReadOutcomeKind.Refused"/> outcome. At most one read runs per server at a time. Throws
    /// <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<RemoteReadOutcome> RunAsync(string readId, Guid serverId, string argument, CancellationToken cancellationToken);
}
