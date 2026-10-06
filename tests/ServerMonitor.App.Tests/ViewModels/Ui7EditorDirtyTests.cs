using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7 B-15 / Cortex R-12: IsDirty against the form as opened. Prefill and the editor's own key pre-selection are not
/// edits; an import, a typed value and a staged secret are; reverting by hand is clean again; and the exit guard asks
/// only when there is something to lose (H-UI7-3). Plus the discard dialog's name line.
/// </summary>
public sealed class Ui7EditorDirtyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "servermonitor-ui7-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AFreshAdd_ADiscoveryPrefill_AndAnOpenedEdit_AreClean()
    {
        Assert.False(Editor().IsDirty);
        Assert.False(Editor(prefill: new ServerDiscoveryPrefill { Name = "nas", Host = "nas.local", Port = 2222 }).IsDirty);
        Assert.False(Editor(server: Saved()).IsDirty);
    }

    [Fact]
    public async Task TheKeyTheEditorPreSelects_IsNotAnEdit_ButPickingOneIs()
    {
        var key = Path.Combine(_directory, "id_ed25519");
        var editor = Editor(keys: new OneRecommendedKey(key));
        await editor.LocalKeyDiscovery;

        Assert.Equal(key, editor.PrivateKeyPath);
        Assert.True(editor.IsPrivateKeyAutoSelected);
        Assert.False(editor.IsDirty);

        editor.PrivateKeyPath = Path.Combine(_directory, "id_rsa");
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void ATypedValue_IsDirty_AndRevertingItByHand_IsCleanAgain()
    {
        var editor = Editor(server: Saved());
        var changes = new List<bool>();
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerEditorViewModel.IsDirty))
            {
                changes.Add(editor.IsDirty);
            }
        };

        editor.Name = "web-02";
        Assert.True(editor.IsDirty);
        editor.Name = "web-01";
        Assert.False(editor.IsDirty);
        editor.Port = "2222";
        Assert.True(editor.IsDirty);
        editor.Port = "22";
        Assert.False(editor.IsDirty);
        editor.SelectedRefreshIntervalIndex = (editor.SelectedRefreshIntervalIndex + 1) % 4;
        Assert.True(editor.IsDirty);

        Assert.Equal([true, false, true, false, true], changes);
    }

    [Fact]
    public void AnSshConfigImport_IsAnEdit()
    {
        var editor = Editor(import: new NotFoundImport());

        Assert.True(editor.ApplySshConfigHost(new SshConfigHostEntry { Alias = "prod", HostName = "10.0.0.8", User = "ops" }));

        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void AStagedSecret_IsAnEdit()
    {
        var editor = Editor(server: Saved());
        editor.SelectedAuthenticationIndex = 1;
        editor.SelectedAuthenticationIndex = 0;
        Assert.False(editor.IsDirty);

        editor.CaptureSecret("passphrase");

        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void TheGuard_LetsACleanFormGoAtOnce_AndAsksOnlyWhenSomethingWouldBeLost()
    {
        var prompt = new ScriptedDiscardPrompt { AutoAnswer = null };
        var editor = Editor(server: Saved());
        var session = new FixedSession(editor);
        using var controller = new ServerEditorPageController(session, prompt);
        var typed = string.Empty;
        controller.HasTypedSecret = () => typed.Length > 0;
        Assert.True(controller.Load(session.Request));

        var clean = controller.ConfirmLeaveAsync();
        Assert.True(clean.IsCompletedSuccessfully);
        Assert.True(clean.Result);
        Assert.Empty(prompt.Asked);

        typed = "unsent"; // typed into a password box, not yet read by the view model
        var unsent = controller.ConfirmLeaveAsync();
        Assert.False(unsent.IsCompleted);
        prompt.Answer(discard: false);
        Assert.False(unsent.Result);

        typed = string.Empty;
        editor.Name = "web-renamed";
        var dirty = controller.ConfirmLeaveAsync();
        prompt.Answer(discard: true);
        Assert.True(dirty.Result);
        var asked = prompt.Asked.Last();
        Assert.Equal(("web-01", "web-renamed", ServerEditorMode.Edit), (asked.OpenedName, asked.CurrentName, asked.Mode));
    }

    [Theory]
    [InlineData("web-01", "web-02", "ServerEditorDiscardRenamedFormat")]
    [InlineData("web-01", "web-01", "web-01")]
    [InlineData("", "", "ServerEditorDiscardUnnamed")]
    [InlineData("", "new-box", "new-box")]
    public void TheDiscardDialog_NamesTheServer_AndShowsTheRenameOnlyWhenTheNameChanged(string opened, string current, string expected)
    {
        var line = DestructiveConfirmation.DiscardAffected(
            new ServerEditorDiscardContext(ServerEditorMode.Edit, opened, current),
            new FakeLocalizationService());

        Assert.Equal(expected, line);
    }

    private ServerEditorViewModel Editor(
        Server? server = null,
        ServerDiscoveryPrefill? prefill = null,
        Core.Interfaces.ILocalSshKeyDiscovery? keys = null,
        Core.Interfaces.ISshConfigImportSource? import = null) => new(
        new ServerValidator(),
        new ScriptedSsh(),
        new InertTrust(),
        new FakeConnectionStateStore(),
        new NullPicker(),
        new FakeLocalizationService(),
        server,
        prefill,
        import,
        routedHostKeyTrustStore: null,
        localSshKeyDiscovery: keys ?? new NoKeys());

    private Server Saved() => new()
    {
        Id = Guid.NewGuid(),
        Name = "web-01",
        Host = "10.0.0.5",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = Path.Combine(_directory, "id_ed25519"),
        CreatedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)
    };

    private sealed class NotFoundImport : Core.Interfaces.ISshConfigImportSource
    {
        public Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(SshConfigImportResult.NotFound);
    }

    private sealed class InertTrust : Core.Interfaces.IHostKeyTrustStore
    {
        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) => Task.FromResult<TrustedHostKey?>(null);

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A session that hands out one prepared view model (the guard test needs no navigation).</summary>
    private sealed class FixedSession(ServerEditorViewModel viewModel) : IServerEditorSession
    {
        public ServerEditorRequest Request { get; } = new(ServerEditorMode.Edit, new Server
        {
            Id = Guid.NewGuid(), Name = "web-01", Host = "h", Port = 22, Username = "u", AuthenticationMethod = AuthenticationMethod.SshKey
        }, null, false, new ServerEditorOrigin(NavigationDestination.Detail));

        public Task OpenAddAsync(ServerEditorPersist persist) => throw new NotSupportedException();

        public Task OpenEditAsync(Server server, ServerEditorPersist persist) => throw new NotSupportedException();

        public Task OpenDiscoveryAsync(ServerDiscoveryPrefill prefill, ServerEditorPersist persist) => throw new NotSupportedException();

        public Task OpenSshImportAsync(ServerEditorPersist persist) => throw new NotSupportedException();

        public ServerEditorViewModel? Attach(ServerEditorRequest request) => viewModel;

        public Task<ServerEditorSaveOutcome> SaveAsync(ServerEditorRequest request, ServerEditorResult result) => throw new NotSupportedException();

        public bool IsSaved(ServerEditorRequest request) => false;

        public void ResumeSavedDestination(ServerEditorRequest request)
        {
        }

        public void Leave(ServerEditorRequest request)
        {
        }

        public void Detach(ServerEditorRequest request) => viewModel.Dispose();
    }
}
