using System.Diagnostics.CodeAnalysis;
using System.Text;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.Core.Probes;

/// <summary>
/// Decides which registered <see cref="IRemoteProbe"/>s run on which server, and keeps the second phase of each poll
/// in check.
/// <para>
/// A poll asks <see cref="TryReserve"/> for a reservation before it starts. There is none when no probe is registered
/// (so a build without probes runs exactly the built-in commands) or while that server's previous second phase is
/// still running (so the poll runs the built-in commands only). Once the built-in result is published, the SSH session
/// calls <see cref="RemoteProbeReservation.Begin"/> with the server's operating system: it picks the due probes within
/// <see cref="RemoteProbeLimits.MaxProbesPerPoll"/> and <see cref="RemoteProbeLimits.TotalOutputBudgetBytes"/>, least
/// recently run first, and returns the <see cref="RemoteProbeBatch"/> to run. The poll calls
/// <see cref="RemoteProbeReservation.Complete"/> when the session is over, whatever happened.
/// </para>
/// <para>
/// Each reservation has its own cancellation, linked to the server's monitoring token. Removing the server
/// (<see cref="Forget"/>) cancels it, and so does every clear or reset of history (<see cref="HistoryGeneration.Advance"/>),
/// which also makes outcomes from before the clear stale, so they are dropped.
/// </para>
/// <para>
/// A watched-only probe (<see cref="IRemoteProbe.RunsOnlyWhileWatched"/>) is due on a server only while the
/// <see cref="RemoteProbeWatch"/> holds a watch of it for that server; without one given here it is never due. While it is
/// not watched it is skipped without being marked as run, so it is due on the first poll after a watch starts.
/// </para>
/// </summary>
public sealed class RemoteProbeScheduler
{
    private readonly Registration[] _registrations;
    private readonly HistoryGeneration _generation;
    private readonly RemoteProbeWatch? _watch;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RemoteProbeReservation> _inFlight = [];
    private readonly Dictionary<(Guid ServerId, int Index), DateTimeOffset> _lastRun = [];

    public RemoteProbeScheduler(
        IEnumerable<IRemoteProbe> probes,
        HistoryGeneration generation,
        TimeProvider? timeProvider = null,
        RemoteProbeWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _watch = watch;

        var accepted = new List<Registration>();
        var rejected = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var probe in probes)
        {
            if (TryRegister(probe, out var registration, out var reason) && !ids.Add(registration.Request.ProbeId))
            {
                reason = "another probe already has its id";
            }

            if (reason is null)
            {
                accepted.Add(registration! with { Index = accepted.Count });
            }
            else
            {
                rejected.Add($"probe #{position}: {reason}");
            }

            position++;
        }

        _registrations = [.. accepted];
        Rejected = rejected;
        _generation.Advanced += (_, _) => CancelAll();
    }

    /// <summary>How many probes were accepted.</summary>
    public int Count => _registrations.Length;

    /// <summary>Why each left-out probe was left out, by position (never its command). For a log line.</summary>
    public IReadOnlyList<string> Rejected { get; }

    /// <summary>
    /// Reserves the second phase of one poll, or returns null when there are no probes, the server's token is already
    /// cancelled, or the server's previous second phase is still running.
    /// </summary>
    public RemoteProbeReservation? TryReserve(Guid serverId, CancellationToken serverToken)
    {
        if (_registrations.Length == 0 || serverToken.IsCancellationRequested)
        {
            return null;
        }

        CancellationTokenSource cancellation;
        try
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        lock (_gate)
        {
            if (_inFlight.ContainsKey(serverId))
            {
                cancellation.Dispose();
                return null;
            }

            var reservation = new RemoteProbeReservation(this, serverId, cancellation);
            _inFlight[serverId] = reservation;
            return reservation;
        }
    }

    /// <summary>True while a reservation for <paramref name="serverId"/> is open.</summary>
    public bool IsInFlight(Guid serverId)
    {
        lock (_gate)
        {
            return _inFlight.ContainsKey(serverId);
        }
    }

    /// <summary>Cancels every second phase that is running. Called on each history clear or reset.</summary>
    public void CancelAll()
    {
        RemoteProbeReservation[] open;
        lock (_gate)
        {
            open = [.. _inFlight.Values];
        }

        foreach (var reservation in open)
        {
            reservation.Cancel();
        }
    }

    /// <summary>The server was removed: cancels its second phase and forgets when its probes last ran.</summary>
    public void Forget(Guid serverId)
    {
        RemoteProbeReservation? open;
        lock (_gate)
        {
            // Marked under the same lock that clears _lastRun: a Begin that runs after this refuses the reservation, so
            // it cannot record a run for the removed server between this lock and the Cancel below. The reservation
            // stays in _inFlight until it completes, so the one-second-phase-per-server cap still holds.
            open = _inFlight.GetValueOrDefault(serverId);
            open?.MarkForgotten();
            foreach (var key in _lastRun.Keys.Where(key => key.ServerId == serverId).ToList())
            {
                _lastRun.Remove(key);
            }
        }

        open?.Cancel();
    }

    internal RemoteProbeBatch? Begin(RemoteProbeReservation reservation, ServerOperatingSystem operatingSystem)
    {
        var selected = new List<Registration>();
        long generation;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(reservation.ServerId, out var current)
                || !ReferenceEquals(current, reservation)
                || reservation.IsForgotten
                || reservation.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            var due = _registrations
                .Where(registration => registration.OperatingSystem == operatingSystem)
                .Where(registration => !registration.RunsOnlyWhileWatched
                    || _watch?.IsWatched(reservation.ServerId, registration.Request.ProbeId) == true)
                .Select(registration => (Registration: registration,
                    LastRun: _lastRun.TryGetValue((reservation.ServerId, registration.Index), out var at) ? at : (DateTimeOffset?)null))
                .Where(candidate => candidate.LastRun is null || now - candidate.LastRun.Value >= candidate.Registration.MinimumInterval)
                .OrderBy(candidate => candidate.LastRun ?? DateTimeOffset.MinValue)
                .ThenBy(candidate => candidate.Registration.Index);

            var budget = RemoteProbeLimits.TotalOutputBudgetBytes;
            foreach (var (registration, _) in due)
            {
                if (selected.Count == RemoteProbeLimits.MaxProbesPerPoll)
                {
                    break;
                }

                if (registration.Request.OutputLimitBytes > budget)
                {
                    continue;
                }

                budget -= registration.Request.OutputLimitBytes;
                selected.Add(registration);
                _lastRun[(reservation.ServerId, registration.Index)] = now;
            }

            generation = _generation.Current;
        }

        return selected.Count == 0
            ? null
            : new RemoteProbeBatch(
                reservation.ServerId,
                generation,
                _generation,
                [.. selected.Select(registration => (registration.Request, registration.Probe))],
                reservation.CancellationToken,
                _timeProvider);
    }

    internal void Release(RemoteProbeReservation reservation)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(reservation.ServerId, out var current) && ReferenceEquals(current, reservation))
            {
                _inFlight.Remove(reservation.ServerId);
            }
        }
    }

    private static bool TryRegister(
        IRemoteProbe? probe,
        [NotNullWhen(true)] out Registration? registration,
        out string? reason)
    {
        registration = null;
        reason = null;
        if (probe is null)
        {
            reason = "it is null";
            return false;
        }

        string? id;
        ServerOperatingSystem operatingSystem;
        string? command;
        int limit;
        TimeSpan timeout;
        TimeSpan interval;
        bool watchedOnly;
        try
        {
            // Read once and kept: the probe cannot change what runs after this point.
            id = probe.Id;
            operatingSystem = probe.OperatingSystem;
            command = probe.Command;
            limit = probe.OutputLimitBytes;
            timeout = probe.Timeout;
            interval = probe.MinimumInterval;
            watchedOnly = probe.RunsOnlyWhileWatched;
        }
        catch (Exception exception)
        {
            reason = $"reading it threw {exception.GetType().Name}";
            return false;
        }

        reason = Validate(id, operatingSystem, command, limit, timeout, interval);
        if (reason is not null)
        {
            return false;
        }

        registration = new Registration(
            probe,
            new RemoteProbeRequest(id!, command!, limit, timeout),
            operatingSystem,
            interval,
            watchedOnly,
            Index: -1);
        return true;
    }

    private static string? Validate(
        string? id,
        ServerOperatingSystem operatingSystem,
        string? command,
        int limit,
        TimeSpan timeout,
        TimeSpan interval)
    {
        if (!IsWellFormedId(id))
        {
            return "its id is not well formed";
        }

        if (operatingSystem is not (ServerOperatingSystem.Linux or ServerOperatingSystem.MacOS))
        {
            return "its operating system is not Linux or macOS";
        }

        if (string.IsNullOrWhiteSpace(command) || command.Length > RemoteProbeLimits.MaxCommandLength)
        {
            return "its script is empty or too long";
        }

        // A constant script, lines separated by LF only. CR, NUL, TAB, ESC and every other control character
        // (including the Unicode line and paragraph separators) are refused, so what is reviewed is what runs.
        if (command.Any(character => character != '\n' && (char.IsControl(character) || IsLineOrParagraphSeparator(character))))
        {
            return "its script has a control character other than LF";
        }

        if (limit < 1 || limit > RemoteProbeLimits.MaxOutputLimitBytes)
        {
            return "its output limit is out of range";
        }

        if (timeout <= TimeSpan.Zero || timeout > RemoteProbeLimits.MaxTimeout)
        {
            return "its timeout is out of range";
        }

        return interval < TimeSpan.Zero ? "its minimum interval is negative" : null;
    }

    // U+2028 and U+2029 are not control characters to char.IsControl, yet some tools treat them as line breaks.
    private static bool IsLineOrParagraphSeparator(char character) =>
        character == (char)0x2028 || character == (char)0x2029;

    private static bool IsWellFormedId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > RemoteProbeLimits.MaxIdLength || id[0] == '.' || id[^1] == '.')
        {
            return false;
        }

        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '.'))
            {
                return false;
            }

            if (c == '.' && id[i - 1] == '.')
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Registration(
        IRemoteProbe Probe,
        RemoteProbeRequest Request,
        ServerOperatingSystem OperatingSystem,
        TimeSpan MinimumInterval,
        bool RunsOnlyWhileWatched,
        int Index);
}

/// <summary>
/// The second phase of one poll on one server, from <see cref="RemoteProbeScheduler.TryReserve"/> until
/// <see cref="Complete"/>. While it is open, later polls of the same server run the built-in commands only.
/// </summary>
public sealed class RemoteProbeReservation
{
    private const int Open = 0;
    private const int Begun = 1;
    private const int Completed = 2;

    private readonly RemoteProbeScheduler _scheduler;
    private readonly CancellationTokenSource _cancellation;
    private int _state;

    internal RemoteProbeReservation(RemoteProbeScheduler scheduler, Guid serverId, CancellationTokenSource cancellation)
    {
        _scheduler = scheduler;
        _cancellation = cancellation;
        ServerId = serverId;
        CancellationToken = cancellation.Token;
    }

    public Guid ServerId { get; }

    /// <summary>Cancelled when the server is removed, its monitoring stops, or history is cleared or reset.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Picks the probes to run now on a server running <paramref name="operatingSystem"/>, or returns null when none is
    /// due, the reservation was cancelled, or it was already begun or completed. Call it only after the built-in result
    /// was published.
    /// </summary>
    public RemoteProbeBatch? Begin(ServerOperatingSystem operatingSystem) =>
        Interlocked.CompareExchange(ref _state, Begun, Open) == Open
            ? _scheduler.Begin(this, operatingSystem)
            : null;

    /// <summary>Ends the reservation. Safe to call more than once; only the first call counts.</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _state, Completed) == Completed)
        {
            return;
        }

        _scheduler.Release(this);
        _cancellation.Dispose();
    }

    /// <summary>Set, under the scheduler's lock, when the server was removed. Read only under that lock.</summary>
    internal bool IsForgotten { get; private set; }

    internal void MarkForgotten() => IsForgotten = true;

    internal void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completed in the meantime: nothing is left to cancel.
        }
        catch (AggregateException)
        {
            // A callback registered on the token threw; the token is cancelled all the same.
        }
    }
}

/// <summary>
/// The probes picked for one poll. <see cref="RunAsync"/> runs them one after another through the SSH layer and hands
/// each outcome to its probe.
/// </summary>
public sealed class RemoteProbeBatch
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    private readonly Guid _serverId;
    private readonly long _generation;
    private readonly HistoryGeneration _currentGeneration;
    private readonly IReadOnlyList<(RemoteProbeRequest Request, IRemoteProbe Probe)> _items;
    private readonly TimeProvider _timeProvider;

    internal RemoteProbeBatch(
        Guid serverId,
        long generation,
        HistoryGeneration currentGeneration,
        IReadOnlyList<(RemoteProbeRequest Request, IRemoteProbe Probe)> items,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        _serverId = serverId;
        _generation = generation;
        _currentGeneration = currentGeneration;
        _items = items;
        _timeProvider = timeProvider;
        CancellationToken = cancellationToken;
        Requests = [.. items.Select(item => item.Request)];
    }

    /// <summary>What will run, in order.</summary>
    public IReadOnlyList<RemoteProbeRequest> Requests { get; }

    /// <summary>The reservation's token: cancelled on removal, stop, or a history clear or reset.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Runs every request through <paramref name="execute"/>, each with its own timeout linked to
    /// <see cref="CancellationToken"/>, and delivers each outcome to its probe. Never throws. Stops at once, delivering
    /// nothing more, when the token is cancelled or history moved to a new generation. A request that ignores its token
    /// is abandoned when its timeout passes.
    /// </summary>
    public async Task RunAsync(Func<RemoteProbeRequest, CancellationToken, Task<RemoteProbeExecution>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        foreach (var (request, probe) in _items)
        {
            if (IsStale)
            {
                return;
            }

            RemoteProbeOutcomeKind kind;
            RemoteProbeExecution? execution = null;
            Task<RemoteProbeExecution>? running = null;
            using (var timeout = new CancellationTokenSource(request.Timeout, _timeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, timeout.Token))
            {
                try
                {
                    running = execute(request, linked.Token);
                    execution = await running.WaitAsync(linked.Token).ConfigureAwait(false);
                    kind = Classify(execution, request);
                }
                catch (Exception) when (CancellationToken.IsCancellationRequested)
                {
                    Observe(running);
                    return;
                }
                catch (Exception) when (timeout.IsCancellationRequested)
                {
                    Observe(running);
                    kind = RemoteProbeOutcomeKind.TimedOut;
                }
                catch (Exception)
                {
                    kind = RemoteProbeOutcomeKind.Failed;
                }
            }

            var completed = kind == RemoteProbeOutcomeKind.Completed;
            Deliver(probe, new RemoteProbeOutcome(
                _serverId,
                request.ProbeId,
                _generation,
                _timeProvider.GetUtcNow(),
                kind,
                kind is RemoteProbeOutcomeKind.Completed or RemoteProbeOutcomeKind.OutputExceededLimit ? execution?.ExitStatus : null,
                completed ? execution!.StandardOutput : null,
                completed ? execution!.StandardError : null));
        }
    }

    private bool IsStale => CancellationToken.IsCancellationRequested || _currentGeneration.Current != _generation;

    private static RemoteProbeOutcomeKind Classify(RemoteProbeExecution? execution, RemoteProbeRequest request)
    {
        if (execution is null)
        {
            return RemoteProbeOutcomeKind.Failed;
        }

        if (execution.OutputExceededLimit)
        {
            return RemoteProbeOutcomeKind.OutputExceededLimit;
        }

        if (!execution.WasExecuted || execution.StandardOutput is null)
        {
            return RemoteProbeOutcomeKind.Failed;
        }

        // The SSH layer bounds the read already; this keeps the limit true for any executor.
        return Utf8.GetByteCount(execution.StandardOutput) > request.OutputLimitBytes
            ? RemoteProbeOutcomeKind.OutputExceededLimit
            : RemoteProbeOutcomeKind.Completed;
    }

    private void Deliver(IRemoteProbe probe, RemoteProbeOutcome outcome)
    {
        if (IsStale)
        {
            return;
        }

        try
        {
            probe.Accept(outcome);
        }
        catch
        {
            // A probe's sink must never break the session, the other probes or the poll.
        }
    }

    private static void Observe(Task? abandoned) =>
        abandoned?.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
