using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Probes;

/// <summary>
/// The registered <see cref="IRemoteRead"/>s, each read and validated ONCE when this is created. A read is checked like
/// an <see cref="IRemoteProbe"/> (id, one operating system, an LF-only script of at most
/// <see cref="RemoteProbeLimits.MaxCommandLength"/> characters with no other control or separator character, limits
/// within <see cref="RemoteProbeLimits"/>) and, because it takes one argument:
/// <list type="bullet">
/// <item>its script uses <c>"$1"</c> at least once;</item>
/// <item>it never uses another positional parameter (<c>$@</c>, <c>$*</c>, <c>$2</c> to <c>$9</c>, or their
/// <c>${...}</c> forms, <c>${10}</c> and up), nor the words <c>shift</c>, <c>set</c> or <c>eval</c>, which could change or re-read them;</item>
/// <item>a <see cref="RemoteReadArgumentKind.SystemdUnit"/> read runs on Linux;</item>
/// <item>its id is not used by another read or by a probe.</item>
/// </list>
/// A read that fails is left out, and <see cref="Rejected"/> says why by position only, never with the script.
/// </summary>
public sealed partial class RemoteReadRegistry
{
    private readonly Dictionary<string, RemoteReadRegistration> _registrations;

    public RemoteReadRegistry(IEnumerable<IRemoteRead> reads, IEnumerable<string>? probeIds = null)
    {
        ArgumentNullException.ThrowIfNull(reads);

        var reserved = new HashSet<string>(probeIds ?? [], StringComparer.Ordinal);
        var accepted = new Dictionary<string, RemoteReadRegistration>(StringComparer.Ordinal);
        var rejected = new List<string>();
        var position = 0;
        foreach (var read in reads)
        {
            if (TryRegister(read, out var registration, out var reason))
            {
                if (reserved.Contains(registration.Id))
                {
                    reason = "a probe already has its id";
                }
                else if (!accepted.TryAdd(registration.Id, registration))
                {
                    reason = "another read already has its id";
                }
            }

            if (reason is not null)
            {
                rejected.Add($"read #{position}: {reason}");
            }

            position++;
        }

        _registrations = accepted;
        Rejected = rejected;
    }

    /// <summary>How many reads were accepted.</summary>
    public int Count => _registrations.Count;

    /// <summary>Why each left-out read was left out, by position (never its script). For a log line.</summary>
    public IReadOnlyList<string> Rejected { get; }

    /// <summary>The accepted read with id <paramref name="readId"/>, compared ordinal.</summary>
    public bool TryGet(string? readId, [NotNullWhen(true)] out RemoteReadRegistration? registration)
    {
        registration = null;
        return readId is not null && _registrations.TryGetValue(readId, out registration);
    }

    private static bool TryRegister(
        IRemoteRead? read,
        [NotNullWhen(true)] out RemoteReadRegistration? registration,
        out string? reason)
    {
        registration = null;
        if (read is null)
        {
            reason = "it is null";
            return false;
        }

        string? id;
        ServerOperatingSystem operatingSystem;
        string? command;
        RemoteReadArgumentKind kind;
        int limit;
        TimeSpan timeout;
        try
        {
            // Read once and kept: the read cannot change what runs after this point.
            id = read.Id;
            operatingSystem = read.OperatingSystem;
            command = read.Command;
            kind = read.ArgumentKind;
            limit = read.OutputLimitBytes;
            timeout = read.Timeout;
        }
        catch (Exception exception)
        {
            reason = $"reading it threw {exception.GetType().Name}";
            return false;
        }

        reason = RemoteProbeScheduler.Validate(id, operatingSystem, command, limit, timeout, TimeSpan.Zero)
                 ?? ValidateArgumentUse(command!, kind, operatingSystem);
        if (reason is not null)
        {
            return false;
        }

        registration = new RemoteReadRegistration(id!, operatingSystem, command!, kind, limit, timeout);
        return true;
    }

    private static string? ValidateArgumentUse(string command, RemoteReadArgumentKind kind, ServerOperatingSystem operatingSystem)
    {
        if (!Enum.IsDefined(kind))
        {
            return "its argument kind is not known";
        }

        if (kind == RemoteReadArgumentKind.SystemdUnit && operatingSystem != ServerOperatingSystem.Linux)
        {
            return "a systemd unit read must run on Linux";
        }

        if (!command.Contains("\"$1\"", StringComparison.Ordinal))
        {
            return "its script never uses \"$1\"";
        }

        if (OtherPositionalParameter().IsMatch(command))
        {
            return "its script uses a positional parameter other than $1";
        }

        return ParameterChangingWord().IsMatch(command)
            ? "its script uses shift, set or eval"
            : null;
    }

    // $@ $* $2..$9, ${@ ${* ${2..${9, and ${10} and up. ("$10" unbraced is "$1" then "0" in POSIX sh.)
    [GeneratedRegex(@"\$\{?[@*2-9]|\$\{[0-9]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex OtherPositionalParameter();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(shift|set|eval)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterChangingWord();
}

/// <summary>One accepted read: the values captured at start, never the read itself.</summary>
/// <param name="Id">The read's id.</param>
/// <param name="OperatingSystem">The one operating system it runs on.</param>
/// <param name="Command">The constant script.</param>
/// <param name="ArgumentKind">What its argument is.</param>
/// <param name="OutputLimitBytes">The most standard output to read, in bytes.</param>
/// <param name="Timeout">How long the script may run.</param>
public sealed record RemoteReadRegistration(
    string Id,
    ServerOperatingSystem OperatingSystem,
    string Command,
    RemoteReadArgumentKind ArgumentKind,
    int OutputLimitBytes,
    TimeSpan Timeout)
{
    /// <summary>Never prints the script.</summary>
    public override string ToString() =>
        $"RemoteReadRegistration {{ Id = {Id}, OperatingSystem = {OperatingSystem}, ArgumentKind = {ArgumentKind} }}";
}
