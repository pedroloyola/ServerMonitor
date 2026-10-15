using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D D-2 root cause: packaged, the override in effect is the one Windows persisted for the package (the Windows App
/// SDK getter alone is process-local); unpackaged, the SDK's process value as before. PR #59 round 2: a failed read is
/// NOT KNOWN (Vigil L-1), and "" clears in Windows, never through the SDK setter that rejects it (Cortex M-1).
/// Never the user's real preferences.
/// </summary>
public sealed class LanguageOverrideReaderTests
{
    [Theory]
    [InlineData("", "pt-PT", "pt-PT")]      // a new process of the installed app: the persisted choice (the VM)
    [InlineData(null, "pt-PT", "pt-PT")]
    [InlineData("pt-PT", "pt-PT", "pt-PT")] // set in this process: the SDK setter persisted it too
    [InlineData("", "", "")]                // "Use system language" / nothing chosen
    [InlineData(null, null, null)]
    public void Packaged_ReadsThePersistedValue(string? process, string? persisted, string? expected) =>
        Assert.Equal(
            new LanguageOverrideRead(expected, IsKnown: true),
            LanguageOverrideReader.Read(PackageIdentity.Packaged, () => process, () => persisted));

    /// <summary>Control (Cortex test 4): unpackaged keeps today's process read - Windows has nothing for it to read.</summary>
    [Theory]
    [InlineData("pt-PT", "en-US", "pt-PT")]
    [InlineData("", "pt-PT", "")]
    [InlineData(null, "pt-PT", null)]
    public void Unpackaged_ReadsTheProcessValue(string? process, string? persisted, string? expected) =>
        Assert.Equal(
            new LanguageOverrideRead(expected, IsKnown: true),
            LanguageOverrideReader.Read(PackageIdentity.Unpackaged, () => process, () => persisted));

    [Fact]
    public void Unpackaged_NeverTouchesThePersistedSource() =>
        Assert.Equal("en-US", LanguageOverrideReader.Read(PackageIdentity.Unpackaged, () => "en-US", () => throw new InvalidOperationException()).Value);

    /// <summary>Vigil L-1: a failed persisted read, or an undetermined identity, is NOT KNOWN; the value is a display fallback.</summary>
    [Fact]
    public void AFailedRead_OrAnUnknownIdentity_IsNotKnown()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException(), new System.Runtime.InteropServices.COMException(), new NotSupportedException() })
        {
            Assert.Equal(
                new LanguageOverrideRead("pt-PT", IsKnown: false),
                LanguageOverrideReader.Read(PackageIdentity.Packaged, () => "pt-PT", () => throw failure));
        }

        Assert.Equal(
            new LanguageOverrideRead("", IsKnown: false),
            LanguageOverrideReader.Read(PackageIdentity.Unknown, () => "", () => "pt-PT"));
    }

    // ---- write (Cortex M-1) ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Packaged")]
    [InlineData("Unpackaged")]
    [InlineData("Unknown")]
    public void ALanguage_IsWrittenThroughTheSdk(string identity)
    {
        var sdk = new List<string>();
        var persisted = new List<string>();

        LanguageOverrideReader.Write(Enum.Parse<PackageIdentity>(identity), "pt-PT", sdk.Add, persisted.Add);

        Assert.Equal(["pt-PT"], sdk);
        Assert.Empty(persisted); // the SDK forwards it itself when packaged
    }

    /// <summary>"" is never given to the SDK setter (it throws E_INVALIDARG): packaged it clears Windows' value directly.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clearing_ResetsWindowsWhenPackaged_AndNeverCallsTheSdk(string? language)
    {
        var sdk = new List<string>();
        var persisted = new List<string>();

        LanguageOverrideReader.Write(PackageIdentity.Packaged, language, sdk.Add, persisted.Add);
        Assert.Empty(sdk);
        Assert.Equal([string.Empty], persisted);

        persisted.Clear();
        LanguageOverrideReader.Write(PackageIdentity.Unpackaged, language, sdk.Add, persisted.Add);
        LanguageOverrideReader.Write(PackageIdentity.Unknown, language, sdk.Add, persisted.Add);
        Assert.Empty(sdk);
        Assert.Empty(persisted); // nothing persisted to clear (unpackaged), nothing trusted (unknown)
    }

    /// <summary>Production: both platforms read and write through the reader; the reader wires the real sources.</summary>
    [Fact]
    public void ThePlatforms_ReadAndWriteThroughTheReader()
    {
        var reader = AppSourceTree.CodeWithoutComments("Services/LanguageOverrideReader.cs");
        Assert.Contains("static () => Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride", reader, StringComparison.Ordinal);
        Assert.Contains("static () => global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride", reader, StringComparison.Ordinal);
        Assert.Contains("static value => Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = value", reader, StringComparison.Ordinal);
        Assert.Contains("static value => global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = value", reader, StringComparison.Ordinal);
        Assert.Contains("GetCurrentPackageFullName(ref length, null)", reader, StringComparison.Ordinal);

        foreach (var file in new[] { "Services/LocalizationService.cs", "Qa/QaSystemCultureComposition.cs" })
        {
            var code = AppSourceTree.CodeWithoutComments(file);
            Assert.Contains("get => LanguageOverrideReader.ReadCurrent().Value;", code, StringComparison.Ordinal);
            Assert.Contains("set => LanguageOverrideReader.WriteCurrent(value);", code, StringComparison.Ordinal);
            Assert.Contains("public bool IsOverrideKnown => LanguageOverrideReader.ReadCurrent().IsKnown;", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ApplicationLanguages.PrimaryLanguageOverride", code, StringComparison.Ordinal);
        }
    }
}
