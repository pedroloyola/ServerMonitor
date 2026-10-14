namespace ServerMonitor.Core.Domain;

/// <summary>What the language provenance file held when it was read (REL.0D).</summary>
public enum LanguageRecordState
{
    /// <summary>No file: a first run, or a profile last used by v1.0 / v1.1.x.</summary>
    Absent,

    /// <summary>A well-formed record of the current schema.</summary>
    Valid,

    /// <summary>Present but unusable (malformed, oversized, unknown schema, unreadable). Proves nothing.</summary>
    Unreadable,

    /// <summary>
    /// PR #55 fix round 1 (Cortex N-1): a well-formed record written by ANOTHER app identity sharing the same folder
    /// (the Store package, a Developer package, an unpackaged run). It proves nothing about this identity's override, so
    /// for every decision it is the same as <see cref="Absent"/>: this identity has no record of its own.
    /// </summary>
    OtherWriter
}

/// <summary>
/// The provenance of the persisted UI language override (REL.0D, <c>language-settings.json</c>).
/// <see cref="FollowSystem"/> is true when the user asked for the system language; <see cref="AutoOverride"/> is then
/// the exact override the app itself wrote for it ("" when none was needed). An explicit choice has
/// <c>FollowSystem = false</c> and no <see cref="AutoOverride"/>.
/// </summary>
public sealed record LanguageProvenance(bool FollowSystem, string? AutoOverride)
{
    public static LanguageProvenance Explicit { get; } = new(false, null);

    public static LanguageProvenance System(string autoOverride) => new(true, autoOverride);
}

/// <summary>The provenance file as read: its state and, when <see cref="LanguageRecordState.Valid"/>, its record.</summary>
public sealed record LanguageRecordRead(LanguageRecordState State, LanguageProvenance? Record)
{
    public static LanguageRecordRead Absent { get; } = new(LanguageRecordState.Absent, null);

    public static LanguageRecordRead Unreadable { get; } = new(LanguageRecordState.Unreadable, null);

    public static LanguageRecordRead OtherWriter { get; } = new(LanguageRecordState.OtherWriter, null);

    public static LanguageRecordRead Of(LanguageProvenance record) =>
        new(LanguageRecordState.Valid, record ?? throw new ArgumentNullException(nameof(record)));
}

/// <summary>
/// What the startup resolution writes. <see cref="RecordToWrite"/> is written FIRST, then <see cref="OverrideToWrite"/>;
/// null means "leave it untouched" and an empty <see cref="OverrideToWrite"/> clears the override.
/// </summary>
public sealed record LanguageStartupPlan(LanguageProvenance? RecordToWrite, string? OverrideToWrite)
{
    public static LanguageStartupPlan Nothing { get; } = new(null, null);

    public bool IsEmpty => RecordToWrite is null && OverrideToWrite is null;
}

/// <summary>
/// REL.0D language recovery, as pure rules over (persisted override, Windows UI culture, provenance record).
/// <list type="bullet">
/// <item>An override is "the system language" only when there is none, or when a VALID record says the user asked for
/// the system language and the override is exactly the one the app wrote for it. Every other override - a v1.x pt-BR
/// without a record, a missing or unreadable record, a value that differs from the record - is the user's choice and is
/// never reinterpreted or rewritten.</item>
/// <item>The record is always written before the override: a record that is ahead of the override never matches it, so
/// an interrupted sequence reads as "explicit" and the language never changes on its own.</item>
/// <item>The legacy hint is offered only for the exact signature of the v1.x defect: pt-BR, no record of this app
/// identity (absent, or written by another identity), and a Windows culture that is not exactly en-US / pt-PT / pt-BR
/// and whose own language is not Brazilian Portuguese.</item>
/// </list>
/// <para>
/// Known residual (Vigil L-1, PR #55): the record cannot see what an OLDER version does to the override. After a
/// downgrade to v1.1.1 (same identity, which ignores the file) an explicit choice that happens to EQUAL the recorded
/// automatic value is indistinguishable from it, so on the re-upgrade it follows a later Windows language change. It needs
/// a sideloaded downgrade plus that exact coincidence; Settings › Language can always set it again.
/// </para>
/// </summary>
public static class LanguagePreferencePolicy
{
    /// <summary>The override the system language needs on <paramref name="systemCulture"/> ("" = none).</summary>
    public static string SystemOverride(string? systemCulture) =>
        SupportedCultures.StartupOverride(null, systemCulture) ?? string.Empty;

    /// <summary>The language the UI shows when it follows <paramref name="systemCulture"/>.</summary>
    public static string EffectiveSystemLanguage(string? systemCulture) => SupportedCultures.Resolve(systemCulture);

    /// <summary>True when the persisted state means "use the system language" (the picker's first item).</summary>
    public static bool FollowsSystem(string? persistedOverride, LanguageRecordRead record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.IsNullOrWhiteSpace(persistedOverride) || IsAppWritten(persistedOverride, record);
    }

    /// <summary>The writes the startup resolution makes (none for a user's choice).</summary>
    public static LanguageStartupPlan AtStartup(string? persistedOverride, string? systemCulture, LanguageRecordRead record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var target = SystemOverride(systemCulture);

        if (string.IsNullOrWhiteSpace(persistedOverride))
        {
            // Nothing chosen: follow the system. An unreadable record is left as it is (nothing at startup rewrites it);
            // the override is then written exactly as REL.0C did, and reads as a choice until the user picks again.
            var systemRecord = record.State == LanguageRecordState.Unreadable ? null : LanguageProvenance.System(target);
            return new LanguageStartupPlan(
                systemRecord == record.Record ? null : systemRecord,
                target.Length == 0 ? null : target);
        }

        if (!IsAppWritten(persistedOverride, record) || Same(persistedOverride, target))
        {
            return LanguageStartupPlan.Nothing;
        }

        // The app's own value for the system language, and Windows changed since: follow it.
        return new LanguageStartupPlan(LanguageProvenance.System(target), target);
    }

    /// <summary>
    /// The language the one-time legacy hint offers (the effective system language), or null when no hint is due.
    /// </summary>
    public static string? LegacyHintTarget(string? persistedOverride, string? systemCulture, LanguageRecordRead record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.State is not (LanguageRecordState.Absent or LanguageRecordState.OtherWriter)
            || !Same(persistedOverride, SupportedCultures.BrazilianPortuguese)
            || SupportedCultures.IsSupported(systemCulture))
        {
            return null;
        }

        var effective = EffectiveSystemLanguage(systemCulture);
        return Same(effective, SupportedCultures.BrazilianPortuguese) ? null : effective;
    }

    private static bool IsAppWritten(string? persistedOverride, LanguageRecordRead record) =>
        record is { State: LanguageRecordState.Valid, Record: { FollowSystem: true, AutoOverride: { } written } }
        && Same(persistedOverride, written);

    private static bool Same(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
