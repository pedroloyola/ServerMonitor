using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// T-18 (UI.2 S5, Boss decision B-1). The icon set is closed, licensed and traceable: exactly the 22 decided
/// Hugeicons Free icons, vendored as path data with a manifest entry each (package, version, integrity, MIT), the
/// licence text in THIRD-PARTY-NOTICES.md, and no icon outside the manifest.
/// </summary>
public sealed partial class IconResourceGuardTests
{
    private const string IconsXaml = "Styles/Components/Sa.Icons.xaml";
    private const string Manifest = "Styles/Components/Icons.manifest.json";

    /// <summary>Boss B-1, verbatim.</summary>
    private static readonly string[] DecidedIcons =
    [
        "ServerStack01", "DashboardSquare01", "Clock01", "Settings01", "Refresh", "Add01", "Search01", "ArrowRight01",
        "ArrowDown01", "HardDrive", "Key01", "LockPassword", "Folder01", "Shield01", "View", "InformationCircle",
        "Alert02", "Computer", "Sun03", "Moon02", "Cancel01", "Tick02"
    ];

    [Fact]
    public void IconResourcesAreLicensedAndComplete()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(AppSourceTree.Full(Manifest)));
        var root = manifest.RootElement;
        Assert.Equal("@hugeicons/core-free-icons", root.GetProperty("package").GetString());
        Assert.Equal("4.3.5", root.GetProperty("version").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());
        Assert.Equal(
            "sha512-Sv+NjHRPnQk+yZsGCMcGznCHdTfJR9PMMOEKcJYAQU4gy90dmlc9PwdtvYOImBZIjiheMsTLL5eMn2DmHxUiUg==",
            root.GetProperty("integrity").GetString());
        Assert.Equal(1.5, root.GetProperty("strokeWidth").GetDouble());

        var entries = root.GetProperty("icons").EnumerateArray()
            .Select(icon => (Name: icon.GetProperty("name").GetString()!, Key: icon.GetProperty("key").GetString()!,
                Source: icon.GetProperty("source").GetString()!))
            .ToList();
        Assert.Equal(DecidedIcons.Order(StringComparer.Ordinal), entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.All(entries, e =>
        {
            Assert.Equal($"SaIcon{e.Name}Data", e.Key);
            Assert.Equal($"dist/esm/{e.Name}Icon.js", e.Source);
        });

        var document = AppSourceTree.LoadXaml(IconsXaml);
        var resources = document.Root!.Elements().ToList();
        Assert.All(resources, element => Assert.Equal("String", element.Name.LocalName));
        var keys = resources.Select(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!).ToList();
        Assert.Equal(entries.Select(e => e.Key).Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
        Assert.All(resources, element => Assert.Matches(PathData(), element.Value));

        var notices = File.ReadAllText(Path.Combine(AppSourceTree.AppRoot, "..", "..", "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("@hugeicons/core-free-icons` 4.3.5", notices, StringComparison.Ordinal);
        var licence = notices[notices.IndexOf("## Hugeicons Free - MIT License", StringComparison.Ordinal)..];
        Assert.Contains("Copyright (c) 2025 Hugeicons", licence, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", licence, StringComparison.Ordinal);
    }

    /// <summary>No icon data outside the vendored dictionary: a SaIcon*Data key defined anywhere else would bypass the manifest.</summary>
    [Fact]
    public void IconDataIsDefinedOnlyInTheVendoredDictionary()
    {
        var strays = AppSourceTree.Files(".xaml").Where(file => file != IconsXaml)
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants().Attributes(AppSourceTree.Xaml + "Key")
                .Where(key => key.Value.StartsWith("SaIcon", StringComparison.Ordinal) && key.Value.EndsWith("Data", StringComparison.Ordinal))
                .Select(key => $"{file}: {key.Value}"))
            .ToList();
        Assert.Empty(strays);
    }

    /// <summary>B-1: the geometry is scaled (Size/24), never the stroke.</summary>
    [Theory]
    [InlineData(16, 16.0 / 24)]
    [InlineData(24, 1.0)]
    [InlineData(48, 2.0)]
    public void IconScaleIsSizeOverTheDesignGridAndTheStrokeIsConstant(double size, double scale)
    {
        Assert.Equal(scale, SaIcon.ScaleFor(size), 6);
        Assert.Equal(1.5, SaIcon.StrokeWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => SaIcon.ScaleFor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaIcon.ScaleFor(double.NaN));
    }

    /// <summary>The template draws the stroke at exactly the constant width, never a Viewbox.</summary>
    [Fact]
    public void IconTemplateStrokesAtTheConstantWidthWithoutAViewbox()
    {
        var style = AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "primitives:SaIcon");
        var path = style.Descendants().Single(e => e.Name.LocalName == "Path");
        Assert.Equal("1.5", (string?)path.Attribute("StrokeThickness"));
        Assert.Null(path.Attribute("Fill"));
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName is "Viewbox" or "ScaleTransform");
    }

    [GeneratedRegex(@"\A[MmLlHhVvCcSsQqTtAaZz0-9 .,\-eE]+\z")]
    private static partial Regex PathData();
}
