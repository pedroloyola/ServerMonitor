using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// REL.0C: an in-memory <see cref="IUiLanguagePlatform"/> - a fixed Windows UI culture and a language override that
/// records every write - so the real <see cref="LocalizationService"/> runs without touching Windows preferences.
/// REL.0D: an optional shared <paramref name="journal"/> receives "override:&lt;value&gt;" for every write, so a test
/// can prove the order of the override and the provenance record.
/// </summary>
internal sealed class FakeUiLanguagePlatform(string systemCulture, string? persisted = null, List<string>? journal = null) : IUiLanguagePlatform
{
    private string? _override = persisted;

    public List<string?> Writes { get; } = [];

    public string? PrimaryLanguageOverride
    {
        get => _override;
        set
        {
            Writes.Add(value);
            journal?.Add("override:" + value);
            _override = value;
        }
    }

    private string? _displayCulture;

    /// <summary>F-1: the profile language - the app's "system language" (<paramref name="systemCulture"/>).</summary>
    public string SystemUiCultureName { get; set; } = systemCulture;

    /// <summary>
    /// F-1: the Windows display language. Unless a test sets it, it follows <see cref="SystemUiCultureName"/> - the
    /// common machine, where the display language is the first preferred language (and every pre-F-1 test's machine).
    /// </summary>
    public string DisplayCultureName
    {
        get => _displayCulture ?? SystemUiCultureName;
        set => _displayCulture = value;
    }
}
