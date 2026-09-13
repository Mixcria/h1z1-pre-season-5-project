using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// Wave 9, DOORS lane (docs/85): the owner walked through doors for a second time with wave 8's fix
/// live, so this file pins the corrected mechanism and — just as importantly — pins the falsified
/// one so it cannot quietly come back as the default.
/// <para>
/// The finding: the collision switch in the <c>0xd6</c> record's second flag byte is <c>+0x1b1</c>
/// bit <b>4</b> (<c>0x10</c>), not bit 5. <c>FUN_140c51c90</c> at <c>0x140c51ddd</c> shifts the byte
/// right by 4 and hands bit 0 to <c>FUN_140c75ef0</c> — which is the same function the
/// <c>0f 1e Character.SetCollidable</c> packet reaches, and which drives <c>FUN_141fed190</c>, the
/// actor's collision enable. The owner's Z1 server sends the identical bit of the identical byte at
/// 1087 and his doors are solid (D53).
/// </para>
/// <para>
/// <b>Everything here is BUILT/TESTED, never LIVE-VERIFIED (D29.)</b> These assert what the server
/// writes and decides. Whether the client then makes a door solid is answered by the owner walking
/// into one, and by nothing in this file.
/// </para>
/// </summary>
public sealed class Wave9DoorCollisionTests
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

    // -------------------------------------------------- E1: which bit, and which one is falsified

    /// <summary>
    /// The two constants, asserted as numbers rather than as each other, so a rename cannot make
    /// this test agree with itself while the wire changes. docs/85 §2a.
    /// </summary>
    [Fact]
    public void TheCollisionSwitchIsBitFourAndTheRebuildGateIsBitFive()
    {
        // shr dl, 4 / and dl, 1 at 0x140c51ddd -> FUN_140c75ef0.
        Assert.Equal(0x10, LightweightEntityBody.CollidableFlag);

        // The owner's ZoneNpcs.Collidable = 1 << 4 on flags2, independently derived at 1087.
        Assert.Equal(1 << 4, LightweightEntityBody.CollidableFlag);

        // Wave 8's byte. Kept, retitled, and no longer the default.
        Assert.Equal(0x20, LightweightEntityBody.PhysicsBodyFlag);
        Assert.NotEqual(LightweightEntityBody.PhysicsBodyFlag, LightweightEntityBody.CollidableFlag);

        Assert.Equal(0x30, LightweightEntityBody.DoorSpawnFlagsDefault);

        // docs/114 §5: what a door actually ships with since 2026-09-03 — the collision bit and
        // nothing else, which is the byte the friend's own server writes (D53).
        Assert.Equal(0x10, LightweightEntityBody.DoorSpawnFlagsRetail);
        Assert.Equal(LightweightEntityBody.CollidableFlag, LightweightEntityBody.DoorSpawnFlagsRetail);
    }

    /// <summary>
    /// <b>The wave-8 byte on its own must never be the shipped default again.</b> It reached the
    /// client on 7 of 7 door records in <c>captures/wire-20260830-203615.txt</c> and the owner walked
    /// through the doors; docs/85 §2c then showed it could not have worked, because its only consumer
    /// chain (<c>FUN_141fea440 → FUN_141fed340</c>) returns on a null collision geom and so can only
    /// rebuild an existing body. This is the regression guard for that, stated as the property the
    /// play-test refuted rather than as a constant somebody could re-point.
    /// </summary>
    [Fact]
    public void TheShippedDefaultCarriesTheCollisionBitAndIsNotWaveEightsByte()
    {
        byte shipped = new ZoneOptions().DoorSpawnFlags1;

        Assert.NotEqual(LightweightEntityBody.PhysicsBodyFlag, shipped);
        Assert.NotEqual(0, shipped & LightweightEntityBody.CollidableFlag);
        Assert.Equal(shipped, MatchDoorOptions.Default.SpawnFlags1);
    }

    // ------------------------------------------------- E2/E3: the byte actually reaches the wire

    /// <summary>
    /// End to end through the real dataset: a door registered from <c>MatchDoors</c> carries the
    /// shipped byte at the derived offset of its own <c>0xd6</c>, and its two neighbour flag bytes
    /// stay zero — docs/85 §5 E3 changes one field and nothing else.
    /// </summary>
    [Fact]
    public void ARegisteredDoorPutsTheCollisionBitOnTheWire()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        Assert.Equal(LightweightEntityBody.DoorSpawnFlagsRetail, door.SpawnFlags1);

        AddLightweightDoor spawn = door.Spawn();
        byte[] bytes = Write(spawn.WriteTo);
        int at = spawn.SpawnFlags1Offset;

        Assert.NotEqual(0, bytes[at] & LightweightEntityBody.CollidableFlag);
        Assert.Equal(LightweightEntityBody.DoorSpawnFlagsRetail, bytes[at]);
        Assert.Equal(0x00, bytes[at - 1]);      // +0x1b0
        Assert.Equal(0x00, bytes[at + 1]);      // +0x1b2
    }

    /// <summary>
    /// <b>Ground loot must still get nothing.</b> The owner's Z1 server withholds the bit from loot
    /// deliberately and Cranberry sent <c>(0, 0, 0)</c> on all 65 loot records of the 20:36 session;
    /// a tin of beans that stops a player is a regression, not a fix (docs/85 §3a).
    /// </summary>
    [Fact]
    public void GroundLootStillCarriesNoFlagByte()
    {
        var loot = new LightweightEntityBody(
            ZoneOpcodes.AddLightweightNpc, 0x4400_0000_0000_0002UL, 1000, 9904, Vector3.Zero,
            new Vector4(0, 0, 0, 1));

        Assert.Equal(0, loot.SpawnFlags1);
        Assert.Equal(0x00, Write(loot.WriteTo)[loot.SpawnFlags1Offset]);
    }

    /// <summary>
    /// The env var keeps every experiment in docs/85 §6 reachable without a rebuild: <c>0x10</c> is
    /// the owner's Z1 byte exactly, <c>0x20</c> reproduces wave 8's known walk-through as the
    /// control, and <c>0</c> is an exact wave-7 rollback.
    /// </summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x10)]
    [InlineData(0x20)]
    [InlineData(0x30)]
    public void EveryDocumentedFlagByteReachesTheRecord(byte flags)
    {
        MatchDoors match = NewMatch(new MatchDoorOptions { SpawnFlags1 = flags });
        AddLightweightDoor spawn = match.Register(0).Spawn();

        Assert.Equal(flags, Write(spawn.WriteTo)[spawn.SpawnFlags1Offset]);
    }

    // ------------------------------------------------------- E5: the ported 0f 1e writer

    /// <summary>
    /// <c>Character.SetCollidable</c>, ported from the owner's <c>ZoneDoors.SetCollidable</c> under
    /// D53 and byte-identical at 1148: <c>u8 0x0f; u8 0x1e; u64 guid; u8 bool</c>, <b>exactly 11
    /// bytes</b>. His 1087 reader <c>FUN_1404f0510</c> runs with <c>allowTrailing = 0</c>, so a
    /// twelfth byte would make the client drop the packet in silence.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void SetCollidableIsElevenBytesInTheOwnersLayout(bool collidable, byte expected)
    {
        const ulong Guid = 0x4400_0000_1234_5678UL;
        byte[] bytes = Write(new SetCollidablePacket(Guid, collidable).WriteTo);

        Assert.Equal(SetCollidablePacket.Length, bytes.Length);
        Assert.Equal(11, bytes.Length);
        Assert.Equal(0x0f, bytes[0]);
        Assert.Equal(0x1e, bytes[1]);
        Assert.Equal(Guid, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(expected, bytes[10]);
    }

    /// <summary>
    /// <b>The pair, and why a lone <c>true</c> is the trap.</b> <c>FUN_140c75ef0</c> opens with
    /// <c>cmp dl,al / je done</c> against <c>entity+0x37e6</c> bit 3, which the spawn record's
    /// collision bit has just set — so <c>SetCollidable(true)</c> alone returns at the first branch
    /// and never reaches the actor. The owner's server sends exactly that lone <c>true</c>, which is
    /// the one place this port corrects him (docs/85 §5 E5).
    /// </summary>
    [Fact]
    public void TheSetCollidableStepIsAFalseThenTruePair()
    {
        ReadOnlySpan<DoorSpawnStep> steps = DoorSpawnSequence.For(
            isOpen: false, sendInteractComponent: true, setCollidable: true);

        Assert.Equal(
            [
                DoorSpawnStep.Spawn,
                DoorSpawnStep.FullNpc,
                DoorSpawnStep.SetCollidable,
                DoorSpawnStep.InteractComponent,
            ],
            steps.ToArray());

        // The pair itself, as the two packets ZoneService emits for that one step.
        byte[] off = Write(new SetCollidablePacket(1, Collidable: false).WriteTo);
        byte[] on = Write(new SetCollidablePacket(1, Collidable: true).WriteTo);
        Assert.Equal(0, off[10]);
        Assert.Equal(1, on[10]);
    }

    /// <summary>
    /// It comes AFTER the <c>0xda</c> promotion — the whole point is to restate the bit once the
    /// entity is complete — and BEFORE the interact component and any state update.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SetCollidableFollowsThePromotion(bool isOpen, bool sendInteractComponent)
    {
        DoorSpawnStep[] steps =
            DoorSpawnSequence.For(isOpen, sendInteractComponent, setCollidable: true).ToArray();

        int at = Array.IndexOf(steps, DoorSpawnStep.SetCollidable);
        Assert.True(at > Array.IndexOf(steps, DoorSpawnStep.FullNpc));
        Assert.Equal(steps.Length - 1, at + (sendInteractComponent ? 1 : 0) + (isOpen ? 1 : 0));
    }

    /// <summary>
    /// <b>Off by default, and the sequence is then byte-for-byte the one wave 8 shipped.</b> This is
    /// deliberate, not an oversight (docs/85 §5 E5): the spawn flag is the fix and it ships on, while
    /// the owner already ran this packet at 1087 and parked it in his round 27 — his click-proven
    /// reference server sends zero of them and its doors are solid on the flag alone, and his own
    /// copy was his prime suspect for three filmed door <em>rendering</em> faults.
    /// </summary>
    [Fact]
    public void SetCollidableIsOffByDefaultAndAddsNothingToTheSequence()
    {
        Assert.False(new ZoneOptions().DoorSetCollidable);

        foreach (bool isOpen in new[] { false, true })
        {
            foreach (bool component in new[] { false, true })
            {
                Assert.Equal(
                    DoorSpawnSequence.For(isOpen, component).ToArray(),
                    DoorSpawnSequence.For(isOpen, component, setCollidable: false).ToArray());
                Assert.DoesNotContain(
                    DoorSpawnStep.SetCollidable,
                    DoorSpawnSequence.For(isOpen, component).ToArray());
            }
        }
    }

    // ------------------------------------------------- E6: positionUpdateType is a negative control

    /// <summary>
    /// <c>0</c> stays the default and stays on the wire. docs/85 §2e re-read <c>FUN_141fdc290</c>'s
    /// own bytes on this build: a non-zero <c>positionUpdateType</c> ends with <c>actor+0x4e1</c>
    /// bit 2 clear, and that static bit is the guard on all four callers of the collision acquire
    /// <c>FUN_141ff4ca0</c>. The owner's Z1 server and the 1087 reference both send <c>1</c>, so the
    /// option exists — but as a control that would falsify docs/55 §2 if it helped, not a candidate.
    /// </summary>
    [Fact]
    public void ADoorShipsPositionUpdateTypeZero()
    {
        Assert.Equal(0, DoorCollision.CollidablePositionUpdateType);
        Assert.Equal(0, new ZoneOptions().DoorPositionUpdateType);
        Assert.Equal(0, MatchDoorOptions.Default.PositionUpdateType);

        MatchDoors match = NewMatch();
        Assert.Equal(0, match.Register(0).Spawn().PositionUpdateType);
    }

    /// <summary>The control is reachable end to end, so one session can run it without a rebuild.</summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void ThePositionUpdateTypeOptionReachesTheRecord(byte value)
    {
        MatchDoors match = NewMatch(new MatchDoorOptions { PositionUpdateType = value });
        DoorInstance door = match.Register(0);

        Assert.Equal(value, door.PositionUpdateType);
        Assert.Equal(value, door.Spawn().Body.PositionUpdateType);
    }

    // ------------------------------------------------------------------- E8: nothing else moved

    /// <summary>
    /// The play-test has to read one variable. The swing guard, the collision model and the
    /// <c>0f 0a</c> reply are all untouched this wave (docs/85 §5 E8), so if the doors are still
    /// walk-through the only thing that changed is the flag byte.
    /// </summary>
    [Fact]
    public void NothingElseInTheDoorLaneMovedThisWave()
    {
        var options = new ZoneOptions();

        Assert.Equal(800, options.DoorPressWindowMs);
        // Wave 10 (docs/91) deliberately moved this one: the twin's ~0.29 m latch-jamb gap is
        // what the owner reported as see-through, and collision no longer depends on the model.
        Assert.Equal(DoorCollisionMode.VisibleMesh, options.DoorCollision);
        Assert.Equal(DoorCollisionMode.VisibleMesh, MatchDoorOptions.Default.CollisionMode);
        Assert.Equal(0f, MatchDoorOptions.Default.ResolveRadiusMetres);
        Assert.True(options.SendInteractComponent);

        // The 0f 0a reply is still the only state packet a door gets: 0f 51 is REUSE_81 at 1148.
        Assert.Equal(0x0a, DoorStateUpdate.SubOpcode);
        Assert.Equal(22, DoorStateUpdate.Length);
    }
}
