using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0D: the language provenance record (<c>language-settings.json</c>). It never holds the language itself - that
/// stays in the Windows App SDK override - only whether the user asked for the system language and which override the
/// app wrote for it.
/// </summary>
public interface ILanguageSettingsStore
{
    /// <summary>Reads the record; never throws (a file that cannot be used reads as <see cref="LanguageRecordState.Unreadable"/>).</summary>
    LanguageRecordRead Read();

    /// <summary>Replaces the record atomically; false (logged) when it could not be written.</summary>
    bool TryWrite(LanguageProvenance record);
}
