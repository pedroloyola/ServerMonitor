using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// The foreground boundary (M13-QA-10). Two rules, both enforced over the source so a regression cannot
/// be argued about:
/// <list type="number">
/// <item><b>One authoritative handle.</b> Only the component that OWNS the window may ask for foreground,
/// using that window's own handle. Guessing the target HWND from outside the process — the
/// <c>Process.MainWindowHandle</c> the redirect used to use — is measurably wrong on the shipping build:
/// the app has several top-level windows of the same class, and the property returns the first VISIBLE
/// unowned one, which is zero while the window sits in the tray.</item>
/// <item><b>No z-order contortions.</b> The QA-10 investigation established that the Widgets board is a
/// <c>WS_EX_TOPMOST</c> window, so a non-topmost window cannot out-rank it however hard it asks. Every
/// technique that tries anyway is off the table by explicit human decision, and this test is the closed
/// list, so a future "just make it topmost for a moment" cannot land quietly.</item>
/// </list>
/// </summary>
public sealed class ActivationForegroundBoundaryTests
{
    /// <summary>The one file allowed to ask for foreground: it owns the window and holds its handle.</summary>
    private const string WindowOwner = "ApplicationWindowController.cs";

    /// <summary>
    /// Closed list from the human's QA-10 decision. Anything here is a z-order/input contortion, not a
    /// supported activation path; <c>CoAllowSetForegroundWindow</c> is included because on its own it
    /// cannot lift a non-topmost window above a still-visible topmost board, so it must not be added as a
    /// presumed fix without separate evidence that it changes the board's lifecycle.
    /// </summary>
    private static readonly string[] ForbiddenForegroundTechniques =
    [
        "HWND_TOPMOST",
        "SetWindowPos",
        "AttachThreadInput",
        "CoAllowSetForegroundWindow",
        "AllowSetForegroundWindow",
        "keybd_event",
        "mouse_event",
        "SendInput",
        "SendKeys",
        "BringWindowToTop",
        "LockSetForegroundWindow",
        "SwitchToThisWindow"
    ];

    [Fact]
    public void The_app_never_guesses_a_window_handle_from_outside_the_process()
    {
        var offenders = AppSourceFiles()
            .Where(path => StripComments(File.ReadAllText(path))
                .Contains("MainWindowHandle", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_window_owner_asks_for_foreground()
    {
        var offenders = AppSourceFiles()
            .Where(path => !Path.GetFileName(path).Equals(WindowOwner, StringComparison.OrdinalIgnoreCase))
            .Where(path => StripComments(File.ReadAllText(path))
                .Contains("SetForegroundWindow", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Neither_the_app_nor_the_widget_provider_contorts_z_order_or_synthesizes_input()
    {
        var offenders = new List<string>();
        foreach (var path in AppSourceFiles().Concat(WidgetProviderSourceFiles()))
        {
            var source = StripComments(File.ReadAllText(path));
            foreach (var technique in ForbiddenForegroundTechniques)
            {
                if (source.Contains(technique, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(path)}: {technique}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> AppSourceFiles() =>
        SourceFilesUnder(Path.Combine("src", "ServerMonitor.App"));

    private static IEnumerable<string> WidgetProviderSourceFiles() =>
        SourceFilesUnder(Path.Combine("src", "ServerMonitor.WidgetProvider"));

    private static IEnumerable<string> SourceFilesUnder(string relativeRoot) =>
        Directory.EnumerateFiles(Path.Combine(FindRepositoryRoot(), relativeRoot), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Relative(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }

    /// <summary>
    /// Comments may name a technique to explain why it is NOT used — that is documentation, not a
    /// dependency. Only executable source counts.
    /// </summary>
    private static string StripComments(string source) =>
        Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", string.Empty, RegexOptions.None);
}
