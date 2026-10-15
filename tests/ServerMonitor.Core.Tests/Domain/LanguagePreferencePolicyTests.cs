using ServerMonitor.Core.Domain;

namespace ServerMonitor.Core.Tests.Domain;

/// <summary>
/// REL.0D language recovery (human GO D-B1 + D-B2), as pure rules. C1: what startup writes for every combination of
/// persisted override × provenance record × Windows culture. C2: the legacy hint's exact predicate. Plus "follows the
/// system" (the picker's first item) and the system language's own override.
/// </summary>
public sealed class LanguagePreferencePolicyTests
{
    private static readonly LanguageRecordRead Absent = LanguageRecordRead.Absent;
    private static readonly LanguageRecordRead Unreadable = LanguageRecordRead.Unreadable;
    private static readonly LanguageRecordRead ExplicitRecord = LanguageRecordRead.Of(LanguageProvenance.Explicit);
    private static readonly LanguageRecordRead OtherWriter = LanguageRecordRead.OtherWriter;

    private static LanguageRecordRead Following(string autoOverride) => LanguageRecordRead.Of(LanguageProvenance.System(autoOverride));

    // ---- the system language's own override (D-B1 mapping) ----------------------------------------------------------

    [Theory]
    [InlineData("en-US", "")]
    [InlineData("pt-PT", "")]
    [InlineData("pt-BR", "")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt-CV", "pt-PT")]
    [InlineData("pt", "pt-PT")]
    [InlineData("pt-Latn-BR", "pt-BR")]
    public void SystemOverride_IsTheD_B1Mapping_OrNothingForAnExactlySupportedCulture(string systemCulture, string expected) =>
        Assert.Equal(expected, LanguagePreferencePolicy.SystemOverride(systemCulture));

    // ---- C1: startup ------------------------------------------------------------------------------------------------

    /// <summary>No override: follow the system - the record first, then the override when one is needed.</summary>
    [Theory]
    [InlineData("fr-FR", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt-CV", "pt-PT")]
    public void NoOverride_OnACultureThatNeedsOne_RecordsTheSystemChoiceAndWritesIt(string systemCulture, string expected)
    {
        foreach (var record in new[] { Absent, ExplicitRecord, Following("pt-BR") })
        {
            var plan = LanguagePreferencePolicy.AtStartup(null, systemCulture, record);

            Assert.Equal(LanguageProvenance.System(expected), plan.RecordToWrite);
            Assert.Equal(expected, plan.OverrideToWrite);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void NoOverride_OnAnExactlySupportedCulture_RecordsTheSystemChoice_AndWritesNoOverride(string systemCulture)
    {
        var plan = LanguagePreferencePolicy.AtStartup("", systemCulture, Absent);

        Assert.Equal(LanguageProvenance.System(""), plan.RecordToWrite);
        Assert.Null(plan.OverrideToWrite);
    }

    [Fact]
    public void NoOverride_WithTheRecordAlreadyInPlace_WritesNothing() =>
        Assert.True(LanguagePreferencePolicy.AtStartup(null, "pt-PT", Following("")).IsEmpty);

    /// <summary>A corrupt record proves nothing and is never rewritten at startup; the override is resolved as REL.0C did.</summary>
    [Fact]
    public void NoOverride_WithAnUnreadableRecord_LeavesTheRecord_AndWritesTheOverrideAsBefore()
    {
        var plan = LanguagePreferencePolicy.AtStartup(null, "fr-FR", Unreadable);

        Assert.Null(plan.RecordToWrite);
        Assert.Equal("en-US", plan.OverrideToWrite);
    }

    /// <summary>A1 / D-B2: a persisted override that the app cannot prove it wrote is the user's choice: no write at all.</summary>
    [Theory]
    [InlineData("pt-BR", "fr-FR")] // v1.x legacy (no record)
    [InlineData("pt-BR", "en-GB")]
    [InlineData("pt-BR", "pt-AO")]
    [InlineData("en-US", "pt-PT")]
    [InlineData("pt-PT", "en-US")]
    [InlineData("en-US", "fr-FR")]
    public void AnOverrideWithoutProof_IsNeverTouched(string persisted, string systemCulture)
    {
        foreach (var record in new[] { Absent, Unreadable, ExplicitRecord })
        {
            Assert.True(LanguagePreferencePolicy.AtStartup(persisted, systemCulture, record).IsEmpty, $"{persisted}/{systemCulture}/{record.State}");
        }
    }

    /// <summary>K3: the override must be EXACTLY the one recorded; any other value (a downgrade's choice) is explicit.</summary>
    [Theory]
    [InlineData("pt-BR", "en-US", "pt-PT")]
    [InlineData("pt-PT", "en-US", "fr-FR")]
    [InlineData("en-US", "", "pt-PT")]
    public void AnOverrideThatDiffersFromTheRecord_IsExplicit_AndNeverFollowsWindows(string persisted, string recorded, string systemCulture)
    {
        Assert.True(LanguagePreferencePolicy.AtStartup(persisted, systemCulture, Following(recorded)).IsEmpty);
        Assert.False(LanguagePreferencePolicy.FollowsSystem(persisted, Following(recorded)));
    }

    /// <summary>A3: the app's own override follows a later change of the Windows language.</summary>
    [Theory]
    [InlineData("en-US", "pt-AO", "pt-PT")]
    [InlineData("pt-PT", "fr-FR", "en-US")]
    [InlineData("en-US", "pt-PT", "")]
    [InlineData("en-US", "pt-BR", "")]
    public void TheAppsOwnOverride_FollowsWindows(string written, string newSystemCulture, string expected)
    {
        var plan = LanguagePreferencePolicy.AtStartup(written, newSystemCulture, Following(written));

        Assert.Equal(LanguageProvenance.System(expected), plan.RecordToWrite);
        Assert.Equal(expected, plan.OverrideToWrite);
    }

    [Fact]
    public void TheAppsOwnOverride_OnAnUnchangedWindows_WritesNothing() =>
        Assert.True(LanguagePreferencePolicy.AtStartup("en-US", "fr-FR", Following("en-US")).IsEmpty);

    [Fact]
    public void TheRecordComparison_IgnoresCase() =>
        Assert.True(LanguagePreferencePolicy.FollowsSystem("EN-us", Following("en-US")));

    // ---- PR #55 fix round 1 (Cortex N-1): another identity's record proves nothing ----------------------------------

    /// <summary>An override equal to what ANOTHER identity recorded is this identity's explicit choice: never re-resolved.</summary>
    [Theory]
    [InlineData("en-US", "pt-PT")]
    [InlineData("pt-PT", "fr-FR")]
    [InlineData("pt-BR", "en-GB")]
    public void AnOverrideWithAnotherWritersRecord_IsExplicit_AndNeverFollowsWindows(string persisted, string systemCulture)
    {
        Assert.True(LanguagePreferencePolicy.AtStartup(persisted, systemCulture, OtherWriter).IsEmpty);
        Assert.False(LanguagePreferencePolicy.FollowsSystem(persisted, OtherWriter));
    }

    /// <summary>Without an override, this identity records its own first run (replacing the other identity's record).</summary>
    [Fact]
    public void NoOverride_WithAnotherWritersRecord_RecordsItsOwn()
    {
        var plan = LanguagePreferencePolicy.AtStartup(null, "fr-FR", OtherWriter);

        Assert.Equal(LanguageProvenance.System("en-US"), plan.RecordToWrite);
        Assert.Equal("en-US", plan.OverrideToWrite);
    }

    /// <summary>The legacy hint is about THIS identity's v1.x pt-BR: another identity's record does not suppress it.</summary>
    [Theory]
    [InlineData("fr-FR", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    public void TheHint_IsOfferedWhenOnlyAnotherWriterHasARecord(string systemCulture, string expected) =>
        Assert.Equal(expected, LanguagePreferencePolicy.LegacyHintTarget("pt-BR", systemCulture, OtherWriter));

    // ---- REL.0E (PR #57 fix round 1, Cortex M-1): the formatting culture ----------------------------------------------

    /// <summary>The app's own override for a regional variant of the Windows language changes only the resources.</summary>
    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("en-ZA", "en-US")]
    [InlineData("en-IN", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt", "pt-PT")]
    public void TheAppsOwnOverride_ForARegionalVariant_KeepsTheWindowsFormat(string systemCulture, string written) =>
        Assert.Null(LanguagePreferencePolicy.FormattingCulture(written, systemCulture, Following(written)));

    /// <summary>A language the app does not have formats in the default it shows (the text and the numbers agree).</summary>
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void TheAppsOwnOverride_ForAnotherLanguage_FormatsInIt(string systemCulture) =>
        Assert.Equal("en-US", LanguagePreferencePolicy.FormattingCulture("en-US", systemCulture, Following("en-US")));

    /// <summary>The user's choice formats in the choice, even in the same base language as Windows.</summary>
    [Theory]
    [InlineData("en-US", "en-GB")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("pt-BR", "pt-PT")]
    [InlineData("en-US", "pt-BR")]
    [InlineData("pt-PT", "en-US")]
    public void AnExplicitChoice_FormatsInTheChoice(string choice, string systemCulture) =>
        Assert.Equal(choice, LanguagePreferencePolicy.FormattingCulture(choice, systemCulture, ExplicitRecord));

    /// <summary>
    /// #55 semantics, kept on purpose: without a valid record of this identity (none, unreadable, another identity's,
    /// or one that no longer matches) the override is the user's choice, and formats in it.
    /// </summary>
    [Fact]
    public void WithoutAValidMatchingRecord_TheOverrideIsAChoice_AndFormatsInIt()
    {
        Assert.Equal("en-US", LanguagePreferencePolicy.FormattingCulture("en-US", "en-GB", Absent));
        Assert.Equal("en-US", LanguagePreferencePolicy.FormattingCulture("en-US", "en-GB", Unreadable));
        Assert.Equal("en-US", LanguagePreferencePolicy.FormattingCulture("en-US", "en-GB", OtherWriter));
        Assert.Equal("en-US", LanguagePreferencePolicy.FormattingCulture("en-US", "en-GB", Following("pt-PT")));
        Assert.Equal("pt-BR", LanguagePreferencePolicy.FormattingCulture("pt-BR", "pt-PT", Absent)); // v1.x legacy
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoOverride_KeepsTheWindowsFormat(string? persisted)
    {
        Assert.Null(LanguagePreferencePolicy.FormattingCulture(persisted, "en-GB", Following("")));
        Assert.Null(LanguagePreferencePolicy.FormattingCulture(persisted, "fr-FR", Absent));
    }

    // ---- follows the system (the picker's first item) ---------------------------------------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("en-US", false)]
    [InlineData("pt-BR", false)]
    public void FollowsSystem_WithoutARecord_OnlyWithoutAnOverride(string? persisted, bool expected)
    {
        Assert.Equal(expected, LanguagePreferencePolicy.FollowsSystem(persisted, Absent));
        Assert.Equal(expected, LanguagePreferencePolicy.FollowsSystem(persisted, Unreadable));
        Assert.Equal(expected, LanguagePreferencePolicy.FollowsSystem(persisted, ExplicitRecord));
    }

    [Fact]
    public void FollowsSystem_ForTheAppsOwnOverride() =>
        Assert.True(LanguagePreferencePolicy.FollowsSystem("en-US", Following("en-US")));

    // ---- C2: the legacy hint ---------------------------------------------------------------------------------------

    /// <summary>D-B1: every culture the v1.x default hit, Portuguese variants included, with the language it would get.</summary>
    [Theory]
    [InlineData("fr-FR", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("de-DE", "en-US")]
    [InlineData("", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt-CV", "pt-PT")]
    [InlineData("pt", "pt-PT")]
    public void TheHint_IsOfferedForTheLegacySignature_WithTheEffectiveLanguage(string systemCulture, string expected) =>
        Assert.Equal(expected, LanguagePreferencePolicy.LegacyHintTarget("pt-BR", systemCulture, Absent));

    [Fact]
    public void TheHint_IgnoresTheOverridesCase() =>
        Assert.Equal("en-US", LanguagePreferencePolicy.LegacyHintTarget("PT-br", "fr-FR", Absent));

    /// <summary>C2 / K4: removing any one condition removes the hint.</summary>
    [Theory]
    [InlineData("pt-BR", "en-US")] // v1.x never wrote on an exactly supported culture: an explicit choice
    [InlineData("pt-BR", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-BR", "pt-Latn-BR")] // "Use the system language" would give pt-BR again
    [InlineData("en-US", "fr-FR")]
    [InlineData("pt-PT", "pt-AO")]
    [InlineData(null, "fr-FR")]
    [InlineData("", "fr-FR")]
    public void TheHint_IsNotOfferedOutsideTheLegacySignature(string? persisted, string systemCulture) =>
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget(persisted, systemCulture, Absent));

    /// <summary>A record of any kind - including an unreadable one, which proves nothing - means no hint.</summary>
    [Fact]
    public void TheHint_IsNeverOfferedWhenAnyRecordExists()
    {
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", Unreadable));
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", ExplicitRecord));
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", Following("pt-BR")));
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", Following("en-US")));
    }

    // ---- F-1 (Cortex A-lite): the profile language drives the decisions; the display only the hint condition ----------

    /// <summary>The startup decision over the PROFILE language (A, B, C and controls): exactly supported → no override.</summary>
    [Theory]
    [InlineData("en-US", "")]      // A: display pt-BR, [en-US, …]
    [InlineData("pt-BR", "")]      // B: display en-US, [pt-BR, …]
    [InlineData("en-GB", "en-US")] // C: display pt-PT, [en-GB, …]
    [InlineData("pt-PT", "")]
    [InlineData("fr-FR", "en-US")]
    public void AtStartup_OverTheProfileLanguage(string profile, string expectedOverride)
    {
        var plan = LanguagePreferencePolicy.AtStartup(null, profile, Absent);

        Assert.Equal(LanguageProvenance.System(expectedOverride), plan.RecordToWrite);
        Assert.Equal(expectedOverride.Length == 0 ? null : expectedOverride, plan.OverrideToWrite);
    }

    /// <summary>The applied culture: the #57 decision over the profile, else the profile when it is not the display.</summary>
    [Theory]
    [InlineData(null, "en-US", "pt-BR", "", "en-US")]       // A
    [InlineData(null, "pt-BR", "en-US", "", "pt-BR")]       // B
    [InlineData("en-US", "en-GB", "pt-PT", "en-US", "en-GB")] // C: the user's regional variant (MS model)
    [InlineData("en-US", "en-GB", "en-GB", "en-US", null)]  // en-GB machine: .NET already formats en-GB
    [InlineData(null, "pt-PT", "pt-PT", "", null)]          // the common machine: nothing
    [InlineData("en-US", "fr-FR", "pt-PT", "en-US", "en-US")] // no app language in the list: the default it shows
    public void AppliedFormattingCulture_FollowsTheProfileLanguage(
        string? persisted, string profile, string display, string written, string? expected) =>
        Assert.Equal(expected, LanguagePreferencePolicy.AppliedFormattingCulture(persisted, profile, display, Following(written)));

    /// <summary>PR #58 round 1 (Cortex N-1): a neutral profile tag formats in the variant the app shows for it.</summary>
    [Theory]
    [InlineData("pt-PT", "pt", "en-US", "pt-PT", "pt-PT")]
    [InlineData("en-US", "en", "pt-PT", "en-US", "en-US")]
    [InlineData("pt-PT", "pt", "pt-PT", "pt-PT", null)]     // resolves to the display language: nothing to apply
    [InlineData("pt-PT", "pt-AO", "en-US", "pt-PT", "pt-AO")] // a region is the user's own variant: kept
    public void AppliedFormattingCulture_ResolvesANeutralProfileTag(
        string persisted, string profile, string display, string written, string? expected) =>
        Assert.Equal(expected, LanguagePreferencePolicy.AppliedFormattingCulture(persisted, profile, display, Following(written)));

    [Fact]
    public void AppliedFormattingCulture_KeepsAnExplicitChoice()
    {
        Assert.Equal("pt-PT", LanguagePreferencePolicy.AppliedFormattingCulture("pt-PT", "en-US", "pt-BR", ExplicitRecord));
        Assert.Equal("pt-BR", LanguagePreferencePolicy.AppliedFormattingCulture("pt-BR", "en-GB", "pt-PT", Absent));
    }

    /// <summary>The hint: condition on the DISPLAY (v1.x decided by it), target from the PROFILE.</summary>
    [Fact]
    public void TheHint_ConditionIsTheDisplay_TargetIsTheProfile()
    {
        Assert.Equal("en-US", LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", "fr-FR", Absent));
        Assert.Equal("pt-PT", LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", "pt-AO", Absent));
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "fr-FR", "pt-BR", Absent)); // already what they have
        // The trap: a pt-BR on a supported display is a genuine choice, whatever the profile says.
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "pt-PT", "en-GB", Absent));
        Assert.Null(LanguagePreferencePolicy.LegacyHintTarget("pt-BR", "en-US", "fr-FR", Absent));
    }
}
