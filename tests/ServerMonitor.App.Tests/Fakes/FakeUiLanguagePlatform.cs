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

    public string SystemUiCultureName { get; set; } = systemCulture;
}
