using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

/// <summary>
/// THEME-1: the user's saved theme choice (<c>theme-settings.json</c>). A device-local UI preference: not part of a
/// backup and not behind the restore write gate, like <see cref="ILanguageSettingsStore"/>.
/// </summary>
public interface IThemePreferenceStore
{
    /// <summary>
    /// The saved choice. Never throws: a missing, unknown, corrupt or oversized record reads as
    /// <see cref="AppThemePreference.System"/>.
    /// </summary>
    AppThemePreference Read();

    /// <summary>Replaces the record atomically; false (logged) when it could not be written. Never throws for I/O.</summary>
    bool TryWrite(AppThemePreference preference);
}
