namespace ServerMonitor.App.Services;

/// <summary>REL.0D: where the language provenance record lives (next to the other small settings files).</summary>
public sealed record LanguageSettingsStorageOptions
{
    public required string FilePath { get; init; }

    /// <summary>
    /// PR #55 fix round 1 (Cortex N-1): who writes the record - the package family of this install, or "unpackaged".
    /// A record written by another identity sharing the same real folder (Store / Developer package / dev run) is never
    /// taken as proof about this identity's override.
    /// </summary>
    public string Writer { get; init; } = LanguageSettingsWriter.Current;

    public static LanguageSettingsStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new LanguageSettingsStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "language-settings.json")
        };
    }
}

/// <summary>The identity of the running app for the language record: <c>package:&lt;family name&gt;</c> or <c>unpackaged</c>.</summary>
internal static class LanguageSettingsWriter
{
    internal const string Unpackaged = "unpackaged";

    private static readonly Lazy<string> CurrentIdentity = new(() => Resolve(ReadPackageFamilyName));

    public static string Current => CurrentIdentity.Value;

    /// <summary>Package.Current throws when unpackaged; any failure means "unpackaged" (never a crash, never empty).</summary>
    internal static string Resolve(Func<string?> packageFamilyName)
    {
        try
        {
            var family = packageFamilyName();
            return string.IsNullOrWhiteSpace(family) ? Unpackaged : "package:" + family;
        }
        catch
        {
            return Unpackaged;
        }
    }

    private static string? ReadPackageFamilyName() =>
        global::Windows.ApplicationModel.Package.Current.Id.FamilyName;
}
