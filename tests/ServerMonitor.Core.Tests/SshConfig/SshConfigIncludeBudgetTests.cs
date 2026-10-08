using System.Text;
using ServerMonitor.Core.SshConfig;
using Fs = ServerMonitor.Core.Tests.SshConfig.InMemorySshConfigFileSystem;

namespace ServerMonitor.Core.Tests.SshConfig;

/// <summary>
/// Vigil M14.4a Safe Include follow-ups: global import budget + enumeration cache (M1), cancellation,
/// canonical ~/.ssh spelling (L3), and the post-open identity statuses (L1/L2) as Core sees them.
/// </summary>
public sealed class SshConfigIncludeBudgetTests
{
    private static SshConfigImportResult Import(Fs fs, CancellationToken cancellationToken = default) =>
        SshConfigResolver.Import(fs, Fs.Profile, cancellationToken);

    private static Fs WithEntries(Fs fs, string directory, int count)
    {
        fs.AddDirectory(directory);
        for (var i = 0; i < count; i++)
        {
            fs.File($"{directory}/f{i:D5}", string.Empty);
        }

        return fs;
    }

    // ---- M1: global budget and enumeration cache

    [Fact]
    public void VigilProbe_RepeatedGlobIncludes_FailFastWithTooManyFiles_EnumeratingTheDirectoryOnce()
    {
        var root = new StringBuilder();
        while (root.Length < 250 * 1024)
        {
            root.Append("Include d/*.x\n");
        }

        root.Append("Host a\n");
        var fs = WithEntries(new Fs().File("config", root.ToString()), "d", 4000);

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooManyFiles, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\config", result.ErrorDetail);
        Assert.Empty(result.Hosts);
        Assert.Equal([Fs.SshDirectory + @"\d"], fs.Enumerations);

        // Fail fast, counted rather than timed: each Include argument admitted by the budget probes
        // its directory chain before globbing, so the directory is probed exactly once per admitted
        // argument and the ~17,000 arguments past the budget are never looked at.
        Assert.Equal(
            SshConfigIncludeExpander.MaxIncludeArguments,
            fs.InfoQueries.Count(path => path == Fs.SshDirectory + @"\d"));
    }

    [Fact]
    public void RepeatedGlobIncludesWithinBudget_EnumerateEachDirectoryOnce()
    {
        var root = string.Concat(Enumerable.Repeat("Include d/*.x ~/.ssh/d/*.y\n", 128)) + "Host a\n  User u\n";
        var fs = WithEntries(new Fs().File("config", root), "d", 4000);

        var result = Import(fs);

        Assert.Equal(SshConfigImportStatus.Loaded, result.Status);
        Assert.Equal("u", Assert.Single(result.Hosts).User);
        Assert.Equal([Fs.SshDirectory + @"\d"], fs.Enumerations);
    }

    [Fact]
    public void MoreThan256IncludeArguments_FailTheWholeImport()
    {
        var fs = new Fs().File("config", "Include" + string.Concat(Enumerable.Range(0, 257).Select(i => $" missing{i}")) + "\nHost a\n");

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooManyFiles, result.ErrorCode);
    }

    [Fact]
    public void Exactly256IncludeArguments_Load()
    {
        var fs = new Fs().File("config", "Include" + string.Concat(Enumerable.Range(0, 256).Select(i => $" missing{i}")) + "\nHost a\n");

        Assert.Equal(SshConfigImportStatus.Loaded, Import(fs).Status);
    }

    [Fact]
    public void MoreThan64DistinctDirectories_FailTheWholeImport()
    {
        var fs = new Fs();
        var root = new StringBuilder();
        for (var i = 0; i < 65; i++)
        {
            fs.AddDirectory($"d{i:D2}");
            root.Append($"Include d{i:D2}/*\n");
        }

        fs.File("config", root + "Host a\n");

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooManyFiles, result.ErrorCode);
        Assert.Equal(64, fs.Enumerations.Count);
    }

    [Fact]
    public void MoreThan16384EntriesInTotal_FailTheWholeImport()
    {
        var fs = new Fs().File("config", "Include d1/*.x d2/*.x d3/*.x d4/*.x d5/*.x\nHost a\n");
        foreach (var directory in new[] { "d1", "d2", "d3", "d4", "d5" })
        {
            WithEntries(fs, directory, 4000);
        }

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooManyFiles, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\d5", result.ErrorDetail);
    }

    // ---- cancellation

    [Fact]
    public void Cancellation_IsObservedBetweenLinesAndArguments_AndStopsFurtherReads()
    {
        using var cancellation = new CancellationTokenSource();
        var fs = new Fs()
            .File("config", "Include a b\nHost h\n")
            .File("a", "User from-a\n")
            .File("b", "Port 22\n");
        fs.OnRead = path =>
        {
            if (path.EndsWith(@"\a", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        Assert.ThrowsAny<OperationCanceledException>(() => Import(fs, cancellation.Token));
        Assert.DoesNotContain(Fs.SshDirectory + @"\b", fs.Reads);
    }

    [Fact]
    public void AlreadyCancelled_ReadsNothing()
    {
        var fs = new Fs().File("config", "Host a\n");

        Assert.ThrowsAny<OperationCanceledException>(() => Import(fs, new CancellationToken(canceled: true)));
        Assert.Empty(fs.Reads);
    }

    // ---- L3: one canonical ~/.ssh spelling after containment

    [Fact]
    public void ContainedPathsAreRespelledWithTheCanonicalSshPrefix()
    {
        var fs = new Fs()
            .File("config", "Include ~/.SSH/x ~/.Ssh/conf.d/*\nHost a\n")
            .File("x", "User x\n")
            .File("conf.d/y", "Port 2022\n");

        var host = Assert.Single(Import(fs).Hosts);

        Assert.Equal("x", host.User);
        Assert.Equal(2022, host.Port);
        Assert.Equal(
            [Fs.SshDirectory + @"\config", Fs.SshDirectory + @"\x", Fs.SshDirectory + @"\conf.d\y"],
            fs.Reads);
        Assert.Equal([Fs.SshDirectory + @"\conf.d"], fs.Enumerations);
    }

    [Fact]
    public void CycleThroughADifferentSpelling_IsReportedWithTheCanonicalPath()
    {
        var result = Import(new Fs().File("config", "Include ~/.SSH/CONFIG\nHost a\n"));

        Assert.Equal(SshConfigImportErrorCode.IncludeCycle, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\CONFIG", result.ErrorDetail);
    }

    // ---- L1/L2 as seen by Core: identity statuses from the file system

    [Theory]
    [InlineData(SshConfigReadStatus.FinalPathMismatch, SshConfigFindingReason.IncludeFinalPathMismatch)]
    [InlineData(SshConfigReadStatus.IdentityUnverifiable, SshConfigFindingReason.IncludeFinalPathMismatch)]
    [InlineData(SshConfigReadStatus.HardLinked, SshConfigFindingReason.IncludeHardLinked)]
    public void IncludedFileWhoseIdentityFails_CannotBeVerified(SshConfigReadStatus status, SshConfigFindingReason reason)
    {
        var fs = new Fs().File("config", "Host a\n  Include x\n").File("x", "ProxyJump none\n"u8.ToArray(), status);

        var result = Import(fs);
        var host = Assert.Single(result.Hosts);

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == reason);
        Assert.Contains(result.Diagnostics, d => d.Kind == SshConfigDiagnosticKind.IncludeNotVerified && d.Argument == "x");
    }

    [Theory]
    [InlineData(SshConfigReadStatus.FinalPathMismatch, SshConfigImportErrorCode.ConfigIsLink)]
    [InlineData(SshConfigReadStatus.HardLinked, SshConfigImportErrorCode.ConfigIsLink)]
    [InlineData(SshConfigReadStatus.IdentityUnverifiable, SshConfigImportErrorCode.Unreadable)]
    public void RootConfigWhoseIdentityFails_FailsTheWholeImport(SshConfigReadStatus status, SshConfigImportErrorCode expected)
    {
        var result = Import(new Fs().File("config", "Host a\n"u8.ToArray(), status));

        Assert.Equal(expected, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }
}
