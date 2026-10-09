using System.Text.RegularExpressions;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.10 copy accepted by the Boss (ui10-plan.md; Prism UI.10A §2.4 and UI.10B R2 F27 table), guarded in the three
/// cultures so a later string cannot quietly bring the old wording back.
/// </summary>
public sealed partial class Ui10CopyTests
{
    /// <summary>F27: showing a hidden server again is "Mostrar"/"Show" - "Restaurar"/"Restore" is reserved for the backup.</summary>
    [Theory]
    [InlineData("pt-PT", "Mostrar", "Restaurar a partir de cópia…")]
    [InlineData("pt-BR", "Mostrar", null)]
    [InlineData("en-US", "Show", null)]
    public void F27_HiddenServersAreShown_RestoreIsOnlyTheBackup(string culture, string show, string? backupRestore)
    {
        var resources = ResWLocalizationService.Load(culture);
        Assert.Equal(show, resources["HiddenServerRestoreButton.Content"]);
        foreach (var key in new[]
                 {
                     "HiddenServerRestoreButton.Content", "HiddenServerRestoreFor", "ServersHiddenNote.Text",
                     "ServersNoticeHiddenMessageFormat", "HiddenOverviewBody.Text", "SettingsServerRestoredTitle"
                 })
        {
            Assert.DoesNotMatch(RestoreWord(), resources[key]);
        }

        if (backupRestore is not null)
        {
            Assert.Equal(backupRestore, resources["BackupRestoreButton.Content"]);
        }
    }

    /// <summary>F34 (D6): pt-PT speaks "tu" everywhere - no formal imperative or "o seu / por si" left in any string.</summary>
    [Fact]
    public void F34_PtPt_NeverUsesTheFormalVoice()
    {
        var offenders = ResWLocalizationService.Load("pt-PT")
            .Where(entry => FormalPtPt().IsMatch(entry.Value))
            .Select(entry => $"{entry.Key} = {entry.Value}")
            .ToList();
        Assert.True(offenders.Count == 0, "Formal pt-PT copy:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>F35: pt-BR says "frase secreta" (the editor's term) - never "passphrase".</summary>
    [Fact]
    public void F35_PtBr_SaysFraseSecreta()
    {
        var resources = ResWLocalizationService.Load("pt-BR");
        Assert.DoesNotContain(resources, entry => entry.Value.Contains("passphrase", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Frase secreta", resources["BackupPassphraseLabel"]);
    }

    /// <summary>F28/F30/F33: the CTA names its real effect; one word for "no data".</summary>
    [Theory]
    [InlineData("pt-PT", "Voltar a mostrar dispositivos ignorados", "Sair do ServerAlyzer", "Sem dados")]
    [InlineData("pt-BR", "Mostrar novamente os dispositivos ignorados", "Sair do ServerAlyzer", "Sem dados")]
    [InlineData("en-US", "Show ignored devices again", "Exit ServerAlyzer", "No data")]
    public void F28_F30_F33_SayWhatHappens(string culture, string devices, string exit, string noData)
    {
        var resources = ResWLocalizationService.Load(culture);
        Assert.Equal(devices, resources["SettingsResetIgnoredButton.Content"]);
        Assert.Equal(exit, resources["RestoreCompletedPrimary"]);
        Assert.Equal(resources["TrayExitMenuItem"], resources["RestoreCompletedPrimary"]);
        Assert.Equal(noData, resources["HistoryValueUnknownAccessible"]);
        Assert.Equal(resources["ServerStatusUnknown"], resources["HistoryValueUnknownAccessible"]);
    }

    /// <summary>F28 + Prism UI.10C RC-2: the failure of that action uses the same verb - nothing is "reset".</summary>
    [Theory]
    [InlineData("pt-PT", "Não foi possível voltar a mostrar os dispositivos ignorados")]
    [InlineData("pt-BR", "Não foi possível mostrar novamente os dispositivos ignorados")]
    [InlineData("en-US", "Couldn't show ignored devices again")]
    public void F28_TheErrorSaysTheSameAction(string culture, string error)
    {
        var resources = ResWLocalizationService.Load(culture);
        Assert.Equal(error, resources["SettingsResetIgnoredError.Title"]);
        foreach (var key in new[] { "SettingsResetIgnoredButton.Content", "SettingsResetIgnoredSuccess.Title", "SettingsResetIgnoredError.Title" })
        {
            Assert.DoesNotMatch(ResetWord(), resources[key]);
        }
    }

    /// <summary>F32: en-US says "no connection" for states and counts; "offline" only for the History period.</summary>
    [Fact]
    public void F32_EnUs_NoConnection_ForStatesAndCounts()
    {
        var resources = ResWLocalizationService.Load("en-US");
        foreach (var key in new[]
                 {
                     "ServerStatusOffline", "HistoryServerStatusOffline", "OverviewHealthChipOfflineOne", "OverviewHealthChipOfflineOther",
                     "OverviewHealthAutomationOfflineOne", "OverviewHealthAutomationOfflineOther",
                     "OverviewHealthSegmentOfflineOne", "OverviewHealthSegmentOfflineOther"
                 })
        {
            Assert.Contains("no connection", resources[key], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without connection", resources[key], StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Offline period", resources["HistoryOfflineNotice.Title"]);
    }

    [GeneratedRegex(@"\b(Restaur\w*|restaur\w*|Restore\w*|restore\w*)\b")]
    private static partial Regex RestoreWord();

    [GeneratedRegex(@"\b([Rr]epor|[Rr]epost\w*|[Rr]edefini\w*|[Rr]eset\w*)\b")]
    private static partial Regex ResetWord();

    // The formal imperatives / possessives the UI.10A audit found (F34), as whole words.
    [GeneratedRegex(@"\b(Introduza|Escolha|Tente|tente|Feche|feche|Abra|Abra-o|Volte|Verifique|Adicione-o|Reveja|Reinicie|Atualize|Restaure|Guarde-o|Confirme a|Use pelo|Use no|use ""Sair|Pode alterar)\b|\bpor si\b|\b[Oo]s? seus?\b|\b[Aa]s? suas?\b|ser-lhe-(á|ão)")]
    private static partial Regex FormalPtPt();
}
