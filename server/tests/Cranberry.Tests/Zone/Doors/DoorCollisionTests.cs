using System.Buffers.Binary;
using Cranberry.Zone;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// Door collision (docs/55). The owner's wave-4 report was "doors did spawn but I am able to walk
/// through them", and the derivation closed the question of what a server can do about it:
/// <b>nothing on the wire</b>. There is no collision field in <c>AddLightweightNpc 0xd6</c>, no
/// collision component beside <c>ClientInteractComponent</c>, and the <c>0xd6</c> apply
/// <c>FUN_140af2900</c> builds its actor-spawn flags from twenty compile-time literals. Collision is
/// a property of the actor definition the model id names, and of nothing else.
/// <para>
/// So the only lever is the model, and these tests pin the two halves of pulling it: the dataset
/// carries the right kinematic twin per family, and the runtime spawns it. They are
/// <b>transcription and wiring</b> tests — none of them can show that the client actually blocks the
/// player, which needs the play-test in docs/55 §I5 (docs/32, D29).
/// </para>
/// </summary>
public sealed class DoorCollisionTests
{
    private static readonly Lazy<Z2Doors> Dataset = new(Z2Doors.LoadDefault);

    /// <summary>
    /// docs/55 §1e + the <c>KINDS</c> table in <c>gen-doors.py</c>. Every id here was resolved by
    /// <b>actor name</b> out of the client's own <c>Models.txt</c>, so this is a transcription
    /// assertion: a failure means the generator stopped naming the actor this row names, not that a
    /// threshold wants retuning.
    /// <para>
    /// The two zeros are not omissions. <c>Camper</c>'s visual mesh <c>Vehicles_Camper01_Door</c> is
    /// 0.126 × 2.349 × 1.336 m with the leaf running along <c>+Z</c> and its origin 0.9 m above the
    /// model base, while every kinematic door in the client is x-hinged with its base at
    /// <c>y = 0</c>; <c>BathroomStall</c>'s is a 1.602 m half-height stall door and the shortest
    /// kinematic actor is 2.258 m. Neither has a twin that would land in the right place.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ResidentialFront", 9904u, 9497u)]   // Common_Props_Doors_ResidentialFront01, WOOD
    [InlineData("Office", 9884u, 9495u)]             // Common_Props_Doors_BuisnessDoorMetal01, METAL
    [InlineData("Camper", 10055u, 0u)]               // no twin: different hinge axis
    [InlineData("Residential", 9905u, 9009u)]        // Common_Props_Doors_ResidentialDoor01, WOOD
    [InlineData("CommercialGlass", 9897u, 9183u)]    // Common_Props_Doors_BuisnessDoorGlass01, GLASS
    [InlineData("Industrial", 9903u, 9495u)]         // IndustrialDoor01 is 21.6 cm too tall
    [InlineData("Cabin", 9901u, 9497u)]              // Common_Props_Doors_ResidentialFront01, WOOD
    [InlineData("BathroomStall", 10083u, 0u)]        // no twin: 1.602 m half-height stall door
    public void EachFamilyCarriesItsDerivedKinematicTwin(string name, uint modelId, uint twinId)
    {
        Z2Doors doors = Dataset.Value;
        int index = doors.KindIndexOf(name);
        Assert.True(index >= 0, $"the dataset has no '{name}' family");

        DoorKind kind = doors.Kinds[index];
        Assert.Equal(modelId, kind.ModelId);
        Assert.Equal(twinId, kind.CollisionModelId);
        Assert.Equal(twinId != 0, kind.HasCollisionModel);

        // A twin equal to the visual mesh would mean the swap is a no-op that still reads as done.
        Assert.NotEqual(kind.ModelId, kind.CollisionModelId);
    }

    /// <summary>
    /// The shipped file is CRDR v2 — the version that carries the twin column at all. A v1 file
    /// still loads (see <see cref="ACrdrV1FileStillLoadsAndKeepsTheWave4Behaviour"/>), so without
    /// this assertion a stale, silently-degraded dataset would pass every other test in this class
    /// by simply having no twins to check.
    /// </summary>
    [Fact]
    public void TheShippedDatasetIsTheVersionThatCarriesTwins()
    {
        Assert.Equal(2, Z2Doors.FormatVersion);
        Assert.Equal(1, Z2Doors.MinimumFormatVersion);

        byte[] bytes = File.ReadAllBytes(DoorDataPaths.Require(Z2Doors.DefaultFileName));
        Assert.Equal(Z2Doors.FormatVersion, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
    }

    /// <summary>
    /// The census that says how much of the map this fix reaches: <b>3,644 of 4,147</b> doors,
    /// 87.9 %. The 503 that remain are the 463 <c>Camper</c> doors, the single
    /// <c>BathroomStall</c> and — since docs/114 §2 — the 39 hospital doors, whose meshes have no
    /// <c>createAsKinematic="1"</c> twin in the client's actor set either. Recorded as a number
    /// rather than left as a surprise in a play-test. Since docs/85 §2a the twin is COSMETIC and
    /// the collision lever is the spawn record's own <c>+0x1b1</c> bit 4, so a family without one
    /// still blocks.
    /// </summary>
    [Fact]
    public void MostOfZ2sDoorsHaveAKinematicTwinAndTheRestAreCounted()
    {
        Z2Doors doors = Dataset.Value;
        int withTwin = 0;

        foreach (DoorPlacement door in doors.Doors)
        {
            if (doors.KindOf(door).HasCollisionModel)
            {
                withTwin++;
            }
        }

        Assert.Equal(3644, withTwin);
        Assert.Equal(503, doors.Count - withTwin);
    }

    /// <summary>
    /// <see cref="DoorCollision.SpawnModelFor"/> is the whole rule, and its contract is that it
    /// never returns 0 and never throws: a family with no twin falls back to the visual mesh under
    /// both modes, which is exactly the wave-4 behaviour.
    /// </summary>
    [Theory]
    [InlineData(DoorCollisionMode.KinematicMesh, 9497u, true)]
    [InlineData(DoorCollisionMode.VisibleMesh, 9904u, false)]
    public void KinematicModeNamesTheTwinAndVisibleModeNamesTheMesh(
        DoorCollisionMode mode, uint expected, bool collidable)
    {
        var withTwin = new DoorKind("ResidentialFront", 9906u, 9904u, 2u, 9497u);

        Assert.Equal(expected, DoorCollision.SpawnModelFor(withTwin, mode));
        Assert.Equal(collidable, DoorCollision.IsCollidable(withTwin, mode));
    }

    [Theory]
    [InlineData(DoorCollisionMode.KinematicMesh)]
    [InlineData(DoorCollisionMode.VisibleMesh)]
    public void AFamilyWithNoTwinAlwaysFallsBackToItsVisibleMesh(DoorCollisionMode mode)
    {
        var noTwin = new DoorKind("Camper", 10054u, 10055u, 4u);

        Assert.False(noTwin.HasCollisionModel);
        Assert.Equal(10055u, DoorCollision.SpawnModelFor(noTwin, mode));
        Assert.False(DoorCollision.IsCollidable(noTwin, mode));
    }

    /// <summary>
    /// The default is the fix, not the baseline. A door lane that shipped
    /// <see cref="DoorCollisionMode.VisibleMesh"/> by default would leave the owner's report
    /// unanswered while every other test here passed.
    /// </summary>
    [Fact]
    public void TheDefaultModeIsTheVisibleMesh()
    {
        // Wave 10 (docs/91). The default moved KinematicMesh -> VisibleMesh: the twin is
        // 0.28-0.31 m narrower than the leaf it replaced and the owner reported the resulting
        // ~0.29 m latch-jamb gap as see-through. The correct-width Doors_* mesh carries its own
        // .cdt collision file, and since wave 9 collision is claimed by the model-independent
        // +0x1b1 bit 0x10 rather than by naming a kinematic actor.
        Assert.Equal(DoorCollisionMode.VisibleMesh, MatchDoorOptions.Default.CollisionMode);
        Assert.Equal(DoorCollisionMode.VisibleMesh, new MatchDoorOptions().CollisionMode);

        // The twin stays one option value away, because it is the revert if doors go walk-through.
        Assert.Equal(
            DoorCollisionMode.KinematicMesh,
            new MatchDoorOptions { CollisionMode = DoorCollisionMode.KinematicMesh }.CollisionMode);
    }

    /// <summary>
    /// The wiring: what <see cref="MatchDoors"/> registers is what <c>0xd6</c> carries. The visual
    /// mesh stays reachable on the instance so a log line can say which door the world file asked
    /// for, but it is <see cref="DoorInstance.SpawnModelId"/> that goes on the wire.
    /// </summary>
    [Theory]
    [InlineData(DoorCollisionMode.KinematicMesh)]
    [InlineData(DoorCollisionMode.VisibleMesh)]
    public void ADoorSpawnsWithTheModelItsModeChose(DoorCollisionMode mode)
    {
        Z2Doors doors = Dataset.Value;
        var match = new MatchDoors(doors, new MatchDoorOptions { CollisionMode = mode });

        int index = FirstDoorOfAFamilyWithATwin(doors);
        DoorKind kind = doors.Kinds[doors[index].KindIndex];
        DoorInstance door = match.Register(index);

        Assert.Equal(kind.ModelId, door.ModelId);
        Assert.Equal(kind.CollisionModelId, door.CollisionModelId);
        Assert.Equal(mode, door.CollisionMode);

        uint expected = mode == DoorCollisionMode.KinematicMesh ? kind.CollisionModelId : kind.ModelId;
        Assert.Equal(expected, door.SpawnModelId);
        Assert.Equal(expected, door.Spawn().ModelId);
        Assert.Equal(mode == DoorCollisionMode.KinematicMesh, door.IsCollidable);
        Assert.Equal(mode == DoorCollisionMode.KinematicMesh ? 1 : 0, match.CollidableCount);
    }

    /// <summary>
    /// <see cref="MatchDoors.CollidableCount"/> is what the host log reports, so it has to track a
    /// burst rather than a single call: it must not double-count an idempotent re-register, and it
    /// must come back down on <see cref="MatchDoors.Unregister"/> and
    /// <see cref="MatchDoors.Clear"/>.
    /// </summary>
    [Fact]
    public void TheCollidableCountTracksRegistrationRatherThanCalls()
    {
        Z2Doors doors = Dataset.Value;
        // Wave 10: this test is ABOUT the kinematic twin, so it names the mode rather than
        // leaning on the default, which is now VisibleMesh (docs/91).
        var match = new MatchDoors(doors, new MatchDoorOptions { CollisionMode = DoorCollisionMode.KinematicMesh });

        int withTwin = FirstDoorOfAFamilyWithATwin(doors);
        int withoutTwin = FirstDoorOfAFamilyWithoutATwin(doors);

        DoorInstance collidable = match.Register(withTwin);
        match.Register(withTwin);                     // idempotent: the same instance, not a second
        match.Register(withoutTwin);

        Assert.Equal(2, match.Count);
        Assert.Equal(1, match.CollidableCount);

        Assert.True(match.Unregister(collidable.WorldGuid));
        Assert.Equal(0, match.CollidableCount);

        match.Register(withTwin);
        Assert.Equal(1, match.CollidableCount);

        match.Clear();
        Assert.Equal(0, match.CollidableCount);
    }

    /// <summary>
    /// docs/55 §5 and D55.12: an open door does <b>not</b> lose collision, and the server has no
    /// packet that could make it — the stepper pushes the swung pose through entity vtable
    /// <c>+0x1a0</c> = <c>FUN_140c1a740</c>, which writes the entity's transform node, so the leaf's
    /// collision instance travels with the leaf. What the server does owe is the <c>0f 0a</c>
    /// re-send after each <c>ea 04</c>, because the client's controller always constructs itself
    /// closed.
    /// <para>
    /// This pins that the model swap did not break it: a door opened, streamed out and streamed
    /// back in comes back <b>open</b> (the open set is keyed by ZONE instance id, so it survives the
    /// new guid) <b>and still names the kinematic actor</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOpenDoorKeepsBothItsOpenBitAndItsCollisionModelAcrossAStreamOut()
    {
        Z2Doors doors = Dataset.Value;
        // Wave 10: this test is ABOUT the kinematic twin, so it names the mode rather than
        // leaning on the default, which is now VisibleMesh (docs/91).
        var match = new MatchDoors(doors, new MatchDoorOptions { CollisionMode = DoorCollisionMode.KinematicMesh });

        int index = FirstDoorOfAFamilyWithATwin(doors);
        DoorInstance before = match.Register(index);
        Assert.True(before.IsCollidable);

        Assert.Equal(
            DoorToggleOutcome.Toggled,
            match.TryToggle(before.WorldGuid, nowMs: 1_000, out DoorInstance? opened));
        Assert.True(opened!.IsOpen);

        Assert.True(match.Unregister(before.WorldGuid));
        DoorInstance after = match.Register(index);

        Assert.NotEqual(before.WorldGuid, after.WorldGuid);     // a fresh spawn, fresh ids
        Assert.Equal(before.InstanceId, after.InstanceId);       // the same door in the world
        Assert.True(after.IsOpen);                               // …and still open
        Assert.True(after.IsCollidable);                         // …and still the kinematic actor
        Assert.Equal(before.SpawnModelId, after.SpawnModelId);

        // The 22-byte 0f 0a that has to follow the ea 04 carries bit 48 and the new guid.
        DoorStateUpdate update = after.StateUpdate();
        Assert.Equal(after.WorldGuid, update.DoorGuid);
        Assert.True(update.IsOpen);
    }

    /// <summary>
    /// docs/55 §2a, D55.7 and §I3: <c>positionUpdateType</c> at <c>+0x11c</c> is the wire field that
    /// reaches the actor's static bit, and <b>0 is the collidable value</b> —
    /// <c>FUN_141fdc290(actor, positionUpdateType == 0)</c> is the only writer of
    /// <c>actor+0x4e1 &amp; 4</c>, and every collision acquire is gated on that bit being set.
    /// Non-zero makes the same function <i>release</i> collision.
    /// <para>
    /// This is here to stop the obvious wrong move: a future wave chasing "still walk-through" by
    /// sweeping this field would make things strictly worse and burn a play-test.
    /// </para>
    /// </summary>
    [Fact]
    public void ADoorSpawnAlwaysCarriesTheCollidablePositionUpdateType()
    {
        Assert.Equal(0, DoorCollision.CollidablePositionUpdateType);

        DoorInstance door = new MatchDoors(Dataset.Value).Register(0);
        LightweightEntityBody body = door.Spawn().Body;

        Assert.Equal(DoorCollision.CollidablePositionUpdateType, body.PositionUpdateType);
    }

    /// <summary>
    /// The compatibility path, exercised rather than asserted: a CRDR <b>v1</b> file — the format
    /// wave 4 shipped, with 12-byte kind records and no twin column — still loads, and every kind
    /// comes back with no twin, which <see cref="DoorCollision.SpawnModelFor"/> resolves to the
    /// visual mesh. That is wave 4 exactly: no crash, and no half-applied swap.
    /// </summary>
    [Fact]
    public void ACrdrV1FileStillLoadsAndKeepsTheWave4Behaviour()
    {
        byte[] v1 = DowngradeToV1(File.ReadAllBytes(DoorDataPaths.Require(Z2Doors.DefaultFileName)));
        Z2Doors doors = Z2Doors.Parse(v1, "synthetic-v1");
        Z2Doors shipped = Dataset.Value;

        Assert.Equal(shipped.Count, doors.Count);
        Assert.Equal(shipped.Kinds.Length, doors.Kinds.Length);

        for (int i = 0; i < doors.Kinds.Length; i++)
        {
            DoorKind kind = doors.Kinds[i];
            Assert.Equal(shipped.Kinds[i].Name, kind.Name);
            Assert.Equal(shipped.Kinds[i].ModelId, kind.ModelId);
            Assert.Equal(0u, kind.CollisionModelId);
            Assert.False(kind.HasCollisionModel);
            Assert.Equal(kind.ModelId, DoorCollision.SpawnModelFor(kind, DoorCollisionMode.KinematicMesh));
        }

        Assert.Equal(0, new MatchDoors(doors).CollidableCount);
    }

    /// <summary>
    /// A twin equal to the family's own visual mesh is a generator bug — the swap would be a no-op
    /// that still reports as applied, which live reads as "the fix did nothing". The loader refuses
    /// it rather than shipping it.
    /// </summary>
    [Fact]
    public void ALoaderRejectsATwinThatIsTheFamilysOwnMesh()
    {
        byte[] bytes = File.ReadAllBytes(DoorDataPaths.Require(Z2Doors.DefaultFileName));
        int kindsAt = 64 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(36));

        // Point kind 0's twin column at kind 0's own model id.
        uint modelId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(kindsAt + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(kindsAt + 12), modelId);

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(() => Z2Doors.Parse(bytes, "self-twinned"));
        Assert.Contains("collision twin", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Rewrites the shipped v2 file as a v1 one: version 1 and 12-byte kind records.</summary>
    private static byte[] DowngradeToV1(byte[] v2)
    {
        int kindCount = BinaryPrimitives.ReadInt32LittleEndian(v2.AsSpan(12));
        int stringBytes = BinaryPrimitives.ReadInt32LittleEndian(v2.AsSpan(36));
        int kindsAt = 64 + stringBytes;

        var v1 = new List<byte>(v2.Length);
        v1.AddRange(v2.AsSpan(0, kindsAt).ToArray());
        for (int i = 0; i < kindCount; i++)
        {
            v1.AddRange(v2.AsSpan(kindsAt + (i * 16), 12).ToArray());
        }

        v1.AddRange(v2.AsSpan(kindsAt + (kindCount * 16)).ToArray());

        byte[] bytes = [.. v1];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        return bytes;
    }

    private static int FirstDoorOfAFamilyWithATwin(Z2Doors doors) => FirstDoorOfAFamily(doors, true);

    private static int FirstDoorOfAFamilyWithoutATwin(Z2Doors doors) => FirstDoorOfAFamily(doors, false);

    private static int FirstDoorOfAFamily(Z2Doors doors, bool hasTwin)
    {
        for (int i = 0; i < doors.Count; i++)
        {
            if (doors.KindOf(doors[i]).HasCollisionModel == hasTwin)
            {
                return i;
            }
        }

        Assert.Fail($"the dataset holds no door whose family {(hasTwin ? "has" : "lacks")} a twin");
        return -1;
    }
}
