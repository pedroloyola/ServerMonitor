using System.Xml.Linq;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// The editor resolves <c>ConnectionError{code}</c> by name, so every <see cref="SshConnectionErrorCode"/>
/// (including the M14.4b jump codes) needs a non-empty string in every shipped culture.
/// </summary>
public sealed class ConnectionErrorLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    [Fact]
    public void EveryConnectionErrorCode_HasAStringInEverySupportedCulture()
    {
        // Target host-key codes render through ConnectionState{State} (the trust panels), not ConnectionError.
        // The jump host-key codes deliberately map to the Error state, so they DO need a ConnectionError string.
        var codes = Enum.GetValues<SshConnectionErrorCode>()
            .Where(code => code is not (SshConnectionErrorCode.None
                or SshConnectionErrorCode.HostKeyUnknown
                or SshConnectionErrorCode.HostKeyMismatch))
            .ToArray();
        Assert.Contains(SshConnectionErrorCode.TargetUnreachableViaJump, codes);

        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var code in codes)
            {
                var key = $"ConnectionError{code}";
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
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
