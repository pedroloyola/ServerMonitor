using ServerMonitor.Core.Backup;

namespace ServerMonitor.Infrastructure.Tests.Backup;

// Adopted from Atlas's final review (Atlas-3 / C-11, .boss/tmp/m14.6-final-atlas-export-proof.cs): the temp's tag
// is corrupted inside the export-verify hook WITHOUT throwing, so only the REAL VerifyExport can notice.
public sealed class AtlasExportVerificationTests
{
    [Fact]
    public async Task CorruptCiphertextBeforeVerify_PreservesExistingDestination()
    {
        using var source = await BackupScenarios.SourceAsync();
        var destination = source.OutPath();
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(destination, original);
        var corruptions = 0;
        var service = source.CreateService(faults: step =>
        {
            if (step != "export-verify") return;
            var temporary = Assert.Single(Directory.EnumerateFiles(source.OutDirectory)
                .Where(path => !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)));
            var bytes = File.ReadAllBytes(temporary);
            Assert.True(bytes.Length > 82);
            bytes[^1] ^= 1;
            File.WriteAllBytes(temporary, bytes);
            corruptions++;
        });
        var result = await service.ExportAsync(destination, BackupHarness.Passphrase.AsMemory(), BackupHarness.Passphrase.AsMemory());
        Assert.Equal(1, corruptions);
        Assert.Equal(BackupError.WriteFailed, result.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Single(Directory.EnumerateFiles(source.OutDirectory));
    }
}
