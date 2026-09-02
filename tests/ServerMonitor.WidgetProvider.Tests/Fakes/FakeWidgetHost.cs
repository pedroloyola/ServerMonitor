using System.Collections.Concurrent;
using ServerMonitor.WidgetProvider.Hosting;

namespace ServerMonitor.WidgetProvider.Tests.Fakes;

/// <summary>Controllable <see cref="IWidgetHost"/> that records updates and can be made to throw.</summary>
internal sealed class FakeWidgetHost : IWidgetHost
{
    public List<WidgetActivation> Existing { get; } = new();
    public ConcurrentBag<(string WidgetId, string Template, string Data)> Updates { get; } = new();

    public bool ThrowOnGetActiveWidgets { get; set; }
    public bool ThrowOnUpdate { get; set; }
    public HashSet<string> ThrowOnUpdateFor { get; } = new(StringComparer.Ordinal);

    /// <summary>Signals when GetActiveWidgets has been entered (for deterministic race setup).</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    /// <summary>When set, GetActiveWidgets blocks on it until the test releases it.</summary>
    public ManualResetEventSlim? BlockGetActiveWidgets { get; set; }

    public IReadOnlyList<WidgetActivation> GetActiveWidgets()
    {
        Entered.Release();
        BlockGetActiveWidgets?.Wait();

        if (ThrowOnGetActiveWidgets)
        {
            throw new InvalidOperationException("host GetWidgetInfos failed");
        }

        return Existing.ToArray();
    }

    /// <summary>Signals when Update has been entered (for deterministic race setup).</summary>
    public SemaphoreSlim UpdateEntered { get; } = new(0);

    /// <summary>When set, Update blocks on it until the test releases it.</summary>
    public ManualResetEventSlim? BlockUpdate { get; set; }

    public void Update(string widgetId, string templateJson, string dataJson)
    {
        UpdateEntered.Release();
        BlockUpdate?.Wait();

        if (ThrowOnUpdate || ThrowOnUpdateFor.Contains(widgetId))
        {
            throw new InvalidOperationException("host UpdateWidget failed");
        }

        Updates.Add((widgetId, templateJson, dataJson));
        Updated?.Invoke(widgetId, templateJson, dataJson);
    }

    /// <summary>
    /// Invoked after each recorded update, so a test driving the REAL pump over a real filesystem can wait
    /// on an event instead of sleeping for a guessed duration.
    /// </summary>
    public Action<string, string, string>? Updated { get; set; }

    public int UpdateCountFor(string widgetId) => Updates.Count(u => u.WidgetId == widgetId);
}
