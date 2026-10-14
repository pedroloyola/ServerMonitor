using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// REL.0D: an in-memory <see cref="ILanguageSettingsStore"/> that records every write (and, optionally, into a journal
/// shared with <see cref="FakeUiLanguagePlatform"/>). <see cref="FailWrites"/> makes every write fail like a locked or
/// full disk would; <see cref="State"/> sets what a read returns (Absent / Valid / Unreadable).
/// </summary>
internal sealed class FakeLanguageSettingsStore(LanguageRecordRead? initial = null, List<string>? journal = null) : ILanguageSettingsStore
{
    public LanguageRecordRead State { get; set; } = initial ?? LanguageRecordRead.Absent;

    public bool FailWrites { get; set; }

    public List<LanguageProvenance> Writes { get; } = [];

    public LanguageRecordRead Read() => State;

    public bool TryWrite(LanguageProvenance record)
    {
        journal?.Add($"record:{record.FollowSystem}:{record.AutoOverride}");
        if (FailWrites)
        {
            return false;
        }

        Writes.Add(record);
        State = LanguageRecordRead.Of(record);
        return true;
    }

    public static FakeLanguageSettingsStore Following(string autoOverride) =>
        new(LanguageRecordRead.Of(LanguageProvenance.System(autoOverride)));

    public static FakeLanguageSettingsStore ExplicitChoice() =>
        new(LanguageRecordRead.Of(LanguageProvenance.Explicit));

    public static FakeLanguageSettingsStore Unreadable() => new(LanguageRecordRead.Unreadable);
}
