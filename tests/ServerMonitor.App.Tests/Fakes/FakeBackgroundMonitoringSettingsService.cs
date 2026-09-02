using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// In-memory background settings. It also counts notice claims, because "attempted exactly once, ever"
/// is the property the first-close notice has to satisfy and the only way to prove it is to count.
/// </summary>
internal sealed class FakeBackgroundMonitoringSettingsService : IBackgroundMonitoringSettingsService
{
    private readonly object _sync = new();
    private bool _enabled;
    private bool _noticeShown;

    public FakeBackgroundMonitoringSettingsService(bool enabled = true, bool noticeShown = false)
    {
        _enabled = enabled;
        _noticeShown = noticeShown;
    }

    public event EventHandler? BackgroundMonitoringEnabledChanged;

    /// <summary>When set, persisting throws — the toggle must revert visibly rather than lie.</summary>
    public bool ThrowOnSet { get; set; }

    public int ClaimAttempts { get; private set; }

    public int ClaimsGranted { get; private set; }

    public bool BackgroundMonitoringEnabled
    {
        get { lock (_sync) { return _enabled; } }
    }

    public bool BackgroundNoticeShown
    {
        get { lock (_sync) { return _noticeShown; } }
    }

    public void SetBackgroundMonitoringEnabled(bool enabled)
    {
        if (ThrowOnSet)
        {
            throw new IOException("background settings unavailable");
        }

        lock (_sync)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
        }

        BackgroundMonitoringEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryClaimBackgroundNotice()
    {
        lock (_sync)
        {
            ClaimAttempts++;
            if (_noticeShown)
            {
                return false;
            }

            _noticeShown = true;
            ClaimsGranted++;
            return true;
        }
    }
}
