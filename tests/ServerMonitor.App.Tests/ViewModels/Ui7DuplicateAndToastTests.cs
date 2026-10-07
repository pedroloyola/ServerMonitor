using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7C, over the production session, controller, view model and services on a temp directory:
/// H-UI7-2 - the duplicate notice is in memory, non-blocking, identity = normalized endpoint + user (ordinal) + via, an edit
/// never matches itself, hidden servers count, "Abrir servidor" goes through the exit guard, import rows say "Já adicionado";
/// B-5 - a Save posts ONE "Servidor adicionado" / "Alterações guardadas" toast for the Detail of THAT server, a failed save
/// posts nothing, and the Detail shows it once, closes it on its countdown (FakeTimeProvider) or on its close button.
/// </summary>
public sealed class Ui7DuplicateAndToastTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ---- H-UI7-2 --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("10.0.0.5", "22", "deploy", true)]
    [InlineData("10.0.0.5.", "22", "deploy", true)] // normalized endpoint
    [InlineData(" 10.0.0.5 ", "22", "deploy", true)]
    [InlineData("10.0.0.5", "2222", "deploy", false)] // another port
    [InlineData("10.0.0.5", "22", "Deploy", false)] // user is ordinal
    [InlineData("10.0.0.6", "22", "deploy", false)]
    public async Task TheDuplicate_IsTheSameEndpointAndUser(string host, string port, string user, bool duplicate)
    {
        var saved = await SeedThenOpenAddAsync();
        var viewModel = _world.ViewModel;

        viewModel.Host = host;
        viewModel.Port = port;
        viewModel.Username = user;

        Assert.Equal(duplicate ? saved.Id : null, _world.Page.Controller.Duplicate()?.Id);
    }

    [Fact]
    public async Task TheRoute_IsPartOfTheIdentity_AndAHiddenServerCounts()
    {
        var routed = await _world.SeedAsync("private-db", route: new ServerRoute
        {
            Jump = new JumpHop { Host = "bastion.example.test", Port = 22, Username = "jumper", AuthenticationMethod = AuthenticationMethod.SshKey, PrivateKeyPath = @"C:\keys\bastion" }
        });
        Assert.True(await _world.ServerService.HideAsync(routed.Id));
        await OpenAddAsync();
        var viewModel = _world.ViewModel;
        viewModel.Host = "10.0.0.5";
        viewModel.Username = "deploy";

        Assert.Null(_world.Page.Controller.Duplicate()); // direct: another identity

        viewModel.UseJumpHost = true;
        viewModel.JumpHost = "BASTION.example.test";
        Assert.Equal(routed.Id, _world.Page.Controller.Duplicate()?.Id); // same via (normalized), hidden still counts

        viewModel.JumpPort = "2200";
        Assert.Null(_world.Page.Controller.Duplicate());
    }

    [Fact]
    public async Task AnEdit_NeverMatchesItself_ButMatchesAnother()
    {
        await _world.StartAsync();
        var first = await _world.SeedAsync("web-01");
        _ = _world.OpenEditAsync(first.Id);
        await _world.Page.Controller.KnownServersLoaded;
        Assert.Null(_world.Page.Controller.Duplicate()); // only itself has this identity

        var second = await _world.SeedAsync("web-02"); // same endpoint and user
        _ = _world.OpenEditAsync(first.Id); // a fresh visit (the clean one leaves at once)
        await _world.Page.Controller.KnownServersLoaded;

        Assert.Equal(second.Id, _world.Page.Controller.Duplicate()?.Id);
    }

    [Fact]
    public async Task ADuplicate_IsAdviceOnly_TheSaveStillGoesThrough()
    {
        await SeedThenOpenAddAsync();
        FillAsDuplicate();
        Assert.NotNull(_world.Page.Controller.Duplicate());

        var outcome = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        Assert.Equal(2, (await _world.ServerService.GetAllAsync()).Count);
    }

    [Fact]
    public async Task AbrirServidor_LeavesThroughTheExitGuard_ToThatServersDetail()
    {
        var saved = await SeedThenOpenAddAsync();
        FillAsDuplicate(); // dirty
        _world.Prompt.AutoAnswer = null;

        _world.Page.Controller.OpenDuplicate();

        Assert.Single(_world.Prompt.Asked); // "Descartar alterações?" first
        var page = _world.Page;
        _world.Prompt.Answer(true);
        var detail = Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content);
        Assert.Equal(saved.Id, detail.ServerId);
        Assert.True(page.Disposed);
        Assert.Single(await _world.ServerService.GetAllAsync()); // nothing saved implicitly
    }

    [Fact]
    public async Task ImportRows_SayJaAdicionado_ForASavedIdentity_Only()
    {
        await SeedThenOpenAddAsync();
        var controller = _world.Page.Controller;

        Assert.True(controller.IsAlreadyAdded(new SshConfigHostEntry { Alias = "web", HostName = "10.0.0.5", User = "deploy" }));
        Assert.True(controller.IsAlreadyAdded(new SshConfigHostEntry { Alias = "web", HostName = "10.0.0.5", User = "deploy", Port = 22 }));
        Assert.False(controller.IsAlreadyAdded(new SshConfigHostEntry { Alias = "web", HostName = "10.0.0.5", User = "root" }));
        Assert.False(controller.IsAlreadyAdded(new SshConfigHostEntry { Alias = "web", HostName = "10.0.0.5" })); // no user: unknown
        Assert.False(controller.IsAlreadyAdded(new SshConfigHostEntry
        {
            Alias = "web", HostName = "10.0.0.5", User = "deploy",
            Jump = new SshConfigJumpHost { Name = "bastion", HostName = "bastion.example.test" }
        }));
    }

    [Fact]
    public void TheDuplicateCopy_IsInEveryCulture() =>
        Ui7Resources.AssertPresentInEveryCulture([
            "ServerEditorDuplicateTitle", "ServerEditorDuplicateMessageFormat", "ServerEditorDuplicateOpenButton.Content",
            "ServerEditorDuplicateOpenAccessibleFormat", "ServerEditorImportAlreadyAdded.Text", "ServerEditorImportAlreadyAddedAccessibleFormat"
        ]);

    // ---- B-5 saved toast ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAdd_PostsServidorAdicionado_ForTheNewServer_Once()
    {
        await OpenAddAsync();
        Ui7EditorSessionTests.FillDirect(_world.ViewModel);

        Assert.Equal(ServerEditorSaveStatus.Saved, (await _world.Page.SubmitAsync())!.Status);

        var saved = Assert.Single(await _world.ServerService.GetAllAsync());
        Assert.Equal(new ServerEditorSaved(ServerEditorMode.Add, saved.Id, "db"), _world.SavedNotice.TakeFor(saved.Id));
        Assert.Null(_world.SavedNotice.TakeFor(saved.Id)); // once

        _world.SavedNotice.Post(new ServerEditorSaved(ServerEditorMode.Add, saved.Id, saved.Name));
        Assert.Null(_world.SavedNotice.TakeFor(Guid.NewGuid())); // another server's Detail never shows it...
        Assert.Null(_world.SavedNotice.TakeFor(saved.Id)); // ...and it is dropped, not kept for later
    }

    [Fact]
    public async Task AnEdit_PostsAlteracoesGuardadas_AndAFailedSave_PostsNothing()
    {
        await _world.StartAsync();
        var server = await _world.SeedAsync();
        _ = _world.OpenEditAsync(server.Id);
        _world.ViewModel.Name = "web-01-renamed";

        Assert.Equal(ServerEditorSaveStatus.Saved, (await _world.Page.SubmitAsync())!.Status);
        Assert.Equal(new ServerEditorSaved(ServerEditorMode.Edit, server.Id, "web-01-renamed"), _world.SavedNotice.TakeFor(server.Id));

        _ = _world.OpenEditAsync(server.Id);
        _world.ViewModel.SelectedAuthenticationIndex = 1;
        _world.Page.TypedPassword = "secret";
        _world.Credentials.FailWrites = true;
        var failed = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Failed, failed!.Status);
        Assert.False(_world.SavedNotice.IsPending); // nothing posted at all, for this server or any other
        Assert.Null(_world.SavedNotice.TakeFor(server.Id));
    }

    /// <summary>A persist that answers "not saved" (ServerNotFound: the server vanished) posts no toast either.</summary>
    [Fact]
    public async Task ARejectedPersist_PostsNoSavedToast()
    {
        await _world.StartAsync();
        var server = await _world.SeedAsync();
        _ = _world.OpenEditAsync(server.Id);
        _world.ViewModel.Name = "renamed";
        Assert.True(await _world.ServerService.RemoveAsync(server.Id));

        var outcome = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Failed, outcome!.Status);
        Assert.False(_world.SavedNotice.IsPending);
    }

    [Fact]
    public async Task TheDetail_ShowsItsSavedToastOnce_AndItClosesOnItsCountdownOrItsButton()
    {
        await _world.StartAsync();
        var server = await _world.SeedAsync();
        var clock = new FakeTimeProvider(Now);
        _world.SavedNotice.Post(new ServerEditorSaved(ServerEditorMode.Add, server.Id, server.Name));

        using (var detail = Detail(clock))
        {
            detail.Load(server.Id, ServerDetailOrigin.Overview);
            Assert.True(detail.IsSavedToastOpen);
            Assert.Equal("ServerEditorSavedAddedTitle", detail.SavedToastTitle);
            Assert.Equal("ServerEditorSavedCloseName", detail.SavedToastCloseName);

            clock.Advance(TransientNoticeTimer.Duration - TimeSpan.FromMilliseconds(1));
            Assert.True(detail.IsSavedToastOpen);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.False(detail.IsSavedToastOpen);
        }

        using (var again = Detail(clock))
        {
            again.Load(server.Id, ServerDetailOrigin.Overview);
            Assert.False(again.IsSavedToastOpen); // taken once: a later visit never shows it again
        }

        _world.SavedNotice.Post(new ServerEditorSaved(ServerEditorMode.Edit, server.Id, server.Name));
        using var closed = Detail(clock);
        closed.Load(server.Id, ServerDetailOrigin.Overview);
        Assert.Equal("ServerEditorSavedEditedTitle", closed.SavedToastTitle);
        closed.DismissSavedToast();
        Assert.False(closed.IsSavedToastOpen);
    }

    [Fact]
    public void TheToastCopy_IsInEveryCulture() =>
        Ui7Resources.AssertPresentInEveryCulture([
            "ServerEditorSavedAddedTitle", "ServerEditorSavedAddedMessageFormat", "ServerEditorSavedEditedTitle",
            "ServerEditorSavedEditedMessageFormat", "ServerEditorSavedCloseName"
        ]);

    private ServerDetailViewModel Detail(FakeTimeProvider clock) => new(
        _world.Dashboard,
        _world.Navigation,
        new FakeLocalizationService(),
        clock: new PresentationClock(clock),
        savedNotice: _world.SavedNotice);

    private async Task<Server> SeedThenOpenAddAsync()
    {
        var saved = await _world.SeedAsync();
        await OpenAddAsync();
        return saved;
    }

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        await _world.Page.Controller.KnownServersLoaded;
    }

    private void FillAsDuplicate()
    {
        var viewModel = _world.ViewModel;
        viewModel.Name = "web-again";
        viewModel.Host = "10.0.0.5";
        viewModel.Username = "deploy";
        viewModel.PrivateKeyPath = @"C:\keys\id_ed25519";
    }
}
