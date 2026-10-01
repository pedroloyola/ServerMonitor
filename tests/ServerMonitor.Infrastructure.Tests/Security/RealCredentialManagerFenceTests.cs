using System.Text.RegularExpressions;

namespace ServerMonitor.Infrastructure.Tests.Security;

/// <summary>
/// TEST-REALDATA-AUDIT R-1 (UI.3 gate 1C). A LEXICAL fence over tests/**: the REAL Credential Manager store -
/// <c>new WindowsCredentialStore()</c> (also target-typed <c>WindowsCredentialStore x = new()</c>), the real
/// <c>new CredentialManagerNative(</c>, or a container type-registration <c>&lt;WindowsCredentialStore&gt;()</c> - may
/// appear only inside a test method whose test attribute is <see cref="RealCredentialManagerFactAttribute"/> (opt-in,
/// skipped by default). Anything else needs an exact, justified entry below. What it proves and no more: the text of
/// the tests; the App suite's structural guard (RealDataIsolationGuard) covers what a composed provider resolves.
/// </summary>
public sealed partial class RealCredentialManagerFenceTests
{
    /// <summary>Uses outside an opt-in test, per file (repo-relative). Exact: a new use AND a stale entry both fail.</summary>
    private static readonly IReadOnlyDictionary<string, int> Allowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // (1) A sentinel self-test of the App guard REGISTERS the production type to prove the guard rejects it on the
        // descriptor; the guard never resolves it. (2) GetRequiredService<WindowsCredentialStore>() inside
        // Assert.Throws<RealDataTripwireException>: the isolated registration is the tripwire, nothing is constructed.
        ["tests/ServerMonitor.App.Tests/Architecture/RealDataIsolationGuardTests.cs"] = 2
    };

    [Fact]
    public void The_real_credential_store_is_constructed_only_in_opt_in_tests()
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

            var code = StripComments(File.ReadAllText(path));
            var count = RealStore().Matches(code).Count(match => !InsideOptInTest(code, match.Index));
            if (count > 0)
            {
                outside[relative] = count;
            }
        }

        var failures = outside.Where(pair => pair.Value > Allowlist.GetValueOrDefault(pair.Key))
            .Select(pair => $"{pair.Key}: {pair.Value} real Credential Manager use(s) outside a [RealCredentialManagerFact] test, allowed {Allowlist.GetValueOrDefault(pair.Key)}")
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
    public void The_patterns_catch_the_real_store(string code) => Assert.Matches(RealStore(), code);

    [Theory]
    [InlineData("var store = new WindowsCredentialStore(native);")]
    [InlineData("using var windows = new WindowsCredentialStore(new FakeCredentialManagerNative());")]
    public void A_store_over_a_fake_native_is_not_caught(string code) => Assert.DoesNotMatch(RealStore(), code);

    [Fact]
    public void Only_a_test_attributed_RealCredentialManagerFact_counts_as_opt_in()
    {
        const string code = """
            [RealCredentialManagerFact]
            public async Task OptedIn() { var s = new WindowsCredentialStore(); }

            [Fact]
            public async Task NotOptedIn() { var s = new WindowsCredentialStore(); }
            """;

        var flags = RealStore().Matches(code).Select(match => InsideOptInTest(code, match.Index)).ToArray();

        Assert.Equal([true, false], flags);
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

    /// <summary>The nearest test attribute before the use decides which test method it is in.</summary>
    private static bool InsideOptInTest(string code, int index)
    {
        var last = TestAttribute().Matches(code[..index]).LastOrDefault();
        return last is not null && last.Groups["name"].Value == "RealCredentialManagerFact";
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

    private static string StripComments(string code) => Comment().Replace(code, " ");

    [GeneratedRegex(@"new\s+WindowsCredentialStore\s*\(\s*\)|WindowsCredentialStore\s+\w+\s*=\s*new\s*\(\s*\)|new\s+CredentialManagerNative\s*\(|<\s*WindowsCredentialStore\s*>\s*\(\s*\)")]
    private static partial Regex RealStore();

    [GeneratedRegex(@"\[\s*(?<name>\w*(?:Fact|Theory))\b")]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
