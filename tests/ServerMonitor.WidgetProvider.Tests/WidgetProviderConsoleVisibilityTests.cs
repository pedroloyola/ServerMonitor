using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// M13-QA-7 regression suite: adding the widget to the Windows board must not put a terminal on the
/// user's desktop.
/// <para>
/// The 1.1.0.0 package shipped ServerAlyzer.WidgetProvider.exe with PE subsystem 3
/// (IMAGE_SUBSYSTEM_WINDOWS_CUI) while the whole suite was green. COM starts an ExeServer with
/// CreateProcess from a parent that has no console and without CREATE_NO_WINDOW, so Windows allocates and
/// SHOWS a console for a CUI image — measured directly: the CUI provider launched from a console-less
/// parent gets a conhost attached, whether or not it writes anything. The old Console.Error sink then
/// decided what was printed inside that window. Both halves are covered here, on BUILT/RUNTIME artifacts
/// rather than on csproj properties, because a smoke launch from a terminal inherits that terminal's
/// console and can never observe either one.
/// </para>
/// The packaged build carries the subsystem check again as a hard MSBuild gate over the staged exe
/// (<c>VerifyPeSubsystem</c> in ServerMonitor.App.csproj); this covers the ordinary build.
/// </summary>
public sealed class WidgetProviderConsoleVisibilityTests
{
    private const ushort ImageSubsystemWindowsGui = 2;
    private const ushort ImageSubsystemWindowsCui = 3;

    [Fact]
    public void Provider_executable_is_built_for_the_windows_gui_subsystem()
    {
        var path = ProviderExecutablePath();

        var subsystem = ReadPeSubsystem(path);

        Assert.False(
            subsystem == ImageSubsystemWindowsCui,
            $"'{path}' is a CONSOLE-subsystem binary. COM activation would show a console window on the " +
            "Widgets board (M13-QA-7). ServerMonitor.WidgetProvider.csproj must use <OutputType>WinExe.");
        Assert.Equal(ImageSubsystemWindowsGui, subsystem);
    }

    /// <summary>
    /// Static belt to the runtime braces below: the C# compiler only emits an assembly reference for a
    /// type that is actually used, so <c>System.Console</c> disappears from the provider's references
    /// exactly when no code in it touches <see cref="Console"/> any more. This fails for a Console call
    /// anywhere in the provider, not just in the log.
    /// </summary>
    [Fact]
    public void Provider_assembly_does_not_reference_System_Console()
    {
        var referenced = typeof(EtwWidgetProviderLog).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            referenced,
            reference => string.Equals(reference.Name, "System.Console", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_log_writes_nothing_to_the_console(bool warning)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var captureOut = new StringWriter();
        using var captureError = new StringWriter();

        try
        {
            Console.SetOut(captureOut);
            Console.SetError(captureError);

            if (warning)
            {
                EtwWidgetProviderLog.Instance.Warn("probe");
            }
            else
            {
                EtwWidgetProviderLog.Instance.Info("probe");
            }
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        Assert.Equal(string.Empty, captureOut.ToString());
        Assert.Equal(string.Empty, captureError.ToString());
    }

    [Fact]
    public void Production_log_survives_a_message_it_cannot_emit()
    {
        // Diagnostics must never be able to fault the COM server (§16); an odd message is still just a log.
        EtwWidgetProviderLog.Instance.Info(string.Empty);
        EtwWidgetProviderLog.Instance.Warn(new string('x', 64 * 1024));
    }

    private static string ProviderExecutablePath()
    {
        // The provider is a ProjectReference of this test project, so its exe sits next to the test
        // assembly. Resolve from the assembly location rather than the working directory, which the test
        // host is free to change.
        var directory = Path.GetDirectoryName(typeof(WidgetProviderConsoleVisibilityTests).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var path = Path.Combine(directory, "ServerAlyzer.WidgetProvider.exe");
        Assert.True(File.Exists(path), $"The built widget provider was not found at '{path}'.");
        return path;
    }

    /// <summary>
    /// Reads <c>IMAGE_OPTIONAL_HEADER.Subsystem</c>: <c>e_lfanew</c> at 0x3C gives the PE signature
    /// offset; the optional header starts after the 4-byte signature and the 20-byte COFF header, and its
    /// Subsystem field is at offset 68 within it — the same offset for PE32 and PE32+.
    /// </summary>
    private static ushort ReadPeSubsystem(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        Assert.Equal((byte)'P', bytes[peOffset]);
        Assert.Equal((byte)'E', bytes[peOffset + 1]);
        return BitConverter.ToUInt16(bytes, peOffset + 4 + 20 + 68);
    }
}
