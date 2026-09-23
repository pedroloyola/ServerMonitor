using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The --qa-ui-language guard. Compiled and run in every configuration, so the Release run proves the
/// flag is ignored there and the harness type is not even compiled in.
/// </summary>
public sealed class QaUiLanguagePolicyTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Release_IgnoresTheFlag(string language)
    {
        Assert.Null(QaUiLanguagePolicy.ResolveLanguage(["app.exe", "--qa-ui-language", language], isDebugBuild: false));
        Assert.Null(QaUiLanguagePolicy.ResolveLanguage(["app.exe", "--qa-ui-language=" + language], isDebugBuild: false));
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("EN-us", "en-US")]
    public void Debug_ResolvesTheCanonicalSupportedTag(string value, string expected)
    {
        Assert.Equal(expected, QaUiLanguagePolicy.ResolveLanguage(["app.exe", "--qa-ui-language", value], isDebugBuild: true));
        Assert.Equal(expected, QaUiLanguagePolicy.ResolveLanguage(["app.exe", "--QA-UI-LANGUAGE=" + value], isDebugBuild: true));
    }

    public static TheoryData<string[]> IgnoredArguments { get; } = new()
    {
        new[] { "app.exe" },
        new[] { "app.exe", "--qa-ui-language" },
        new[] { "app.exe", "--qa-ui-language", "" },
        new[] { "app.exe", "--qa-ui-language", "fr-FR" },
        new[] { "app.exe", "--qa-ui-language", "en" },
        new[] { "app.exe", "--qa-ui-language", "pt" },
        new[] { "app.exe", "--qa-ui-language=" },
        new[] { "app.exe", "--qa-ui-languages", "en-US" }
    };

    [Theory]
    [MemberData(nameof(IgnoredArguments))]
    public void Debug_AnyOtherValue_IsIgnored_NeverAFallbackLanguage(string[] args)
    {
        Assert.Null(QaUiLanguagePolicy.ResolveLanguage(args, isDebugBuild: true));
    }

    [Fact]
    public void Harness_IsCompiledOnlyInDebug()
    {
        var harness = typeof(App).Assembly.GetType("ServerMonitor.App.Qa.QaUiLanguageComposition");
#if DEBUG
        Assert.NotNull(harness);
#else
        Assert.Null(harness);
#endif
    }
}
