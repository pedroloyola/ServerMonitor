using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Core.Tests.SshConfig;

/// <summary>
/// Vigil M14.4a M2 follow-up, without a clock: the resolver's private <c>ResolutionPlan</c> index is
/// probed by reflection (no production seam). This proves the plan's STRUCTURE (which segments one
/// alias visits, independent of how many aliases the file has) plus a structural IL guard that the
/// resolver reads segments only through the plan. It does not measure end-to-end Resolve() cost.
/// </summary>
public sealed class SshConfigResolutionPlanTests
{
    private static readonly Type PlanType =
        typeof(SshConfigResolver).GetNestedType("ResolutionPlan", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SshConfigResolver.ResolutionPlan not found: update this probe with the plan.");

    private static readonly MethodInfo SegmentsForMethod =
        PlanType.GetMethod("SegmentsFor", BindingFlags.Instance | BindingFlags.Public, [typeof(string)])
        ?? throw new InvalidOperationException("ResolutionPlan.SegmentsFor(string) not found: update this probe with the plan.");

    private static readonly MethodInfo DocumentSegmentsGetter =
        typeof(SshConfigSplicedDocument).GetProperty(nameof(SshConfigSplicedDocument.Segments))!.GetMethod!;

    private static SshConfigSplicedDocument Document(string text) =>
        SshConfigIncludeExpander.FromText(text).Document
        ?? throw new InvalidOperationException("fixture did not parse");

    private static IReadOnlyList<SshConfigSegment> SegmentsFor(SshConfigSplicedDocument document, string alias)
    {
        var plan = Activator.CreateInstance(PlanType, [document])!;
        return ((IEnumerable<SshConfigSegment>)SegmentsForMethod.Invoke(plan, [alias])!).ToList();
    }

    /// <summary>What a full rescan would keep for <paramref name="alias"/> in a fixture without Match.</summary>
    private static List<SshConfigSegment> RescanFor(SshConfigSplicedDocument document, string alias) =>
        document.Segments
            .Where(segment => segment.Scope.Kind != SshConfigBlockKind.Host || SshConfigResolver.BlockApplies(segment.Scope.Patterns, alias))
            .ToList();

    private static string ManyHosts(int count)
    {
        var text = new StringBuilder("Host *\n  ServerAliveInterval 30\n");
        for (var i = 0; i < count; i++)
        {
            text.Append("Host h").Append(i).Append("\n  HostName 10.0.0.1\n");
        }

        return text.ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(33_000)]
    public void Plan_GivesEachAliasOnlyTheWildcardAndItsOwnSegments_WhateverTheNumberOfAliases(int hostCount)
    {
        var document = Document(ManyHosts(hostCount));
        var wildcard = Assert.Single(document.Segments, segment => segment.Scope.Patterns.SequenceEqual(["*"]));

        foreach (var alias in new[] { "h0", $"h{hostCount / 2}", $"h{hostCount - 1}" }.Distinct())
        {
            var visited = SegmentsFor(document, alias);

            // Exactly the Host * block, then the alias's own block, by reference: two segments per
            // alias for 1 or 33,000 hosts. A per-alias rescan would hand back all hostCount + 1.
            Assert.Equal(2, visited.Count);
            Assert.Same(wildcard, visited[0]);
            Assert.Same(Assert.Single(document.Segments, segment => segment.Scope.Patterns.SequenceEqual([alias])), visited[1]);
            Assert.Equal(RescanFor(document, alias), visited);
        }
    }

    [Fact]
    public void Plan_KeepsSplicedOrder_AcrossRepeatedAliasBlocksAndWildcards()
    {
        var document = Document(
            """
            User early
            Host a
              User one
            Host *
              Port 2
            Host b
              Port 4
            Host a
              Port 3
            Host a?
              Port 5
            """);

        Assert.Equal(["", "a", "*", "b", "a", "a?"], document.Segments.Select(segment => string.Join(",", segment.Scope.Patterns)));

        // The header-less lines and every wildcard block are offered to every alias (the resolver
        // evaluates them); a concrete block only to the alias it names. Spliced order is kept.
        Assert.Equal(
            [document.Segments[0], document.Segments[1], document.Segments[2], document.Segments[4], document.Segments[5]],
            SegmentsFor(document, "a"));
        Assert.Equal(
            [document.Segments[0], document.Segments[2], document.Segments[3], document.Segments[5]],
            SegmentsFor(document, "b"));
        Assert.Equal(
            [document.Segments[0], document.Segments[2], document.Segments[5]],
            SegmentsFor(document, "unlisted"));
    }

    // ---- structural guard: Resolve stays wired to the plan

    [Fact]
    public void Resolver_ReadsDocumentSegmentsOnlyInThePlan_AndResolvesDestinationsThroughSegmentsFor()
    {
        var readers = ResolverMethods()
            .Where(method => Calls(method).Contains(DocumentSegmentsGetter))
            .Select(Name)
            .ToList();

        // Only the plan's constructor may read the full segment list; any other reader is a rescan.
        Assert.Equal(["ResolutionPlan..ctor"], readers);

        var resolveDestination = typeof(SshConfigResolver).GetMethod("ResolveDestination", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SshConfigResolver.ResolveDestination not found: update this guard.");
        Assert.Contains(SegmentsForMethod, Calls(resolveDestination));

        // The plan hands segments out through SegmentsFor alone (a new accessor needs this guard reviewed).
        Assert.Equal(
            ["SegmentsFor", "get_HasUnverifiedInclude"],
            PlanType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(m => m.Name).Order(StringComparer.Ordinal));
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType!.Name}.{method.Name}";

    /// <summary>Every method body of the resolver, its nested types and their compiler-generated ones.</summary>
    private static IEnumerable<MethodBase> ResolverMethods()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = new List<Type>();
        var pending = new Stack<Type>([typeof(SshConfigResolver)]);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            types.Add(type);
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                pending.Push(nested);
            }
        }

        return types.SelectMany(type => type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)));
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    /// <summary>Methods called or referenced (call, callvirt, newobj, ldftn…) by <paramref name="method"/>'s IL.</summary>
    private static List<MethodBase> Calls(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var called = new List<MethodBase>();
        var position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[position++]));
            }

            var opCode = OpCodesByValue[value];
            switch (opCode.OperandType)
            {
                case OperandType.InlineMethod:
                    called.Add(method.Module.ResolveMethod(
                        BitConverter.ToInt32(il, position),
                        method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null)!);
                    position += 4;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (4 * BitConverter.ToInt32(il, position));
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    position += 8;
                    break;
                default:
                    position += 4;
                    break;
            }
        }

        return called;
    }
}
