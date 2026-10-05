using System.Reflection;
using System.Text.RegularExpressions;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Architecture;

public sealed class Ui6ArchitectureTests
{
    [Fact]
    public void OneNavigationSource_AndColdStartGuard_AreWiredInProduction()
    {
        var router = AppSourceTree.CodeWithoutComments("Services/NavigationService.cs");
        Assert.Single(Regex.Matches(router, @"Host\.Content\s*="));
        Assert.Single(Regex.Matches(router, @"CurrentDestination\s*=(?!=)"));
        var show = router[router.IndexOf("private void Show", StringComparison.Ordinal)..];
        Assert.Contains("CurrentDestination = destination", show);
        Assert.Contains("Navigated?.Invoke", show);
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("_navigationService.EnsureInitialNavigation()", window);
        Assert.DoesNotContain("_navigationService.GoToDashboard()", window);
        Assert.Contains("Onboarding.OnMainWindowShownAsync", window);
        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        Assert.Contains("AddSingleton<ShellViewModel>", app);
        Assert.Contains("AddSingleton<OnboardingViewModel>", app);
        Assert.Contains("SuppressForActivation", app);
    }

    [Fact]
    public void StartupOrder_PreservesCompactAndRecoveryBeforeOnboarding()
    {
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        var capture = window.IndexOf("var normalStart = OnboardingStartup.IsNormalStart(_navigationService.CurrentDestination, Program.LaunchMode)", StringComparison.Ordinal);
        var ensure = window.IndexOf("_navigationService.EnsureInitialNavigation()", StringComparison.Ordinal);
        var compact = window.IndexOf("_ = _dashboardViewModel.LoadAsync()", ensure, StringComparison.Ordinal);
        var recovery = window.IndexOf("_ = _backupRestore.ShowStartupRecoveryOnceAsync()", compact, StringComparison.Ordinal);
        var evaluate = window.IndexOf("_ = Onboarding.OnMainWindowShownAsync(normalStart)", recovery, StringComparison.Ordinal);
        Assert.True(capture >= 0 && capture < ensure && ensure < compact && compact < recovery && recovery < evaluate);
        Assert.Contains("Onboarding.SetWindowMode(_modeCoordinator.CurrentMode)", window);
        Assert.Contains("Onboarding.SetWindowMode(mode)", window);
    }

    [Fact]
    public void ActivationHandOff_UsesDependencyFreeLatchBeforeWindowConstruction()
    {
        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        var constructor = app[app.IndexOf("public App()", StringComparison.Ordinal)..app.IndexOf("private void ExecuteActivationIntent", StringComparison.Ordinal)];
        Assert.DoesNotContain("GetRequiredService<OnboardingViewModel>", constructor);
        Assert.DoesNotContain("GetRequiredService<DashboardViewModel>", constructor);
        Assert.Contains("GetRequiredService<ActivationLatch>", constructor);
        Assert.Contains("activation.Record()", constructor);
        Assert.Contains("AddSingleton<ActivationLatch>()", app);
        Assert.Empty(typeof(ActivationLatch).GetConstructors().Single().GetParameters());
    }

    [Fact]
    public void NavigationContract_HasNoDefaultImplementations()
    {
        foreach (var method in typeof(INavigationService).GetMethods()) Assert.True(method.IsAbstract, method.Name);
    }

    [Fact]
    public void ShellAndOnboarding_DoNotReferenceTrustCredentialsOrPersistence()
    {
        foreach (var type in new[] { typeof(ShellViewModel), typeof(OnboardingViewModel) })
        {
            var types = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.FieldType)
                .Concat(type.GetConstructors().SelectMany(c => c.GetParameters().Select(p => p.ParameterType))).ToArray();
            Assert.NotEmpty(types);
            Assert.DoesNotContain(types, t => t.Name.Contains("Trust", StringComparison.Ordinal) || t.Name.Contains("Credential", StringComparison.Ordinal)
                || t.Name.Contains("Repository", StringComparison.Ordinal) || t.Name.Contains("Settings", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CommunityShell_HasExactlyTheFourApprovedDestinations()
    {
        Assert.Equal(new[] { "Overview", "Servers", "History", "Settings" }, Enum.GetNames<ShellDestination>());
        var shell = AppSourceTree.CodeWithoutComments("ViewModels/ShellViewModel.cs");
        Assert.DoesNotContain("IFeatureCatalog", shell);
        Assert.DoesNotContain("FUTURE_PRO", shell);
    }

    [Fact]
    public void QaShell_IsExcludedFromRelease_AndHandOffUsesPendingActivation()
    {
        var project = File.ReadAllText(AppSourceTree.Full("ServerMonitor.App.csproj"));
        Assert.Contains("Qa\\**", project);
        var program = AppSourceTree.CodeWithoutComments("Program.cs");
        var call = program.IndexOf("Qa.QaShellStartup.Activation", StringComparison.Ordinal);
        Assert.True(call > 0);
        Assert.True(program.LastIndexOf("#if DEBUG", call, StringComparison.Ordinal) > program.LastIndexOf("#endif", call, StringComparison.Ordinal));
        Assert.Contains("_pendingActivation.Deliver(Qa.QaShellStartup.Activation", program);
    }
}
