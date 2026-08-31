using ServerMonitor.ActivationContract;

namespace ServerMonitor.App.Services;

/// <summary>
/// The single convergence point for widget/protocol activation intents (ADR-018 §4/§18/§28/§29). It
/// buffers an intent that arrives before the shell/navigation is ready and runs it once readiness is
/// signalled; an intent that arrives when ready runs promptly. Rapid activations coalesce to the LATEST
/// intent — the user's most recent click wins.
/// <para>
/// Concurrency: <c>_pending</c>, <c>_ready</c> and <c>_draining</c> are mutated only under <c>_gate</c>,
/// and exactly one thread ever drains at a time (<c>_draining</c> ownership). The draining thread takes
/// the LATEST pending intent under the lock, then runs the executor OUTSIDE the lock; so a concurrent
/// Route that arrives during execution just replaces <c>_pending</c> and is picked up next, and the
/// latest intent can never be overtaken at the ready boundary (§M-1). Readiness is an explicit signal —
/// no timers, no waits.
/// </para>
/// </summary>
public sealed class ActivationRouter
{
    private readonly Action<ActivationIntent> _execute;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private ActivationIntent? _pending;
    private bool _ready;
    private bool _draining;

    public ActivationRouter(Action<ActivationIntent> execute, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _onError = onError;
    }

    /// <summary>Routes an intent (null is ignored). Runs when ready; buffers the latest until then.</summary>
    public void Route(ActivationIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        lock (_gate)
        {
            _pending = intent; // latest wins (§28)
            if (!_ready || _draining)
            {
                return; // not ready yet, or a drain already owns execution and will pick this up
            }

            _draining = true;
        }

        Drain();
    }

    /// <summary>Signals the shell is ready and flushes the latest buffered intent. Idempotent.</summary>
    public void MarkReady()
    {
        lock (_gate)
        {
            _ready = true;
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            ActivationIntent next;
            lock (_gate)
            {
                if (!_ready || _pending is null)
                {
                    _draining = false;
                    return;
                }

                next = _pending;
                _pending = null;
            }

            // A throwing executor must never wedge the drain owner (leaving _draining stuck true would
            // silently drop every later activation) nor strand a newer pending intent. Isolate each
            // execution: report via the optional sink and keep draining (L-1, Atlas reliability review).
            try
            {
                _execute(next);
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }
    }

    // The error sink itself must never wedge the drain: a throw here would escape Drain with _draining
    // still true and freeze all future activation. Swallow any sink failure (L-1, Atlas reliability review).
    private void Report(Exception exception)
    {
        try
        {
            _onError?.Invoke(exception);
        }
        catch
        {
            // A failing error sink is not allowed to break activation routing.
        }
    }
}
