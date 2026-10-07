using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.8 D-UI8-10 / Cortex RC-4 §4.4 (SPEC §4.5). Over the REAL mode coordinator, which ignores a switch before it is
/// initialized - exactly why the old "switch, then RestoreAndActivate" left a headless Compact process in Compact: the
/// Standard sequence materializes first (which initializes the coordinator with the persisted mode), then switches, then
/// shows; a minimized window is shown first and switched after. The window-free halves of the wiring are pinned by
/// source: activation, Settings and Settings › Background surface in Standard; "Abrir", the OpenDashboard notification and
/// the second-instance redirect keep the mode. Deterministic: fakes only.
/// </summary>
public sealed partial class StandardSurfacingTests
{
    [Fact]
    public void AHeadlessProcessWithCompactPersisted_EndsInStandard_MaterializedFirst()
    {
        var (coordinator, order) = Headless(WindowMode.Compact);

        var ran = StandardSurfacing.Run(
            () => { order.Add("materialize"); coordinator.Initialize(); return true; },
            coordinator,
            () => false,
            () => order.Add("show"),
            () => order.Add("navigate"));

        Assert.True(ran);
        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.Equal(["materialize", "applied:Compact", "navigate", "applied:Standard", "show"], order);
    }

    [Fact]
    public void AMinimizedWindow_IsShownFirst_ThenSwitched()
    {
        var (coordinator, order) = Headless(WindowMode.Compact);
        coordinator.Initialize();
        order.Clear();

        StandardSurfacing.Run(() => { order.Add("materialize"); return true; }, coordinator, () => true, () => order.Add("show"));

        Assert.Equal(["materialize", "show", "applied:Standard"], order);
        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
    }

    [Fact]
    public void AStandardWindow_IsOnlyShown_AndNoWindowMeansNothing()
    {
        var (coordinator, order) = Headless(WindowMode.Standard);
        coordinator.Initialize();
        order.Clear();
        StandardSurfacing.Run(() => true, coordinator, () => false, () => order.Add("show"));
        Assert.Equal(["show"], order); // already Standard: no transition, no bounds change

        var (headless, nothing) = Headless(WindowMode.Compact);
        Assert.False(StandardSurfacing.Run(() => false, headless, () => false, () => nothing.Add("show"), () => nothing.Add("navigate")));
        Assert.Empty(nothing);
    }

    [Fact]
    public void TheOldOrder_SwitchThenMaterialize_IsWhatLeftItInCompact()
    {
        // The defect, reproduced over the same coordinator: a switch before Initialize is ignored, then the persisted
        // Compact is applied. This is the order StandardSurfacing must never use.
        var (coordinator, _) = Headless(WindowMode.Compact);
        coordinator.SwitchTo(WindowMode.Standard);
        coordinator.Initialize();

        Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);
    }

    // ---- the wiring (WinUI types: pinned by source) ----

    [Fact]
    public void TheActivation_SurfacesInStandard_ThroughTheController_AndNeverTouchesTheModeItself()
    {
        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        var intent = Body(app, "private void ExecuteActivationIntent");

        Assert.Contains("GetRequiredService<IApplicationWindowController>().RestoreAndActivateStandard();", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("IWindowModeCoordinator", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("SwitchTo", intent, StringComparison.Ordinal);
        Assert.DoesNotContain(".RestoreAndActivate()", intent, StringComparison.Ordinal);
        // The guard still runs after the surfacing, and the onboarding is still suppressed before it.
        var suppress = intent.IndexOf("SuppressForActivation()", StringComparison.Ordinal);
        var surface = intent.IndexOf("RestoreAndActivateStandard()", StringComparison.Ordinal);
        var guard = intent.IndexOf("LeaveCurrentPageForActivation(", StringComparison.Ordinal);
        Assert.True(suppress >= 0 && suppress < surface && surface < guard);
    }

    [Fact]
    public void SettingsAndBackgroundSettings_SurfaceInStandard_RestoreAndRedirectKeepTheMode()
    {
        var controller = AppSourceTree.CodeWithoutComments("Services/ApplicationWindowController.cs");

        var restore = Body(controller, "public void RestoreAndActivate()");
        Assert.DoesNotContain("SwitchTo", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardSurfacing", restore, StringComparison.Ordinal);

        var standard = Body(controller, "public void RestoreAndActivateStandard()");
        Assert.Contains("StandardSurfacing.Run(TryMaterialize, modeCoordinator, IsMinimized, ShowAndActivate)", standard, StringComparison.Ordinal);

        var settings = Body(controller, "public void OpenSettings()");
        Assert.Contains("StandardSurfacing.Run(TryMaterialize, modeCoordinator, IsMinimized, ShowAndActivate)", settings, StringComparison.Ordinal);
        Assert.True(settings.IndexOf("StandardSurfacing.Run", StringComparison.Ordinal) < settings.IndexOf("navigationService.GoToSettings()", StringComparison.Ordinal));

        var background = Body(controller, "public void OpenBackgroundSettings()");
        Assert.Contains("navigateWhileHidden:", background, StringComparison.Ordinal);
        Assert.Matches(NavigateInsideTheHiddenStep(), background);

        var redirect = Body(AppSourceTree.CodeWithoutComments("App.xaml.cs"), "RestoreOnRedirect");
        Assert.Contains(".RestoreAndActivate();", redirect, StringComparison.Ordinal);
        Assert.DoesNotContain("Standard", redirect, StringComparison.Ordinal);

        var notifications = AppSourceTree.CodeWithoutComments("Services/WindowsAppNotificationService.cs");
        Assert.Matches(OpenDashboardRestores(), notifications);
        var tray = AppSourceTree.CodeWithoutComments("Services/TrayService.cs");
        Assert.Contains("OnOpenRequested(object? sender, EventArgs args) => windowController.RestoreAndActivate();", tray, StringComparison.Ordinal);
    }

    private static (WindowModeCoordinator Coordinator, List<string> Order) Headless(WindowMode persisted)
    {
        var order = new List<string>();
        var coordinator = new WindowModeCoordinator(
            new RecordingPlacementAdapter(),
            new FakeWindowPlacementStore(new WindowPlacementSettings { Mode = persisted }),
            NullLogger<WindowModeCoordinator>.Instance);
        coordinator.ModeChanged += (_, mode) => order.Add("applied:" + mode);
        return (coordinator, order);
    }

    // From the member's signature to the next member at the same indentation (good enough for these short bodies).
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found");
        var next = NextMember().Match(source, start + signature.Length);
        return next.Success ? source[start..next.Index] : source[start..];
    }

    [GeneratedRegex(@"\n    (public|private|internal) ")]
    private static partial Regex NextMember();

    [GeneratedRegex(@"navigateWhileHidden:\s*\(\)\s*=>\s*\{\s*navigationService\.GoToSettings\(\);\s*navigationService\.RequestBackgroundSettingsFocus\(\);\s*\}")]
    private static partial Regex NavigateInsideTheHiddenStep();

    [GeneratedRegex(@"case NotificationAction\.OpenDashboard:\s*_windowController\.RestoreAndActivate\(\);")]
    private static partial Regex OpenDashboardRestores();
}
