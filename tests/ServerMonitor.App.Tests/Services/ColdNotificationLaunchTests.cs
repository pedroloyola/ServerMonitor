using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D D-3: a click on a toast while the app is closed. COM starts the app with the toast activator's
/// <c>----AppNotificationActivated:</c> argument, and the Windows App SDK answers <c>GetActivatedEventArgs()</c> by
/// waiting on an event only <c>AppNotificationManager.Register()</c> creates; before <c>Register()</c> that wait
/// fail-fasts the process natively. These tests pin the DECISION (the documented order and its fallbacks); the
/// native crash itself is only provable in the packaged VM (relay-d3.md E1/E6).
/// </summary>
public sealed class ColdNotificationLaunchTests
{
    private const string Exe = @"C:\Program Files\WindowsApps\PedroLoy.ServerAlyzer_1.1.7.0_x64__htb92ajfwgw1e\ServerMonitor.App.exe";

    // ------------------------------------------------------------------ the launch marker

    [Theory]
    [InlineData("\"" + Exe + "\" ----AppNotificationActivated:")]
    [InlineData(Exe + " ----AppNotificationActivated:")]
    [InlineData("\"" + Exe + "\" ----AppNotificationActivated:kind=ServerHealth&action=OpenDashboard")]
    [InlineData("\"" + Exe + "\" \"----AppNotificationActivated:\"")]
    [InlineData("\"" + Exe + "\" ----appnotificationactivated:")]
    public void A_notification_launch_is_recognized(string commandLine) =>
        Assert.True(ColdNotificationLaunch.IsNotificationLaunch(commandLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"" + Exe + "\"")]
    [InlineData("\"" + Exe + "\" --background")]
    [InlineData("\"" + Exe + "\" serveralyzer://server/0b5c9f8e-3d0a-4f39-9a3e-3a61e2a1c0de")]
    [InlineData("\"" + Exe + "\" ----ms-protocol:serveralyzer://dashboard")]
    [InlineData("\"" + Exe + "\" ----WindowsAppRuntimePushServer:")]
    [InlineData("\"" + Exe + "\" ----AppNotificationActivated")] // no colon: not the SDK's marker
    public void Anything_else_is_not_a_notification_launch(string? commandLine) =>
        Assert.False(ColdNotificationLaunch.IsNotificationLaunch(commandLine));

    // ------------------------------------------------------------------ the documented order

    [Fact]
    public void A_notification_launch_registers_BEFORE_it_reads_its_activation()
    {
        var journal = new List<string>();

        var args = ColdNotificationLaunch.ReadActivation(
            "\"" + Exe + "\" ----AppNotificationActivated:",
            () => { journal.Add("register"); return true; },
            () => { journal.Add("read"); return "notification-args"; });

        Assert.Equal(["register", "read"], journal);
        Assert.Equal("notification-args", args);
    }

    [Fact]
    public void A_notification_launch_that_cannot_register_never_reads_and_starts_plain()
    {
        // The read would wait on a handle Register() never created: a native fail-fast. Not reading is the only safe answer.
        var reads = 0;

        var args = ColdNotificationLaunch.ReadActivation<string>(
            "\"" + Exe + "\" ----AppNotificationActivated:",
            () => false,
            () => { reads++; throw new InvalidOperationException("the read must not happen without Register()"); });

        Assert.Null(args);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("\"" + Exe + "\"")]
    [InlineData("\"" + Exe + "\" --background")]
    [InlineData("\"" + Exe + "\" ----ms-protocol:serveralyzer://dashboard")]
    public void Any_other_launch_reads_once_and_never_registers_early(string commandLine)
    {
        var registers = 0;
        var reads = 0;

        var args = ColdNotificationLaunch.ReadActivation(
            commandLine,
            () => { registers++; return true; },
            () => { reads++; return "launch-args"; });

        Assert.Equal("launch-args", args); // widget/protocol activation is read exactly as before
        Assert.Equal(0, registers);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("\"" + Exe + "\" ----AppNotificationActivated:")]
    [InlineData("\"" + Exe + "\"")]
    public void A_read_that_throws_is_a_plain_launch_never_a_failed_start(string commandLine)
    {
        // E.g. the SDK's ERROR_TIMEOUT when the click's COM Activate never arrives.
        var args = ColdNotificationLaunch.ReadActivation<string>(
            commandLine,
            () => true,
            () => throw new System.Runtime.InteropServices.COMException("timeout", unchecked((int)0x800705B4)));

        Assert.Null(args);
    }

    // ------------------------------------------------------------------ what the cold click opens

    [Fact]
    public void A_cold_health_or_fail_safe_click_opens_the_dashboard_once()
    {
        Assert.Equal(ActivationIntent.Dashboard, ColdNotificationLaunch.IntentFor(NotificationActivationContract.ForServerHealth()));
        Assert.Equal(ActivationIntent.Dashboard, ColdNotificationLaunch.IntentFor(NotificationActivationContract.ForFailSafeExit()));
    }

    [Fact]
    public void Anything_else_is_a_plain_launch()
    {
        Assert.Null(ColdNotificationLaunch.IntentFor(NotificationActivationContract.ForBackgroundCloseNotice()));
        Assert.Null(ColdNotificationLaunch.IntentFor(null));
        Assert.Null(ColdNotificationLaunch.IntentFor(new Dictionary<string, string> { ["kind"] = "ServerHealth", ["action"] = "Delete" }));
    }

    // ------------------------------------------------------------------ the token is the manifests' own

    [Theory]
    [InlineData("Package.appxmanifest")]
    [InlineData("Package.Dev.appxmanifest")]
    public void The_token_is_the_toast_activators_ExeServer_argument_in_both_manifests(string manifest)
    {
        var document = XDocument.Load(AppSourceTree.Full(manifest));
        var activatorClsid = document.Descendants()
            .Single(element => element.Name.LocalName == "ToastNotificationActivation")
            .Attribute("ToastActivatorCLSID")!.Value;

        var server = document.Descendants()
            .Where(element => element.Name.LocalName == "ExeServer")
            .Single(element => element.Elements().Any(child => child.Name.LocalName == "Class"
                && string.Equals((string?)child.Attribute("Id"), activatorClsid, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal("ServerMonitor.App.exe", (string?)server.Attribute("Executable"));
        Assert.Equal(ColdNotificationLaunch.ActivationToken, (string?)server.Attribute("Arguments"));
    }

    // ------------------------------------------------------------------ the order is pinned in Program

    [Fact]
    public void Program_reads_its_activation_only_through_the_ordered_sequence()
    {
        var program = AppSourceTree.CodeWithoutComments("Program.cs");

        // Exactly one GetActivatedEventArgs in the whole entry point, and it is the read handed to ReadActivation
        // AFTER the early registration: nothing can ask the SDK for the arguments before Register().
        Assert.Single(Regex.Matches(program, @"GetActivatedEventArgs\s*\("));
        Assert.Matches(
            new Regex(@"ColdNotificationLaunch\.ReadActivation\(\s*Environment\.CommandLine,\s*TryRegisterNotificationsEarly,\s*\(\)\s*=>\s*AppInstance\.GetCurrent\(\)\.GetActivatedEventArgs\(\)\)", RegexOptions.Singleline),
            program);

        // Both call sites of the old direct read (the Debug bypass and the single-instance path) now go through it,
        // and the single-instance decision comes after the read, as before.
        var shouldRedirect = program[program.IndexOf("private static bool ShouldRedirectToExistingInstance()", StringComparison.Ordinal)..];
        Assert.Equal(2, Regex.Matches(shouldRedirect[..shouldRedirect.IndexOf("RedirectActivationTo(activationArgs", StringComparison.Ordinal)], @"ReadOwnActivation\(\)").Count);
        Assert.True(shouldRedirect.IndexOf("var activationArgs = ReadOwnActivation();", StringComparison.Ordinal)
            < shouldRedirect.IndexOf("AppInstance.FindOrRegisterForKey(key)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_early_registration_is_the_process_platform_the_service_adopts()
    {
        var program = AppSourceTree.CodeWithoutComments("Program.cs");
        Assert.Contains("var platform = WindowsAppNotificationPlatform.Process;", program, StringComparison.Ordinal);

        var service = AppSourceTree.CodeWithoutComments("Services/WindowsAppNotificationService.cs");
        Assert.Contains("WindowsAppNotificationPlatform.Process,", service, StringComparison.Ordinal);
        Assert.DoesNotContain("new WindowsAppNotificationPlatform()", service, StringComparison.Ordinal);
    }
}
