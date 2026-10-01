using System.Text.RegularExpressions;
using ServerMonitor.TestSupport;

namespace ServerMonitor.Infrastructure.Tests.Security;

/// <summary>
/// TEST-REALDATA-AUDIT R-1 (UI.3 gate 1C). A LEXICAL fence over tests/**: every reference that can bring the REAL
/// Credential Manager store into a test - <c>new WindowsCredentialStore()</c> (also target-typed <c>new()</c>), the real
/// <c>new CredentialManagerNative(</c>, <c>WindowsCredentialStore</c> in ANY generic argument position
/// (<c>&lt;WindowsCredentialStore&gt;</c>, <c>&lt;IServerCredentialStore, WindowsCredentialStore&gt;</c>) and
/// <c>typeof(WindowsCredentialStore)</c> (reflection, <c>Activator</c>) - must sit INSIDE the body of a test method
/// attributed <see cref="RealCredentialManagerFactAttribute"/> (opt-in, skipped by default), the body being delimited
/// by its closing brace. Anything else needs an exact, justified entry below. Matching runs on code with comments AND
/// literal contents blanked (<see cref="CSharpSourceText"/>), so text in a string neither hides nor fakes a use.
/// What it proves and no more: the text of the tests; the App suite's structural guard (RealDataIsolationGuard)
/// covers what a composed provider resolves.
/// </summary>
public sealed partial class RealCredentialManagerFenceTests
{
    /// <summary>Uses outside an opt-in test body, per file (repo-relative). Exact: a new use AND a stale entry both fail.</summary>
    private static readonly IReadOnlyDictionary<string, int> Allowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // (1) AddSingleton<WindowsCredentialStore>() in a sentinel self-test: the guard rejects it on the descriptor and
        // never resolves it. (2) GetRequiredService<WindowsCredentialStore>() inside Assert.Throws<RealDataTripwireException>:
        // the isolated registration is the tripwire, nothing is constructed.
        ["tests/ServerMonitor.App.Tests/Architecture/RealDataIsolationGuardTests.cs"] = 2,
        // Assert.IsNotType<WindowsCredentialStore>(raw): a type assertion on the harness's in-memory store.
        ["tests/ServerMonitor.App.Tests/Qa/QaProxyJumpHarnessTests.cs"] = 1,
        // (1) Assert.IsNotType<WindowsCredentialStore>(...): a type assertion on the isolated store. (2)
        // GetUninitializedObject(typeof(WindowsCredentialStore)): an instance with NO constructor run (no native wrapper),
        // used only for the QaStartupIsolation type checks to refuse; nothing calls it, so the Credential Manager is
        // never reached.
        ["tests/ServerMonitor.App.Tests/Qa/QaStartupIsolationTests.cs"] = 2,
        // typeof(...) x3: the production DESCRIPTOR's ImplementationType, and the type the production factory REQUESTS
        // from a RequestRecordingProvider that constructs nothing.
        ["tests/ServerMonitor.App.Tests/Services/BackupSettingsParticipantTests.cs"] = 3,
        // AddSingleton<WindowsCredentialStore>(_ => throw ...): THE tripwire that replaces the real store.
        ["tests/ServerMonitor.App.Tests/TestSupport/IsolatedAppComposition.cs"] = 1,
        // typeof(...) x3 in the guard: find the effective descriptor, probe that it trips (only when it is a factory),
        // exclude it from the constructed phase.
        ["tests/ServerMonitor.App.Tests/TestSupport/RealDataIsolationGuard.cs"] = 3
    };

    [Fact]
    public void The_real_credential_store_is_referenced_only_in_opt_in_tests()
    {
        var outside = new Dictionary<string, int>(StringComparer.Ordinal);
        var repository = RepositoryRoot();

        foreach (var path in Directory.EnumerateFiles(Path.Combine(repository, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repository, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith("/RealCredentialManagerFenceTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var count = UsesOutsideOptIn(File.ReadAllText(path)).Count;
            if (count > 0)
            {
                outside[relative] = count;
            }
        }

        var failures = outside.Where(pair => pair.Value > Allowlist.GetValueOrDefault(pair.Key))
            .Select(pair => $"{pair.Key}: {pair.Value} real Credential Manager reference(s) outside a [RealCredentialManagerFact] test body, allowed {Allowlist.GetValueOrDefault(pair.Key)}")
            .Concat(Allowlist.Where(pair => outside.GetValueOrDefault(pair.Key) < pair.Value)
                .Select(pair => $"{pair.Key}: allowlist says {pair.Value}, found {outside.GetValueOrDefault(pair.Key)} - lower or remove the entry"))
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("var store = new WindowsCredentialStore();")]
    [InlineData("var store = new WindowsCredentialStore ( );")]
    [InlineData("WindowsCredentialStore store = new();")]
    [InlineData("var native = new CredentialManagerNative();")]
    [InlineData("services.AddSingleton<WindowsCredentialStore>();")]
    [InlineData("services.AddSingleton<IServerCredentialStore, WindowsCredentialStore>();")]
    [InlineData("Register<IServerCredentialStore, WindowsCredentialStore >();")]
    [InlineData("Activator.CreateInstance(typeof(WindowsCredentialStore));")]
    [InlineData("var t = typeof ( WindowsCredentialStore );")]
    public void The_patterns_catch_the_real_store(string code) => Assert.Single(UsesOutsideOptIn(code));

    [Theory]
    [InlineData("var store = new WindowsCredentialStore(native);")]
    [InlineData("using var windows = new WindowsCredentialStore(new FakeCredentialManagerNative());")]
    [InlineData("var s = \"new WindowsCredentialStore()\"; // new WindowsCredentialStore()")]
    public void A_store_over_a_fake_native_or_text_in_a_literal_is_not_caught(string code) =>
        Assert.Empty(UsesOutsideOptIn(code));

    [Fact]
    public void Only_the_body_of_a_RealCredentialManagerFact_test_counts_as_opt_in()
    {
        // Vigil L-1C-3: a helper placed AFTER the opt-in test must not inherit its status; braces inside strings
        // must not end the body early.
        const string code = """
            [RealCredentialManagerFact]
            public async Task OptedIn() { var x = "}"; var s = new WindowsCredentialStore(); }

            private static WindowsCredentialStore Helper() => new WindowsCredentialStore();

            [Fact]
            public async Task NotOptedIn() { var s = new WindowsCredentialStore(); }

            [RealCredentialManagerFact]
            public Task ExpressionBodied() => Use(new WindowsCredentialStore());

            private void After() { var s = new WindowsCredentialStore(); }
            """;

        var outside = UsesOutsideOptIn(code);

        // Five constructions: the two inside opt-in bodies are allowed; Helper, NotOptedIn and After are not.
        Assert.Equal(3, outside.Count);
        Assert.True(outside[0] > code.IndexOf("Helper()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("1", true)]
    public void Only_exactly_1_opts_in(string? value, bool expected) =>
        Assert.Equal(expected, RealCredentialManagerFactAttribute.IsOptedIn(value));

    [Fact]
    public void Without_the_opt_in_the_real_test_is_skipped_with_a_reason()
    {
        var attribute = new RealCredentialManagerFactAttribute();
        var optedIn = OperatingSystem.IsWindows()
                      && RealCredentialManagerFactAttribute.IsOptedIn(
                          Environment.GetEnvironmentVariable(RealCredentialManagerFactAttribute.OptInVariable));

        if (optedIn)
        {
            Assert.Null(attribute.Skip);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(attribute.Skip));
        }
    }

    /// <summary>Indexes of real-store references that are not inside the body of an opt-in test.</summary>
    internal static IReadOnlyList<int> UsesOutsideOptIn(string source)
    {
        var code = CSharpSourceText.CodeOnly(source);
        var spans = OptInAttribute().Matches(code).Select(match => (Start: match.Index, End: BodyEnd(code, match.Index + match.Length))).ToList();

        return [.. RealStore().Matches(code)
            .Select(match => match.Index)
            .Where(index => !spans.Any(span => index >= span.Start && index <= span.End))];
    }

    /// <summary>
    /// The end of the member after an attribute: the brace closing a block body, or the ';' ending an expression body.
    /// Braces are counted on code whose literals are blanked, so a brace in a string does not count.
    /// </summary>
    private static int BodyEnd(string code, int from)
    {
        var brace = code.IndexOf('{', from);
        var arrow = code.IndexOf("=>", from, StringComparison.Ordinal);
        if (arrow >= 0 && (brace < 0 || arrow < brace))
        {
            var semicolon = code.IndexOf(';', arrow);
            return semicolon < 0 ? code.Length : semicolon;
        }

        if (brace < 0)
        {
            return from;
        }

        var depth = 0;
        for (var i = brace; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return code.Length;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"new\s+WindowsCredentialStore\s*\(\s*\)|WindowsCredentialStore\s+\w+\s*=\s*new\s*\(\s*\)|new\s+CredentialManagerNative\s*\(|\bWindowsCredentialStore\s*>|typeof\s*\(\s*WindowsCredentialStore\s*\)")]
    private static partial Regex RealStore();

    [GeneratedRegex(@"\[\s*RealCredentialManagerFact\b[^\]]*\]")]
    private static partial Regex OptInAttribute();
}
