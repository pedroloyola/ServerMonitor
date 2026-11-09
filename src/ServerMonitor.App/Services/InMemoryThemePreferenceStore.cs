using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

/// <summary>
/// THEME-1: a theme store that lives only in this process - for launches that must never write
/// <c>theme-settings.json</c> (tests, QA). Starts at System unless told otherwise, like a first run.
/// </summary>
public sealed class InMemoryThemePreferenceStore(AppThemePreference initial = AppThemePreference.System) : IThemePreferenceStore
{
    private AppThemePreference _preference = Enum.IsDefined(initial) ? initial : AppThemePreference.System;

    public AppThemePreference Read() => _preference;

    public bool TryWrite(AppThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            return false;
        }

        _preference = preference;
        return true;
    }
}
