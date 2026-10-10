using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S2). <c>--qa-components</c> / <c>--qa-tokens</c> open the component gallery INSTEAD of the
/// application: the App constructor short-circuits here before the host is built, and OnLaunched before
/// StartupRestoreRecovery / OrphanTemporaryCleaner. So the gallery never composes services, never starts the
/// tray, notifications, engine, discovery or SSH, never reads or writes settings, and never touches the
/// Credential Manager. <c>App.ServicesHost</c> stays null: anything that reaches for it fails loudly.
/// The only file it writes is the --qa-tokens report under the policy's output directory.
/// Excluded from Release (see ServerMonitor.App.csproj); <see cref="QaGalleryPolicy"/> returns None there.
/// </summary>
internal static class QaGalleryComposition
{
    /// <summary>Exit code of a refused or broken gallery launch (harness error).</summary>
    public const int HarnessErrorExitCode = 3;

    private static QaGalleryRequest? _request;

    /// <summary>The user's real data folder. The gallery never writes there; the policy refuses it as a report folder.</summary>
    public static string RealDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerMonitor");

    /// <summary>Per-process report folder used when <c>--qa-gallery-out</c> is not given.</summary>
    public static string DefaultOutputDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "ServerMonitor-QA", "components", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static QaGalleryRequest Request => _request ??= QaGalleryPolicy.Resolve(
        Environment.GetCommandLineArgs(), isDebugBuild: true, RealDataDirectory, DefaultOutputDirectory);

    public static bool IsRequested() => Request.Mode != QaGalleryMode.None;

    /// <summary>
    /// Runs in the App constructor, before InitializeComponent loads the dictionaries. A refused launch ends the
    /// process here (exit 3) - it never falls through to another composition.
    /// </summary>
    public static void InitializeBeforeResources()
    {
        if (Request.Mode == QaGalleryMode.Refused)
        {
            Debug.WriteLine("QA gallery refused: " + Request.RefusalReason);
            Console.Error.WriteLine("QA gallery refused: " + Request.RefusalReason);
            Environment.Exit(HarnessErrorExitCode);
        }

        QaUiLanguageComposition.ApplyRequested();

        // UI.11: the gallery also honours --qa-reduced-motion (the motion patterns' instant form).
        Services.Motion.MotionPolicy.Install(Services.Motion.MotionPolicy.CreateSystemSource(
            forceReduced: QaReducedMotionPolicy.IsRequested(Environment.GetCommandLineArgs(), isDebugBuild: true)));

        // The same language rule the application applies at startup, without a host: an explicit override wins,
        // an unsupported system culture falls back to the product default. Nothing is persisted.
        new LocalizationService(NullLogger<LocalizationService>.Instance).InitializeFromSystem();
    }

    /// <summary>Runs in OnLaunched instead of the application startup.</summary>
    public static void Launch()
    {
        // A gallery crash is a harness finding: record it next to the report (never in user data) and exit 3.
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, args) =>
        {
            try
            {
                var directory = Request.OutputDirectory ?? DefaultOutputDirectory;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "gallery-error.txt"), args.Exception?.ToString() ?? args.Message);
            }
            catch
            {
                // The exit code alone still reports the harness error.
            }

            Environment.Exit(HarnessErrorExitCode);
        };

        var window = new QaGalleryWindow(Request);
        window.Activate();
        if (Request.Mode == QaGalleryMode.Tokens)
        {
            window.RunTokenSelfCheckAndExit();
        }
    }
}
