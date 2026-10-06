using System.Xml.Linq;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.7B: the real .resw of one culture as name → value (copy assertions on the shipped text).</summary>
internal static class Ui7Resources
{
    public static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);
}
