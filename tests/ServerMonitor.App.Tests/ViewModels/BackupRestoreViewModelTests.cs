using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.6 Slice C. The real <see cref="BackupRestoreViewModel"/> against scripted doubles: the fake
/// localization resolves a key to itself, so an asserted message IS the resource key that was chosen.
/// </summary>
public sealed class BackupRestoreViewModelTests
{
    private const string Passphrase = BackupRestoreHarness.ValidPassphrase;
    private const string JournalDirectory = @"C:\Users\qa\AppData\Local\ServerMonitor\restore-journal\";

    // ------------------------------------------------------------------ create: passphrase validation

    [Theory]
    [InlineData("", "", "", false)]                                                // pristine: quiet
    [InlineData("short", "", "PassphraseTooShort", false)]
    [InlineData("correct horse battery", "", "", false)]                          // confirmation untouched: quiet
    [InlineData("correct horse battery", "correct horse", "PassphraseMismatch", false)]
    [InlineData("correct horse battery", "correct horse battery", "", true)]
    public void Create_live_validation_maps_each_passphrase_problem_to_its_message(
        string passphrase,
        string confirmation,
        string expectedMessage,
        bool canSubmit)
    {
        var session = CaptureCreateSession(out _);

        session.SetPassphrase(passphrase, confirmation);

        Assert.Equal(expectedMessage, session.ProblemMessage);
        Assert.Equal(expectedMessage.Length > 0, session.HasProblem);
        Assert.Equal(canSubmit, session.CanSubmit);
    }

    [Fact]
    public void Create_live_validation_reports_a_broken_paste_as_invalid_characters()
    {
        var session = CaptureCreateSession(out _);

        session.SetPassphrase("twelve chars " + (char)0xD800, string.Empty); // a lone surrogate

        Assert.Equal("PassphraseInvalidCharacters", session.ProblemMessage);
        Assert.False(session.CanSubmit);
    }

    [Fact]
    public void Create_live_validation_rejects_a_passphrase_over_the_maximum()
    {
        var session = CaptureCreateSession(out _);

        session.SetPassphrase(new string('a', BackupPassphrasePolicy.MaximumLength + 1), string.Empty);

        Assert.Equal("PassphraseTooLong", session.ProblemMessage);
        Assert.False(session.CanSubmit);
    }

    [Theory]
    [InlineData(BackupPassphraseProblem.Empty, "PassphraseEmpty")]
    [InlineData(BackupPassphraseProblem.TooShort, "PassphraseTooShort")]
    [InlineData(BackupPassphraseProblem.TooLong, "PassphraseTooLong")]
    [InlineData(BackupPassphraseProblem.InvalidCharacters, "PassphraseInvalidCharacters")]
    [InlineData(BackupPassphraseProblem.ConfirmationMismatch, "PassphraseMismatch")]
    public async Task Create_a_passphrase_problem_from_the_service_stays_in_the_dialog(
        BackupPassphraseProblem problem,
        string expectedKey)
    {
        var harness = new BackupRestoreHarness();
        harness.Service.OnExport = () => Task.FromResult(new BackupExportResult { PassphraseProblem = problem });
        bool? closed = null;
        string? shown = null;
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            closed = await session.SubmitAsync();
            shown = session.ProblemMessage;
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.False(closed);
        Assert.Equal(expectedKey, shown);
        Assert.False(viewModel.IsStatusOpen);
    }

    // ------------------------------------------------------------------------------ create: the flow

    [Fact]
    public async Task Create_exports_to_the_picked_file_and_reports_the_summary()
    {
        var harness = new BackupRestoreHarness();
        bool? closed = null;
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            closed = await session.SubmitAsync();
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.True(closed);
        Assert.Equal(1, harness.Service.ExportCount);
        Assert.Equal(harness.Picker.SavePath, harness.Service.ExportPath);
        Assert.Equal(Passphrase, harness.Service.ExportPassphrase);
        Assert.Equal(Passphrase, harness.Service.ExportConfirmation);
        Assert.EndsWith(BackupRestoreViewModel.BackupFileExtension, harness.Picker.SuggestedFileName);
        Assert.StartsWith("ServerAlyzer-backup-", harness.Picker.SuggestedFileName);
        Assert.Equal("BackupFileTypeLabel", harness.Picker.FileTypeLabel);
        Assert.True(viewModel.IsStatusOpen);
        Assert.Equal(InfoBarSeverity.Success, viewModel.StatusSeverity);
        Assert.Equal("BackupCreatedTitle", viewModel.StatusTitle);
        Assert.Equal("BackupCreatedMessage", viewModel.StatusMessage);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task Create_success_formats_the_counts_and_the_warnings()
    {
        var harness = new BackupRestoreHarness
        {
            Localization = new FormatLocalization(new Dictionary<string, string>
            {
                ["BackupCreatedMessage"] = "{0} servers, {1} keys, {2} passwords, {3}",
                ["BackupMissingCredentialsWarning"] = "missing: {0}",
                ["BackupExcludedEntriesWarning"] = "unreadable: {0}",
                ["BackupExcludedTrustNote"] = "unused keys: {0}"
            })
        };
        harness.Service.OnExport = () => Task.FromResult(new BackupExportResult
        {
            Summary = new BackupExportSummary
            {
                DirectServers = 4,
                RoutedServers = 2,
                Credentials = 3,
                DirectTrustedHostKeys = 4,
                RoutedTrustedHostKeys = 1,
                ExcludedUnreferencedTrustedHostKeys = 7,
                ExcludedUnreadableServers = 1,
                MissingCredentials =
                [
                    new BackupCredentialFlag(Guid.NewGuid(), "nas", IsJump: false),
                    new BackupCredentialFlag(Guid.NewGuid(), "db", IsJump: true)
                ]
            }
        });
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            await session.SubmitAsync();
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.Equal(InfoBarSeverity.Warning, viewModel.StatusSeverity);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                $"6 servers, 5 keys, 3 passwords, {harness.Picker.SavePath}",
                "missing: nas, db (jump host)",
                "unreadable: 1",
                "unused keys: 7"),
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Create_a_canceled_picker_writes_nothing_and_keeps_the_dialog()
    {
        var harness = new BackupRestoreHarness();
        harness.Picker.SavePath = null;
        bool? closed = null;
        bool? busyAfter = null;
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            closed = await session.SubmitAsync();
            busyAfter = session.IsBusy;
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.False(closed);
        Assert.False(busyAfter);
        Assert.Equal(0, harness.Service.ExportCount);
        Assert.False(viewModel.IsStatusOpen);
    }

    [Fact]
    public async Task Create_cannot_be_submitted_while_the_passphrase_is_invalid()
    {
        var harness = new BackupRestoreHarness();
        bool? closed = null;
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase("short", "short");
            closed = await session.SubmitAsync();
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.False(closed);
        Assert.Equal(0, harness.Picker.SaveCount);
        Assert.Equal(0, harness.Service.ExportCount);
    }

    [Fact]
    public async Task Create_is_busy_and_not_resubmittable_while_the_export_runs()
    {
        var harness = new BackupRestoreHarness();
        var gate = new TaskCompletionSource<BackupExportResult>();
        harness.Service.OnExport = () => gate.Task;
        var observed = new List<bool>();
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            var first = session.SubmitAsync();
            observed.Add(session.IsBusy);
            observed.Add(session.CanSubmit);
            observed.Add(await session.SubmitAsync());
            gate.SetResult(new BackupExportResult { Summary = new BackupExportSummary() });
            await first;
            observed.Add(session.IsBusy);
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.Equal(new[] { true, false, false, false }, observed);
        Assert.Equal(1, harness.Service.ExportCount);
    }

    public static TheoryData<BackupError, string?> ExportErrors()
    {
        var data = new TheoryData<BackupError, string?>();
        foreach (var error in Enum.GetValues<BackupError>())
        {
            data.Add(error, BackupMessageKeys.ForExportError(error));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExportErrors))]
    public async Task Create_every_export_error_shows_its_own_message_and_never_a_success(
        BackupError error,
        string? expectedKey)
    {
        var harness = new BackupRestoreHarness();
        harness.Service.OnExport = () => Task.FromResult(new BackupExportResult
        {
            Error = error,
            PendingJournalDirectory = error == BackupError.RestorePending ? JournalDirectory : null
        });
        bool? closed = null;
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            closed = await session.SubmitAsync();
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        if (expectedKey is null)
        {
            Assert.Equal(BackupError.Canceled, error);
            Assert.False(closed);
            Assert.False(viewModel.IsStatusOpen);
            return;
        }

        Assert.True(closed);
        Assert.True(viewModel.IsStatusOpen);
        Assert.Equal(expectedKey, viewModel.StatusMessage);
        Assert.NotEqual(InfoBarSeverity.Success, viewModel.StatusSeverity);
        Assert.NotEqual("BackupCreatedTitle", viewModel.StatusTitle);
        Assert.Equal(error == BackupError.RestorePending, viewModel.IsBlocked);
    }

    [Theory]
    [InlineData(BackupError.WriteFailed, "BackupFailedWrite")]
    [InlineData(BackupError.CredentialStoreUnavailable, "BackupFailedCredentialStore")]
    [InlineData(BackupError.TrustStoreUnreadable, "BackupFailedTrustStore")]
    [InlineData(BackupError.TooLarge, "BackupFailedTooLarge")]
    [InlineData(BackupError.InvalidContent, "BackupFailedInvalidContent")]
    [InlineData(BackupError.Unsupported, "BackupUnsupported")]
    [InlineData(BackupError.Busy, "ConfigurationLocked")]
    [InlineData(BackupError.RestorePending, "RestorePendingBlocked")]
    public void Export_error_keys_follow_the_copy_contract(BackupError error, string expectedKey) =>
        Assert.Equal(expectedKey, BackupMessageKeys.ForExportError(error));

    [Fact]
    public async Task Create_an_unexpected_failure_is_reported_without_its_text()
    {
        var harness = new BackupRestoreHarness();
        harness.Service.OnExport = () => throw new IOException("C:\\secret\\path is locked by " + Passphrase);
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            await session.SubmitAsync();
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();

        Assert.Equal(InfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal("BackupFailedTitle", viewModel.StatusTitle);
        Assert.Equal(BackupMessageKeys.Generic, viewModel.StatusMessage);
        Assert.All(harness.Logger.Lines, line => Assert.DoesNotContain("secret", line));
        Assert.True(viewModel.CanStart);
    }

    // ------------------------------------------------------------------------ restore: open / inspect

    [Fact]
    public async Task Restore_a_canceled_file_picker_shows_nothing()
    {
        var harness = new BackupRestoreHarness();
        harness.Picker.OpenPath = null;
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Empty(harness.Interaction.Calls);
        Assert.Equal(0, harness.Service.InspectCount);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task Restore_the_open_button_is_disabled_while_the_inspect_runs()
    {
        var harness = new BackupRestoreHarness();
        var gate = new TaskCompletionSource<RestoreInspectResult>();
        harness.Service.OnInspect = () => gate.Task;
        var observed = new List<bool>();
        harness.Interaction.OnOpen = async session =>
        {
            observed.Add(session.CanSubmit); // nothing typed yet
            session.SetPassphrase(Passphrase);
            observed.Add(session.CanSubmit);
            var first = session.SubmitAsync();
            observed.Add(session.IsBusy);
            observed.Add(session.CanSubmit);
            observed.Add(await session.SubmitAsync()); // a second click while busy does nothing
            gate.SetResult(new RestoreInspectResult { Error = BackupError.WrongPassphraseOrDamaged });
            await first;
            observed.Add(session.IsBusy);
        };
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Equal(new[] { false, true, true, false, false, false }, observed);
        Assert.Equal(1, harness.Service.InspectCount);
        Assert.Equal(harness.Picker.OpenPath, harness.Service.InspectPath);
        Assert.Equal(Passphrase, harness.Service.InspectPassphrase);
    }

    [Fact]
    public async Task Restore_a_wrong_passphrase_stays_in_the_dialog_with_the_exact_message_and_can_be_retried()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        var results = new Queue<RestoreInspectResult>(
        [
            new RestoreInspectResult { Error = BackupError.WrongPassphraseOrDamaged },
            new RestoreInspectResult { Plan = plan }
        ]);
        harness.Service.OnInspect = () => Task.FromResult(results.Dequeue());
        var closed = new List<bool>();
        string? error = null;
        harness.Interaction.OnOpen = async session =>
        {
            session.SetPassphrase("wrong passphrase");
            closed.Add(await session.SubmitAsync());
            error = session.ErrorMessage;
            session.SetPassphrase(Passphrase);
            closed.Add(await session.SubmitAsync());
        };
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Equal(new[] { false, true }, closed);
        Assert.Equal("RestoreErrorWrongPassphraseOrDamaged", error);
        Assert.Equal(2, harness.Service.InspectCount);
        Assert.Equal(new[] { "open", "confirm" }, harness.Interaction.Calls);
        Assert.False(viewModel.IsStatusOpen);
    }

    public static TheoryData<BackupError, string?> InspectErrors()
    {
        var data = new TheoryData<BackupError, string?>();
        foreach (var error in Enum.GetValues<BackupError>())
        {
            data.Add(error, BackupMessageKeys.ForInspectError(error));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InspectErrors))]
    public async Task Restore_every_inspect_error_shows_its_own_message_and_never_reaches_the_confirm(
        BackupError error,
        string? expectedKey)
    {
        var harness = new BackupRestoreHarness();
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult
        {
            Error = error,
            PendingJournalDirectory = error == BackupError.RestorePending ? JournalDirectory : null
        });
        bool? closed = null;
        string? inline = null;
        harness.Interaction.OnOpen = async session =>
        {
            session.SetPassphrase(Passphrase);
            closed = await session.SubmitAsync();
            inline = session.ErrorMessage;
        };
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Equal(new[] { "open" }, harness.Interaction.Calls);
        Assert.Equal(0, harness.Service.ApplyCount);
        switch (error)
        {
            case BackupError.Canceled:
                Assert.Null(expectedKey);
                Assert.False(closed);
                Assert.False(viewModel.IsStatusOpen);
                break;
            case BackupError.WrongPassphraseOrDamaged:
                Assert.False(closed);
                Assert.Equal(expectedKey, inline);
                Assert.False(viewModel.IsStatusOpen);
                break;
            default:
                Assert.True(closed);
                Assert.True(viewModel.IsStatusOpen);
                Assert.Equal(expectedKey, viewModel.StatusMessage);
                Assert.Equal(error == BackupError.RestorePending, viewModel.IsBlocked);
                break;
        }
    }

    [Theory]
    [InlineData(BackupError.NotABackup, "RestoreErrorNotABackup")]
    [InlineData(BackupError.Damaged, "RestoreErrorDamaged")]
    [InlineData(BackupError.IncompatibleVersion, "RestoreErrorIncompatible")]
    [InlineData(BackupError.WrongPassphraseOrDamaged, "RestoreErrorWrongPassphraseOrDamaged")]
    [InlineData(BackupError.InvalidContent, "RestoreErrorInvalidContent")]
    [InlineData(BackupError.TooLarge, "RestoreErrorTooLarge")]
    [InlineData(BackupError.ReadFailed, "RestoreErrorFileAccess")]
    [InlineData(BackupError.Unsupported, "BackupUnsupported")]
    [InlineData(BackupError.RestorePending, "RestorePendingBlocked")]
    public void Inspect_error_keys_follow_the_copy_contract(BackupError error, string expectedKey) =>
        Assert.Equal(expectedKey, BackupMessageKeys.ForInspectError(error));

    [Theory]
    [InlineData(BackupPassphraseProblem.TooLong, "PassphraseTooLong")]
    [InlineData(BackupPassphraseProblem.InvalidCharacters, "PassphraseInvalidCharacters")]
    [InlineData(BackupPassphraseProblem.Empty, "PassphraseEmpty")]
    public async Task Restore_a_passphrase_problem_from_the_service_stays_in_the_dialog(
        BackupPassphraseProblem problem,
        string expectedKey)
    {
        var harness = new BackupRestoreHarness();
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { PassphraseProblem = problem });
        bool? closed = null;
        string? inline = null;
        harness.Interaction.OnOpen = async session =>
        {
            session.SetPassphrase(Passphrase);
            closed = await session.SubmitAsync();
            inline = session.ErrorMessage;
        };
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.False(closed);
        Assert.Equal(expectedKey, inline);
    }

    [Fact]
    public async Task Restore_an_invalid_passphrase_is_rejected_before_any_key_derivation()
    {
        var harness = new BackupRestoreHarness();
        bool? closed = null;
        string? inline = null;
        harness.Interaction.OnOpen = async session =>
        {
            session.SetPassphrase(new string('a', BackupPassphrasePolicy.MaximumLength + 1));
            closed = await session.SubmitAsync();
            inline = session.ErrorMessage;
        };
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.False(closed);
        Assert.Equal("PassphraseTooLong", inline);
        Assert.Equal(0, harness.Service.InspectCount);
    }

    // ------------------------------------------------------------- restore: destructive confirm + plan

    [Fact]
    public async Task Restore_apply_requires_the_explicit_confirm()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => Task.FromResult(false);
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Equal(new[] { "open", "confirm" }, harness.Interaction.Calls);
        Assert.Equal(0, harness.Service.ApplyCount);
        Assert.Equal(0, harness.Lifecycle.ExitRequests);
        Assert.False(viewModel.IsStatusOpen);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task Restore_the_plan_is_disposed_when_the_confirm_is_canceled()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => Task.FromResult(false);
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.True(plan.IsDisposed);
    }

    [Fact]
    public async Task Restore_applies_only_after_the_confirm_and_then_disposes_the_plan()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.Service.OnApply = _ => Task.FromResult(new RestoreApplyResult { Outcome = RestoreApplyOutcome.RolledBack });
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => Task.FromResult(true);
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Equal(new[] { "open", "confirm", "restoring" }, harness.Interaction.Calls);
        Assert.Equal("RestoreApplying", harness.Interaction.RestoringMessage);
        Assert.Equal(1, harness.Service.ApplyCount);
        Assert.False(harness.Service.PlanWasDisposedAtApply);
        Assert.True(plan.IsDisposed);
    }

    public static TheoryData<RestoreApplyOutcome> ApplyOutcomes()
    {
        var data = new TheoryData<RestoreApplyOutcome>();
        foreach (var outcome in Enum.GetValues<RestoreApplyOutcome>())
        {
            data.Add(outcome);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ApplyOutcomes))]
    public async Task Restore_every_apply_outcome_is_handled_and_the_plan_is_disposed(RestoreApplyOutcome outcome)
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.Service.OnApply = _ => Task.FromResult(new RestoreApplyResult
        {
            Outcome = outcome,
            Error = outcome == RestoreApplyOutcome.RolledBack ? BackupError.WriteFailed : null,
            JournalDirectory = outcome is RestoreApplyOutcome.RestorePending
                or RestoreApplyOutcome.PartialRestoreRollbackPending
                ? JournalDirectory
                : null
        });
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => Task.FromResult(true);
        var planDisposedAtCompletion = false;
        harness.Interaction.OnCompleted = () =>
        {
            planDisposedAtCompletion = plan.IsDisposed;
            return Task.CompletedTask;
        };
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.True(plan.IsDisposed);
        var expectedKey = BackupMessageKeys.ForApplyOutcome(outcome);
        if (outcome == RestoreApplyOutcome.Completed)
        {
            Assert.Equal(("RestoreCompletedTitle", "RestoreCompletedMessage", "RestoreCompletedPrimary"), harness.Interaction.Completed);
            Assert.True(planDisposedAtCompletion);
            Assert.Equal(new[] { ExitReason.RestoreCompleted }, harness.Lifecycle.ExitReasons);
            Assert.False(viewModel.CanStart); // configuration writes are refused until the process ends
            Assert.False(viewModel.IsAvailable);
            Assert.False(viewModel.CreateBackupCommand.CanExecute(null));
            Assert.False(viewModel.RestoreCommand.CanExecute(null));
            return;
        }

        Assert.Equal(0, harness.Lifecycle.ExitRequests);
        Assert.Null(harness.Interaction.Completed);
        Assert.True(viewModel.IsStatusOpen);
        Assert.Equal(expectedKey, viewModel.StatusMessage);
        Assert.Equal(outcome == RestoreApplyOutcome.RestorePending, viewModel.IsBlocked);
        Assert.Equal(outcome != RestoreApplyOutcome.RestorePending, viewModel.CanStart);
    }

    [Theory]
    [InlineData(RestoreApplyOutcome.Completed, "RestoreCompletedMessage")]
    [InlineData(RestoreApplyOutcome.RolledBack, "RestoreRolledBackMessage")]
    [InlineData(RestoreApplyOutcome.Canceled, "RestoreCanceled")]
    [InlineData(RestoreApplyOutcome.PartialRestoreRollbackPending, "RestorePartialPendingMessage")]
    [InlineData(RestoreApplyOutcome.Busy, "RestoreBusy")]
    [InlineData(RestoreApplyOutcome.RestorePending, "RestorePendingBlocked")]
    public void Apply_outcome_keys_follow_the_copy_contract(RestoreApplyOutcome outcome, string expectedKey) =>
        Assert.Equal(expectedKey, BackupMessageKeys.ForApplyOutcome(outcome));

    [Fact]
    public async Task Restore_completed_exits_even_when_the_completion_surface_fails()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => Task.FromResult(true);
        harness.Interaction.OnCompleted = () => throw new InvalidOperationException("no window");
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.Equal(new[] { ExitReason.RestoreCompleted }, harness.Lifecycle.ExitReasons);
    }

    [Fact]
    public async Task Restore_the_plan_is_disposed_when_apply_throws()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.Service.OnApply = _ => throw new InvalidOperationException("detail that must not be shown");
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => Task.FromResult(true);
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.True(plan.IsDisposed);
        Assert.Equal(InfoBarSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal("RestoreRolledBackTitle", viewModel.StatusTitle);
        // After apply started nothing is claimed about the configuration being untouched.
        Assert.Equal("ServerOperationError.Title", viewModel.StatusMessage);
        Assert.Equal(0, harness.Lifecycle.ExitRequests);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task Restore_the_plan_is_disposed_when_the_confirm_surface_throws()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.OpenWithPassphrase();
        harness.Interaction.OnConfirm = _ => throw new InvalidOperationException("no window");
        var viewModel = harness.Create();

        await viewModel.RestoreAsync();

        Assert.True(plan.IsDisposed);
        Assert.Equal(0, harness.Service.ApplyCount);
        Assert.Equal(BackupMessageKeys.Generic, viewModel.StatusMessage);
    }

    /// <summary>The window/host going away while the confirm is up: the secrets are zeroed at once and a
    /// confirm that arrives afterwards applies nothing.</summary>
    [Fact]
    public async Task Restore_the_plan_is_disposed_when_the_view_model_is_disposed_during_the_confirm()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.OpenWithPassphrase();
        var confirm = new TaskCompletionSource<bool>();
        harness.Interaction.OnConfirm = _ => confirm.Task;
        var viewModel = harness.Create();

        var restore = viewModel.RestoreAsync();
        Assert.False(plan.IsDisposed);

        viewModel.Dispose();
        Assert.True(plan.IsDisposed);

        confirm.SetResult(true);
        await restore;

        Assert.Equal(0, harness.Service.ApplyCount);
        Assert.Equal(0, harness.Lifecycle.ExitRequests);
    }

    [Fact]
    public async Task Restore_a_second_flow_cannot_start_while_one_is_running()
    {
        var harness = new BackupRestoreHarness();
        var plan = new FakeRestorePlan(BackupRestoreHarness.Summary());
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Plan = plan });
        harness.OpenWithPassphrase();
        var confirm = new TaskCompletionSource<bool>();
        harness.Interaction.OnConfirm = _ => confirm.Task;
        var viewModel = harness.Create();

        var restore = viewModel.RestoreAsync();

        Assert.False(viewModel.CanStart);
        // The buttons stay enabled (the dialogs are modal and keep the keyboard focus where it was);
        // what refuses a second flow is the guard, however it is invoked.
        Assert.True(viewModel.CreateBackupCommand.CanExecute(null));
        viewModel.CreateBackupCommand.Execute(null);
        viewModel.RestoreCommand.Execute(null);
        await viewModel.CreateBackupAsync();
        await viewModel.RestoreAsync();
        Assert.Equal(new[] { "open", "confirm" }, harness.Interaction.Calls);

        confirm.SetResult(false);
        await restore;
        Assert.True(viewModel.CanStart);
    }

    // --------------------------------------------------------------------------- restore: the summary

    [Fact]
    public void Confirm_lists_current_versus_backup_and_every_warning()
    {
        var harness = new BackupRestoreHarness
        {
            Localization = new FormatLocalization(new Dictionary<string, string>
            {
                ["RestoreSummaryServers"] = "servers {0}>{1} ({2} routed)",
                ["RestoreSummaryTrust"] = "trust {0}>{1}",
                ["RestoreSummaryTrustRemoved"] = "{0} removed",
                ["RestoreSummaryPasswords"] = "passwords {0}",
                ["RestoreSummaryMissingPasswords"] = "missing passwords {0}",
                ["RestoreSummaryMissingKeys"] = "missing keys {0}",
                ["RestoreSummaryNetworkKeys"] = "network keys {0}",
                ["RestoreSummaryUnsupportedKeys"] = "unsupported keys {0}"
            })
        };
        var viewModel = harness.Create();

        var confirmation = viewModel.BuildConfirmation(BackupRestoreHarness.Summary(
            backup: new RestoreCounts(3, 1, 2, 3, 1),
            current: new RestoreCounts(2, 0, 1, 5, 0),
            directToRemove: 2,
            routedToRemove: 1,
            backupSettings: new PortableSettings(false, true),
            currentSettings: new PortableSettings(true, true),
            missingCredentials: [new BackupCredentialFlag(Guid.NewGuid(), "nas", IsJump: false)],
            keyPathWarnings:
            [
                new KeyPathWarning(Guid.NewGuid(), "web", IsJump: false, KeyPathStatus.Missing),
                new KeyPathWarning(Guid.NewGuid(), "db", IsJump: true, KeyPathStatus.NotChecked),
                new KeyPathWarning(Guid.NewGuid(), "cache", IsJump: false, KeyPathStatus.Unsupported)
            ]));

        Assert.Equal("RestoreConfirmTitle", confirmation.Title);
        Assert.Equal("RestoreConfirmPrimary", confirmation.PrimaryText);
        Assert.Equal("Cancel", confirmation.CancelText);
        Assert.Equal("RestoreConfirmNote", confirmation.Note);
        Assert.Equal(
            new[]
            {
                new RestoreConfirmationLine("servers 2>4 (1 routed)", false),
                new RestoreConfirmationLine("trust 5>4", false),
                new RestoreConfirmationLine("3 removed", true),
                new RestoreConfirmationLine("RestoreSummaryTrustDelegated", true),
                new RestoreConfirmationLine("passwords 2", false),
                new RestoreConfirmationLine("missing passwords nas", true),
                new RestoreConfirmationLine("missing keys web", true),
                new RestoreConfirmationLine("network keys db (jump host)", true),
                new RestoreConfirmationLine("unsupported keys cache", true),
                new RestoreConfirmationLine("RestoreSummarySettings", false)
            },
            confirmation.Lines);
    }

    [Fact]
    public void Confirm_says_unknown_and_some_when_the_current_trust_cannot_be_read()
    {
        var harness = new BackupRestoreHarness
        {
            Localization = new FormatLocalization(new Dictionary<string, string>
            {
                ["RestoreSummaryTrust"] = "trust {0}>{1}",
                ["RestoreSummaryTrustRemoved"] = "{0} removed"
            })
        };
        var viewModel = harness.Create();

        var confirmation = viewModel.BuildConfirmation(BackupRestoreHarness.Summary(
            current: new RestoreCounts(2, 0, 1, null, 0),
            directToRemove: null,
            routedToRemove: 0));

        Assert.Contains(new RestoreConfirmationLine("trust RestoreSummaryUnknown>4", false), confirmation.Lines);
        Assert.Contains(new RestoreConfirmationLine("RestoreSummarySome removed", true), confirmation.Lines);
    }

    [Fact]
    public void Confirm_omits_what_does_not_apply()
    {
        var viewModel = new BackupRestoreHarness().Create();

        var confirmation = viewModel.BuildConfirmation(BackupRestoreHarness.Summary(
            backup: new RestoreCounts(1, 0, 0, 0, 0),
            current: new RestoreCounts(1, 0, 0, 0, 0)));

        Assert.Equal(
            new[] { "RestoreSummaryServers", "RestoreSummaryTrust", "RestoreSummaryPasswords", "RestoreSummaryHistoryKept" },
            confirmation.Lines.Select(line => line.Text));
        Assert.DoesNotContain(confirmation.Lines, line => line.IsWarning);
    }

    // ----------------------------------------------------------------------------- startup recovery

    [Theory]
    [InlineData(RestoreRecoveryOutcome.RolledBack, "RestoreRecoveredUndone")]
    [InlineData(RestoreRecoveryOutcome.Completed, "RestoreRecoveredCompleted")]
    [InlineData(RestoreRecoveryOutcome.Stuck, "RestorePendingBlocked")]
    public async Task Startup_recovery_is_shown_exactly_once(RestoreRecoveryOutcome outcome, string expectedKey)
    {
        var harness = new BackupRestoreHarness();
        harness.Service.StartupRecovery = new RestoreRecoveryReport(outcome, JournalDirectory);
        var viewModel = harness.Create();

        await viewModel.ShowStartupRecoveryOnceAsync();
        await viewModel.ShowStartupRecoveryOnceAsync();

        var notice = Assert.Single(harness.Interaction.Notices);
        Assert.Equal(expectedKey, notice.Message);
        Assert.Equal(outcome == RestoreRecoveryOutcome.Stuck, viewModel.IsBlocked);
    }

    [Fact]
    public async Task Startup_with_nothing_to_recover_shows_nothing()
    {
        var harness = new BackupRestoreHarness();
        var viewModel = harness.Create();

        await viewModel.ShowStartupRecoveryOnceAsync();

        Assert.Empty(harness.Interaction.Notices);
        Assert.False(viewModel.IsStatusOpen);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    public async Task A_stuck_journal_blocks_the_feature_and_keeps_the_folder_selectable()
    {
        var harness = new BackupRestoreHarness
        {
            Localization = new FormatLocalization(new Dictionary<string, string>
            {
                ["RestorePendingBlocked"] = "blocked until {0} is removed"
            })
        };
        harness.Service.StartupRecovery = new RestoreRecoveryReport(RestoreRecoveryOutcome.Stuck, JournalDirectory);
        var viewModel = harness.Create();

        await viewModel.ShowStartupRecoveryOnceAsync();

        var notice = Assert.Single(harness.Interaction.Notices);
        Assert.Equal($"blocked until {JournalDirectory} is removed", notice.Message);
        Assert.True(viewModel.IsStatusOpen);
        Assert.False(viewModel.IsStatusClosable);
        Assert.Equal(notice.Message, viewModel.StatusMessage);
        Assert.Equal(JournalDirectory, viewModel.StatusDetail);
        Assert.True(viewModel.HasStatusDetail);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CreateBackupCommand.CanExecute(null));
        Assert.False(viewModel.RestoreCommand.CanExecute(null));

        await viewModel.CreateBackupAsync();
        await viewModel.RestoreAsync();

        Assert.Equal(new[] { "notice" }, harness.Interaction.Calls);
        Assert.Equal(0, harness.Picker.OpenCount);
    }

    // ------------------------------------------------------------------------ the passphrase stays put

    [Fact]
    public async Task The_passphrase_never_reaches_a_log_line_or_a_string_representation()
    {
        var harness = new BackupRestoreHarness();
        harness.Service.OnExport = () => Task.FromResult(new BackupExportResult { Error = BackupError.WriteFailed });
        harness.Service.OnInspect = () => Task.FromResult(new RestoreInspectResult { Error = BackupError.Damaged });
        var representations = new List<string>();
        harness.Interaction.OnCreate = async session =>
        {
            session.SetPassphrase(Passphrase, Passphrase);
            representations.Add(session.ToString());
            await session.SubmitAsync();
        };
        harness.Interaction.OnOpen = async session =>
        {
            session.SetPassphrase(Passphrase);
            representations.Add(session.ToString());
            await session.SubmitAsync();
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();
        await viewModel.RestoreAsync();

        Assert.NotEmpty(harness.Logger.Lines);
        Assert.All(harness.Logger.Lines, line => Assert.DoesNotContain(Passphrase, line));
        Assert.All(representations, text => Assert.DoesNotContain(Passphrase, text));
        Assert.DoesNotContain(Passphrase, viewModel.StatusMessage);
    }

    [Fact]
    public async Task The_sessions_drop_the_passphrase_when_their_dialog_is_done()
    {
        var harness = new BackupRestoreHarness();
        harness.Picker.SavePath = null; // the create dialog is then canceled with a passphrase typed
        BackupCreateSession? create = null;
        RestoreOpenSession? open = null;
        harness.Interaction.OnCreate = session =>
        {
            create = session;
            session.SetPassphrase(Passphrase, Passphrase);
            return Task.CompletedTask;
        };
        harness.Interaction.OnOpen = session =>
        {
            open = session;
            session.SetPassphrase(Passphrase);
            return Task.CompletedTask;
        };
        var viewModel = harness.Create();

        await viewModel.CreateBackupAsync();
        await viewModel.RestoreAsync();

        // With the passphrase gone neither session can submit again, and nothing was sent.
        Assert.False(create!.CanSubmit);
        Assert.False(await create.SubmitAsync());
        Assert.False(open!.CanSubmit);
        Assert.False(await open.SubmitAsync());
        Assert.Equal(0, harness.Service.ExportCount);
        Assert.Equal(0, harness.Service.InspectCount);
    }

    private static BackupCreateSession CaptureCreateSession(out BackupRestoreHarness harness)
    {
        harness = new BackupRestoreHarness();
        BackupCreateSession? captured = null;
        harness.Interaction.OnCreate = session =>
        {
            captured = session;
            return Task.CompletedTask;
        };
        harness.Create().CreateBackupAsync().GetAwaiter().GetResult();

        // The owner cleared it when the dialog "closed"; a fresh one behaves identically for validation.
        return captured!;
    }
}
