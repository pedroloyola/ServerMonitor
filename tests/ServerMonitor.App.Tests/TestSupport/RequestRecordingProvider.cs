namespace ServerMonitor.App.Tests.TestSupport;

/// <summary>
/// Runs a production factory without constructing anything: records the first service it asks for, then stops it.
/// Used to prove what a registration WOULD resolve (e.g. the Credential Manager store) without resolving it.
/// </summary>
internal sealed class RequestRecordingProvider : IServiceProvider
{
    public List<Type> Requested { get; } = [];

    public object? GetService(Type serviceType)
    {
        Requested.Add(serviceType);
        throw new Stop();
    }

    internal sealed class Stop : Exception;
}
