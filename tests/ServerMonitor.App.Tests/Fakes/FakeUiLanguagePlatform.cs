using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// REL.0C: an in-memory <see cref="IUiLanguagePlatform"/> - a fixed Windows UI culture and a language override that
/// records every write - so the real <see cref="LocalizationService"/> runs without touching Windows preferences.
/// </summary>
internal sealed class FakeUiLanguagePlatform(string systemCulture, string? persisted = null) : IUiLanguagePlatform
{
    private string? _override = persisted;

    public List<string?> Writes { get; } = [];

    public string? PrimaryLanguageOverride
    {
        get => _override;
        set
        {
            Writes.Add(value);
            _override = value;
        }
    }

    public string SystemUiCultureName => systemCulture;
}
