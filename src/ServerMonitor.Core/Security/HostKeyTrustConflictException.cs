namespace ServerMonitor.Core.Security;

public sealed class HostKeyTrustConflictException : InvalidOperationException
{
    public HostKeyTrustConflictException()
        : base("The endpoint already has a different trusted host identity.")
    {
    }
}
