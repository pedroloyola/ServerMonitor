using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class Ui6LoadStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ui6-load-" + Guid.NewGuid());
    private string Direct => Path.Combine(_root, "servers.json");
    private string Routed => Path.Combine(_root, "routed-servers.json");
    private JsonServerRepository Repository() => new(new ServerStorageOptions { FilePath = Direct },
        NullLogger<JsonServerRepository>.Instance, new ConfigurationWriteGate());
    private async Task Write(string path, string text) { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(path, text); }

    [Fact]
    public async Task AbsentBoth_IsNotFound_AndLegacyListIsEmpty()
    {
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.NotFound, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("[]", ServerLoadStatus.Loaded)]
    [InlineData("{ invalid", ServerLoadStatus.Unavailable)]
    [InlineData("{}", ServerLoadStatus.Unavailable)]
    [InlineData("[null]", ServerLoadStatus.Unavailable)]
    public async Task DirectStates_PreserveBytesAndLegacyList(string json, ServerLoadStatus expected)
    {
        await Write(Direct, json);
        var before = await File.ReadAllBytesAsync(Direct);
        using var repository = Repository();
        Assert.Empty(await repository.GetAllAsync());
        Assert.Equal(expected, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(Direct));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(1, ServerLoadStatus.Loaded)]
    [InlineData(2, ServerLoadStatus.Unavailable)]
    public async Task RoutedOnly_EmptyOrFutureSchema(int schema, ServerLoadStatus expected)
    {
        await Write(Routed, "{\"schemaVersion\":" + schema + ",\"servers\":[]}");
        using var repository = Repository();
        Assert.Equal(expected, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task LockedFile_IsUnavailable_AndLegacyStillReturnsEmpty()
    {
        await Write(Direct, "[]");
        using var locked = new FileStream(Direct, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.Unavailable, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task MixedQuarantineAndValid_IsLoaded_WithoutChangingLegacyResults()
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "test", Host = "test.local", Username = "qa" };
        var json = JsonSerializer.Serialize(server, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Write(Direct, "[null," + json + "]");
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.Loaded, await repository.GetLoadStatusAsync());
        Assert.Equal(server.Id, Assert.Single(await repository.GetAllAsync()).Id);
    }

    [Fact]
    public async Task ServiceStatusAndList_RemainProcessCached()
    {
        using var repository = Repository();
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        Assert.Equal(ServerLoadStatus.NotFound, await service.GetLoadStatusAsync());
        await Write(Direct, "invalid");
        Assert.Equal(ServerLoadStatus.NotFound, await service.GetLoadStatusAsync());
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task DirectoryAtConfigPath_IsUnavailable()
    {
        Directory.CreateDirectory(Direct);
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.Unavailable, await repository.GetLoadStatusAsync());
    }

    [Fact]
    public async Task DomainQuarantineWithNoValidServers_IsUnavailable_AndLegacyFilteringIsUnchanged()
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "invalid", Host = "", Username = "" };
        await Write(Direct, JsonSerializer.Serialize(new[] { server }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var repository = Repository();
        Assert.Single(await repository.GetAllAsync());
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        Assert.Empty(await service.GetAllAsync());
        Assert.Equal(ServerLoadStatus.Unavailable, await service.GetLoadStatusAsync());
        Assert.Empty(await service.GetAllAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedList_WinsOverLaterDiskDiagnosis(bool lockedAtFirstRead)
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "test", Host = "test.local", Username = "qa" };
        await Write(Direct, JsonSerializer.Serialize(new[] { server }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var repository = Repository();
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        if (lockedAtFirstRead)
        {
            using (var locked = new FileStream(Direct, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Empty(await service.GetAllAsync());
            Assert.Equal(ServerLoadStatus.Unavailable, await service.GetLoadStatusAsync());
            Assert.Empty(await service.GetAllAsync());
        }
        else
        {
            Assert.Single(await service.GetAllAsync());
            using var locked = new FileStream(Direct, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(ServerLoadStatus.Loaded, await service.GetLoadStatusAsync());
            Assert.Single(await service.GetAllAsync());
        }
    }

    [WindowsAclFact]
    public async Task AccessDenied_IsUnavailable_ForRepositoryAndService_AndAclIsRestored()
    {
        await Write(Direct, "[]");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = new DirectoryInfo(_root);
        var original = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var originalSecurity = directory.GetAccessControl();
        var denied = directory.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ExecuteFile,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny);
        denied.AddAccessRule(rule);
        try
        {
            directory.SetAccessControl(denied);
            Assert.Throws<UnauthorizedAccessException>(() => File.GetAttributes(Direct));
            Assert.False(File.Exists(Direct));
            using var repository = Repository();
            Assert.Equal(ServerLoadStatus.Unavailable, await repository.GetLoadStatusAsync());
            using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
            Assert.Equal(ServerLoadStatus.Unavailable, await service.GetLoadStatusAsync());
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
        try
        {
            var restored = directory.GetAccessControl();
            Assert.DoesNotContain(restored.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                candidate => candidate.AccessControlType == AccessControlType.Deny && candidate.IdentityReference.Equals(rule.IdentityReference));
            Assert.Equal(AccessRules(originalSecurity), AccessRules(restored));
            var originalDescriptor = new RawSecurityDescriptor(original);
            var restoredDescriptor = new RawSecurityDescriptor(restored.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.Equal(originalDescriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited,
                restoredDescriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
            Assert.Equal("[]", await File.ReadAllTextAsync(Direct));
        }
        finally
        {
            // The counterproof deliberately leaves the deny ACE; always clean the temporary directory's ACL.
            var cleanup = new DirectorySecurity();
            cleanup.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            directory.SetAccessControl(cleanup);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string[] AccessRules(DirectorySecurity security) => security
        .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
        .Select(rule => $"{rule.IdentityReference.Value}|{rule.FileSystemRights}|{rule.AccessControlType}|{rule.IsInherited}|{rule.InheritanceFlags}|{rule.PropagationFlags}")
        .OrderBy(rule => rule, StringComparer.Ordinal).ToArray();

    public sealed class WindowsAclFactAttribute : FactAttribute
    {
        public WindowsAclFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows ACL integration test."; }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
