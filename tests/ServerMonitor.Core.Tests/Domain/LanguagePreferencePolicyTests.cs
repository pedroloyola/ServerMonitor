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
}
