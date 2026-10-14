using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0D: a provenance store that lives only in this process - for launches that must never write
/// <c>language-settings.json</c> (the Debug QA gallery). Starts Absent, like a first run.
/// </summary>
public sealed class InMemoryLanguageSettingsStore : ILanguageSettingsStore
{
    private LanguageRecordRead _record = LanguageRecordRead.Absent;

    public LanguageRecordRead Read() => _record;

    public bool TryWrite(LanguageProvenance record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _record = LanguageRecordRead.Of(record);
        return true;
    }
}
