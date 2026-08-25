using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Infrastructure.Discovery;

namespace ServerMonitor.Infrastructure.Tests.Discovery;

public sealed class TmdsMdnsServiceBrowserTests
{
    [Fact]
    public void StartFailure_DetachesHandlersStopsDisposesAndCanRetryCleanly()
    {
        var failed = new FakeSession { StartException = new InvalidOperationException("socket failed") };
        var recovered = new FakeSession();
        var sessions = new Queue<IMdnsBrowserSession>([failed, recovered]);
        using var browser = new TmdsMdnsServiceBrowser(
            NullLogger<TmdsMdnsServiceBrowser>.Instance,
            MdnsServiceBrowserOptions.Default,
            () => sessions.Dequeue());

        Assert.Throws<InvalidOperationException>(browser.Start);
        Assert.Equal(1, failed.StopCount);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(0, failed.SubscriberCount);

        browser.Start();
        Assert.Equal(1, recovered.StartCount);
        Assert.Equal(3, recovered.SubscriberCount);

        browser.Stop();
        Assert.Equal(1, recovered.StopCount);
        Assert.Equal(1, recovered.DisposeCount);
        Assert.Equal(0, recovered.SubscriberCount);
    }

    private sealed class FakeSession : IMdnsBrowserSession
    {
        private EventHandler<DiscoveryObservation>? _found;
        private EventHandler<DiscoveryObservation>? _updated;
        private EventHandler<DiscoveryObservation>? _removed;

        public Exception? StartException { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int SubscriberCount =>
            InvocationCount(_found) + InvocationCount(_updated) + InvocationCount(_removed);

        public event EventHandler<DiscoveryObservation>? Found
        {
            add => _found += value;
            remove => _found -= value;
        }

        public event EventHandler<DiscoveryObservation>? Updated
        {
            add => _updated += value;
            remove => _updated -= value;
        }

        public event EventHandler<DiscoveryObservation>? Removed
        {
            add => _removed += value;
            remove => _removed -= value;
        }

        public void Start(string serviceType, int queryIntervalMilliseconds)
        {
            StartCount++;
            if (StartException is not null)
            {
                throw StartException;
            }
        }

        public void Stop() => StopCount++;

        public void Dispose() => DisposeCount++;

        private static int InvocationCount(Delegate? handler) =>
            handler?.GetInvocationList().Length ?? 0;
    }
}
