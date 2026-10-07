using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// Cortex R-6 end to end from the editor PAGE (session → view model → dashboard persist → profile service → server service
/// → JSON repository on a temp directory): saving a routed server untouched keeps its identity, route, file and jump
/// secret; unchecking the jump host is what makes it direct, in the legacy file, with the jump secret retired.
/// </summary>
public sealed class Ui7EditorRouteIdentityTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task ARoutedServer_SavedUntouched_KeepsIdCreatedAtRouteFileAndJumpSecret()
    {
        var seeded = await SeedRoutedAsync();
        await _world.StartAsync();
        var visit = _world.OpenEditAsync(seeded.Id);
        Assert.True(_world.ViewModel.UseJumpHost);
        Assert.False(_world.ViewModel.IsDirty);

        var outcome = await _world.Page.SubmitAsync();
        Assert.True(_world.EditorPages[0].Disposed, "the editor page was never released");
        await visit;

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        var saved = Assert.Single(await _world.ServerService.GetAllAsync());
        Assert.Equal(seeded.Id, saved.Id);
        Assert.Equal(seeded.CreatedAt, saved.CreatedAt);
        Assert.Equal(seeded.Route, saved.Route);
        Assert.Contains(seeded.Id.ToString(), File.ReadAllText(_world.StorageOptions.RoutedFilePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.Id.ToString(), ReadOrEmpty(_world.StorageOptions.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Equal(0, _world.Credentials.Deletes);
    }

    [Fact]
    public async Task UncheckingTheJumpHost_MakesItDirect_InTheLegacyFile_AndRetiresTheJumpSecret()
    {
        var seeded = await SeedRoutedAsync();
        var secretsBefore = _world.Credentials.Count;
        await _world.StartAsync();
        var visit = _world.OpenEditAsync(seeded.Id);

        _world.ViewModel.UseJumpHost = false;
        var outcome = await _world.Page.SubmitAsync();
        Assert.True(_world.EditorPages[0].Disposed, "the editor page was never released");
        await visit;

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        var saved = Assert.Single(await _world.ServerService.GetAllAsync());
        Assert.Equal(seeded.Id, saved.Id);
        Assert.Null(saved.Route);
        Assert.Contains(seeded.Id.ToString(), File.ReadAllText(_world.StorageOptions.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.Id.ToString(), ReadOrEmpty(_world.StorageOptions.RoutedFilePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _world.Credentials.Deletes);
        Assert.Equal(secretsBefore - 1, _world.Credentials.Count);
    }

    private Task<Server> SeedRoutedAsync() => _world.SeedAsync(
        route: new ServerRoute
        {
            Jump = new JumpHop { Host = "bastion.example.com", Port = 22, Username = "admin", AuthenticationMethod = AuthenticationMethod.Password }
        },
        jumpPassword: "jump-secret");

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;
}
