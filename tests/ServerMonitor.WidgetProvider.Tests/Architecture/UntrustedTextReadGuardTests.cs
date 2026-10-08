using System.Reflection;
using System.Reflection.Emit;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Architecture;

/// <summary>
/// UI.9 C3 fixes (Vigil V-C3-1). <see cref="UntrustedText.Value"/> stays public (tests and the record need it),
/// but in PRODUCTION code it may be read only inside <see cref="UntrustedText.ForCard"/>, the M-3-neutralised
/// emission path. A future emitter that reads the raw value would bypass the <c>{{</c> neutralisation.
/// <para>
/// The check is on the provider assembly's IL, so it is type-aware (no grep false positives on other
/// <c>.Value</c>s). Every method body, including lambdas, local functions, iterators and async state machines
/// (compiler-generated types are scanned too), is walked opcode by opcode. Any of these is a read:
/// </para>
/// <list type="bullet">
/// <item><c>call</c>/<c>callvirt</c>/<c>ldftn</c> of <c>get_Value</c> or <c>Deconstruct</c>;</item>
/// <item><c>ldfld</c>/<c>ldflda</c> of the <c>Value</c> backing field.</item>
/// </list>
/// Inside the type, only <c>ForCard</c> and the members the compiler generates for the record struct may
/// read it.
/// </summary>
public sealed class UntrustedTextReadGuardTests
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    // The record struct's synthesized members (they use the backing field / getter by construction).
    private static readonly HashSet<string> AllowedInsideType =
        ["ForCard", "get_Value", "set_Value", ".ctor", "Deconstruct", "Equals", "GetHashCode", "PrintMembers", "op_Equality", "op_Inequality"];

    private static readonly BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static List<string> ProductionReadersOfValue(Assembly assembly)
    {
        var type = typeof(UntrustedText);
        var getter = type.GetProperty(nameof(UntrustedText.Value))!.GetMethod!;
        var deconstruct = type.GetMethod("Deconstruct");
        var backingField = type.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var readers = new List<string>();

        foreach (var declaring in assembly.GetTypes())
        {
            var methods = declaring.GetMethods(All).Cast<MethodBase>().Concat(declaring.GetConstructors(All));
            foreach (var method in methods)
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null || !ReadsValue(method, il, getter, deconstruct, backingField))
                {
                    continue;
                }

                if (declaring == type && AllowedInsideType.Contains(method.Name))
                {
                    continue;
                }

                readers.Add($"{declaring.FullName}.{method.Name}");
            }
        }

        return readers;
    }

    private static bool ReadsValue(MethodBase method, byte[] il, MethodInfo getter, MethodInfo? deconstruct, FieldInfo backingField)
    {
        var module = method.Module;
        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            short value = il[position];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[position + 1]));
                position += 2;
            }
            else
            {
                position += 1;
            }

            var opcode = OpCodesByValue[value];
            var operandStart = position;
            position += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, operandStart),
                _ => 4
            };

            if (opcode.OperandType == OperandType.InlineMethod && (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Ldftn))
            {
                var target = module.ResolveMethod(BitConverter.ToInt32(il, operandStart), typeArgs, methodArgs);
                if (target == getter || (deconstruct is not null && target == deconstruct))
                {
                    return true;
                }
            }
            else if (opcode.OperandType == OperandType.InlineField && (opcode == OpCodes.Ldfld || opcode == OpCodes.Ldflda))
            {
                if (module.ResolveField(BitConverter.ToInt32(il, operandStart), typeArgs, methodArgs) == backingField)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Production_code_reads_the_untrusted_value_only_inside_ForCard()
    {
        Assert.Empty(ProductionReadersOfValue(typeof(UntrustedText).Assembly));
    }

    [Fact]
    public void The_guard_sees_the_one_legitimate_reader()
    {
        // Anti-vacuity: with the allowance removed, the scan must find ForCard itself.
        var type = typeof(UntrustedText);
        var forCard = type.GetMethod(nameof(UntrustedText.ForCard))!;
        Assert.True(ReadsValue(forCard, forCard.GetMethodBody()!.GetILAsByteArray()!,
            type.GetProperty(nameof(UntrustedText.Value))!.GetMethod!, type.GetMethod("Deconstruct"),
            type.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!));
    }

    [Fact]
    public void The_guard_catches_a_read_in_any_other_method()
    {
        // Anti-vacuity on a real call site: this test assembly reads Value (here), and the scan flags it.
        var raw = new UntrustedText("x{{y").Value;
        Assert.Equal("x{{y", raw);
        Assert.Contains(ProductionReadersOfValue(typeof(UntrustedTextReadGuardTests).Assembly),
            r => r.EndsWith(nameof(The_guard_catches_a_read_in_any_other_method), StringComparison.Ordinal));
    }
}
