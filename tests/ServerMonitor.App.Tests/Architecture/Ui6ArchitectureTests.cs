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
