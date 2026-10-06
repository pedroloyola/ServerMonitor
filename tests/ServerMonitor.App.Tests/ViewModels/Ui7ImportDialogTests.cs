using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7B "Importar de SSH" (B-11, Vigil IMP-5/IMP-6/IMP-8, CP-12) through the page controller over the production view
/// model, real trust stores and a recording credential store: the classification is the resolver's, a blocked profile is
/// never used, Apply is Add-only and sets no auth, secret or trust, closing cancels the load, and the dialog's states come
/// from the view model's existing load result.
/// </summary>
public sealed class Ui7ImportDialogTests : IDisposable
{
    private const string Profile = @"C:\Users\tester";

    private const string Config =
        """
        Host web
            HostName 10.0.0.5
            User deploy
            Port 2222
            IdentityFile ~/.ssh/id_web

        Host private-db
            HostName 10.0.0.8
            User dba
            ProxyJump admin@bastion.example.test:2200

        Host legacy
            HostName 10.0.0.9
            ProxyCommand nc %h %p
        """;

    private readonly ScriptedImportSource _source = new(SshConfigResolver.Import(Config, Profile));
    private readonly Ui7EditorWorld _world;

    public Ui7ImportDialogTests()
    {
        _world = new Ui7EditorWorld(importSource: _source);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task TheList_IsTheResolversClassification_WithItsBlockedProfileCounted()
    {
        await OpenAddAsync();

        await _world.Page.Controller.OpenImportAsync();

        var viewModel = _world.ViewModel;
        Assert.True(viewModel.IsSshConfigImportOpen);
        Assert.Equal(SshConfigLoadOutcome.Hosts, viewModel.SshConfigLoadOutcome);
        var hosts = viewModel.SshConfigHosts;
        Assert.Equal(2, SshConfigImportPresentation.Available(hosts));
        Assert.Equal(1, SshConfigImportPresentation.Blocked(hosts));
        var legacy = Assert.Single(hosts, host => host.Alias == "legacy");
        Assert.True(legacy.IsBlocked);
        Assert.False(SshConfigImportPresentation.CanUse(legacy));
        Assert.True(legacy.HasRequirement); // the dialog says why
        Assert.Equal(string.Empty, SshConfigImportPresentation.StateTitleKey(viewModel.SshConfigLoadOutcome));
    }

    /// <summary>Vigil CP-12 / IMP-8 with the trust stores counted: Apply sets no auth, no secret and no trust.</summary>
    [Fact]
    public async Task CP12_UsingAProfile_FillsOnlyEmptyFields_AndWritesNoTrustNoSecret_NorChangesTheAuth()
    {
        await OpenAddAsync();
        _world.ViewModel.SelectedAuthenticationIndex = 1; // the user chose password: never changed nor inferred
        _world.ResetTrustCounts();
        await _world.Page.Controller.OpenImportAsync();
        var web = _world.ViewModel.SshConfigHosts.Single(host => host.Alias == "web");

        Assert.True(_world.Page.Controller.ApplyImport(web));

        var viewModel = _world.ViewModel;
        Assert.Equal(("web", "10.0.0.5", "2222", "deploy"), (viewModel.Name, viewModel.Host, viewModel.Port, viewModel.Username));
        Assert.Equal(1, viewModel.SelectedAuthenticationIndex);
        Assert.False(viewModel.IsSshConfigImportOpen); // the dialog closes with the apply
        Assert.True(viewModel.IsDirty); // B-15: an import is an edit
        Assert.Equal(0, _world.DirectTrust.Trusts);
        Assert.Equal(0, _world.RoutedTrust.Trusts);
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Null(_world.PersistedSnapshot()["known-hosts"]);
        Assert.Equal(0, _world.Ssh.TestConnectionCount);

        // Closing after the apply leaves its "filled from web" message alone.
        _world.Page.Controller.CloseImport();
        Assert.True(viewModel.HasSshConfigStatus);
    }

    [Fact]
    public async Task CP12_AJumpProfile_IsImportedRouted_NeverDirect_AndStillWritesNoTrust()
    {
        await OpenAddAsync();
        _world.ResetTrustCounts();
        await _world.Page.Controller.OpenImportAsync();

        Assert.True(_world.Page.Controller.ApplyImport(_world.ViewModel.SshConfigHosts.Single(host => host.Alias == "private-db")));

        var viewModel = _world.ViewModel;
        Assert.True(viewModel.UseJumpHost);
        Assert.Equal(("bastion.example.test", "2200", "admin"), (viewModel.JumpHost, viewModel.JumpPort, viewModel.JumpUsername));
        Assert.Equal(0, _world.DirectTrust.Trusts + _world.RoutedTrust.Trusts);
        Assert.Equal(0, _world.Credentials.Writes);
    }

    [Fact]
    public async Task ABlockedProfile_CanNeverBeUsed_AndChangesNothing()
    {
        await OpenAddAsync();
        await _world.Page.Controller.OpenImportAsync();
        var legacy = _world.ViewModel.SshConfigHosts.Single(host => host.Alias == "legacy");

        Assert.False(_world.Page.Controller.ApplyImport(legacy));
        Assert.False(_world.Page.Controller.ApplyImport(null));

        var viewModel = _world.ViewModel;
        Assert.True(viewModel.IsSshConfigImportOpen); // the dialog stays; nothing applied
        Assert.Equal(string.Empty, viewModel.Host);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task ClosingTheDialog_CancelsALoadStillRunning_AndNothingArrivesLater()
    {
        await OpenAddAsync();
        _source.Hold = true;

        var load = _world.Page.Controller.OpenImportAsync();
        Assert.True(_world.ViewModel.IsSshConfigImportOpen);
        Assert.True(_world.ViewModel.IsLoadingSshConfig);

        _world.Page.Controller.CloseImport();
        Assert.True(load.IsCompleted, "closing must end the load at once (it is cancelled, not left running)");
        await load;

        Assert.True(_source.LastToken.IsCancellationRequested);
        Assert.False(_world.ViewModel.IsSshConfigImportOpen);
        Assert.Empty(_world.ViewModel.SshConfigHosts);
        Assert.Equal(SshConfigLoadOutcome.None, _world.ViewModel.SshConfigLoadOutcome);
        Assert.False(_world.ViewModel.IsDirty);
    }

    /// <summary>
    /// Cortex section 8: the import is an in-page LAYER, so an external activation can ask "Descartar alteracoes?" over
    /// it; "Continuar a editar" finds the import exactly as it was, "Descartar" leaves (the load ends with the editor).
    /// </summary>
    [Fact]
    public async Task AnActivationWithTheImportOpen_AsksOverIt_AndKeepEditingFindsItAsItWas()
    {
        await OpenAddAsync();
        _world.ViewModel.Name = "typed"; // dirty
        await _world.Page.Controller.OpenImportAsync();
        Assert.Equal(ServerEditorLayer.Import, _world.Page.Controller.LayerToShow(accepting: false));
        _world.Prompt.AutoAnswer = false;

        _world.Navigation.LeaveCurrentPageForActivation(_world.Navigation.GoToDashboard);

        Assert.Single(_world.Prompt.Asked);
        Assert.Equal(ServerEditorLayer.Import, _world.Page.Controller.LayerToShow(accepting: false));
        Assert.Equal(3, _world.ViewModel.SshConfigHosts.Count);

        _world.Prompt.AutoAnswer = true;
        var page = _world.Page;
        _world.Navigation.LeaveCurrentPageForActivation(_world.Navigation.GoToDashboard);
        Assert.True(page.Disposed);
    }

    [Fact]
    public async Task ClosingTheImportLayer_ClosesTheImport()
    {
        await OpenAddAsync();
        await _world.Page.Controller.OpenImportAsync();

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Import, accepting: false);

        Assert.False(_world.ViewModel.IsSshConfigImportOpen);
        Assert.Equal(ServerEditorLayer.None, _world.Page.Controller.LayerToShow(accepting: false));
    }

    [Fact]
    public async Task AnEdit_NeverOpensTheImport()
    {
        var server = await _world.SeedAsync();
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id); // completes when the visit ends

        await _world.Page.Controller.OpenImportAsync();

        Assert.False(_world.ViewModel.IsSshConfigImportOpen);
        Assert.Equal(0, _source.LoadCount);
    }

    public static TheoryData<string, SshConfigLoadOutcome, string> States => new()
    {
        { "not-found", SshConfigLoadOutcome.NotFound, "ServerEditorImportNotFoundTitle" },
        { "error", SshConfigLoadOutcome.Error, "ServerEditorImportErrorTitle" },
        { "empty", SshConfigLoadOutcome.NoHosts, "ServerEditorImportNoHostsTitle" }
    };

    [Theory]
    [MemberData(nameof(States))]
    public async Task TheDialogState_ComesFromTheExistingLoadResult(string state, SshConfigLoadOutcome outcome, string titleKey)
    {
        _source.Result = state switch
        {
            "not-found" => SshConfigImportResult.NotFound,
            "error" => SshConfigImportResult.Failed(SshConfigImportErrorCode.Unreadable),
            _ => SshConfigResolver.Import("Host *\n  User everyone\n", Profile)
        };
        await OpenAddAsync();

        await _world.Page.Controller.OpenImportAsync();

        Assert.Equal(outcome, _world.ViewModel.SshConfigLoadOutcome);
        Assert.Equal(titleKey, SshConfigImportPresentation.StateTitleKey(outcome));
        Assert.True(_world.ViewModel.HasSshConfigStatus); // the body is the view model's own message
        Assert.Empty(_world.ViewModel.SshConfigHosts);
    }

    [Fact]
    public async Task ImportFromTheDashboard_OpensTheEditorWithTheImportAlreadyLoading()
    {
        await _world.StartAsync();

        _ = ((AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand).ExecuteAsync(); // completes when the visit ends

        Assert.True(_world.ViewModel.IsSshConfigImportOpen); // the page's dialog host shows it once it has a window
        Assert.Equal(1, _source.LoadCount);
    }

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
    }

    private sealed class ScriptedImportSource(SshConfigImportResult result) : ISshConfigImportSource
    {
        public SshConfigImportResult Result { get; set; } = result;

        public bool Hold { get; set; }

        public int LoadCount { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public async Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LastToken = cancellationToken;
            if (!Hold)
            {
                return Result;
            }

            // Held until the load is cancelled (no clock): only Close / the editor going away ends it.
            var held = new TaskCompletionSource<SshConfigImportResult>();
            using (cancellationToken.Register(() => held.TrySetCanceled(cancellationToken)))
            {
                return await held.Task;
            }
        }
    }
}
