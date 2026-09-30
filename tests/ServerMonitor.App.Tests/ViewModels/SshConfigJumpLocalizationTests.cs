using System.Xml.Linq;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.4c: every reason a host is not imported has its own, non-empty string in every supported culture
/// (the option row reads <c>SshConfigHostBlocked{Blocker}</c>), and the retired "ProxyJump not supported"
/// string is gone now that a single hop is imported.
/// </summary>
public sealed class SshConfigJumpLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    public static TheoryData<string> Blockers()
    {
        var data = new TheoryData<string>();
        foreach (var blocker in Enum.GetValues<SshConfigHostBlocker>().Where(b => b != SshConfigHostBlocker.None))
        {
            data.Add(blocker.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Blockers))]
    public void EveryBlocker_HasADistinctNonEmptyReasonInEveryCulture(string blocker)
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            var key = "SshConfigHostBlocked" + blocker;

            Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
            Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            Assert.Single(
                resources,
                pair => pair.Key.StartsWith("SshConfigHostBlocked", StringComparison.Ordinal) && pair.Value == value);
        }
    }

    [Fact]
    public void JumpPreviewAndNotes_ArePresentWithTheirPlaceholders_AndTheOldBlockerIsRetired()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);

            Assert.Contains("{0}", resources["SshConfigPreviewJumpFormat"]);
            Assert.Contains("{0}", resources["SshConfigHostJumpClassificationFormat"]);
            Assert.Contains("{1}", resources["SshConfigHostJumpClassificationFormat"]);
            Assert.False(resources.ContainsKey("SshConfigHostBlockedProxyJump"), $"{culture} still has the retired ProxyJump blocker.");
        }
    }

    private static IReadOnlyDictionary<string, string> LoadResources(string culture)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ServerMonitor.App",
            "Resources",
            culture,
            "Resources.resw");
        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ServerMonitor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
