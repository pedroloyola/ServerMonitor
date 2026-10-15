using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// REL.0D D-2: an <see cref="IUiLanguagePlatform"/> with the Windows App SDK's real semantics, read and written through
/// the production <see cref="LanguageOverrideReader"/>:
/// <list type="bullet">
/// <item>the SDK value lives in the process only; in a packaged process the SDK setter also writes the value Windows
/// persists for the package (<see cref="PersistedOverride"/>, shared by every "process" of one install);</item>
/// <item>PR #59 round 2 (Cortex M-1, fidelity): the SDK setter REJECTS "" like the real one (IsWellFormedTag →
/// E_INVALIDARG → <see cref="ArgumentException"/>);</item>
/// <item>PR #59 round 2 (Vigil L-1): <see cref="FailPersistedRead"/> makes Windows' persisted read throw.</item>
/// </list>
/// A new instance over the same <see cref="PersistedOverride"/> is a relaunch. Never the user's real preferences.
/// </summary>
internal sealed class PackagedUiLanguagePlatform(string systemCulture, PackagedUiLanguagePlatform.PersistedOverride persisted)
    : IUiLanguagePlatform
{
    private string? _processOverride;

    /// <summary>What Windows keeps for the package across processes (Windows.Globalization.ApplicationLanguages).</summary>
    public sealed class PersistedOverride
    {
        public string? Value { get; set; }
    }

    /// <summary>Unpackaged models the Debug process: the SDK setter does not persist, the read is the process value.</summary>
    public PackageIdentity Identity { get; init; } = PackageIdentity.Packaged;

    public bool FailPersistedRead { get; init; }

    public List<string?> Writes { get; } = [];

    public string? PrimaryLanguageOverride
    {
        get => Read().Value;
        set
        {
            Writes.Add(value);
            LanguageOverrideReader.Write(Identity, value, SdkSetter, language => persisted.Value = language);
        }
    }

    public bool IsOverrideKnown => Read().IsKnown;

    /// <summary>F-1 (PR #58): the profile language - the app's "system language".</summary>
    public string SystemUiCultureName => systemCulture;

    /// <summary>F-1 (PR #58): the Windows display language; the same as the profile language unless a test sets it.</summary>
    public string DisplayCultureName { get; init; } = systemCulture;

    private LanguageOverrideRead Read() => LanguageOverrideReader.Read(
        Identity,
        () => _processOverride,
        () => FailPersistedRead ? throw new InvalidOperationException("persisted read failed") : persisted.Value);

    /// <summary>The SDK setter: validates the tag ("" is not one), stores it in the process, forwards it when packaged.</summary>
    private void SdkSetter(string language)
    {
        if (string.IsNullOrEmpty(language))
        {
            throw new ArgumentException("The parameter is incorrect.", nameof(language));
        }

        _processOverride = language;
        if (Identity == PackageIdentity.Packaged)
        {
            persisted.Value = language;
        }
    }
}
