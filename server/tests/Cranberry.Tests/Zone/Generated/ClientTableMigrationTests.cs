using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Lighting;
using Cranberry.Zone.Match;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Generated;

/// <summary>
/// Lane 2B (docs/104): every id and every sentence that used to be a literal in <c>src</c> is now
/// generated from one of the August client's own tables, and <b>the value did not move</b>.
/// <para>
/// Each test below pins the historical literal — the number or the string this tree carried on
/// 2026-09-02 before the migration — against the generated constant that replaced it. That is the
/// whole safety argument of the lane: a derivation is only an improvement if it reproduces the
/// value it replaced, and these are the assertions that say it did. They are deliberately written
/// as bare literals so that a change to the derivation cannot quietly change them too.
/// </para>
/// </summary>
public sealed class ClientTableMigrationTests
{
    // ==========================================================================================
    // 1. AugustStrings - the locale ids and sentences (derive_strings.py)
    // ==========================================================================================

    /// <summary>
    /// The four <c>11 31 TextAlert</c> sentences the gas broadcasts, and the ids behind them.
    /// The literals are <c>GasAlerts.cs</c>'s, verbatim, as of before the migration.
    /// </summary>
    [Fact]
    public void TheGasAlertSentencesAreTheOnesGasAlertsUsedToCarry()
    {
        Assert.Equal("The Match has begun!", GasAlerts.MatchBegun);
        Assert.Equal("Proceed to the safe area marked on your map.", GasAlerts.Proceed);
        Assert.Equal("Releasing the toxic gas.", GasAlerts.ReleasingGas);
        Assert.Equal(
            "The safe zone has been marked on your map. Toxic gas will be released in 150 seconds.",
            GasAlerts.SafeZoneMarked(150));

        // The ids the client's own CodeStringMappings.txt gives those four message names.
        Assert.Equal(11115u, GasAlerts.MatchBegunId);
        Assert.Equal(11118u, GasAlerts.ProceedId);
        Assert.Equal(11120u, GasAlerts.ReleasingGasId);
        Assert.Equal(11102u, GasAlerts.SafeZoneMarkedId);

        // ...and the same four, straight off the generated table.
        Assert.Equal(11115u, AugustStrings.Alerts.MatchBegun);
        Assert.Equal(11118u, AugustStrings.Alerts.Proceed);
        Assert.Equal(11120u, AugustStrings.Alerts.ReleasingGas);
        Assert.Equal(11102u, AugustStrings.Alerts.SafeZoneMarked);
    }

    /// <summary>The four match-end banners, pinned against <c>MatchAlerts.cs</c>'s old literals.</summary>
    [Fact]
    public void TheMatchAlertSentencesAreTheOnesMatchAlertsUsedToCarry()
    {
        Assert.Equal("Only 12 remain.", MatchAlerts.Remaining(12));
        Assert.Equal("Only 0 remain.", MatchAlerts.Remaining(-4));
        Assert.Equal("Sam has won the match!", MatchAlerts.WinnerAnnounced("Sam"));
        Assert.Equal("Ending winner celebration in 30 seconds.", MatchAlerts.CelebrationEnding(30));
        Assert.Equal("7 connected.", MatchAlerts.Connected(7));

        Assert.Equal(11113u, MatchAlerts.RemainingId);
        Assert.Equal(11124u, MatchAlerts.WinnerAnnouncedId);
        Assert.Equal(11106u, MatchAlerts.CelebrationEndingId);
        Assert.Equal(11123u, MatchAlerts.ConnectedId);
    }

    /// <summary>
    /// The three <c>ce 0f</c> countdown labels <c>GasHud</c> returns. It used to reach for
    /// <c>GameModeHud</c>'s literals; it now reaches for the generated table, and the two agree —
    /// which is also what keeps <c>MatchFlowPackets.cs</c> (another lane's file) honest.
    /// </summary>
    [Fact]
    public void TheGasHudLabelsAreTheOnesGameModeHudCarries()
    {
        Assert.Equal(14153u, AugustStrings.HudLabels.RevealingSafeZone);
        Assert.Equal(14151u, AugustStrings.HudLabels.GasAdvancesIn);
        Assert.Equal(14152u, AugustStrings.HudLabels.GasIsSpreading);
        Assert.Equal(13356u, AugustStrings.HudLabels.StartingMatch);
        Assert.Equal(13198u, AugustStrings.HudLabels.WaitingForPlayers);

        Assert.Equal(GameModeHud.RevealingSafeZoneLabelId, AugustStrings.HudLabels.RevealingSafeZone);
        Assert.Equal(GameModeHud.GasAdvancesInLabelId, AugustStrings.HudLabels.GasAdvancesIn);
        Assert.Equal(GameModeHud.GasIsSpreadingLabelId, AugustStrings.HudLabels.GasIsSpreading);
        Assert.Equal(GameModeHud.StartingMatchLabelId, AugustStrings.HudLabels.StartingMatch);

        Assert.Equal("Revealing safe zone in", AugustStrings.HudLabels.RevealingSafeZoneText);
        Assert.Equal("Gas advances in", AugustStrings.HudLabels.GasAdvancesInText);
        Assert.Equal("Gas is spreading!", AugustStrings.HudLabels.GasIsSpreadingText);
    }

    /// <summary>
    /// The eight <c>09 2d</c> prompt ids, pinned against
    /// <c>InteractionStringPackets.cs</c>'s old literals. None of these has a
    /// <c>CodeStringMappings.txt</c> row, so each is the id whose en_us text is exactly the one
    /// quoted here — three of the eight texts are carried by two ids, and the derivation's
    /// occurrence rule picks the one this file has always used.
    /// </summary>
    [Theory]
    [InlineData(13338u, "[[*key*]] Pick Up [*target*]")]
    [InlineData(12416u, "[[*key*]] Open")]
    [InlineData(8922u, "[[*key*]] Close Door")]
    [InlineData(8882u, "<[[*key*]] Use Door>")]
    [InlineData(12156u, "[[*key*]] Open [*target*]")]
    [InlineData(1004u, "[[*key*]] Use Gate")]
    [InlineData(1326u, "[[*key*]] Search [*target*]")]
    [InlineData(29u, "[[*key*]] Take [*target*]")]
    public void EveryInteractionPromptIdStillCarriesItsOwnText(uint id, string text)
    {
        AugustStringRow row = Assert.Single(AugustStrings.All, r => r.Id == id);
        Assert.Equal("Prompts", row.Group);
        Assert.Equal(text, row.Text);
    }

    /// <summary>The prompt constants themselves, one by one, against the pre-migration literals.</summary>
    [Fact]
    public void TheInteractionPromptConstantsDidNotMove()
    {
        Assert.Equal(0u, InteractionPromptStrings.None);
        Assert.Equal(13338u, InteractionPromptStrings.PickUpTarget);
        Assert.Equal(12416u, InteractionPromptStrings.Open);
        Assert.Equal(8922u, InteractionPromptStrings.CloseDoor);
        Assert.Equal(8882u, InteractionPromptStrings.UseDoor);
        Assert.Equal(12156u, InteractionPromptStrings.OpenTarget);
        Assert.Equal(1004u, InteractionPromptStrings.UseGate);
        Assert.Equal(1326u, InteractionPromptStrings.SearchTarget);
        Assert.Equal(29u, InteractionPromptStrings.TakeTarget);

        // ...and the kind -> id mapping the reply writer uses is unchanged with them.
        Assert.Equal(12416u, InteractionPromptStrings.For(InteractionTargetKind.ClosedDoor));
        Assert.Equal(8922u, InteractionPromptStrings.For(InteractionTargetKind.OpenDoor));
        Assert.Equal(1004u, InteractionPromptStrings.For(InteractionTargetKind.Gate));
        Assert.Equal(13338u, InteractionPromptStrings.For(InteractionTargetKind.GroundLoot));
        Assert.Equal(8882u, InteractionPromptStrings.For(InteractionTargetKind.Vehicle));
        Assert.Equal(0u, InteractionPromptStrings.For(InteractionTargetKind.Unknown));
    }

    /// <summary>
    /// The table itself: 21 rows, unique members, unique ids, every text non-empty, and the locale
    /// key really is the Jenkins hash the client uses (spot-checked against the four values
    /// <c>GasAlerts.cs</c> quoted in its own doc comments before the migration).
    /// </summary>
    [Fact]
    public void TheStringTableIsWellFormed()
    {
        Assert.Equal(21, AugustStrings.All.Count);
        Assert.Equal(21, AugustStrings.All.Select(row => row.Member).Distinct().Count());
        Assert.Equal(21, AugustStrings.All.Select(row => row.Id).Distinct().Count());
        Assert.All(AugustStrings.All, row => Assert.False(string.IsNullOrEmpty(row.Text)));

        Assert.Equal(185602287u, AugustStrings.All.Single(r => r.Id == 11115).LocaleKey);
        Assert.Equal(1065232486u, AugustStrings.All.Single(r => r.Id == 11118).LocaleKey);
        Assert.Equal(2344308363u, AugustStrings.All.Single(r => r.Id == 11120).LocaleKey);
        Assert.Equal(2260824175u, AugustStrings.All.Single(r => r.Id == 11102).LocaleKey);
        Assert.Equal(1256554906u, AugustStrings.All.Single(r => r.Id == 11113).LocaleKey);
        Assert.Equal(4204263506u, AugustStrings.All.Single(r => r.Id == 11124).LocaleKey);
        Assert.Equal(2034493096u, AugustStrings.All.Single(r => r.Id == 11106).LocaleKey);
        Assert.Equal(3204975855u, AugustStrings.All.Single(r => r.Id == 11123).LocaleKey);

        // The 13 rows with a client message name carry it; the 8 prompts have none.
        Assert.Equal(13, AugustStrings.All.Count(row => row.CodeName is not null));
        Assert.Equal(8, AugustStrings.All.Count(row => row.CodeName is null));
        Assert.Equal("BR.Start", AugustStrings.All.Single(r => r.Id == 11115).CodeName);
        Assert.Equal("Match.WaitingForPlayers", AugustStrings.All.Single(r => r.Id == 13198).CodeName);
    }

    /// <summary>
    /// The two placeholder tokens are the client's markup, and every template that needs one has
    /// it — otherwise the expansion helpers would be silently doing nothing.
    /// </summary>
    [Fact]
    public void ThePlaceholderTokensAreTheClientsOwn()
    {
        Assert.Equal("#count([*slot0*])", AugustStrings.CountToken);
        Assert.Equal("[*slot0*]", AugustStrings.SlotToken);

        Assert.Contains(AugustStrings.CountToken, AugustStrings.Alerts.RemainingText, StringComparison.Ordinal);
        Assert.Contains(AugustStrings.CountToken, AugustStrings.Alerts.ConnectedText, StringComparison.Ordinal);
        Assert.Contains(AugustStrings.CountToken, AugustStrings.Alerts.SafeZoneMarkedText, StringComparison.Ordinal);
        Assert.Contains(AugustStrings.CountToken, AugustStrings.Alerts.CelebrationEndingText, StringComparison.Ordinal);
        Assert.Contains(AugustStrings.SlotToken, AugustStrings.Alerts.WinnerAnnouncedText, StringComparison.Ordinal);

        // An expansion leaves no markup behind.
        Assert.DoesNotContain("#count", MatchAlerts.Remaining(3), StringComparison.Ordinal);
        Assert.DoesNotContain("slot0", MatchAlerts.WinnerAnnounced("x"), StringComparison.Ordinal);
    }

    // ==========================================================================================
    // 2. AugustEffectCatalog - the composite-effect allow list (derive_effects.py)
    // ==========================================================================================

    /// <summary>
    /// The client's own <c>ActorCompositeEffectDefinitions.xml</c>: 1,047 definitions over ids
    /// 1..6,025, 2,070 effect parts, every id and every name unique.
    /// </summary>
    [Fact]
    public void TheEffectCatalogueIsTheClientsWholeTable()
    {
        Assert.Equal(1047, AugustEffectCatalog.Count);
        Assert.Equal(1047, AugustEffectCatalog.Definitions.Count);
        Assert.Equal(1u, AugustEffectCatalog.MinId);
        Assert.Equal(6025u, AugustEffectCatalog.MaxId);
        Assert.Equal(2070, AugustEffectCatalog.PartCount);
        Assert.Equal(2070, AugustEffectCatalog.Definitions.Sum(row => row.PartCount));

        Assert.Equal(1047, AugustEffectCatalog.Definitions.Select(row => row.Id).Distinct().Count());
        Assert.Equal(1047, AugustEffectCatalog.Definitions.Select(row => row.Name).Distinct().Count());
        Assert.All(AugustEffectCatalog.Definitions, row => Assert.False(string.IsNullOrEmpty(row.Name)));
    }

    /// <summary>
    /// The named rows S8 §6.4 said the gate would need one day — the door swings, the bleed
    /// overlays, the parachute flare — resolve by the client's own name.
    /// </summary>
    [Theory]
    [InlineData("SFX_Door_Wood_Open", 5048u)]
    [InlineData("SFX_Door_Wood_Close", 5049u)]
    [InlineData("SFX_Door_Office_Open", 5089u)]
    [InlineData("SFX_Door_Glass_Business_Open", 5085u)]
    [InlineData("SFX_Door_Metal_Industrial_Open", 5095u)]
    [InlineData("SFX_Door_Placeable_Metal_Open", 5075u)]
    [InlineData("EFX_Flare_Parachute_Rocket_Red", 5051u)]
    [InlineData("PFX_Bleeding_Human_moderate_loop", 5042u)]
    public void EffectsResolveByTheClientsOwnName(string name, uint id)
    {
        Assert.Equal(id, AugustEffectCatalog.IdOf(name));
        Assert.True(AugustEffectCatalog.TryIdOf(name, out uint viaTry));
        Assert.Equal(id, viaTry);
        Assert.Equal(name, AugustEffectCatalog.ById(id)!.Value.Name);
    }

    /// <summary>
    /// <b>Id 0 is not in the client's table</b>, which is what turns D100's rule from a log-line
    /// observation into a property of the data. The gate refuses it either way.
    /// </summary>
    [Fact]
    public void IdZeroIsNotADefinitionAndIsRefusedTwiceOver()
    {
        Assert.False(AugustEffectCatalog.Contains(0));
        Assert.Null(AugustEffectCatalog.ById(0));
        Assert.False(CompositeEffectGate.IsDefined(0));
        Assert.True(CompositeEffectGate.IsDenied(0));
        Assert.False(CompositeEffectGate.Default.Allowed(0, "migration test"));

        // Every explicitly denied id must also be absent from the allow list, or the two rules
        // would be saying different things about the same number.
        Assert.All(
            CompositeEffectGate.DeniedInThisBuild,
            row => Assert.False(AugustEffectCatalog.Contains(row.Id)));
    }

    /// <summary>
    /// The gate is now an allow list: an id the client's table defines passes, an id it does not
    /// is dropped. Z1's three 1087 ids (5836 / 5840 / 5904) are decided by the table like any
    /// other number rather than by a hand list — which is what D53's "the value crosses, the file
    /// does not" asks for. As it happens all three exist at 1148 as well, so the deny list would
    /// never have caught a genuinely wrong one; the table would.
    /// </summary>
    [Fact]
    public void TheGateIsAnAllowListOverTheClientsTable()
    {
        Assert.True(CompositeEffectGate.Default.Allowed(5048, "migration test"));
        Assert.True(CompositeEffectGate.IsDefined(5048));

        Assert.False(CompositeEffectGate.IsDefined(uint.MaxValue));
        Assert.False(CompositeEffectGate.Default.Allowed(uint.MaxValue, "migration test"));

        foreach (uint z1Id in new uint[] { 5836, 5840, 5904 })
        {
            Assert.Equal(AugustEffectCatalog.Contains(z1Id), CompositeEffectGate.IsDefined(z1Id));
        }
    }

    // ==========================================================================================
    // 3. AugustDoorTable - the Doors.txt row per family (gen-doors.py)
    // ==========================================================================================

    /// <summary>
    /// The client's <c>Doors.txt</c>, all 17 rows, with the effect names its two id columns point
    /// at and the 1,000 ms transition every row carries.
    /// </summary>
    [Fact]
    public void TheClientDoorTableIsSeventeenRowsOfPairedSwingSounds()
    {
        Assert.Equal(17, AugustDoorTable.RowCount);
        Assert.Equal(17, AugustDoorTable.Rows.Count);
        Assert.Equal(2u, AugustDoorTable.Rows[0].Id);
        Assert.Equal(18u, AugustDoorTable.Rows[^1].Id);

        Assert.All(AugustDoorTable.Rows, row =>
        {
            Assert.Equal(1000u, row.TransitionMs);
            Assert.EndsWith("_Open", row.OpenEffectName, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("_Close", row.CloseEffectName, StringComparison.OrdinalIgnoreCase);
            Assert.True(AugustEffectCatalog.Contains(row.OpenEffectId));
            Assert.True(AugustEffectCatalog.Contains(row.CloseEffectId));
        });

        AugustDoorRow wood = AugustDoorTable.Row(2)!.Value;
        Assert.Equal(5048u, wood.OpenEffectId);
        Assert.Equal(5049u, wood.CloseEffectId);
        Assert.Equal("SFX_Door_Wood_Open", wood.OpenEffectName);
    }

    /// <summary>
    /// <b>The byte-identity proof, in C#.</b> The eight row ids <c>gen-doors.py</c> used to type
    /// into <c>KINDS</c> by hand are exactly the eight the sound-name lookup now resolves — and
    /// <c>z2-doors.bin</c>, whose kind records carry them, is byte-for-byte the file it was before
    /// the change (the pipeline reports it unchanged; this asserts the same thing from the data).
    /// </summary>
    [Theory]
    [InlineData("ResidentialFront", 2u, "SFX_Door_Wood_Open")]
    [InlineData("Office", 13u, "SFX_Door_Office_Open")]
    [InlineData("Camper", 4u, "SFX_Door_Placeable_Metal_Open")]
    [InlineData("Residential", 2u, "SFX_Door_Wood_Open")]
    [InlineData("CommercialGlass", 7u, "SFX_Door_Glass_Business_Open")]
    [InlineData("Industrial", 12u, "SFX_Door_Metal_Industrial_Open")]
    [InlineData("Cabin", 2u, "SFX_Door_Wood_Open")]
    [InlineData("BathroomStall", 13u, "SFX_Door_Office_Open")]
    public void EveryDoorFamilyResolvesToTheRowItUsedToHardCode(string kind, uint rowId, string sound)
    {
        AugustDoorKind row = Assert.Single(AugustDoorTable.Kinds, k => k.Kind == kind);
        Assert.Equal(rowId, row.RowId);
        Assert.Equal(sound, row.SoundName);
        Assert.Equal(rowId, AugustDoorTable.RowIdFor(kind));

        // The row really is the lowest one that plays that sound on open.
        uint openEffect = AugustEffectCatalog.IdOf(sound);
        AugustDoorRow lowest = AugustDoorTable.Rows.First(r => r.OpenEffectId == openEffect);
        Assert.Equal(rowId, lowest.Id);
    }

    /// <summary>
    /// And the shipped <c>z2-doors.bin</c> agrees with the generated table, family for family, in
    /// the same order — so nothing can change one without the other.
    /// </summary>
    [Fact]
    public void TheShippedDoorFileCarriesTheResolvedRowIds()
    {
        Z2Doors doors = Z2Doors.LoadDefault();

        Assert.Equal(AugustDoorTable.Kinds.Count, doors.Kinds.Length);
        for (int i = 0; i < doors.Kinds.Length; i++)
        {
            Assert.Equal(AugustDoorTable.Kinds[i].Kind, doors.Kinds[i].Name);
            Assert.Equal(AugustDoorTable.Kinds[i].RowId, doors.Kinds[i].DoorTableId);
            Assert.NotNull(AugustDoorTable.Row(doors.Kinds[i].DoorTableId));
        }
    }
}
