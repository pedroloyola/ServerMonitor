using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D D-2 root cause: packaged, the override in effect is the one Windows persisted for the package (the Windows App
/// SDK getter alone is process-local); unpackaged, the SDK's process value as before. Never the user's real preferences.
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
        Assert.Equal(expected, LanguageOverrideReader.Read(isPackaged: true, () => process, () => persisted));

    /// <summary>Control (Cortex test 4): unpackaged keeps today's process read - Windows has nothing for it to read.</summary>
    [Theory]
    [InlineData("pt-PT", "en-US", "pt-PT")]
    [InlineData("", "pt-PT", "")]
    [InlineData(null, "pt-PT", null)]
    public void Unpackaged_ReadsTheProcessValue(string? process, string? persisted, string? expected) =>
        Assert.Equal(expected, LanguageOverrideReader.Read(isPackaged: false, () => process, () => persisted));

    [Fact]
    public void Unpackaged_NeverTouchesThePersistedSource() =>
        Assert.Equal("en-US", LanguageOverrideReader.Read(isPackaged: false, () => "en-US", () => throw new InvalidOperationException()));

    [Fact]
    public void Packaged_AFailedPersistedRead_FallsBackToTheProcessValue()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException(), new System.Runtime.InteropServices.COMException(), new NotSupportedException() })
        {
            Assert.Equal("pt-PT", LanguageOverrideReader.Read(isPackaged: true, () => "pt-PT", () => throw failure));
        }
    }

    /// <summary>Production: the persisted value when packaged, else the SDK's - for the app and the QA harness alike.</summary>
    [Fact]
    public void ThePlatforms_ReadThroughTheReader()
    {
        var reader = AppSourceTree.CodeWithoutComments("Services/LanguageOverrideReader.cs");
        Assert.Contains("static () => Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride", reader, StringComparison.Ordinal);
        Assert.Contains("static () => global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride", reader, StringComparison.Ordinal);
        Assert.Contains("global::Windows.ApplicationModel.Package.Current.Id", reader, StringComparison.Ordinal);

        foreach (var file in new[] { "Services/LocalizationService.cs", "Qa/QaSystemCultureComposition.cs" })
        {
            var code = AppSourceTree.CodeWithoutComments(file);
            Assert.Contains("get => LanguageOverrideReader.ReadCurrent();", code, StringComparison.Ordinal);
            Assert.DoesNotContain("get => ApplicationLanguages.PrimaryLanguageOverride", code, StringComparison.Ordinal);
        }
    }
}
