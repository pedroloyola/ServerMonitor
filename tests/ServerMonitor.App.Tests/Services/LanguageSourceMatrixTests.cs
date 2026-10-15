using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Tests.Services;

/// <summary>What a machine shows after a launch: the resw text, the number/date culture and the widget card's language.</summary>
internal sealed record LanguageOutcome(string Text, string Numbers, string Widget);

/// <summary>One machine of the F-1 matrix and what it must show (Cortex cortex-f1.md addendum, A-lite).</summary>
internal sealed record LanguageSourceCase(string Id, FakeWindowsLanguages Machine, LanguageOutcome Expected)
{
    public override string ToString() => $"{Id}: {Machine}";
}

/// <summary>
/// F-1 (REL.0D, VM QA; decision: Cortex A-lite). The text (MRT) was right - it follows the preferred-language list - and
/// everything else followed the display language. Now the profile language (the first preferred language in en / pt)
/// drives the language decisions, the number format and the widget, so one machine shows one language.
/// Through the real <see cref="LocalizationService"/> over a fake platform and store and the real
/// <see cref="App.BuildServicesHost"/> (culture recorded, never the process's); MRT simulated by
/// <see cref="MrtRuntimeLanguages"/>. Never the user's real language settings.
/// </summary>
public sealed class LanguageSourceMatrixTests
{
    private static readonly LanguageOutcome EnUs = new("en-US", "en-US", "en-US");
    private static readonly LanguageOutcome PtPt = new("pt-PT", "pt-PT", "pt-PT");
    private static readonly LanguageOutcome PtBr = new("pt-BR", "pt-BR", "pt-BR");

    internal static IReadOnlyList<LanguageSourceCase> Cases { get; } =
    [
        // The common machine (display = first preferred language): unchanged by F-1.
        new("en-US", FakeWindowsLanguages.Display("en-US"), EnUs),
        new("pt-PT", FakeWindowsLanguages.Display("pt-PT"), PtPt),
        new("pt-BR", FakeWindowsLanguages.Display("pt-BR"), PtBr),
        new("en-GB", FakeWindowsLanguages.Display("en-GB"), new("en-US", "en-GB", "en-US")), // #57: the user's regional format
        new("fr-FR", FakeWindowsLanguages.Display("fr-FR"), EnUs),
        new("pt-AO", FakeWindowsLanguages.Display("pt-AO"), new("pt-PT", "pt-AO", "pt-PT")),
        // F-1: display ≠ first preferred language. A is the VM; B and C are HYPOTHESIS until measured on the VM.
        new("A (VM)", FakeWindowsLanguages.Display("pt-BR", "en-US", "pt-BR"), EnUs),
        new("B", FakeWindowsLanguages.Display("en-US", "pt-BR", "en-US"), PtBr),
        new("C", FakeWindowsLanguages.Display("pt-PT", "en-GB", "pt-PT"), new("en-US", "en-GB", "en-US")),
        new("D (no app language first)", FakeWindowsLanguages.Display("pt-PT", "fr-FR", "pt-PT"), PtPt),
        new("E (no app language at all)", FakeWindowsLanguages.Display("pt-PT", "fr-FR"), EnUs),
        // PR #58 round 1 (Cortex N-1): a neutral "pt" formats as the pt-PT the app shows, not ICU's neutral (Brazilian) data.
        new("F (neutral pt)", FakeWindowsLanguages.Display("en-US", "de-DE", "pt", "en-US"), PtPt),
    ];

    public static TheoryData<string> CaseIds() => new(Cases.Select(c => c.Id));

    private static LanguageSourceCase Case(string id) => Cases.Single(c => c.Id == id);

    // ---- the matrix, first run and the next launch ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void EveryMachine_ShowsOneLanguage_TextNumbersAndWidget_OnEveryLaunch(string id)
    {
        var machine = Case(id);
        var platform = machine.Machine.Platform();
        var store = new FakeLanguageSettingsStore();

        Assert.Equal(machine.Expected, Launch(machine.Machine, platform, store));
        Assert.Equal(machine.Expected, Launch(machine.Machine, platform, store));
        Assert.True(Service(platform, store).FollowsSystemLanguage);
        Assert.Null(Service(platform, store).PendingLegacyLanguageHint);
    }

    /// <summary>The text is MRT's and was already right: the override written is exactly what keeps it (nothing on A/B).</summary>
    [Theory]
    [InlineData("A (VM)", null)]
    [InlineData("B", null)]
    [InlineData("C", "en-US")]
    [InlineData("D (no app language first)", null)]
    [InlineData("E (no app language at all)", "en-US")]
    public void TheStartupOverride_FollowsTheProfileLanguage(string id, string? expectedOverride)
    {
        var machine = Case(id).Machine;
        var platform = machine.Platform();
        var store = new FakeLanguageSettingsStore();

        Launch(machine, platform, store);

        Assert.Equal(expectedOverride, platform.PrimaryLanguageOverride);
        Assert.Equal([LanguageProvenance.System(expectedOverride ?? string.Empty)], store.Writes);
    }

    /// <summary>
    /// A QA install of the RC already holds the #55 record System("") (pt-BR display was "supported"). The profile en-US
    /// is supported too, so the record already matches: nothing is migrated or written, and the launch now shows en-US.
    /// </summary>
    [Fact]
    public void TheVmsExistingSystemRecord_NeedsNoMigration_AndNowShowsOneLanguage()
    {
        var vm = Case("A (VM)").Machine;
        var platform = vm.Platform();
        var store = FakeLanguageSettingsStore.Following(string.Empty);

        Assert.Equal(EnUs, Launch(vm, platform, store));
        Assert.Empty(platform.Writes);
        Assert.Empty(store.Writes);
    }

    /// <summary>"Use system language" stays persisted and follows the profile language.</summary>
    [Fact]
    public void UseSystemLanguage_FollowsTheProfileLanguage()
    {
        var c = Case("C").Machine;
        var platform = c.Platform("pt-BR");
        var store = FakeLanguageSettingsStore.ExplicitChoice();

        Assert.True(Service(platform, store).TrySetLanguage(null));

        Assert.Equal("en-US", platform.PrimaryLanguageOverride);
        Assert.Equal([LanguageProvenance.System("en-US")], store.Writes);
        Assert.Equal(new LanguageOutcome("en-US", "en-GB", "en-US"), Launch(c, platform, store));
        Assert.True(Service(platform, store).FollowsSystemLanguage);
    }

    /// <summary>A language picked in Settings wins on any machine and is never rewritten (#55 rules).</summary>
    [Fact]
    public void AnExplicitChoice_IsShownAndNeverRewritten_OnADivergentMachine()
    {
        var vm = Case("A (VM)").Machine;
        var platform = vm.Platform("pt-PT");
        var store = FakeLanguageSettingsStore.ExplicitChoice();

        var outcome = Launch(vm, platform, store);

        Assert.Equal("pt-PT", outcome.Text);
        Assert.Equal("pt-PT", outcome.Numbers);
        Assert.Equal("en-US", outcome.Widget); // pre-existing (ADR-018 §17): the widget never sees an in-app choice
        Assert.Empty(platform.Writes);
        Assert.Empty(store.Writes);
    }

    // ---- #55 recovery hint: the condition stays on the DISPLAY language, the target is the profile's ------------------

    [Fact]
    public void TheLegacyHint_StillComesForAV1PtBr_OnAnUnsupportedDisplay()
    {
        var machine = FakeWindowsLanguages.Display("fr-FR", "fr-FR");
        var platform = machine.Platform("pt-BR");
        var store = new FakeLanguageSettingsStore();

        Launch(machine, platform, store);

        Assert.Empty(platform.Writes);
        Assert.Equal("en-US", Service(platform, store).PendingLegacyLanguageHint);
    }

    /// <summary>Its target is what the user has NOW: the profile language (display fr-FR with [pt-AO, …] → pt-PT).</summary>
    [Fact]
    public void TheLegacyHint_OffersTheProfileLanguage()
    {
        var platform = FakeWindowsLanguages.Display("fr-FR", "pt-AO", "fr-FR").Platform("pt-BR");

        Assert.Equal("pt-PT", Service(platform, new FakeLanguageSettingsStore()).PendingLegacyLanguageHint);
    }

    /// <summary>
    /// The trap A-lite avoids: on a SUPPORTED display (pt-PT) v1.x never wrote pt-BR, so a pt-BR there is a genuine
    /// choice - even when the profile list starts with a language the app does not resolve to pt-BR.
    /// </summary>
    [Fact]
    public void AGenuinePtBrChoice_OnASupportedDisplay_GetsNoHint_WhateverTheProfile()
    {
        var platform = FakeWindowsLanguages.Display("pt-PT", "en-GB", "pt-PT").Platform("pt-BR");
        var store = new FakeLanguageSettingsStore();

        Assert.Null(Service(platform, store).PendingLegacyLanguageHint);
        Assert.Equal(new LanguageOutcome("pt-BR", "pt-BR", "en-US"), Launch(Case("C").Machine, platform, store));
    }

    // ---- the scaffolding itself --------------------------------------------------------------------------------------

    [Fact]
    public void TheSimulatedManifest_IsThePackageManifest()
    {
        var manifest = XDocument.Load(AppSourceTree.Full("Package.appxmanifest"));
        var languages = manifest.Descendants().Where(e => e.Name.LocalName == "Resource")
            .Select(e => (string?)e.Attribute("Language")).ToArray();

        Assert.Equal(MrtRuntimeLanguages.Manifest, languages);
    }

    [Theory]
    [InlineData("pt-PT", new[] { "en-US" }, "pt-PT", "pt-PT")]          // the override comes first
    [InlineData(null, new[] { "en-US", "pt-BR" }, "en-US", "en-US")]     // profile order (the VM)
    [InlineData(null, new[] { "fr-FR", "pt-BR" }, "pt-BR", "pt-BR")]     // a language without resources is skipped
    [InlineData(null, new[] { "en-GB" }, "en-US", "en-GB")]              // regional variant: en-US text, en-GB runtime language
    [InlineData(null, new[] { "fr-FR" }, "en-US", "en-US")]              // nothing matches: the default
    [InlineData(null, new[] { "pt-AO" }, "pt-PT", "pt-AO")]              // HYPOTHESIS: MRT's pt-* choice
    public void TheMrtSimulator_FollowsTheDocumentedRule(string? languageOverride, string[] profile, string text, string runtime)
    {
        Assert.Equal(text, MrtRuntimeLanguages.ResourceLanguage(languageOverride, profile));
        Assert.Equal(runtime, MrtRuntimeLanguages.RuntimeLanguage(languageOverride, profile));
    }

    /// <summary>With no override the numbers are exactly MRT's runtime language: the app now follows the documented model.</summary>
    [Theory]
    [MemberData(nameof(CaseIds))]
    public void WithoutAnOverride_TheNumbersAreTheRuntimeLanguage(string id)
    {
        var machine = Case(id);
        var platform = machine.Machine.Platform();
        var outcome = Launch(machine.Machine, platform, new FakeLanguageSettingsStore());

        if (platform.PrimaryLanguageOverride is null)
        {
            Assert.Equal(MrtRuntimeLanguages.RuntimeLanguage(null, machine.Machine.ProfileLanguages), outcome.Numbers);
        }
    }

    // ---- harness -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One launch: the real startup resolution and formatting decision. Text = what MRT loads for the resulting override
    /// and the machine's profile list; numbers = the culture the launch applied, else the display language (.NET's
    /// CurrentUICulture is left alone); widget = the provider's source - the profile language - through the app's table
    /// (WidgetProvider tests pin WidgetStrings.ProfileLanguage = SupportedCultures.ProfileLanguage and ForCulture = Resolve).
    /// </summary>
    internal static LanguageOutcome Launch(FakeWindowsLanguages machine, FakeUiLanguagePlatform platform, FakeLanguageSettingsStore store)
    {
        var applied = new List<CultureInfo>();
        var service = Service(platform, store);
        using (App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(service), applied.Add))
        {
        }

        return new LanguageOutcome(
            MrtRuntimeLanguages.ResourceLanguage(service.CurrentLanguageOverride, machine.ProfileLanguages),
            applied.Count > 0 ? applied[^1].Name : machine.DisplayCulture,
            SupportedCultures.Resolve(machine.ProfileLanguage));
    }

    private static LocalizationService Service(FakeUiLanguagePlatform platform, FakeLanguageSettingsStore store) =>
        new(NullLogger<LocalizationService>.Instance, platform, store);
}
