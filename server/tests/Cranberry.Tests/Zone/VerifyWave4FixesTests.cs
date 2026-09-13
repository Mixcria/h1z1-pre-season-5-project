using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone;

/// <summary>
/// The verify pass over wave 4. Each case pins one defect the triage confirmed, so that the fix
/// cannot be undone by the next lane without a red test.
/// <para>
/// Send-side and pure-logic assertions only (D29): none of this is LIVE-VERIFIED, and none of it
/// claims to be. What it buys is the half that cost hours on 2026-08-29 — that a knob or a posture
/// transition cannot silently turn a feature off.
/// </para>
/// </summary>
public class VerifyWave4FixesTests
{
    private static readonly Lazy<Z2Doors> Doors = new(Z2Doors.LoadDefault);

    // ---------------------------------------------------------------------------------------
    // Door pump — the arm-order race (ZoneService.PumpDoors)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The defect: <c>state.Doors</c> is created lazily by the landing burst on
    /// <c>GroundLootDelayMs</c> (2000), while the pump is armed at <c>DoorRestreamIntervalMs</c>
    /// (3000). Folding "no MatchDoors yet" into the terminal arm made the whole re-stream depend on
    /// 3000 &gt; 2000 holding — and <c>CRANBERRY_DOOR_RESTREAM_MS</c> is overridable while
    /// <c>GroundLootDelayMs</c> is not, so <c>=1000</c> disabled doors for the rest of the match
    /// with nothing in the log. A tick before the burst must WAIT.
    /// </summary>
    [Fact]
    public void ATickBeforeTheLandingBurstWaitsRatherThanEndingThePump()
    {
        Assert.Equal(
            DoorPumpStep.Wait,
            MatchDoors.NextPumpStep(
                inMatch: true,
                sendDoors: true,
                restreamIntervalMs: 1000,
                doors: null,
                centre: new Vector3(100f, 40f, 100f),
                radius: 60f));
    }

    /// <summary>Under the canopy there is a MatchDoors but no pose: also a wait, not an end.</summary>
    [Fact]
    public void ATickWithNoPoseYetWaitsRatherThanEndingThePump()
    {
        var doors = new MatchDoors(Doors.Value);

        Assert.Equal(
            DoorPumpStep.Wait,
            MatchDoors.NextPumpStep(true, true, 3000, doors, centre: null, radius: 60f));
    }

    /// <summary>Only the match ending, the feature being off, or a non-positive interval may stop it.</summary>
    [Theory]
    [InlineData(false, true, 3000)]
    [InlineData(true, false, 3000)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, -1)]
    public void OnlyTheMatchEndingOrTheFeatureBeingOffStopsThePump(
        bool inMatch,
        bool sendDoors,
        int intervalMs)
    {
        Assert.Equal(
            DoorPumpStep.Stop,
            MatchDoors.NextPumpStep(
                inMatch,
                sendDoors,
                intervalMs,
                new MatchDoors(Doors.Value),
                new Vector3(100f, 40f, 100f),
                radius: 60f));
    }

    /// <summary>A player who has moved half a radius re-streams; a stationary one waits.</summary>
    [Fact]
    public void MovingHalfARadiusRestreamsAndStayingPutWaits()
    {
        var doors = new MatchDoors(Doors.Value);
        var centre = new Vector3(100f, 40f, 100f);

        // Nothing streamed yet: the first burst is always due.
        Assert.Equal(DoorPumpStep.Restream, MatchDoors.NextPumpStep(true, true, 3000, doors, centre, 60f));

        doors.NoteStreamed(centre);
        Assert.Equal(DoorPumpStep.Wait, MatchDoors.NextPumpStep(true, true, 3000, doors, centre, 60f));

        var far = new Vector3(100f + 60f, 40f, 100f);
        Assert.Equal(DoorPumpStep.Restream, MatchDoors.NextPumpStep(true, true, 3000, doors, far, 60f));
    }

    // ---------------------------------------------------------------------------------------
    // Movement — a posture transition is not a plateau
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The defect: <c>SetReportedPosture</c> swapped the mode with no discontinuity marker, so
    /// <c>RecordSpeedCheck</c> compared the first sample of the NEW mode against the last sample of
    /// the OLD one and called the pair a settled plateau.
    /// <para>
    /// The posture word is input state and the horizontal speed is physics, so the bit leads the
    /// velocity by at least a frame — <see cref="MovementPostureBits.HasMovementInput"/>'s own note
    /// records exactly that lead. Press crouch at a full 5.50 m/s run and the first crouching record
    /// still carries 5.50: two samples 0.00 apart, well inside <c>PlateauTolerance</c>, judged as
    /// <c>(Crouching, Forward)</c> against a 3.03 prediction. That is a +2.47 MISMATCH line and
    /// <c>_checked.Add(mode)</c> — the crouch measurement is then retired for the whole match, and
    /// <c>Reset()</c> only runs at zoning. Requiring two samples in the SAME mode is the fix.
    /// </para>
    /// </summary>
    [Fact]
    public void EnteringCrouchAtRunSpeedDoesNotLatchAFalseMismatch()
    {
        var tracker = new PlayerMovementTracker();

        // Speeds are the wave-8 ladder (docs/76): run 4.10, crouch 2.87. The fix under test is the
        // posture-change plateau break, which is unchanged; only the numbers moved.
        tracker.SetReportedPosture(0x0401u);            // standing, forward
        tracker.Observe(4.10f);
        tracker.Observe(4.10f);
        Assert.Contains("MATCH", tracker.TakeSpeedCheckLine());

        // Crouch pressed. The bit is set on this record; the velocity has not moved yet.
        tracker.SetReportedPosture(0x0403u);            // crouching, forward
        tracker.Observe(4.10f);
        Assert.Null(tracker.TakeSpeedCheckLine());

        // The deceleration that follows is a ramp, not a plateau, and is never judged either.
        tracker.Observe(3.80f);
        tracker.Observe(3.20f);
        Assert.Null(tracker.TakeSpeedCheckLine());

        // ...so the genuine crouch plateau is still there to be measured when it arrives.
        tracker.Observe(2.87f);
        tracker.Observe(2.87f);

        string? line = tracker.TakeSpeedCheckLine();
        Assert.NotNull(line);
        Assert.Contains("Crouching", line);
        Assert.Contains("MATCH", line);
        Assert.DoesNotContain("MISMATCH", line);
    }

    /// <summary>
    /// The same discontinuity on the axis, not the stance: turning to backpedal at full run speed
    /// must not settle a <c>(Standing, Backward)</c> plateau at the forward speed.
    /// </summary>
    [Fact]
    public void TurningToBackpedalAtRunSpeedDoesNotLatchAFalseMismatch()
    {
        var tracker = new PlayerMovementTracker();

        tracker.SetReportedPosture(0x0401u);            // standing, forward
        tracker.Observe(5.50f);
        tracker.SetReportedPosture(0x8401u);            // standing, backwards — bit 15
        tracker.Observe(5.50f);

        Assert.Null(tracker.TakeSpeedCheckLine());
    }

    /// <summary>
    /// The guard is a mode-change guard, not a "posture word changed" guard: a posture whose only
    /// difference is an unmodelled bit must still settle.
    /// </summary>
    [Fact]
    public void RepeatingThePostureWordStillSettlesAPlateau()
    {
        var tracker = new PlayerMovementTracker();

        tracker.SetReportedPosture(0x0401u);
        tracker.Observe(5.50f);
        tracker.SetReportedPosture(0x0411u);            // bit 4, the [lead] with no speed effect
        tracker.Observe(5.50f);

        Assert.Contains("MATCH", tracker.TakeSpeedCheckLine());
    }

    // ---------------------------------------------------------------------------------------
    // Inventory — the zoning burst carries no weapon-factory row
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The defect: <c>Bootstrap</c> binds item <b>85 "Fists"</b>, a <c>CODE_FACTORY_NAME = Weapon</c>
    /// row, and wave 4's new bootstrap <c>ItemAdd</c> burst would have put it inside the
    /// <c>ClientIsReady</c> zoning sequence — with the one-byte generic item-class tail that
    /// docs/45 §3b argues is wrong for a Weapon row, on the burst docs/32 proves is the most fragile
    /// in the server. The item stays in the model; only the zoning grant is withheld.
    /// </summary>
    [Fact]
    public void TheBootstrapGrantBurstIncludesFistsAndBinocularsWithWeaponTails()
    {
        ulong next = 0x9000;
        var inventory = new PlayerInventory(0x1001UL, () => ++next);
        inventory.Bootstrap();

        InventoryItemInstance[] weapons =
            [.. inventory.LoadoutSlots.Values.Where(item => item.Fact.CodeFactory == ItemCodeFactory.Weapon)];
        Assert.Equal(2, weapons.Length);
        Assert.Contains(weapons, item => item.DefinitionId == PlayerInventory.SurvivorFistsItemDefinitionId);
        Assert.Contains(weapons, item => item.DefinitionId == PlayerInventory.SurvivorBinocularsItemDefinitionId);

        IReadOnlyList<InventoryItem> bootstrap = inventory.ToBootstrapGrants();
        Assert.All(weapons, weapon =>
            Assert.Contains(bootstrap, grant => grant.ItemGuid == weapon.Guid));
        Assert.Equal(inventory.ToItemGrants().Count, bootstrap.Count);
        Assert.Equal(inventory.BaseBag!.Guid, bootstrap[0].ItemGuid);
    }

    /// <summary>The A/B is one option, so the withholding can be lifted when the weapon tail is derived.</summary>
    [Fact]
    public void TheWeaponGrantCanStillBeWithheldWithOneRollbackOption()
    {
        ulong next = 0x9000;
        var inventory = new PlayerInventory(
            0x1001UL,
            () => ++next,
            new InventoryOptions { GrantWeaponItemsAtBootstrap = false });
        inventory.Bootstrap();

        Assert.Equal(inventory.ToItemGrants().Count - 2, inventory.ToBootstrapGrants().Count);
        Assert.DoesNotContain(
            inventory.ToBootstrapGrants(),
            grant => inventory.Items[grant.ItemGuid].Fact.CodeFactory == ItemCodeFactory.Weapon);
    }

    /// <summary>Apparel is never withheld: the starting outfit is the whole of the owner's item 2.</summary>
    [Fact]
    public void EveryStarterOutfitPieceIsStillGranted()
    {
        ulong next = 0x9000;
        var inventory = new PlayerInventory(0x1001UL, () => ++next);
        inventory.Bootstrap();

        IReadOnlyList<InventoryItem> bootstrap = inventory.ToBootstrapGrants();
        foreach (uint definitionId in new InventoryOptions().StarterOutfit)
        {
            Assert.Contains(bootstrap, grant => grant.DefinitionId == definitionId);
        }
    }
}
