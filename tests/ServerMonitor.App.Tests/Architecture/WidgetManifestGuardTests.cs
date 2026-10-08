using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.9 C2 — the widget registration in BOTH package manifests.
/// <list type="bullet">
/// <item>Frozen identifiers never move: CLSID, AppExtension Id, Definition Id, protocol, sizes,
/// AllowMultiple, no IsCustomizable. Changing them orphans pinned widgets.</item>
/// <item>The user-visible widget strings are either the brand "ServerAlyzer" or an
/// <c>ms-resource:</c> key that exists, non-empty, in all three Resources.resw files. No hard-coded
/// Portuguese is left in the widget registration.</item>
/// <item>The picker screenshots (neutral root + pt-pt + pt-br, dark + light) are 300×304 RGBA PNGs
/// with transparent rounded corners and an opaque body.</item>
/// <item>The project packs every Public subfolder (<c>Public\**</c>).</item>
/// </list>
/// The packaged-build PRI check (keys and locale candidates) is recorded in the C2 report; whether the
/// Widgets host resolves <c>ms-resource:</c> inside <c>uap3:Properties</c> is C0-online.
/// </summary>
public sealed class WidgetManifestGuardTests
{
    private const string WidgetClsid = "78CFFBEF-7A95-4400-BB8B-A2376C6642C3";
    private static readonly string[] Manifests = ["Package.appxmanifest", "Package.Dev.appxmanifest"];
    private static readonly string[] Languages = ["en-US", "pt-PT", "pt-BR"];

    private static string AppDirectory => Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.App");

    private static XElement Definition(XDocument manifest) =>
        manifest.Descendants().Single(e => e.Name.LocalName == "Definition" && (string?)e.Attribute("Id") == "ServerAlyzer_Widget");

    public static TheoryData<string> ManifestNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Manifests)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ManifestNames))]
    public void Frozen_widget_identifiers_are_unchanged(string manifestName)
    {
        var manifest = XDocument.Load(Path.Combine(AppDirectory, manifestName));
        var all = manifest.Descendants().ToList();

        var exeServer = all.Single(e => e.Name.LocalName == "ExeServer" && (string?)e.Attribute("Executable") == "ServerAlyzer.WidgetProvider.exe");
        Assert.Equal(WidgetClsid, (string?)exeServer.Elements().Single(e => e.Name.LocalName == "Class").Attribute("Id"));

        var extension = all.Single(e => e.Name.LocalName == "AppExtension" && (string?)e.Attribute("Name") == "com.microsoft.windows.widgets");
        Assert.Equal("ServerAlyzerWidgetProvider", (string?)extension.Attribute("Id"));
        Assert.Equal("Public", (string?)extension.Attribute("PublicFolder"));
        Assert.Equal(WidgetClsid, (string?)all.Single(e => e.Name.LocalName == "CreateInstance").Attribute("ClassId"));

        var definition = Definition(manifest);
        Assert.Equal("true", (string?)definition.Attribute("AllowMultiple"));
        Assert.Null(definition.Attribute("IsCustomizable"));
        Assert.Equal(["small", "medium", "large"],
            definition.Descendants().Where(e => e.Name.LocalName == "Size").Select(e => (string?)e.Attribute("Name")));

        Assert.Contains(all, e => e.Name.LocalName == "Protocol" && (string?)e.Attribute("Name") == "serveralyzer");
    }

    [Theory]
    [MemberData(nameof(ManifestNames))]
    public void Widget_strings_are_the_brand_or_an_ms_resource_key_present_in_every_language(string manifestName)
    {
        var definition = Definition(XDocument.Load(Path.Combine(AppDirectory, manifestName)));
        Assert.Equal("ServerAlyzer", (string?)definition.Attribute("DisplayName"));

        var localized = new List<string> { (string)definition.Attribute("Description")! };
        localized.AddRange(definition.Descendants().Where(e => e.Name.LocalName == "Screenshot")
            .Select(e => (string)e.Attribute("DisplayAltText")!));
        Assert.Equal(4, localized.Count); // Description + fallback, dark and light screenshots

        var resources = Languages.ToDictionary(l => l, l => XDocument.Load(Path.Combine(AppDirectory, "Resources", l, "Resources.resw")));
        foreach (var value in localized)
        {
            Assert.StartsWith("ms-resource:", value, StringComparison.Ordinal);
            var key = value["ms-resource:".Length..];
            foreach (var (language, resw) in resources)
            {
                var entry = resw.Descendants("data").SingleOrDefault(d => (string?)d.Attribute("name") == key);
                Assert.True(entry is not null, $"{language} has no resw key '{key}'");
                Assert.False(string.IsNullOrWhiteSpace((string?)entry!.Element("value")), $"{language} '{key}' is empty");
            }
        }

        // The three languages really differ (no copy-pasted English).
        Assert.Equal(3, Languages.Select(l => (string?)resources[l].Descendants("data")
            .Single(d => (string?)d.Attribute("name") == "WidgetDescription").Element("value")).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(ManifestNames))]
    public void No_hard_coded_portuguese_is_left_in_the_widget_registration(string manifestName)
    {
        var extension = XDocument.Load(Path.Combine(AppDirectory, manifestName)).Descendants()
            .Single(e => e.Name.LocalName == "AppExtension" && (string?)e.Attribute("Name") == "com.microsoft.windows.widgets");
        var values = extension.DescendantsAndSelf().SelectMany(e => e.Attributes()).Select(a => a.Value).ToList();

        Assert.NotEmpty(values);
        foreach (var marker in new[] { "ã", "ç", "á", "é", "ú", "servidores", "saúde", "Widget do" })
        {
            Assert.DoesNotContain(values, v => v.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static TheoryData<string> Screenshots()
    {
        var data = new TheoryData<string>();
        foreach (var folder in new[] { "", "pt-pt", "pt-br" })
        {
            foreach (var file in new[] { "ServerAlyzerWidgetScreenshot.png", "ServerAlyzerWidgetScreenshotLight.png" })
            {
                data.Add(Path.Combine("Public", folder, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Screenshots))]
    public void Picker_screenshots_are_300x304_rgba_with_transparent_rounded_corners(string relative)
    {
        var path = Path.Combine(AppDirectory, relative);
        Assert.True(File.Exists(path), relative);

        var png = Png.Read(path);
        Assert.Equal((300, 304), (png.Width, png.Height));
        foreach (var (x, y) in new[] { (0, 0), (299, 0), (0, 303), (299, 303) })
        {
            Assert.Equal(0, png.Alpha(x, y)); // transparent rounded corner
        }

        foreach (var (x, y) in new[] { (150, 152), (20, 20), (280, 284) })
        {
            Assert.Equal(255, png.Alpha(x, y)); // opaque card body
        }
    }

    [Fact]
    public void The_project_packs_every_public_subfolder()
    {
        var project = XDocument.Load(Path.Combine(AppDirectory, "ServerMonitor.App.csproj"));
        Assert.Contains(project.Descendants().Where(e => e.Name.LocalName == "Content"),
            e => (string?)e.Attribute("Include") == @"Public\**");
    }

    /// <summary>A minimal PNG reader for 8-bit RGBA, non-interlaced images: enough to read alpha.</summary>
    private sealed class Png
    {
        private readonly byte[] _pixels;

        private Png(int width, int height, byte[] pixels) => (Width, Height, _pixels) = (width, height, pixels);

        public int Width { get; }
        public int Height { get; }

        public int Alpha(int x, int y) => _pixels[(y * Width + x) * 4 + 3];

        public static Png Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
            int width = 0, height = 0;
            using var idat = new MemoryStream();
            for (var offset = 8; offset < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
                var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
                var data = bytes.AsSpan(offset + 8, length);
                if (type == "IHDR")
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    Assert.Equal(8, data[8]);  // bit depth
                    Assert.Equal(6, data[9]);  // colour type RGBA
                    Assert.Equal(0, data[12]); // not interlaced
                }
                else if (type == "IDAT")
                {
                    idat.Write(data);
                }

                offset += 12 + length;
            }

            idat.Position = 0;
            using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            inflater.CopyTo(raw);
            return new Png(width, height, Unfilter(raw.ToArray(), width, height));
        }

        private static byte[] Unfilter(byte[] data, int width, int height)
        {
            const int bpp = 4;
            var stride = width * bpp;
            var output = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                var filter = data[y * (stride + 1)];
                for (var i = 0; i < stride; i++)
                {
                    var raw = data[y * (stride + 1) + 1 + i];
                    int a = i >= bpp ? output[y * stride + i - bpp] : 0;
                    int b = y > 0 ? output[(y - 1) * stride + i] : 0;
                    int c = i >= bpp && y > 0 ? output[(y - 1) * stride + i - bpp] : 0;
                    var predicted = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException($"PNG filter {filter}")
                    };
                    output[y * stride + i] = (byte)(raw + predicted);
                }
            }

            return output;
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
