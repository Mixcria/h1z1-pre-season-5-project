using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// Wave 8, DOORS lane (docs/79): the three things that actually changed when the owner's Z1 door
/// work was carried across to the August build — the press window, the spawn record's physics flag,
/// and resolving strictly by guid — plus the two properties that were already right and must stay
/// that way (the unconditional <c>0xda</c>, and loot never getting a physics body).
/// <para>
/// Everything here is BUILT/TESTED, never LIVE-VERIFIED (D29): these assert what the server writes
/// and decides, not what the client does with it.
/// </para>
/// </summary>
public sealed class Wave8DoorPortTests
{
    private static readonly Lazy<Z2Doors> Dataset = new(Z2Doors.LoadDefault);

    private static MatchDoors NewMatch(MatchDoorOptions? options = null) =>
        new(Dataset.Value, options);

    private static byte[] Write(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ------------------------------------------------------------ E1: the press window

    /// <summary>
    /// The value itself, and the arithmetic behind it. 800 ms is the 1148 client's own swing —
    /// <c>FUN_14149dd10</c> steps a fixed 2 rad/s, so π/2 takes 785 ms (docs/42 §6b) — rounded up,
    /// so the guard cannot expire while the leaf is still moving. It is asserted against that
    /// derivation rather than as a bare constant, because a future edit that shortens it below the
    /// swing reintroduces exactly the defect this lane came to fix.
    /// </summary>
    [Fact]
    public void ThePressWindowIsTheClientsOwnSwingRoundedUp()
    {
        const float radiansPerSecond = 2f;
        double swingMs = MathF.PI / 2f / radiansPerSecond * 1000d;

        Assert.Equal(785, (int)Math.Round(swingMs));
        Assert.Equal(800, MatchDoorOptions.Default.PressWindowMs);
        Assert.Equal(800, new ZoneOptions().DoorPressWindowMs);
        Assert.True(MatchDoorOptions.Default.PressWindowMs >= swingMs);

        // …and inside the empty band the owner measured over 26 toggles: no held-key re-fire wider
        // than 640 ms, no deliberate second press closer than 1,010 ms.
        Assert.True(MatchDoorOptions.Default.PressWindowMs > 640);
        Assert.True(MatchDoorOptions.Default.PressWindowMs < 1010);
    }

    /// <summary>
    /// The defect the window exists to stop, at the cadence the August client actually re-fires at.
    /// A press HELD across four of the client's 171 ms interaction re-evaluations
    /// (<c>logs/host-20260830-163007.log</c>: median 171, min 135) must be one toggle, not five —
    /// at the wave-4 value of 250 ms it was five, so the door opened, closed, opened, closed and
    /// opened again inside its own 785 ms animation.
    /// </summary>
    [Fact]
    public void AHeldKeyAtTheClientsReFireCadenceIsOneToggle()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out _));
        Assert.True(door.IsOpen);

        foreach (long reFire in new long[] { 135, 171, 342, 513, 684, 799 })
        {
            Assert.Equal(DoorToggleOutcome.Absorbed, match.TryToggle(door.WorldGuid, reFire, out _));
            Assert.True(door.IsOpen);
        }

        Assert.Equal(1, match.TotalToggles);
        Assert.Equal(6, match.AbsorbedRequests);

        // The smallest deliberate second press the owner ever recorded still closes it.
        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 1010, out _));
        Assert.False(door.IsOpen);
        Assert.Equal(2, match.TotalToggles);
    }

    /// <summary>
    /// The guard is measured from the last <b>accepted</b> toggle, never from the last request
    /// (docs/79 §4 E12, and the owner's own rule at <c>ZoneDoors.cs:812</c>). Measuring from the
    /// request would let a held key extend the guard forever and eat the genuine press that follows
    /// the swing — the door would stop answering [F] for as long as the player kept trying.
    /// </summary>
    [Fact]
    public void AbsorbedRequestsDoNotExtendTheGuard()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out _));
        for (long t = 100; t < 800; t += 100)
        {
            Assert.Equal(DoorToggleOutcome.Absorbed, match.TryToggle(door.WorldGuid, t, out _));
        }

        // 800 ms after the ACCEPTED toggle, not 800 ms after the last absorbed request at 700.
        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 800, out _));
        Assert.False(door.IsOpen);
    }

    // ------------------------------------------------------- E5–E8: the physics flag byte

    /// <summary>
    /// The flag byte lands at record offset <b>135</b> for a door (transient ids from 1,000,000 take
    /// a three-byte client varint) and <b>133</b> for the one-byte varint docs/68 §1 hexdumped, and
    /// the offset is derived from the record length rather than hardcoded so a body change moves it
    /// instead of misplacing it.
    /// <para>
    /// <b>Wave 9 moved the value this asserts, not the offset.</b> Wave 8 shipped <c>0x20</c> here on
    /// the reading that it was the physics gate; it reached the client on 7 of 7 doors in the owner's
    /// 20:36 session and he walked through them anyway. docs/85 §2a puts the collision switch one bit
    /// along at <c>0x10</c>, so the shipped byte is now <c>0x30</c> — bit 4, plus the falsified bit 5
    /// kept because it is already on the wire and provably inert.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1u, 133)]
    [InlineData(1000u, 134)]
    [InlineData(1_000_000u, 135)]
    public void ADoorSpawnCarriesThePhysicsFlagAtTheDerivedOffset(uint transientId, int expectedOffset)
    {
        var spawn = new AddLightweightDoor(
            0x4400_0000_0000_0001UL, transientId, 9904, Vector3.Zero, 0f, DoorTableId: 2);

        Assert.Equal(expectedOffset, spawn.SpawnFlags1Offset);

        byte[] bytes = Write(spawn.WriteTo);
        // docs/114 §5: the shipped byte is the friend server's 0x10; 0x30 survives as the revert.
        Assert.Equal(LightweightEntityBody.DoorSpawnFlagsRetail, bytes[expectedOffset]);
        Assert.Equal(0x10, LightweightEntityBody.DoorSpawnFlagsRetail);
        Assert.Equal(0x30, LightweightEntityBody.DoorSpawnFlagsDefault);
        Assert.Equal(0x10, LightweightEntityBody.CollidableFlag);
        Assert.Equal(0x20, LightweightEntityBody.PhysicsBodyFlag);

        // The two neighbours stay zero: docs/68 §2d gives +0x1b0 & 0x04 and +0x1b1 & 0x08 their own
        // entity bits, and this lane is asking for one thing only.
        Assert.Equal(0x00, bytes[expectedOffset - 1]);
        Assert.Equal(0x00, bytes[expectedOffset + 1]);
    }

    /// <summary>
    /// <b>Ground loot must never get a physics body.</b> An item with one is an obstacle you cannot
    /// step over (docs/68 §5 F1 open question 4), so the shared body's parameter defaults to 0 and
    /// only the door writer opts in.
    /// </summary>
    [Fact]
    public void AGroundItemNeverCarriesThePhysicsFlag()
    {
        var item = new AddLightweightItem(
            0x2000_0000_0000_0001UL, 1000, 9904, Vector3.Zero, 0, new Vector4(0, 0, 0, 1));

        byte[] bytes = Write(item.WriteTo);
        Assert.Equal(0x00, bytes[item.Body.SpawnFlags1Offset]);
    }

    /// <summary>
    /// <c>SpawnFlags1 = 0</c> is an exact wave-7 rollback — the byte was always written, as a zero,
    /// so turning the flag off restores the previous record byte for byte and cannot change its
    /// length. That is what makes <c>CRANBERRY_DOOR_SPAWN_FLAGS1=0</c> a safe bisect.
    /// </summary>
    [Fact]
    public void ClearingTheFlagReproducesTheWaveSevenRecord()
    {
        var with = new AddLightweightDoor(
            0x4400_0000_0000_0001UL, 1_000_000, 9904, Vector3.One, 1.5708f, DoorTableId: 12);
        AddLightweightDoor without = with with { SpawnFlags1 = 0 };

        byte[] a = Write(with.WriteTo);
        byte[] b = Write(without.WriteTo);

        Assert.Equal(a.Length, b.Length);
        Assert.Equal(with.Length, without.Length);
        Assert.Equal(with.DoorIdOffset, without.DoorIdOffset);

        var differing = new List<int>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                differing.Add(i);
            }
        }

        Assert.Equal([with.SpawnFlags1Offset], differing);
    }

    /// <summary>
    /// The option reaches the wire: <c>MatchDoorOptions.SpawnFlags1</c> → <c>DoorInstance</c> →
    /// <c>Spawn()</c>. Swept as a byte rather than a bool so <c>0x30</c> — the obvious second
    /// experiment if <c>0x20</c> alone does not make a door solid — is an env var, not a rebuild.
    /// </summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x20)]
    [InlineData(0x30)]
    public void TheSpawnFlagOptionReachesTheRecord(byte flags)
    {
        MatchDoors match = NewMatch(new MatchDoorOptions { SpawnFlags1 = flags });
        DoorInstance door = match.Register(0);

        Assert.Equal(flags, door.SpawnFlags1);

        AddLightweightDoor spawn = door.Spawn();
        Assert.Equal(flags, spawn.SpawnFlags1);
        Assert.Equal(flags, Write(spawn.WriteTo)[spawn.SpawnFlags1Offset]);
    }

    /// <summary>
    /// The shipped default carries the collision bit, on every door, from the host's own options.
    /// docs/85 §5 E2: wave 8's <c>0x20</c> is no longer the default, and the owner must not have to
    /// set an environment variable to get a solid door. docs/114 §5 then took 0x20 off the wire
    /// entirely, so the shipped byte is the friend server's own 0x10 — the property this test is
    /// really about, "the collision bit is set without an environment variable", is unchanged.
    /// </summary>
    [Fact]
    public void TheShippedDefaultIsThePhysicsFlag()
    {
        Assert.Equal(LightweightEntityBody.DoorSpawnFlagsRetail, new ZoneOptions().DoorSpawnFlags1);
        Assert.Equal(LightweightEntityBody.DoorSpawnFlagsRetail, MatchDoorOptions.Default.SpawnFlags1);
        Assert.NotEqual(
            0,
            new ZoneOptions().DoorSpawnFlags1 & LightweightEntityBody.CollidableFlag);
    }

    // ---------------------------------------------------------- E9: resolve by guid only

    /// <summary>
    /// The August client echoes the exact server-minted guid (probe D4), so the 6 m positional
    /// fallback has never had to fire — and left on, any <c>09 07</c> naming a guid this session does
    /// not recognise swings whichever door is within 6 m and swallows the dispatcher's fall-through.
    /// Off by default; the mechanism and its option stay.
    /// </summary>
    [Fact]
    public void AnUnknownGuidResolvesToNothingByDefault()
    {
        Assert.Equal(0f, MatchDoorOptions.Default.ResolveRadiusMetres);

        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        var rightOnTopOfIt = new DoorToggleRequest(
            DoorRequestKind.InteractRequest,
            TargetGuid: 0xDEAD_BEEFUL,
            Point: new Vector4(door.Position.X, door.Position.Y, door.Position.Z, 1f),
            HasPoint: true);

        Assert.False(match.TryResolve(rightOnTopOfIt, out DoorInstance? resolved));
        Assert.Null(resolved);
        Assert.Equal(DoorToggleOutcome.NotADoor, match.TryToggle(rightOnTopOfIt, 0, out _));
        Assert.False(door.IsOpen);

        // The guid the server actually minted still works, which is the only path that ever fires.
        var byGuid = new DoorToggleRequest(DoorRequestKind.InteractRequest, door.WorldGuid, 0x1003);
        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(byGuid, 0, out _));
        Assert.True(door.IsOpen);
    }

    // ------------------------------------------- E10: the [F] caption refreshes on a toggle

    /// <summary>
    /// The payload behind the prompt re-reply. The client's prompt driver polls at most once a
    /// second, so without it the caption reads "Open" on a door that is now open. Both ids are the
    /// August locale's own (docs/47 §4e), NOT the owner's Z1 <c>UseDoorStringId = 78</c>, which comes
    /// from a still-forbidden third-party enum and is wrong for 1148 anyway.
    /// </summary>
    [Fact]
    public void TheDoorPromptFlipsWithTheDoorsState()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        Assert.False(door.IsOpen);
        Assert.Equal(InteractionPromptStrings.Open, InteractionStringReply.ForDoor(door).StringId);
        Assert.Equal(12416u, InteractionPromptStrings.Open);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out _));

        InteractionStringReply after = InteractionStringReply.ForDoor(door);
        Assert.Equal(InteractionPromptStrings.CloseDoor, after.StringId);
        Assert.Equal(8922u, InteractionPromptStrings.CloseDoor);
        Assert.Equal(door.WorldGuid, after.TargetGuid);

        // 19 bytes, and the guid it echoes is the one the client just named — FUN_14129e7c0 drops a
        // reply whose guid is not the UI's current target, which is exactly why Z1's proximity sweep
        // is NOT ported.
        Assert.Equal(InteractionStringReply.MinimalLength, Write(after.WriteTo).Length);
        Assert.Equal(19, InteractionStringReply.MinimalLength);
    }

    // ---------------------------------------- §3d: the 0xda is load-bearing and unconditional

    /// <summary>
    /// The smooth-swing property nothing else guards. The <c>0xda</c> must follow every door's
    /// <c>0xd6</c> whatever else is or is not sent, because it clears the client's "no full data yet"
    /// bit and takes the door off the entity-update throttle; without it the 785 ms swing advances in
    /// a staircase, which is the owner's Z1 round-37 <i>"opens kinda choppy"</i>.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheFullNpcPromotionAlwaysFollowsTheSpawn(bool isOpen, bool component)
    {
        DoorSpawnStep[] steps = DoorSpawnSequence.For(isOpen, component).ToArray();

        Assert.Equal(DoorSpawnStep.Spawn, steps[0]);
        Assert.Equal(DoorSpawnStep.FullNpc, steps[1]);
        Assert.Contains(DoorSpawnStep.FullNpc, steps);
    }

    /// <summary>
    /// The rest of the order: the component binds <c>[F]</c> before anything asks the client to move
    /// the leaf, and the <c>0f 0a</c> that re-opens an already-open door comes last and only then —
    /// the controller reads the state bit at construction, when it is necessarily 0, so a state
    /// update sent any earlier would be read into a controller that does not exist yet.
    /// </summary>
    [Fact]
    public void TheSpawnSequenceIsSpawnPromoteBindThenReopen()
    {
        Assert.Equal(
            [DoorSpawnStep.Spawn, DoorSpawnStep.FullNpc, DoorSpawnStep.InteractComponent],
            DoorSpawnSequence.For(isOpen: false, sendInteractComponent: true).ToArray());

        Assert.Equal(
            [
                DoorSpawnStep.Spawn,
                DoorSpawnStep.FullNpc,
                DoorSpawnStep.InteractComponent,
                DoorSpawnStep.StateUpdate,
            ],
            DoorSpawnSequence.For(isOpen: true, sendInteractComponent: true).ToArray());

        // A closed door is never told it is closed: a 0f 0a that changes no bit is a wasted packet
        // per door per burst, and the client's applier does nothing with it.
        Assert.DoesNotContain(
            DoorSpawnStep.StateUpdate,
            DoorSpawnSequence.For(isOpen: false, sendInteractComponent: true).ToArray());
    }

    /// <summary>Allocation-free: the same span backs every call, so a 48-door burst allocates nothing here.</summary>
    [Fact]
    public void TheSpawnSequenceIsCached()
    {
        Assert.True(
            DoorSpawnSequence.For(true, true) == DoorSpawnSequence.For(true, true),
            "DoorSpawnSequence must hand back cached arrays, not build one per door.");
    }

    // ------------------------------------------------- E11: the burst log names the shortfall

    /// <summary>
    /// Probe D6 failed for one reason only: 464 of Z2's 4,103 doors (463 <c>Camper</c>, 1
    /// <c>BathroomStall</c>) have no usable kinematic twin. An unnamed shortfall makes the owner's
    /// play-test misleading, so the burst line names the families — and under the physics flag a
    /// camper door should block anyway, which makes it the discriminator between "the flag was the
    /// cause" and "the model was".
    /// </summary>
    [Fact]
    public void TheBurstLineNamesTheWalkThroughFamilies()
    {
        // Wave 10: the walk-through line only speaks about the twin, so this names that mode;
        // under the new VisibleMesh default the line is empty by design (docs/91).
        MatchDoors match = NewMatch(new MatchDoorOptions { CollisionMode = DoorCollisionMode.KinematicMesh });

        // Every door of a family with a kinematic twin: nothing to report.
        int collidable = FindDoor(match.Dataset, wantCollidable: true);
        DoorInstance solid = match.Register(collidable);
        Assert.True(solid.IsCollidable);
        Assert.Equal(string.Empty, DoorBurstReport.DescribeWalkThrough(match));

        int walkThrough = FindDoor(match.Dataset, wantCollidable: false);
        DoorInstance camper = match.Register(walkThrough);
        Assert.False(camper.IsCollidable);

        string line = DoorBurstReport.DescribeWalkThrough(match);
        Assert.Contains("1 walk-through", line, StringComparison.Ordinal);
        Assert.Contains(camper.KindName, line, StringComparison.Ordinal);
        Assert.DoesNotContain(solid.KindName, line, StringComparison.Ordinal);
    }

    private static int FindDoor(Z2Doors doors, bool wantCollidable)
    {
        for (int i = 0; i < doors.Count; i++)
        {
            DoorKind kind = doors.KindOf(doors[i]);
            bool collidable = DoorCollision.SpawnModelFor(kind, DoorCollisionMode.KinematicMesh)
                == kind.CollisionModelId && kind.CollisionModelId != 0;
            if (collidable == wantCollidable)
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"z2-doors.bin holds no {(wantCollidable ? "kinematic" : "walk-through")} door — "
            + "the dataset or DoorCollision has changed.");
    }
}
