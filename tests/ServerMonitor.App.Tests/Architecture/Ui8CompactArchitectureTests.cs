using System.Reflection;
using System.Text.RegularExpressions;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.8 SPEC §0 / §4.1 and §4.9. The Compact presentation is a VIEW: none of its types reaches the engine, a store, the
/// server service, a clock or a timer, or the window mode (that knowledge stays on WindowModeViewModel) - by reflection
/// over every member AND by a scan of their sources. And the compact harness ships only in Debug: Release has no
/// <c>Qa*Compact*</c> type and gives a <c>--qa-compact</c> argument no surface. Lives in Architecture/ so it also runs in
/// Release.
/// </summary>
public sealed partial class Ui8CompactArchitectureTests
{
    private static readonly Type[] CompactTypes =
    [
        typeof(CompactServerRowViewModel),
        typeof(CompactPresentationViewModel),
        typeof(CompactBodyState),
        typeof(ServerMetricPresentation),
        typeof(ServerMetricReading),
        typeof(ServerMetricKind)
    ];

    private static readonly string[] CompactSources =
    [
        "ViewModels/CompactServerRowViewModel.cs",
        "ViewModels/CompactPresentationViewModel.cs",
        "ViewModels/ServerMetricPresentation.cs"
    ];

    private static readonly Type[] Forbidden =
    [
        typeof(IMonitoringEngine),
        typeof(IServerMetricsStore),
        typeof(IServerMonitoringStateStore),
        typeof(IServerConnectionStateStore),
        typeof(IServerService),
        typeof(IWindowModeCoordinator),
        typeof(IApplicationWindowController),
        typeof(INavigationService),
        typeof(TimeProvider),
        typeof(PresentationClock),
        typeof(ITimer),
        typeof(System.Threading.Timer),
        typeof(PeriodicTimer),
        typeof(Microsoft.UI.Dispatching.DispatcherQueue),
        typeof(Microsoft.UI.Dispatching.DispatcherQueueTimer)
    ];

    [Fact]
    public void NoCompactType_ReferencesTheEngineAStoreTheServerServiceAClockATimerOrTheWindowMode()
    {
        var offenders = CompactTypes
            .SelectMany(type => ReferencedTypes(type).Select(reference => (type, reference)))
            .Where(pair => Forbidden.Any(forbidden => forbidden.IsAssignableFrom(pair.reference)))
            .Select(pair => $"{pair.type.Name} -> {pair.reference.FullName}")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoCompactSource_StartsATimerADelayOrADispatcherOfItsOwn()
    {
        var offenders = CompactSources
            .SelectMany(file => ForbiddenSource().Matches(AppSourceTree.CodeWithoutComments(file)).Select(match => $"{file}: {match.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Counterproof of the scan itself: the pattern does catch each forbidden construct.</summary>
    [Theory]
    [InlineData("await Task.Delay(100);")]
    [InlineData("var t = queue.CreateTimer();")]
    [InlineData("new PeriodicTimer(TimeSpan.FromSeconds(1))")]
    [InlineData("new System.Threading.Timer(_ => { })")]
    [InlineData("TimeProvider.System.GetUtcNow()")]
    [InlineData("DispatcherQueue.GetForCurrentThread()")]
    [InlineData("_engine.RefreshNowAsync(id)")]
    public void TheSourceScan_CatchesEachForbiddenConstruct(string code) =>
        Assert.Matches(ForbiddenSource(), code);

    [Fact]
    public void TheCompactHarness_ShipsOnlyInDebug()
    {
        var harness = typeof(App).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("ServerMonitor.App.Qa", StringComparison.Ordinal) == true
                && type.Name.Contains("Compact", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToList();
#if DEBUG
        Assert.Contains("ServerMonitor.App.Qa.QaCompactComposition", harness);
        Assert.Contains("ServerMonitor.App.Qa.QaCompactTicker", harness);
#else
        Assert.Empty(harness);
#endif
    }

    /// <summary>In Release a --qa-compact argument selects nothing: no harness type, and no single-instance bypass either.</summary>
    [Fact]
    public void InRelease_ACompactQaArgument_HasNoSurface()
    {
        string[] args = ["ServerMonitor.App.exe", "--qa-compact", "--qa-compact-scenario", "n200", "--qa-compact-start=standard"];

        Assert.NotNull(SingleInstancePolicy.ResolveInstanceKey(args, isDebugBuild: false));
#if !DEBUG
        Assert.Null(typeof(App).Assembly.GetType("ServerMonitor.App.Qa.QaCompactComposition", throwOnError: false));
        Assert.Null(typeof(App).Assembly.GetType("ServerMonitor.App.Qa.QaStartupIsolation", throwOnError: false));
#endif
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var types = new List<Type>();
        types.AddRange(type.GetFields(all).Select(field => field.FieldType));
        types.AddRange(type.GetProperties(all).Select(property => property.PropertyType));
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            types.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
            if (method is MethodInfo info)
            {
                types.Add(info.ReturnType);
            }

            if (method.GetMethodBody() is { } body)
            {
                types.AddRange(body.LocalVariables.Select(local => local.LocalType));
            }
        }

        foreach (var nested in type.GetNestedTypes(all))
        {
            types.AddRange(ReferencedTypes(nested));
        }

        return types.SelectMany(Flatten);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        var element = type.HasElementType ? type.GetElementType()! : type;
        yield return element;
        if (element.IsGenericType)
        {
            foreach (var argument in element.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }

    [GeneratedRegex(@"Task\.Delay|CreateTimer|PeriodicTimer|Threading\.Timer|\bTimeProvider\b|PresentationClock|DispatcherQueue|RefreshNowAsync|StateChanged|GetLastSnapshot")]
    private static partial Regex ForbiddenSource();
}
