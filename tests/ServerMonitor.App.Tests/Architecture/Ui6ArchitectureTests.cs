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
        var forbidden = new[]
        {
            typeof(ServerMonitor.Core.Interfaces.IServerService),
            typeof(ServerMonitor.Core.Interfaces.IServerRepository),
            typeof(ServerMonitor.Core.Interfaces.IServerCredentialStore),
            typeof(ServerMonitor.Core.Interfaces.IHostKeyTrustStore),
            typeof(ServerMonitor.Core.Interfaces.IRoutedHostKeyTrustStore),
            typeof(ServerMonitor.Core.Backup.IConfigurationWriteGate),
            typeof(DashboardViewModel),
        };
        foreach (var owner in new[] { typeof(ShellViewModel), typeof(OnboardingViewModel), typeof(OnboardingActions),
            typeof(ServerMonitor.App.Controls.SaSidebar), typeof(ServerMonitor.App.Controls.OnboardingView), typeof(MainWindow) })
        foreach (var type in TypeFamily(owner))
        foreach (var reference in ReferencedTypes(type).SelectMany(Flatten))
            Assert.False(forbidden.Any(f => f.IsAssignableFrom(reference) && !(owner == typeof(MainWindow) && f == typeof(DashboardViewModel))), $"{type}: forbidden reference {reference}");
    }

    private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static IEnumerable<Type> TypeFamily(Type type) => new[] { type }.Concat(type.GetNestedTypes(AllDeclared).SelectMany(TypeFamily));
    private static IEnumerable<Type> Flatten(Type type) => new[] { type }
        .Concat(type.HasElementType ? Flatten(type.GetElementType()!) : [])
        .Concat(type.IsGenericType ? type.GetGenericArguments().SelectMany(Flatten) : []);
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        foreach (var field in type.GetFields(AllDeclared)) yield return field.FieldType;
        foreach (var method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
        {
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
            if (method is MethodInfo info) yield return info.ReturnType;
            var body = method.GetMethodBody();
            if (body is null) continue;
            foreach (var local in body.LocalVariables) yield return local.LocalType;
            var il = body.GetILAsByteArray()!;
            for (var offset = 0; offset < il.Length;)
            {
                var value = il[offset++];
                var opcode = OpCodesByValue[value == 0xfe ? (short)(0xfe00 | il[offset++]) : (short)value];
                var operand = opcode.OperandType;
                var size = operand switch
                {
                    System.Reflection.Emit.OperandType.InlineNone => 0,
                    System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                    System.Reflection.Emit.OperandType.InlineVar => 2,
                    System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                    System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il,offset),
                    _ => 4
                };
                if (operand is System.Reflection.Emit.OperandType.InlineType or System.Reflection.Emit.OperandType.InlineMethod
                    or System.Reflection.Emit.OperandType.InlineField or System.Reflection.Emit.OperandType.InlineTok)
                {
                    var member=type.Module.ResolveMember(BitConverter.ToInt32(il,offset),type.IsGenericType?type.GetGenericArguments():null,
                        method is MethodInfo { IsGenericMethod:true } generic?generic.GetGenericArguments():null);
                    if (member is Type used) yield return used;
                    else if (member?.DeclaringType is {} declaring) yield return declaring;
                    if (member is MethodInfo called)
                    {
                        yield return called.ReturnType;
                        foreach(var arg in called.GetGenericArguments()) yield return arg;
                    }
                    if (member is FieldInfo accessed) yield return accessed.FieldType;
                }
                offset += size;
            }
        }
    }
    private static readonly Dictionary<short,System.Reflection.Emit.OpCode> OpCodesByValue = typeof(System.Reflection.Emit.OpCodes)
        .GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(System.Reflection.Emit.OpCode))
        .Select(f=>(System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o=>o.Value);

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
