using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// In-memory ignored-device store with nothing ignored. Open by default; constructed closed, <see cref="LoadAsync"/>
/// signals <see cref="Entered"/> and waits for <see cref="Release"/>, so a test can hold a discovery start in flight.
/// </summary>
internal sealed class GatedIgnoredDeviceStore(bool open = true) : IIgnoredDeviceStore
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public async Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        if (!open)
        {
            await _gate.Task.WaitAsync(cancellationToken);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    public Task<bool> IgnoreAsync(string identityHash, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
