using System.Text;
using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.VigilProbe;

// Adopted from Vigil's final review (Vigil-2, .boss/tmp/m14.6-vigil-probe-tests.patch). The UNC/device probe paths
// are written with the double leading backslash they represent (the patch text showed a single one).
public sealed class VigilGuardAndProbeTests
{
    private static string Escaped(string value) =>
        string.Concat(value.Select(c => @"\u" + ((int)c).ToString("x4")));

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Vigil_Guard_NeverProvesAbsenceOfAReferenceTheStoreCouldRead(bool bom, bool escaped)
    {
        var id = Guid.NewGuid();
        var text = escaped ? Escaped(id.ToString("D")) : id.ToString("D").ToUpperInvariant();
        var json = "[{\"id\":\"" + Guid.NewGuid() + "\",\"credentialReferenceId\":\"" + text + "\"}]";
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bom)
        {
            bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        }

        var ok = ReferenceGuard.TryCreate(bytes, out var isReferenced);
        Assert.True(!ok || isReferenced(id));
    }

    // Vigil-2: a BOM-prefixed servers.json (which the store itself reads) stays PROVABLE, not merely safe.
    [Fact]
    public void Vigil_Guard_BomPrefixedJson_IsProvable_LikeTheStoreParser()
    {
        var referenced = Guid.NewGuid();
        var json = "[{\"credentialReferenceId\":\"" + Escaped(referenced.ToString("D")) + "\"}]";
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)];

        Assert.True(ReferenceGuard.TryCreate(bytes, out var isReferenced));
        Assert.True(isReferenced(referenced));
        Assert.False(isReferenced(Guid.NewGuid()));
    }

    [Fact]
    public void Vigil_Guard_TruncatedJsonWithEscapes_IsUnprovable()
    {
        var id = Guid.NewGuid();
        var json = "[{\"credentialReferenceId\":\"" + Escaped(id.ToString("D")) + "\"";
        Assert.False(ReferenceGuard.TryCreate(Encoding.UTF8.GetBytes(json), out _));
    }

    [Theory]
    [InlineData(@"\\?\C:\keys\id")]
    [InlineData(@"\\.\C:\keys\id")]
    [InlineData(@"\\?\UNC\host\share\id")]
    [InlineData(@"\\host\share\id")]
    [InlineData(@"//host/share/id")]
    [InlineData(@"C:/keys/id")]
    [InlineData(@"C:\keys\..\id")]
    [InlineData(@"C:\keys\id.")]
    [InlineData(@"C:keys\id")]
    [InlineData(@"\keys\id")]
    [InlineData(@"\\host@SSL@443\share\id")]
    [InlineData(@"\\host@8080\DavWWWRoot\id")]
    public void Vigil_Probe_NonLocalOrNonCanonical_IsNeverTouched(string path)
    {
        var touched = 0;
        var access = new LocalKeyFileAccess(
            _ => { touched++; return DriveType.Network; },
            _ => { touched++; throw new InvalidOperationException(); },
            _ => { touched++; throw new InvalidOperationException(); },
            _ => { touched++; throw new InvalidOperationException(); });

        var status = LocalKeyPathPolicy.Probe(path, access);
        Assert.Equal(KeyPathStatus.NotChecked, status);
        Assert.True(touched <= 1); // at most the drive-type query, never attributes/open/describe
    }
}
