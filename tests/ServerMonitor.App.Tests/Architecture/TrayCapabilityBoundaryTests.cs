using System.Reflection;
using System.Runtime.CompilerServices;
using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// CV-20, the half the compiler cannot enforce (T14).
/// <para>
/// The compiler already makes the effect channel unusable from outside: the effect types and the
/// executor are private nested types of <c>TrayStateMachine</c>, so no other type in the assembly can
/// name them, declare them, construct them or feed them to the executor. This file is the REGRESSION
/// GUARD for that — a <c>private</c> can be widened by accident — plus the one thing the type system
/// genuinely cannot express: that nobody registers the capability in the container.
/// </para>
/// <para>
/// The allowlist below is fixed BY IDENTITY, not by count: counting two constructor parameters does not
/// say WHICH two. Compiler-generated members are INSPECTED rather than excluded, because a closure, an
/// auto-property or a captured parameter could retain the capability in a generated field — that is
/// additional retention, not noise, and excluding by category would be a negation in disguise.
/// </para>
/// </summary>
public sealed class TrayCapabilityBoundaryTests
{
    private static readonly Type Capability = typeof(INativeTrayRegistration);
    private static readonly Assembly AppAssembly = typeof(TrayStateMachine).Assembly;

    /// <summary>Every member allowed to name the capability, by exact metadata identity.</summary>
    private static readonly string[] AllowedSignatureMembers =
    [
        "ServerMonitor.App.Shell.Tray.TrayStateMachine..ctor",
        "ServerMonitor.App.Shell.Tray.TrayStateMachine+EffectExecutor..ctor"
    ];

    /// <summary>The single field allowed to RETAIN it.</summary>
    private const string AllowedHolderField = "ServerMonitor.App.Shell.Tray.TrayStateMachine+EffectExecutor._native";

    // ------------------------------------------------------------------ T14a: visibility

    [Fact]
    public void T14a_the_effect_types_and_the_executor_are_not_visible_outside_the_state_machine()
    {
        var nested = typeof(TrayStateMachine)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .ToArray();

        Assert.NotEmpty(nested);

        foreach (var type in nested)
        {
            Assert.True(
                type.IsNestedPrivate,
                $"{type.Name} must stay private-nested: widening it is what reopens CV-20");
        }

        // Named by IDENTITY rather than by a substring heuristic: the two types that carry the effect
        // channel must both still be there and both still be private. A heuristic on the name would also
        // sweep in ShellEffectState, which is a legitimate PUBLIC observation of the effect gate and
        // carries no capability.
        var names = nested.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Contains("Effect", names);
        Assert.Contains("EffectExecutor", names);
        Assert.Contains("EffectKind", names);
    }

    // ------------------------------------------------------------------ T14b: identity allowlist

    [Fact]
    public void T14b_exactly_one_field_retains_the_capability_and_it_is_the_executor()
    {
        var holders = AllFields()
            .Where(f => f.FieldType == Capability)
            .Select(Identify)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Generated fields are INCLUDED in the search on purpose: a captured parameter or a closure
        // retaining the capability is exactly what this must catch.
        Assert.Equal([AllowedHolderField], holders);
    }

    [Fact]
    public void T14b_only_the_allowlisted_members_name_the_capability_at_all()
    {
        var offenders = new List<string>();

        foreach (var type in AppAssembly.GetTypes())
        {
            foreach (var method in type.GetMethods(AllMembers).Concat<MethodBase>(type.GetConstructors(AllMembers)))
            {
                var names = method.GetParameters().Any(p => p.ParameterType == Capability)
                            || (method is MethodInfo info && info.ReturnType == Capability);

                if (names && !AllowedSignatureMembers.Contains(Identify(method)))
                {
                    offenders.Add(Identify(method));
                }
            }

            offenders.AddRange(type
                .GetProperties(AllMembers)
                .Where(p => p.PropertyType == Capability)
                .Select(Identify));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void T14b_the_allowlist_is_not_vacuous()
    {
        // A guard against the WidgetCardUrlGrammarTests defect: an allowlist that matches nothing would
        // make the assertions above pass over an empty set.
        var named = AllFields().Count(f => f.FieldType == Capability);
        Assert.Equal(1, named);
        Assert.Equal(2, AllowedSignatureMembers.Length);
    }

    // T14c lives in TrayOwnershipCompletenessTests now. It used to read the text of App.xaml.cs, and
    // when the composition root grew a doc comment NAMING the capability, the text assertion failed over
    // prose. That is the whole argument against the technique: it cannot tell a registration from a
    // sentence. The replacement inspects the ServiceDescriptors the root really produces.

    // ------------------------------------------------------------------

    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    private static IEnumerable<FieldInfo> AllFields() =>
        AppAssembly.GetTypes().SelectMany(t => t.GetFields(AllMembers));

    private static string Identify(FieldInfo field) =>
        $"{Normalise(field.DeclaringType!)}.{field.Name}";

    private static string Identify(MethodBase method) =>
        $"{Normalise(method.DeclaringType!)}.{method.Name}";

    private static string Identify(PropertyInfo property) =>
        $"{Normalise(property.DeclaringType!)}.{property.Name}";

    private static string Normalise(Type type) => type.FullName!.Replace('/', '+');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
