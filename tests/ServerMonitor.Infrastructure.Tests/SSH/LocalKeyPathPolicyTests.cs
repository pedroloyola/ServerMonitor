using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.SSH;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.SSH;

// M14.6 V1 / V11 / N3: ONE local-path predicate for the connect path and the restore probe. Nothing
// non-local is ever opened, probed or even asked for its drive type.
public sealed class LocalKeyPathPolicyTests
{
    public static TheoryData<string> NonLocalPaths() =>
    [
        @"\\host\share\k",
        "//host/share/k",
        @"\\?\UNC\host\share\k",
        @"\\.\UNC\host\share\k",
        @"\\?\C:\keys\id",
        @"\\.\PhysicalDrive0",
        @"\\host",
    ];

    [Theory]
    [MemberData(nameof(NonLocalPaths))]
    public void NonLocalPaths_AreNotCandidates_WithoutAskingTheDriveType(string path)
    {
        var asked = 0;

        Assert.False(LocalKeyPathPolicy.IsLocalCandidate(path, _ => { asked++; return DriveType.Fixed; }));
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.Unknown, false)]
    [InlineData(DriveType.NoRootDirectory, false)]
    public void LocalShape_DependsOnTheDriveType(DriveType driveType, bool expected)
    {
        Assert.Equal(expected, LocalKeyPathPolicy.IsLocalCandidate(@"C:\keys\id", _ => driveType));
    }

    [Theory]
    [InlineData(@"C:keys\id")]
    [InlineData(@"keys\id")]
    [InlineData(@"C:\keys\..\id")]
    [InlineData("")]
    public void NonCanonicalOrRelative_AreNotCandidates(string path)
    {
        Assert.False(LocalKeyPathPolicy.IsLocalCandidate(path, _ => DriveType.Fixed));
    }

    // ---------------------------------------------------------------- connect path (V11 regression)

    [Theory]
    [MemberData(nameof(NonLocalPaths))]
    public void ConnectPath_RejectsNonLocalKeyPaths_BeforeAnyFileSystemAccess(string path)
    {
        var spy = new Spy(DriveType.Fixed);

        Assert.Throws<UnauthorizedAccessException>(() => SshNetSessionFactory.LoadPrivateKey(path, null, spy.Access));

        Assert.Empty(spy.Calls);
    }

    [Fact]
    public void ConnectPath_RejectsAMappedNetworkDrive_BeforeAnyOpen()
    {
        var spy = new Spy(DriveType.Network);

        Assert.Throws<UnauthorizedAccessException>(() => SshNetSessionFactory.LoadPrivateKey(@"Z:\keys\id", null, spy.Access));

        Assert.Equal([@"drive:Z:\"], spy.Calls);
    }

    [Fact]
    public void ConnectPath_RejectsAReparsePointOnTheWay_BeforeTheOpen_CheckingRootToLeaf()
    {
        var spy = new Spy(DriveType.Fixed, reparse: @"C:\keys");

        Assert.Throws<UnauthorizedAccessException>(() => SshNetSessionFactory.LoadPrivateKey(@"C:\keys\sub\id", null, spy.Access));

        // N3: the ancestor is checked before anything below it; the leaf is never reached, nothing is opened.
        Assert.Equal([@"drive:C:\", @"attributes:C:\", @"attributes:C:\keys"], spy.Calls);
    }

    [Fact]
    public void ConnectPath_OpensALocalRegularFile_AfterCheckingEveryComponent()
    {
        var spy = new Spy(DriveType.Fixed);

        Assert.ThrowsAny<Exception>(() => SshNetSessionFactory.LoadPrivateKey(@"C:\keys\id", null, spy.Access));

        Assert.Equal([@"drive:C:\", @"attributes:C:\", @"attributes:C:\keys", @"attributes:C:\keys\id", @"open:C:\keys\id"], spy.Calls);
    }

    // ---------------------------------------------------------------- restore probe (C-7)

    [Theory]
    [MemberData(nameof(NonLocalPaths))]
    public void Probe_NonLocal_IsNotChecked_AndTouchesNothing(string path)
    {
        var spy = new Spy(DriveType.Fixed);

        Assert.Equal(KeyPathStatus.NotChecked, LocalKeyPathPolicy.Probe(path, spy.Access));
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public void Probe_NetworkDrive_IsNotChecked_AndNeverDescribed()
    {
        var spy = new Spy(DriveType.Network);

        Assert.Equal(KeyPathStatus.NotChecked, LocalKeyPathPolicy.Probe(@"Z:\keys\id", spy.Access));
        Assert.Equal([@"drive:Z:\"], spy.Calls);
    }

    [Fact]
    public void Probe_ReparseAncestor_IsUnsupported_LeafNeverDescribed()
    {
        var spy = new Spy(DriveType.Fixed, reparse: @"C:\keys");

        Assert.Equal(KeyPathStatus.Unsupported, LocalKeyPathPolicy.Probe(@"C:\keys\id", spy.Access));
        Assert.DoesNotContain(spy.Calls, call => call.StartsWith("describe:", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_MissingDirectory_IsMissing()
    {
        var spy = new Spy(DriveType.Fixed, missing: @"C:\keys");

        Assert.Equal(KeyPathStatus.Missing, LocalKeyPathPolicy.Probe(@"C:\keys\id", spy.Access));
    }

    [Theory]
    [InlineData(0, false, KeyPathStatus.Missing)]      // LocalSshKeyPathKind.Missing
    [InlineData(1, true, KeyPathStatus.Unsupported)]   // RegularFile, but a reparse point
    [InlineData(2, false, KeyPathStatus.Unsupported)]  // Directory
    [InlineData(3, false, KeyPathStatus.Unsupported)]  // Other
    public void Probe_Leaf(int kind, bool reparse, KeyPathStatus expected)
    {
        var spy = new Spy(DriveType.Fixed, leaf: new LocalSshKeyFileMetadata((LocalSshKeyPathKind)kind, reparse, 10));

        Assert.Equal(expected, LocalKeyPathPolicy.Probe(@"C:\keys\id", spy.Access));
    }

    [Fact]
    public void Probe_RegularLocalFile_IsFine_AndNeverOpened()
    {
        var spy = new Spy(DriveType.Fixed);

        Assert.Null(LocalKeyPathPolicy.Probe(@"C:\keys\id", spy.Access));
        Assert.DoesNotContain(spy.Calls, call => call.StartsWith("open:", StringComparison.Ordinal));
    }

    private sealed class Spy
    {
        public Spy(
            DriveType driveType,
            string? reparse = null,
            string? missing = null,
            LocalSshKeyFileMetadata? leaf = null)
        {
            Access = new LocalKeyFileAccess(
                root =>
                {
                    Calls.Add("drive:" + root);
                    return driveType;
                },
                path =>
                {
                    Calls.Add("attributes:" + path);
                    if (path == missing)
                    {
                        throw new DirectoryNotFoundException();
                    }

                    return path == reparse
                        ? FileAttributes.Directory | FileAttributes.ReparsePoint
                        : FileAttributes.Directory;
                },
                path =>
                {
                    Calls.Add("open:" + path);
                    throw new IOException("synthetic open");
                },
                path =>
                {
                    Calls.Add("describe:" + path);
                    return leaf ?? new LocalSshKeyFileMetadata(LocalSshKeyPathKind.RegularFile, false, 10);
                });
        }

        public List<string> Calls { get; } = [];

        public LocalKeyFileAccess Access { get; }
    }
}
