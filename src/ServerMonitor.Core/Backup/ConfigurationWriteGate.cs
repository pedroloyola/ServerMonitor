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
    /// when a restore holds the gate or it is sealed. A lease taken while the same logical call flow already
    /// holds one always succeeds (a profile save that writes a credential and then the server list is one
    /// mutation, and a restore that is draining waits for it to finish rather than breaking it in half).
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

    // Leases held by the current logical call flow; makes a nested lease re-entrant.
    private readonly AsyncLocal<int> _flowDepth = new();

    private GateState _state;
    private RestoreWriteToken? _token;
    private int _activeLeases;
    private TaskCompletionSource? _drained;

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
        lock (_sync)
        {
            if (_state != GateState.Open && _flowDepth.Value == 0)
            {
                throw new ConfigurationLockedException();
            }

            _activeLeases++;
        }

        _flowDepth.Value++;
        return new Lease(this);
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
            await drained.WaitAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
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

    private void Exit()
    {
        _flowDepth.Value = Math.Max(0, _flowDepth.Value - 1);
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

    private sealed class Lease(ConfigurationWriteGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
