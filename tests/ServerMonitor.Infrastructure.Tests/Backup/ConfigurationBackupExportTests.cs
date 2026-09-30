using System.Text.Json.Nodes;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Tests.Backup;

// M14.6 §4 export, against the REAL stores, gate and codec.
public sealed class ConfigurationBackupExportTests
{
    private static readonly ReadOnlyMemory<char> Pass = BackupHarness.Passphrase.AsMemory();

    [Fact]
    public async Task Export_ReferencedTrustOnly_JumpTrustIncluded_SummaryCounts()
    {
        using var source = await BackupScenarios.SourceAsync();

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.True(result.Succeeded);
        var summary = result.Summary!;
        Assert.Equal(2, summary.DirectServers);
        Assert.Equal(2, summary.RoutedServers);
        Assert.Equal(6, summary.Credentials);
        Assert.Equal(2, summary.DirectTrustedHostKeys);   // web + bastion (the jump), not old
        Assert.Equal(2, summary.RoutedTrustedHostKeys);   // the two used routes, not 10.9.9.9
        Assert.Equal(2, summary.ExcludedUnreferencedTrustedHostKeys);
        Assert.Empty(summary.MissingCredentials);

        var payload = await PayloadOf(source.OutPath());
        var hosts = payload["knownHosts"]!.AsArray().Select(node => (string)node!["endpoint"]!["host"]!).ToList();
        Assert.Equal(["bastion.example.com", "web.example.com"], hosts);
        var routedTargets = payload["routedKnownHosts"]!.AsArray().Select(node => (string)node!["endpoint"]!["host"]!).ToList();
        Assert.Equal(["10.0.0.5", "10.0.0.6"], routedTargets);
        Assert.DoesNotContain("old.example.com", payload.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("10.9.9.9", payload.ToJsonString(), StringComparison.Ordinal);
        source.AssertNoSecretsLeaked();
    }

    [Fact]
    public async Task Export_AllFourCredentialKinds_TravelAsLowercaseHex()
    {
        using var source = await BackupScenarios.SourceAsync();

        await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        var credentials = (await PayloadOf(source.OutPath()))["credentials"]!.AsArray();
        Assert.Equal(
            ["jump-key-passphrase", "jump-password", "key-passphrase", "key-passphrase", "password", "password"],
            credentials.Select(node => (string)node!["kind"]!).Order().ToList());
        Assert.All(credentials, node =>
        {
            var hex = (string)node!["secretUtf8Hex"]!;
            Assert.Matches("^[0-9a-f]+$", hex);
            Assert.StartsWith(BackupHarness.SecretMarker, System.Text.Encoding.UTF8.GetString(Convert.FromHexString(hex)), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Export_MissingCredential_IsExportedAndFlagged()
    {
        using var source = await BackupScenarios.SourceAsync();
        var jump = new CredentialReference(BackupScenarios.RoutedPassword, ServerCredentialKind.JumpPrivateKeyPassphrase, BackupHarness.Id(2002));
        Assert.True(await source.Raw.DeleteAsync(jump));

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.True(result.Succeeded);
        var flag = Assert.Single(result.Summary!.MissingCredentials);
        Assert.Equal(new BackupCredentialFlag(BackupScenarios.RoutedPassword, "routed-2", IsJump: true), flag);
        var missing = Assert.Single((await PayloadOf(source.OutPath()))["missingCredentials"]!.AsArray());
        Assert.Equal("jump-key-passphrase", (string)missing!["kind"]!);
    }

    // D6: unknown is not missing. The exception message carries a marker that must never reach a log (C-6).
    [Fact]
    public async Task Export_UnreadableCredential_Fails_WritesNothing_LogsNoMessage()
    {
        using var source = await BackupScenarios.SourceAsync();
        source.Raw.ReadFault = _ => new InvalidOperationException(BackupHarness.SecretMarker + "native error text");

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.Equal(BackupError.CredentialStoreUnavailable, result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(source.OutDirectory));
        Assert.Contains(source.Log.Entries, entry => entry.Contains("InvalidOperationException", StringComparison.Ordinal));
        source.AssertNoSecretsLeaked();
    }

    [Fact]
    public async Task Export_InvalidTrustFile_Fails()
    {
        using var source = await BackupScenarios.SourceAsync();
        await File.WriteAllTextAsync(source.TrustOptions.FilePath, "{ not json");
        source.Reopen();

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.Equal(BackupError.TrustStoreUnreadable, result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(source.OutDirectory));
    }

    // V6 / cp 17: the temp is verified BEFORE it replaces anything.
    [Fact]
    public async Task Export_VerifyFailure_LeavesAPreviousBackupByteIdentical_AndRemovesOnlyItsTemp()
    {
        using var source = await BackupScenarios.SourceAsync();
        var destination = source.OutPath();
        byte[] previous = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(destination, previous);
        var service = source.CreateService(faults: step =>
        {
            if (step == "export-verify")
            {
                throw new IOException("verify failed");
            }
        });

        var result = await service.ExportAsync(destination, Pass, Pass);

        Assert.Equal(BackupError.WriteFailed, result.Error);
        Assert.Equal(previous, await File.ReadAllBytesAsync(destination));
        Assert.Equal([destination], Directory.EnumerateFiles(source.OutDirectory).ToList());
    }

    [Fact]
    public async Task Export_AnUnrelatedUserFileNamedLikeATemp_IsNeverTouched()
    {
        using var source = await BackupScenarios.SourceAsync();
        var destination = source.OutPath();
        var userFile = destination + ".tmp";
        await File.WriteAllTextAsync(userFile, "mine");

        var result = await source.CreateService().ExportAsync(destination, Pass, Pass);

        Assert.True(result.Succeeded);
        Assert.Equal("mine", await File.ReadAllTextAsync(userFile));
        Assert.Equal(2, Directory.EnumerateFiles(source.OutDirectory).Count());
    }

    [Fact]
    public async Task Export_OverwritesTheChosenDestination_OnSuccess()
    {
        using var source = await BackupScenarios.SourceAsync();
        var destination = source.OutPath();
        await File.WriteAllBytesAsync(destination, [9, 9]);

        Assert.True((await source.CreateService().ExportAsync(destination, Pass, Pass)).Succeeded);

        Assert.True(new FileInfo(destination).Length > BackupFileCodec.MinFileLength);
        Assert.Single(Directory.EnumerateFiles(source.OutDirectory));
    }

    // C-3 / C-11: a retry (or any second export) re-encrypts with fresh salt and nonce.
    [Fact]
    public async Task TwoExportsOfTheSameConfiguration_DifferInSaltNonceAndCiphertext()
    {
        using var source = await BackupScenarios.SourceAsync();
        var service = source.CreateService();

        await service.ExportAsync(source.OutPath("a"), Pass, Pass);
        await service.ExportAsync(source.OutPath("b"), Pass, Pass);

        var a = await File.ReadAllBytesAsync(source.OutPath("a"));
        var b = await File.ReadAllBytesAsync(source.OutPath("b"));
        Assert.NotEqual(a[18..50], b[18..50]);
        Assert.NotEqual(a[50..62], b[50..62]);
        Assert.NotEqual(a[66..], b[66..]);
    }

    public static TheoryData<string, string, BackupPassphraseProblem> RejectedPassphrases() => new()
    {
        { "elevenchars", "elevenchars", BackupPassphraseProblem.TooShort },
        { new string('p', 257), new string('p', 257), BackupPassphraseProblem.TooLong },
        { "lone surrogate " + '\uD800', "lone surrogate " + '\uD800', BackupPassphraseProblem.InvalidCharacters },
        { BackupHarness.Passphrase, BackupHarness.Passphrase + "!", BackupPassphraseProblem.ConfirmationMismatch },
    };

    // Vigil L3 / C-4: the REAL confirmation is checked before anything else, and no KDF runs.
    [Theory]
    [MemberData(nameof(RejectedPassphrases), DisableDiscoveryEnumeration = true)]
    public async Task Export_PassphrasePolicy_IsCheckedFirst_NoFile_NoKdf(string passphrase, string confirmation, BackupPassphraseProblem expected)
    {
        using var source = await BackupScenarios.SourceAsync();
        var kdf = 0;
        var service = source.CreateService(codec: new BackupFileCodec(new BackupFileCodecSeams
        {
            WriterIterations = BackupFileCodec.MinimumIterations,
            OnKeyDerivation = _ => kdf++,
        }));

        var result = await service.ExportAsync(source.OutPath(), passphrase.AsMemory(), confirmation.AsMemory());

        Assert.Equal(expected, result.PassphraseProblem);
        Assert.Null(result.Error);
        Assert.Equal(0, kdf);
        Assert.Empty(Directory.EnumerateFileSystemEntries(source.OutDirectory));
    }

    [Fact]
    public async Task Export_WhileAJournalIsPending_IsRefused_WithTheFolder()
    {
        using var source = await BackupScenarios.SourceAsync();
        Directory.CreateDirectory(source.JournalDirectory);

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.Equal(BackupError.RestorePending, result.Error);
        Assert.Equal(source.JournalDirectory, result.PendingJournalDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(source.OutDirectory));
    }

    [Fact]
    public async Task Export_WhileARestoreHoldsTheGate_IsBusy()
    {
        using var source = await BackupScenarios.SourceAsync();
        Assert.NotNull(await source.Gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)));

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.Equal(BackupError.Busy, result.Error);
    }

    // Export never produces a backup its own reader would reject.
    [Fact]
    public async Task Export_AServerTheReaderWouldReject_FailsAsInvalidContent()
    {
        using var source = new BackupHarness();
        await source.SeedAsync([BackupHarness.Direct(1, "h") with { Name = new string('n', 300) }]);

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.Equal(BackupError.InvalidContent, result.Error);
    }

    // Vigil N2: export emits the store's NORMALIZED entries, so a legacy mixed-case / trailing-dot trust file
    // still produces a backup its own reader accepts.
    [Fact]
    public async Task Export_NonNormalizedTrustFile_IsExportedNormalized_AndRoundTrips()
    {
        using var source = new BackupHarness();
        await source.SeedAsync([BackupHarness.Direct(1, "Web.Example.COM.")]);
        var entry = BackupHarness.DirectTrustEntry("web.example.com");
        await File.WriteAllTextAsync(
            source.TrustOptions.FilePath,
            $$"""[{"endpoint":{"host":"WEB.Example.com.","port":22},"identity":{"algorithm":"ssh-ed25519","sha256Fingerprint":"{{entry.Identity.Sha256Fingerprint}}"},"confirmedAt":"2026-03-04T05:06:07+00:00"}]""");
        source.Reopen();

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Summary!.DirectTrustedHostKeys);
        var payload = await PayloadOf(source.OutPath());
        Assert.Equal("web.example.com", (string)payload["knownHosts"]![0]!["endpoint"]!["host"]!);
        using var target = new BackupHarness();
        var inspected = await target.CreateService().InspectAsync(source.OutPath(), Pass);
        Assert.True(inspected.Succeeded);
        inspected.Plan!.Dispose();
    }

    [Fact]
    public async Task Export_LegacyOnlyCredential_Succeeds()
    {
        using var native = new Security.CredentialNamespaceMigrationTests.DictionaryCredentialManagerNative();
        using var windows = new WindowsCredentialStore(native);
        using var source = new BackupHarness(windows);
        var server = BackupHarness.Direct(1, "web.example.com");
        await source.SeedAsync([server], withSecrets: false);
        var reference = ServerCredentialReferences.Target(server)!.Value;
        native.Seed(CredentialTargetName.CreateLegacy(reference), BackupHarness.SecretMarker + "legacy");

        var result = await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Summary!.Credentials);
    }

    internal static async Task<JsonObject> PayloadOf(string path)
    {
        using var decoded = BackupHarness.FastCodec.Decrypt(await File.ReadAllBytesAsync(path), BackupHarness.Passphrase);
        Assert.True(decoded.IsSuccess);
        return JsonNode.Parse(decoded.Plaintext!.Span)!.AsObject();
    }
}
