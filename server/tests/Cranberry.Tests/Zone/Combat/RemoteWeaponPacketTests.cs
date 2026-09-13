using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// <c>82 15 RemoteWeaponBase</c> — the family that puts a gun in another player's hands.
///
/// <para>
/// <b>Every expected value here is S5c §3.8's own worked hex</b>, which was written from the August
/// readers before any C# existed. That makes these tests a check of the writer against an
/// independent transcription, not a restatement of it. What they cannot prove is that the client
/// likes the packet: nothing in this family has ever been sent, and it never can be to the local
/// player (S5c §0 finding 2), so the first real evidence is two clients in one match.
/// </para>
///
/// <para>
/// <b>One correction to S5c is pinned here.</b> Its byte-count column for every <c>UpdateBase</c>
/// row (<c>SwitchFireMode 24</c>, <c>Chamber 22</c>, <c>Reload 22</c>, <c>FireState 39</c>,
/// <c>ProjectileLaunch 34</c>) is 5 too high — count the hex it prints on the same line and the
/// answers are 19, 17, 17, 34 and 29. The non-<c>UpdateBase</c> rows (<c>AddWeapon 55</c>,
/// <c>Reset 63</c>, <c>RemoveWeapon 16</c>) are right, so the error is in the arithmetic for sub
/// <c>0x04</c> alone and not in the layout. The hex is the specification; these tests encode it.
/// </para>
/// </summary>
public sealed class RemoteWeaponPacketTests
{
    /// <summary>S5c §3.8: owner transient 5, which the client varint writes as the single byte 0x14.</summary>
    private const uint Owner = 5;

    /// <summary>S5c §3.8: item instance <c>0x3000000000000042</c>.</summary>
    private const ulong ItemGuid = 0x3000_0000_0000_0042;

    /// <summary>S5c §3.8's AR-15: definition 6, right hand, one group id 6 with charges 30 and 1.</summary>
    private static RemoteWeaponBlob Ar15() => new(
        WeaponDefinitionId: 6,
        EquipmentSlotId: RemoteWeaponBlob.RightHandSlot,
        FireGroups: [new RemoteFireGroup(6, [new RemoteFireMode(30), new RemoteFireMode(1)])]);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Hex(string spaced) => spaced.Replace(" ", string.Empty).ToLowerInvariant();

    // ------------------------------------------------------------------ the blob

    [Fact]
    public void TheNestedBlobIsTheThirtyFiveBytesS5cWorked()
    {
        RemoteWeaponBlob blob = Ar15();

        Assert.Equal(35, blob.Length);
        Assert.Equal(
            Hex("06000000 07 01 06000000 02 1E000000 00000000 01000000 00000000 00000000 00000000"),
            Hex(blob.ToArray()));
    }

    /// <summary>
    /// The per-mode record is eight bytes, not the thirteen the <em>local</em> component's tail uses
    /// (docs/58 §5). S5c §3.6 calls the difference out explicitly, and getting it wrong shifts every
    /// byte after the first mode.
    /// </summary>
    [Fact]
    public void AModeIsEightBytesNotThirteen()
    {
        int oneMode = new RemoteWeaponBlob(6, 7, [new RemoteFireGroup(6, [new RemoteFireMode(30)])]).Length;
        int twoModes = Ar15().Length;

        Assert.Equal(8, twoModes - oneMode);
        Assert.Equal(8, RemoteFireMode.WireLength);
    }

    // ------------------------------------------------------------------ the subs

    [Fact]
    public void AddWeaponIsFiftyFiveBytesAndMatchesTheWorkedHex()
    {
        byte[] wire = RemoteWeaponPackets.AddWeapon(Owner, ItemGuid, Ar15());

        Assert.Equal(55, wire.Length);
        Assert.Equal(RemoteWeaponPackets.AddWeaponLength(Owner, Ar15()), wire.Length);
        Assert.Equal(
            Hex("82 00000000 15 02 14 4200000000000030 23000000"
                + "06000000 07 01 06000000 02 1E000000 00000000 01000000 00000000 00000000 00000000"),
            Hex(wire));
    }

    [Fact]
    public void ResetIsSixtyThreeBytesAndItsInnerLengthIsFiftyOne()
    {
        byte[] wire = RemoteWeaponPackets.Reset(Owner, [new RemoteWeaponEntry(ItemGuid, Ar15())]);

        Assert.Equal(63, wire.Length);
        Assert.Equal(RemoteWeaponPackets.ResetLength(Owner, [new RemoteWeaponEntry(ItemGuid, Ar15())]), wire.Length);

        // 0x33 = 51 = u32 count + u64 guid + 35-byte blob + i32 stateCount.
        Assert.Equal(
            Hex("82 00000000 15 01 14 33000000 01000000 4200000000000030"
                + "06000000 07 01 06000000 02 1E000000 00000000 01000000 00000000 00000000 00000000"
                + "00000000"),
            Hex(wire));
    }

    /// <summary>
    /// An empty arsenal is a legal <c>Reset</c> — it is how a peer's weapons are taken away wholesale,
    /// because the handler destroys every registered weapon before it reads a byte of the body.
    /// </summary>
    [Fact]
    public void AnEmptyResetStripsTheArsenalAndIsTwelveBytesOfBody()
    {
        byte[] wire = RemoteWeaponPackets.Reset(Owner, []);

        Assert.Equal(Hex("82 00000000 15 01 14 08000000 00000000 00000000"), Hex(wire));
    }

    [Fact]
    public void RemoveWeaponIsSixteenBytes()
    {
        byte[] wire = RemoteWeaponPackets.RemoveWeapon(Owner, ItemGuid);

        Assert.Equal(16, wire.Length);
        Assert.Equal(RemoteWeaponPackets.RemoveWeaponLength(Owner), wire.Length);
        Assert.Equal(Hex("82 00000000 15 03 14 4200000000000030"), Hex(wire));
    }

    // ------------------------------------------------------------------ UpdateBase

    [Fact]
    public void SwitchFireModeMatchesTheWorkedHex()
    {
        byte[] wire = RemoteWeaponPackets.SwitchFireMode(Owner, ItemGuid, 0, 0);

        Assert.Equal(Hex("82 00000000 15 04 14 06 4200000000000030 00 00"), Hex(wire));
        Assert.Equal(19, wire.Length);
    }

    [Fact]
    public void ChamberAndReloadCarryNoPayloadAtAll()
    {
        Assert.Equal(
            Hex("82 00000000 15 04 14 0c 4200000000000030"),
            Hex(RemoteWeaponPackets.Chamber(Owner, ItemGuid)));
        Assert.Equal(
            Hex("82 00000000 15 04 14 03 4200000000000030"),
            Hex(RemoteWeaponPackets.Reload(Owner, ItemGuid)));
        Assert.Equal(
            Hex("82 00000000 15 04 14 05 4200000000000030"),
            Hex(RemoteWeaponPackets.ReloadInterrupt(Owner, ItemGuid)));
        Assert.Equal(
            Hex("82 00000000 15 04 14 0f 4200000000000030"),
            Hex(RemoteWeaponPackets.ChamberInterrupt(Owner, ItemGuid)));
    }

    /// <summary>
    /// S5c §3.8 gives <c>02</c> for start and <c>03</c> for stop: bit 1 is "an aim point follows"
    /// and bit 0 is "stop". Flipping either one silently turns a shot into a cease-fire.
    /// </summary>
    [Fact]
    public void FireStateStartAndStopDifferOnlyInBitZero()
    {
        var aim = new Vector4(0f, 0f, 0f, 0f);
        byte[] start = RemoteWeaponPackets.FireState(Owner, ItemGuid, firing: true, aim);
        byte[] stop = RemoteWeaponPackets.FireState(Owner, ItemGuid, firing: false, aim);

        Assert.Equal(34, start.Length);
        Assert.Equal(RemoteWeaponPackets.FireStateLength(Owner), start.Length);
        Assert.Equal(
            Hex("82 00000000 15 04 14 01 4200000000000030 02 00000000 00000000 00000000 00000000"),
            Hex(start));
        Assert.Equal(
            Hex("82 00000000 15 04 14 01 4200000000000030 03 00000000 00000000 00000000 00000000"),
            Hex(stop));
    }

    [Fact]
    public void ProjectileLaunchIsTwentyNineBytes()
    {
        byte[] wire = RemoteWeaponPackets.ProjectileLaunch(Owner, ItemGuid, projectileId: 0);

        Assert.Equal(29, wire.Length);
        Assert.Equal(
            Hex("82 00000000 15 04 14 0b 4200000000000030 00000000 0000000000000000"),
            Hex(wire));
    }

    [Fact]
    public void AimBlockedAndReloadLoopEndCarryOneBool()
    {
        Assert.Equal(
            Hex("82 00000000 15 04 14 10 4200000000000030 01"),
            Hex(RemoteWeaponPackets.AimBlocked(Owner, ItemGuid, blocked: true)));
        Assert.Equal(
            Hex("82 00000000 15 04 14 04 4200000000000030 00"),
            Hex(RemoteWeaponPackets.ReloadLoopEnd(Owner, ItemGuid, value: false)));
    }

    // ------------------------------------------------------------------ the hints

    [Fact]
    public void TheProjectileHintsHaveTheirDerivedLengths()
    {
        byte[] launch = RemoteWeaponPackets.ProjectileLaunchHint(
            Owner, ItemGuid, projectileId: 7, Vector3.Zero, Vector3.UnitX);
        byte[] detonate = RemoteWeaponPackets.ProjectileDetonateHint(Owner, projectileId: 7, Vector3.Zero);

        // 8 header + 8 guid + 4 id + 24 of vectors.
        Assert.Equal(44, launch.Length);
        Assert.Equal(RemoteWeaponPackets.ProjectileLaunchHintLength(Owner), launch.Length);

        // The detonate hint has NO item instance id: the projectile is addressed by the character.
        Assert.Equal(24, detonate.Length);
        Assert.Equal(RemoteWeaponPackets.ProjectileDetonateHintLength(Owner), detonate.Length);
        Assert.Equal(Hex("82 00000000 15 06 14 07000000 00000000 00000000 00000000"), Hex(detonate));
    }

    /// <summary>
    /// Sub <c>0x07</c> refuses to be written whole. The prefix is the proven part and stops at the
    /// projectile id; the rest of the record is [U] and stays that way until its fields are named.
    /// </summary>
    [Fact]
    public void TheContactReportShipsOnlyItsProvenPrefix()
    {
        Assert.True(RemoteWeaponPackets.ContactReportNotYetDerived);
        Assert.Equal(
            Hex("82 00000000 15 07 14 07000000"),
            Hex(RemoteWeaponPackets.ContactReportPrefix(Owner, projectileId: 7)));
    }

    // ------------------------------------------------------------------ the invariants

    /// <summary>
    /// <b>The self-echo invariant, and the reason this whole family exists as a separate file.</b>
    /// An <c>82 15</c> whose owner id is the receiving client's own transient id is discarded by
    /// <c>FUN_140b07010</c> case <c>0x15</c> before any weapon code runs. Both ids that can mean
    /// "me" — 1, which the self record will carry, and 0, which it carries today — are refused at
    /// the writer instead of producing a packet that does nothing.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(TransientIdTable.LocalPlayer)]
    public void APacketAddressedToTheViewersOwnActorIsRefused(uint ownerTransientId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteWeaponPackets.Reset(ownerTransientId, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteWeaponPackets.Chamber(ownerTransientId, ItemGuid));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteWeaponPackets.AddWeapon(ownerTransientId, ItemGuid, Ar15()));
    }

    /// <summary>
    /// The <c>weaponItemInstanceId</c> must be the equipment-row guid the viewer already has. 0 is
    /// the client's "no item instance id" sentinel, and an <c>UpdateBase</c> for an id no
    /// <c>AddWeapon</c> registered is logged and dropped.
    /// </summary>
    [Fact]
    public void AWeaponWithNoEquipmentRowGuidIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteWeaponPackets.Chamber(Owner, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteWeaponPackets.AddWeapon(Owner, 0, Ar15()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteWeaponPackets.Reset(Owner, [new RemoteWeaponEntry(0, Ar15())]));
    }

    /// <summary>
    /// <c>gameTime</c> is 0 in every packet this file writes, and is not a parameter. A non-zero
    /// value queues the packet at <c>world+0x35238</c> until the proxied entity's interpolated clock
    /// reaches it (S5c §3.1), so a server timestamp there would stall the arsenal indefinitely.
    /// </summary>
    [Fact]
    public void EveryPacketAppliesImmediately()
    {
        byte[][] all =
        [
            RemoteWeaponPackets.Reset(Owner, [new RemoteWeaponEntry(ItemGuid, Ar15())]),
            RemoteWeaponPackets.AddWeapon(Owner, ItemGuid, Ar15()),
            RemoteWeaponPackets.RemoveWeapon(Owner, ItemGuid),
            RemoteWeaponPackets.Chamber(Owner, ItemGuid),
            RemoteWeaponPackets.SwitchFireMode(Owner, ItemGuid, 0, 0),
            RemoteWeaponPackets.FireState(Owner, ItemGuid, true, Vector4.Zero),
            RemoteWeaponPackets.ProjectileDetonateHint(Owner, 1, Vector3.Zero),
        ];

        foreach (byte[] wire in all)
        {
            Assert.Equal(0x82, wire[0]);
            Assert.Equal(0u, BitConverter.ToUInt32(wire, 1));
            Assert.Equal(0x15, wire[5]);
        }
    }

    /// <summary>
    /// The owner id is a client varint, so a crowded match (transient ids past 63) widens the header
    /// by a byte and every length helper has to follow it.
    /// </summary>
    [Fact]
    public void TheOwnerVarintWidensTheHeader()
    {
        Assert.Equal(8, RemoteWeaponPackets.HeaderLength(5));
        Assert.Equal(9, RemoteWeaponPackets.HeaderLength(64));
        Assert.Equal(
            RemoteWeaponPackets.HeaderLength(64) + 8,
            RemoteWeaponPackets.RemoveWeapon(64, ItemGuid).Length);
    }
}
