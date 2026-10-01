using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// T-14 (UI.2 S2). The gallery policy is compiled in EVERY configuration, so these run in Debug and in the
/// Release test run: the gallery does not exist in Release, it is exclusive with every other harness, it
/// validates its own options fail-closed, and it never writes its report under the real data folder.
/// </summary>
public sealed class QaGalleryPolicyTests
{
    private static readonly string RealData = Path.Combine(Path.GetTempPath(), "qa-policy-tests", "fake-user-data", "ServerMonitor");
    private static readonly string DefaultOut = Path.Combine(Path.GetTempPath(), "ServerMonitor-QA", "components", "1234");

    private static QaGalleryRequest Resolve(bool isDebugBuild, params string[] args) =>
        QaGalleryPolicy.Resolve(["ServerMonitor.App.exe", .. args], isDebugBuild, RealData, DefaultOut);

    [Theory]
    [InlineData("--qa-components")]
    [InlineData("--qa-tokens")]
    [InlineData("--qa-tokens", "--qa-gallery-theme", "light", "--qa-gallery-page", "colors")]
    public void ReleaseNeverOpensTheGallery(params string[] args)
    {
        Assert.Equal(QaGalleryMode.None, Resolve(false, args).Mode);
    }

    [Fact]
    public void WithoutAGalleryFlagTheProductionCompositionRuns()
    {
        Assert.Equal(QaGalleryMode.None, Resolve(true).Mode);
        Assert.Equal(QaGalleryMode.None, Resolve(true, "--qa-health").Mode);
    }

    [Theory]
    [InlineData("--qa-gallery-theme", "dark")]
    [InlineData("--qa-gallery-page", "tokens")]
    [InlineData("--qa-gallery-out", "C:\\qa")]
    [InlineData("--qa-gallery-theme=light", null)]
    public void AGalleryOptionWithoutTheGalleryIsRefusedNotIgnored(string flag, string? value)
    {
        string[] args = value is null ? [flag] : [flag, value];

        var request = Resolve(true, args);

        Assert.Equal(QaGalleryMode.Refused, request.Mode);
        Assert.Contains(QaGalleryPolicy.ComponentsFlag, request.RefusalReason, StringComparison.Ordinal);
        Assert.Equal(QaGalleryMode.None, Resolve(false, args).Mode);
    }

    [Fact]
    public void ComponentsFlagOpensTheGalleryWithDefaults()
    {
        var request = Resolve(true, "--qa-components");

        Assert.Equal(QaGalleryMode.Components, request.Mode);
        Assert.True(request.IsGallery);
        Assert.Equal("dark", request.Theme);
        Assert.Equal("tokens", request.Page);
        Assert.Equal(Path.GetFullPath(DefaultOut), request.OutputDirectory);
    }

    [Fact]
    public void TokensFlagRunsTheSelfCheckEvenWithComponents()
    {
        Assert.Equal(QaGalleryMode.Tokens, Resolve(true, "--qa-tokens").Mode);
        Assert.Equal(QaGalleryMode.Tokens, Resolve(true, "--qa-components", "--qa-tokens").Mode);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("hc-sim")]
    [InlineData("HC-SIM")]
    public void EveryDocumentedThemeIsAccepted(string theme)
    {
        Assert.Equal(theme.ToLowerInvariant(), Resolve(true, "--qa-components", "--qa-gallery-theme", theme).Theme);
        Assert.Equal(theme.ToLowerInvariant(), Resolve(true, "--qa-components", $"--qa-gallery-theme={theme}").Theme);
    }

    [Fact]
    public void EveryDocumentedPageIsAccepted()
    {
        Assert.All(QaGalleryPolicy.Pages, page =>
            Assert.Equal(page, Resolve(true, "--qa-components", "--qa-gallery-page", page).Page));
    }

    [Theory]
    [InlineData("--qa-health")]
    [InlineData("--qa-proxyjump")]
    [InlineData("--qa-backup=create")]
    [InlineData("--qa-ssh-config")]
    [InlineData("--qa-compact")]
    [InlineData("--qa-store-screenshot")]
    public void CombiningWithAnotherHarnessIsRefused(string other)
    {
        var request = Resolve(true, "--qa-components", other);

        Assert.Equal(QaGalleryMode.Refused, request.Mode);
        Assert.False(request.IsGallery);
        Assert.Contains("exclusive", request.RefusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUiLanguageHarnessMayAccompanyTheGallery()
    {
        Assert.Equal(QaGalleryMode.Components, Resolve(true, "--qa-components", "--qa-ui-language", "pt-PT").Mode);
    }

    [Theory]
    [InlineData("--qa-gallery-theme", "high-contrast")]
    [InlineData("--qa-gallery-theme", null)]
    [InlineData("--qa-gallery-page", "no-such-page")]
    [InlineData("--qa-gallery-page", null)]
    [InlineData("--qa-gallery-out", null)]
    public void UnknownOrMissingOptionValuesAreRefused(string flag, string? value)
    {
        string[] args = value is null ? ["--qa-components", flag] : ["--qa-components", flag, value];

        Assert.Equal(QaGalleryMode.Refused, Resolve(true, args).Mode);
    }

    [Theory]
    [InlineData(@"\\localhost\C$\qa")]
    [InlineData(@"\\.\C:\qa")]
    [InlineData(@"\\?\C:\qa")]
    [InlineData("//server/share/qa")]
    public void UncAndDevicePathReportFoldersAreRefused(string folder)
    {
        Assert.Equal(QaGalleryMode.Refused, Resolve(true, "--qa-tokens", "--qa-gallery-out", folder).Mode);
    }

    [Fact]
    public void ReportUnderTheRealDataFolderIsRefused()
    {
        Assert.Equal(QaGalleryMode.Refused, Resolve(true, "--qa-tokens", "--qa-gallery-out", RealData).Mode);
        Assert.Equal(QaGalleryMode.Refused, Resolve(true, "--qa-tokens", "--qa-gallery-out", Path.Combine(RealData, "qa")).Mode);
        Assert.Equal(QaGalleryMode.Refused, Resolve(true, "--qa-tokens", "--qa-gallery-out", RealData.ToUpperInvariant() + Path.DirectorySeparatorChar).Mode);
    }

    [Fact]
    public void ASiblingOfTheRealDataFolderIsNotMistakenForIt()
    {
        var sibling = RealData + "-QA";

        var request = Resolve(true, "--qa-tokens", "--qa-gallery-out", sibling);

        Assert.Equal(QaGalleryMode.Tokens, request.Mode);
        Assert.Equal(Path.GetFullPath(sibling), request.OutputDirectory);
    }

    [Fact]
    public void GalleryBypassesSingleInstancingInDebugOnlyLikeEveryHarness()
    {
        Assert.Null(SingleInstancePolicy.ResolveInstanceKey(["ServerMonitor.App.exe", QaGalleryPolicy.ComponentsFlag], isDebugBuild: true));
        Assert.Null(SingleInstancePolicy.ResolveInstanceKey(["ServerMonitor.App.exe", QaGalleryPolicy.TokensFlag], isDebugBuild: true));
        Assert.NotNull(SingleInstancePolicy.ResolveInstanceKey(["ServerMonitor.App.exe", QaGalleryPolicy.ComponentsFlag], isDebugBuild: false));
    }
}
