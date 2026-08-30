namespace ServerMonitor.WidgetProvider.Diagnostics;

/// <summary>
/// Minimal logging seam for the provider — deliberately tiny so the provider takes no dependency on a
/// logging framework (ADR-018 §24). Messages are coarse and MUST never include the snapshot payload,
/// server names, or file contents (§31); only operation + exception type.
/// </summary>
public interface IWidgetProviderLog
{
    void Warn(string message);

    void Info(string message);
}

/// <summary>No-op log used by default and in tests.</summary>
public sealed class NullWidgetProviderLog : IWidgetProviderLog
{
    public static readonly NullWidgetProviderLog Instance = new();

    private NullWidgetProviderLog()
    {
    }

    public void Warn(string message)
    {
    }

    public void Info(string message)
    {
    }
}
