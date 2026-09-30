using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>Runtime-free doubles for the M14.6 backup/restore view model: no window, no disk, no crypto.</summary>
internal sealed class BackupRestoreHarness
{
    public const string ValidPassphrase = "correct horse battery";

    public FakeBackupService Service { get; } = new();

    public FakeBackupFilePicker Picker { get; } = new();

    public ScriptedBackupInteraction Interaction { get; } = new();

    public FakeAppLifecycleController Lifecycle { get; } = new();

    public RecordingLogger Logger { get; } = new();

    public ILocalizationService Localization { get; init; } = new FakeLocalizationService();

    public BackupRestoreViewModel Create() => new(
        Service,
        Picker,
        Interaction,
        Lifecycle,
        Localization,
        Logger);

    /// <summary>Scripts the restore passphrase dialog: type the passphrase and press "Open backup".</summary>
    public void OpenWithPassphrase(string passphrase = ValidPassphrase) =>
        Interaction.OnOpen = async session =>
        {
            session.SetPassphrase(passphrase);
            await session.SubmitAsync();
        };

    public static RestoreSummary Summary(
        RestoreCounts? backup = null,
        RestoreCounts? current = null,
        int? directToRemove = 0,
        int? routedToRemove = 0,
        PortableSettings? backupSettings = null,
        PortableSettings? currentSettings = null,
        IReadOnlyList<BackupCredentialFlag>? missingCredentials = null,
        IReadOnlyList<KeyPathWarning>? keyPathWarnings = null) => new()
        {
            BackupCreatedAt = new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero),
            BackupAppVersion = "1.2.0",
            Backup = backup ?? new RestoreCounts(3, 1, 2, 3, 1),
            Current = current ?? new RestoreCounts(2, 0, 1, 2, 0),
            DirectTrustedHostKeysToRemove = directToRemove,
            RoutedTrustedHostKeysToRemove = routedToRemove,
            BackupSettings = backupSettings ?? new PortableSettings(true, true),
            CurrentSettings = currentSettings ?? new PortableSettings(true, true),
            MissingCredentials = missingCredentials ?? [],
            KeyPathWarnings = keyPathWarnings ?? []
        };
}

internal sealed class FakeRestorePlan(RestoreSummary summary) : RestorePlan(summary)
{
    public int DisposeCount { get; private set; }

    public override bool IsDisposed => DisposeCount > 0;

    protected override void Dispose(bool disposing) => DisposeCount++;
}

internal sealed class FakeBackupService : IConfigurationBackupService
{
    public RestoreRecoveryReport StartupRecovery { get; set; } =
        RestoreRecoveryReport.None(@"C:\Users\qa\AppData\Local\ServerMonitor\restore-journal\");

    public Func<Task<BackupExportResult>> OnExport { get; set; } = () => Task.FromResult(new BackupExportResult
    {
        Summary = new BackupExportSummary { DirectServers = 2, RoutedServers = 1, Credentials = 2, DirectTrustedHostKeys = 3 }
    });

    public Func<Task<RestoreInspectResult>> OnInspect { get; set; } = () =>
        Task.FromResult(new RestoreInspectResult { Error = BackupError.NotABackup });

    public Func<RestorePlan, Task<RestoreApplyResult>> OnApply { get; set; } = _ =>
        Task.FromResult(new RestoreApplyResult { Outcome = RestoreApplyOutcome.Completed });

    public int ExportCount { get; private set; }

    public int InspectCount { get; private set; }

    public int ApplyCount { get; private set; }

    public string? ExportPath { get; private set; }

    public string? ExportPassphrase { get; private set; }

    public string? ExportConfirmation { get; private set; }

    public string? InspectPath { get; private set; }

    public string? InspectPassphrase { get; private set; }

    /// <summary>Whether the plan was still alive when apply was entered.</summary>
    public bool PlanWasDisposedAtApply { get; private set; }

    public Task<BackupExportResult> ExportAsync(
        string destinationPath,
        ReadOnlyMemory<char> passphrase,
        ReadOnlyMemory<char> confirmation,
        CancellationToken cancellationToken = default)
    {
        ExportCount++;
        ExportPath = destinationPath;
        ExportPassphrase = passphrase.ToString();
        ExportConfirmation = confirmation.ToString();
        return OnExport();
    }

    public Task<RestoreInspectResult> InspectAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        InspectCount++;
        InspectPath = sourcePath;
        InspectPassphrase = passphrase.ToString();
        return OnInspect();
    }

    public Task<RestoreApplyResult> ApplyAsync(RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ApplyCount++;
        PlanWasDisposedAtApply = plan.IsDisposed;
        return OnApply(plan);
    }
}

internal sealed class FakeBackupFilePicker : IBackupFilePicker
{
    public string? SavePath { get; set; } = @"D:\Backups\ServerAlyzer-backup.serveralyzer-backup";

    public string? OpenPath { get; set; } = @"D:\Backups\home-lab.serveralyzer-backup";

    public int SaveCount { get; private set; }

    public int OpenCount { get; private set; }

    public string? SuggestedFileName { get; private set; }

    public string? FileTypeLabel { get; private set; }

    public Task<string?> PickSaveAsync(
        string suggestedFileName,
        string fileTypeLabel,
        CancellationToken cancellationToken = default)
    {
        SaveCount++;
        SuggestedFileName = suggestedFileName;
        FileTypeLabel = fileTypeLabel;
        return Task.FromResult(SavePath);
    }

    public Task<string?> PickOpenAsync(CancellationToken cancellationToken = default)
    {
        OpenCount++;
        return Task.FromResult(OpenPath);
    }
}

/// <summary>Plays the user: each surface runs the scripted callback and records that it was shown.</summary>
internal sealed class ScriptedBackupInteraction : IBackupRestoreInteraction
{
    public Func<BackupCreateSession, Task> OnCreate { get; set; } = _ => Task.CompletedTask;

    public Func<RestoreOpenSession, Task> OnOpen { get; set; } = _ => Task.CompletedTask;

    public Func<RestoreConfirmation, Task<bool>> OnConfirm { get; set; } = _ => Task.FromResult(false);

    public Func<Task> OnCompleted { get; set; } = () => Task.CompletedTask;

    /// <summary>The order in which surfaces were shown.</summary>
    public List<string> Calls { get; } = [];

    public RestoreConfirmation? Confirmation { get; private set; }

    public List<(string Title, string Message, string CloseText)> Notices { get; } = [];

    public (string Title, string Message, string PrimaryText)? Completed { get; private set; }

    public string? RestoringMessage { get; private set; }

    public Task ShowCreateDialogAsync(BackupCreateSession session)
    {
        Calls.Add("create");
        return OnCreate(session);
    }

    public Task ShowRestoreOpenDialogAsync(RestoreOpenSession session)
    {
        Calls.Add("open");
        return OnOpen(session);
    }

    public Task<bool> ConfirmRestoreAsync(RestoreConfirmation confirmation)
    {
        Calls.Add("confirm");
        Confirmation = confirmation;
        return OnConfirm(confirmation);
    }

    public async Task<RestoreApplyResult> ShowRestoringAsync(string message, Func<Task<RestoreApplyResult>> apply)
    {
        Calls.Add("restoring");
        RestoringMessage = message;
        return await apply();
    }

    public Task ShowRestoreCompletedAsync(string title, string message, string primaryText)
    {
        Calls.Add("completed");
        Completed = (title, message, primaryText);
        return OnCompleted();
    }

    public Task ShowNoticeAsync(string title, string message, string closeText)
    {
        Calls.Add("notice");
        Notices.Add((title, message, closeText));
        return Task.CompletedTask;
    }
}

/// <summary>Keeps every rendered log line so a test can prove what never reaches the log.</summary>
internal sealed class RecordingLogger : ILogger<BackupRestoreViewModel>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
}

/// <summary>Resolves the listed keys to a composite format and every other key to itself.</summary>
internal sealed class FormatLocalization(IReadOnlyDictionary<string, string> formats) : ILocalizationService
{
    public string? CurrentLanguageOverride => null;

    public string GetString(string resourceKey) =>
        formats.TryGetValue(resourceKey, out var value) ? value : resourceKey;

    public void InitializeFromSystem()
    {
    }

    public void SetLanguage(string? languageTag)
    {
    }
}
