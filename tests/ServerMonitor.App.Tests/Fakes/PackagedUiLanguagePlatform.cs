using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// REL.0D D-2: an <see cref="IUiLanguagePlatform"/> with the Windows App SDK's real semantics in a PACKAGED process -
/// the SDK value lives in the process only, its setter also writes the value Windows persists for the package
/// (<see cref="PersistedOverride"/>, shared by every "process" of one install) - read through the production
/// <see cref="LanguageOverrideReader"/>. A new instance over the same <see cref="PersistedOverride"/> is a relaunch.
/// Never the user's real preferences.
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

    /// <summary>False models the unpackaged Debug process: the setter does not persist, the read is the process value.</summary>
    public bool IsPackaged { get; init; } = true;

    public List<string?> Writes { get; } = [];

    public string? PrimaryLanguageOverride
    {
        get => LanguageOverrideReader.Read(IsPackaged, () => _processOverride, () => persisted.Value);
        set
        {
            Writes.Add(value);
            _processOverride = value;
            if (IsPackaged)
            {
                persisted.Value = value; // the SDK setter forwards to Windows only in a packaged process
            }
        }
    }

    public string SystemUiCultureName => systemCulture;
}
