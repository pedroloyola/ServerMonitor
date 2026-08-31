using ServerMonitor.ActivationContract;

namespace ServerMonitor.App.Services;

/// <summary>
/// The single, atomic hand-off for activation intents across the App-construction boundary (§M-1/§M-2).
/// A redirected activation can arrive BEFORE <c>new App()</c> has built the <see cref="ActivationRouter"/>,
/// and <c>Application.Current</c> is NOT a safe readiness flag — the base <c>Application</c> constructor
/// sets it while the derived constructor is still wiring DI and the router. So every intent (the initial
/// cold launch AND every redirect) is funneled through this one gate: it is delivered straight to the
/// consumer once one is attached, or buffered (latest-wins) until then.
/// <para>
/// Concurrency mirrors <see cref="ActivationRouter"/>: <c>_pending</c>, <c>_consumer</c> and
/// <c>_draining</c> are mutated only under <c>_gate</c>, and exactly ONE thread ever drains at a time
/// (<c>_draining</c> ownership). The draining thread takes the LATEST pending intent under the lock, then
/// invokes the consumer OUTSIDE the lock. So a redirect that races <see cref="Attach"/> can never be
/// overtaken by the older buffered intent: both go through the same single drain, in order, and the newest
/// wins at the consumer. Thread-safe; a null intent (a non-deep-link activation) is ignored so it never
/// clobbers a real pending intent.
/// </para>
/// </summary>
public sealed class PendingActivation
{
    private readonly object _gate = new();
    private ActivationIntent? _pending;
    private Action<ActivationIntent>? _consumer;
    private bool _draining;

    /// <summary>
    /// Delivers an intent: buffers it (latest-wins) and, if a consumer is attached and no drain is already
    /// in progress, drains it. A null intent leaves any existing pending intent intact.
    /// </summary>
    public void Deliver(ActivationIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        lock (_gate)
        {
            _pending = intent; // latest wins (§28)
            if (_consumer is null || _draining)
            {
                return; // no consumer yet, or a drain already owns delivery and will pick this up
            }

            _draining = true;
        }

        Drain();
    }

    /// <summary>
    /// Attaches the single consumer (the router's Route) and, if an intent is buffered and no drain is in
    /// progress, drains it. Called once, when the router is ready.
    /// </summary>
    public void Attach(Action<ActivationIntent> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        lock (_gate)
        {
            _consumer = consumer;
            if (_pending is null || _draining)
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
            Action<ActivationIntent> consumer;
            lock (_gate)
            {
                if (_consumer is not { } current || _pending is null)
                {
                    _draining = false;
                    return;
                }

                consumer = current;
                next = _pending;
                _pending = null;
            }

            consumer(next);
        }
    }
}
