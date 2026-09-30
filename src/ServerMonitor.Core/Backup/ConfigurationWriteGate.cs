namespace ServerMonitor.Core.Backup;

/// <summary>
/// The data-layer restore gate (M14.6 spec §5.5, Vigil V5/C-10). Every ordinary configuration writer takes
/// a short shared lease around each mutation; a restore takes the gate exclusively, and at its commit point
/// SEALS it for the rest of the process, so a stale in-memory cache can never write the pre-restore state
/// back over the committed one. Protection is structural: it does not depend on which window, tray item or
/// activation started the write.
/// </summary>
public interface IConfigurationWriteGate
{
    /// <summary>True while a restore holds the gate or after it was sealed.</summary>
    bool IsLocked { get; }

    /// <summary>
    /// Takes a shared lease for one mutation. Never waits: throws <see cref="ConfigurationLockedException"/>
    /// when a restore holds the gate or it is sealed. While a restore is DRAINING, a lease nested inside a
    /// still-open lease of the same logical call flow succeeds (a profile save that writes a credential and then
    /// the server list is one mutation; the restore waits for it rather than breaking it in half). Re-entry is
    /// tied to that exact outer lease: once it is disposed, a flow that inherited it (a task or loop started
    /// inside it) gets no bypass. After the seal, every request is refused, re-entrant or not (M14.6 Cortex-1).
    /// </summary>
    IDisposable EnterWrite();

    /// <summary>
    /// Blocks new leases, then waits for active leases to drain. Returns the restore token, or
    /// <see langword="null"/> when the gate is already held/sealed or the drain did not finish within
    /// <paramref name="drainTimeout"/> (the gate is then released again: nothing was touched).
    /// </summary>
    Task<RestoreWriteToken?> BeginRestoreAsync(TimeSpan drainTimeout, CancellationToken cancellationToken = default);

    /// <summary>Ends a restore that did not commit: ordinary writes resume.</summary>
    void Release(RestoreWriteToken token);

    /// <summary>The commit point: irreversible for the life of the process.</summary>
    void Seal(RestoreWriteToken token);

    /// <summary>Throws unless <paramref name="token"/> is the token of the restore currently holding the gate.</summary>
    void EnsureHeldBy(RestoreWriteToken token);
}

/// <summary>Proof that the caller is the restore holding <see cref="IConfigurationWriteGate"/>. Only the gate
/// mints it (internal constructor), so no other code can call a store's restore-only write.</summary>
public sealed class RestoreWriteToken
{
    internal RestoreWriteToken()
    {
    }

    public override string ToString() => nameof(RestoreWriteToken);
}

/// <summary>A configuration write was refused because a restore is running or has committed.</summary>
public sealed class ConfigurationLockedException : InvalidOperationException
{
    public ConfigurationLockedException()
        : base("The configuration is locked by a restore.")
    {
    }
}

public sealed class ConfigurationWriteGate : IConfigurationWriteGate
{
    private enum GateState
    {
        Open,
        Held,
        Sealed,
    }

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    // The innermost lease of the current logical call flow. Re-entry is granted only while THAT lease is still
    // open; a child flow that inherited it keeps nothing once it is disposed (Cortex-1).
    private readonly AsyncLocal<Lease?> _current = new();

    private GateState _state;
    private RestoreWriteToken? _token;
    private int _activeLeases;
    private TaskCompletionSource? _drained;

    public ConfigurationWriteGate()
        : this(TimeProvider.System)
    {
    }

    /// <summary><paramref name="timeProvider"/> drives only the drain timeout (tests use a manual clock).</summary>
    public ConfigurationWriteGate(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool IsLocked
    {
        get
        {
            lock (_sync)
            {
                return _state != GateState.Open;
            }
        }
    }

    public IDisposable EnterWrite()
    {
        var outer = _current.Value;
        Lease lease;
        lock (_sync)
        {
            var nestedInOpenLease = outer is not null && ReferenceEquals(outer.Owner, this) && !outer.IsDisposed;
            if (_state == GateState.Sealed || (_state == GateState.Held && !nestedInOpenLease))
            {
                throw new ConfigurationLockedException();
            }

            _activeLeases++;
            lease = new Lease(this, outer);
        }

        _current.Value = lease;
        return lease;
    }

    public async Task<RestoreWriteToken?> BeginRestoreAsync(
        TimeSpan drainTimeout,
        CancellationToken cancellationToken = default)
    {
        RestoreWriteToken token;
        Task drained;
        lock (_sync)
        {
            if (_state != GateState.Open)
            {
                return null;
            }

            token = new RestoreWriteToken();
            _state = GateState.Held;
            _token = token;
            if (_activeLeases == 0)
            {
                return token;
            }

            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drained.Task;
        }

        try
        {
            await drained.WaitAsync(drainTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            return token;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_token, token) && _state == GateState.Held)
                {
                    _state = GateState.Open;
                    _token = null;
                    _drained = null;
                }
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            return null;
        }
    }

    public void Release(RestoreWriteToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_sync)
        {
            if (_state == GateState.Sealed)
            {
                throw new InvalidOperationException("A sealed configuration gate cannot be released.");
            }

            EnsureHeldByLocked(token);
            _state = GateState.Open;
            _token = null;
            _drained = null;
        }
    }

    public void Seal(RestoreWriteToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_sync)
        {
            EnsureHeldByLocked(token);
            _state = GateState.Sealed;
        }
    }

    public void EnsureHeldBy(RestoreWriteToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_sync)
        {
            EnsureHeldByLocked(token);
            if (_state != GateState.Held)
            {
                throw new InvalidOperationException("The restore has already committed.");
            }
        }
    }

    private void EnsureHeldByLocked(RestoreWriteToken token)
    {
        if (_state == GateState.Open || !ReferenceEquals(_token, token))
        {
            throw new InvalidOperationException("The restore token does not hold the configuration gate.");
        }
    }

    private void Exit(Lease lease)
    {
        // Best effort for the disposing flow only; correctness never depends on it (IsDisposed does).
        if (ReferenceEquals(_current.Value, lease))
        {
            _current.Value = lease.Outer;
        }

        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _activeLeases--;
            if (_activeLeases == 0 && _drained is not null)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    private sealed class Lease(ConfigurationWriteGate owner, Lease? outer) : IDisposable
    {
        private int _disposed;

        public ConfigurationWriteGate Owner { get; } = owner;

        public Lease? Outer { get; } = outer;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Owner.Exit(this);
            }
        }
    }
}
