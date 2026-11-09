using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// THEME-1: an in-memory <see cref="IThemePreferenceStore"/> that records every read and write.
/// <see cref="FailWrites"/> makes a write report failure like a locked or full disk would; <see cref="ThrowOnWrite"/>
/// makes it throw, breaking the store's own contract, so the caller's last line of defence is testable.
/// </summary>
internal sealed class FakeThemePreferenceStore(AppThemePreference saved = AppThemePreference.System) : IThemePreferenceStore
{
    public AppThemePreference Saved { get; private set; } = saved;

    public List<AppThemePreference> Writes { get; } = [];

    public int Reads { get; private set; }

    public bool FailWrites { get; set; }

    public Exception? ThrowOnWrite { get; set; }

    public Exception? ThrowOnRead { get; set; }

    public AppThemePreference Read()
    {
        Reads++;
        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        return Saved;
    }

    public bool TryWrite(AppThemePreference preference)
    {
        Writes.Add(preference);
        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        if (FailWrites)
        {
            return false;
        }

        Saved = preference;
        return true;
    }
}
