using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// <c>WeaponBase.AddFireGroup</c> (<c>82 11</c>) - docs/56 §1.6.
/// <para>
/// <b>What these tests can and cannot prove.</b> They prove the bytes match the layout recovered by
/// decompiling <c>FUN_1414859b0</c> → <c>FUN_141484300</c> → <c>FUN_141483ec0</c>. They prove
/// nothing about the client, because <b>this packet has never been sent</b> - the layout is DERIVED,
/// not LIVE-VERIFIED, and docs/56 §1.9 deliberately ships it with no call site.
/// </para>
/// </summary>
public sealed class FireGroupPacketTests
{
    private const ulong FistsGuid = 0x1122_3344_5566_7788;

    private static byte[] Bytes(AddFireGroup packet)
    {
        using var writer = new PacketWriter();
        packet.WriteTo(writer);
        return writer.Written.ToArray();
    }

    private static FireGroupDefinition Group(int modes, int charge = 1) =>
        new(12, [.. Enumerable.Range(0, modes).Select(_ => new FireModeDefinition(0, 0, charge))]);

    // ------------------------------------------------------------------ the wire

    /// <summary>
    /// The whole packet, byte for byte, for the fists group docs/56 §1.9 step 2 would send. If this
    /// test is ever edited, the Ghidra citation in <c>FireGroupPackets.cs</c> must be re-checked
    /// first - a wrong length field here is a client-side buffer overrun, not a rejected packet.
    /// </summary>
    [Fact]
    public void TheUnarmedPacketIsExactlyTheRecoveredLayout()
    {
        byte[] wire = Bytes(AddFireGroup.ForUnarmed(FistsGuid));

        byte[] expected =
            [
                0x82,                                               // u8 opcode
                0x00, 0x00, 0x00, 0x00,                             // u32, read by FUN_140a2e9b0 and never used
                0x11,                                               // u8 sub
                0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,     // u64 itemGuid -> rec + 0x20
                0x00,                                               // u8 flag      -> rec + 0x28
                0x1f, 0x00, 0x00, 0x00,                             // i32 n = 5 + 13 * 2 = 31 bytes
                0x0c, 0x00, 0x00, 0x00,                             // u32 fireGroupId = 12
                0x02,                                               // i8 fireModeCount = 2
                0x00,                                               // mode 0: u8  -> mode + 0x10
                0x00, 0x00, 0x00, 0x00,                             //         u32 -> mode + 0x14
                0x01, 0x00, 0x00, 0x00,                             //         i32 a -> mode + 0x18 (positive)
                0x00, 0x00, 0x00, 0x00,                             //         i32 b (0, so a stands)
                0x00,                                               // mode 1: u8
                0x00, 0x00, 0x00, 0x00,                             //         u32
                0x01, 0x00, 0x00, 0x00,                             //         i32 a
                0x00, 0x00, 0x00, 0x00,                             //         i32 b
            ];

        Assert.Equal(expected, wire);
    }

    [Fact]
    public void TheDeclaredLengthIsWhatIsActuallyWritten()
    {
        for (int modes = 0; modes <= 4; modes++)
        {
            var packet = new AddFireGroup(FistsGuid, Group(modes));
            Assert.Equal(packet.Length, Bytes(packet).Length);
        }
    }

    /// <summary>
    /// <c>n</c> counts <b>bytes</b>, not modes (docs/45 §3b: the cursor advances by <c>n</c>). Getting
    /// this wrong is the difference between a fire group and a desynchronised read cursor.
    /// </summary>
    [Fact]
    public void TheCountFieldIsAByteCountAndCoversExactlyThePayload()
    {
        for (int modes = 0; modes <= 4; modes++)
        {
            byte[] wire = Bytes(new AddFireGroup(FistsGuid, Group(modes)));
            int declared = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(AddFireGroup.EnvelopeLength - 4));

            Assert.Equal(5 + (13 * modes), declared);
            Assert.Equal(wire.Length - AddFireGroup.EnvelopeLength, declared);
        }
    }

    [Fact]
    public void APayloadIsFiveBytesPlusThirteenPerMode()
    {
        for (int modes = 0; modes <= 8; modes++)
        {
            Assert.Equal(
                FireGroupDefinition.HeaderLength + (FireModeDefinition.Length * modes),
                Group(modes).PayloadLength);
        }
    }

    [Fact]
    public void TheFlagByteIsCarriedThrough()
    {
        byte[] wire = Bytes(new AddFireGroup(FistsGuid, Group(2), Flag: 0x5a));
        Assert.Equal((byte)0x5a, wire[14]);
    }

    // ------------------------------------------------------- the local trigger gate

    /// <summary>
    /// <c>FUN_142291b90</c> hard-codes fire-mode index 1 and refuses a mode whose <c>+0x18</c> is not
    /// positive, so one mode - or two with no charge - can never swing (docs/56 §1.7).
    /// </summary>
    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, true)]
    [InlineData(3, 1, true)]
    public void OnlyTwoModesWithAPositiveChargeCanAttack(int modes, int charge, bool expected) =>
        Assert.Equal(expected, Group(modes, charge).SupportsLocalAttack);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 5)]
    [InlineData(0, 7, 7)]
    [InlineData(5, 7, 7)]   // FUN_141483ec0 tests b second, so b wins when positive
    [InlineData(5, -1, 5)]  // ... but only when positive
    public void TheChargeFollowsTheClientsTwoTestsInOrder(int a, int b, int expected) =>
        Assert.Equal(expected, new FireModeDefinition(0, 0, a, b).EffectiveCharge);

    /// <summary>
    /// The mode count is a <b>signed</b> byte on the wire. A count the client would read as negative
    /// leaves it with an empty group whose bytes it never consumes - i.e. the docs/45 crash state
    /// plus a desynchronised cursor - so it is refused here rather than serialised.
    /// </summary>
    [Fact]
    public void MoreModesThanASignedByteCanHoldAreRefused()
    {
        FireModeDefinition[] tooMany = [.. Enumerable.Range(0, 128).Select(_ => new FireModeDefinition(0, 0, 1))];
        Assert.Throws<ArgumentOutOfRangeException>(() => new FireGroupDefinition(12, tooMany));

        FireModeDefinition[] theLimit = [.. tooMany.Take(FireGroupDefinition.MaximumModes)];
        Assert.Equal(127, new FireGroupDefinition(12, theLimit).Modes.Count);
    }

    // ------------------------------------------------------------------- the fists

    /// <summary>
    /// The fists' group id is the client's own: <c>ClientItemDatasheetData.txt</c> row 85 is
    /// <c>85^2^12^12^…</c> (<c>WEAPON_ID 12</c>, <c>FIRE_GROUP_ID 12</c>) and
    /// <c>ClientItemDefinitions.txt</c> row 85's <c>PARAM1</c> is 12 as well.
    /// </summary>
    [Fact]
    public void TheUnarmedGroupUsesTheClientsOwnFireGroupId()
    {
        Assert.Equal(12u, UnarmedFireGroup.FireGroupId);
        Assert.Equal(12u, UnarmedFireGroup.Group.FireGroupId);
        Assert.Equal(FistsGuid, AddFireGroup.ForUnarmed(FistsGuid).ItemGuid);
    }

    /// <summary>
    /// The one thing about the fists group that is <b>designed rather than derived</b>: fists carry
    /// <c>CLIP_SIZE 0</c>, so the positive <c>+0x18</c> the trigger gate demands is a server value.
    /// This test exists to make that choice visible if anyone changes it.
    /// </summary>
    [Fact]
    public void TheUnarmedGroupCarriesTheDesignedSentinelOnBothModes()
    {
        Assert.Equal(2, UnarmedFireGroup.Group.Modes.Count);
        Assert.All(UnarmedFireGroup.Group.Modes, mode => Assert.Equal(UnarmedFireGroup.TriggerCharge, mode.EffectiveCharge));
        Assert.True(UnarmedFireGroup.Group.SupportsLocalAttack);
        Assert.Equal(1, UnarmedFireGroup.TriggerCharge);
    }

    /// <summary>
    /// <c>Flags</c> and <c>EffectId</c> are docs/56 open question 2 - which is the fire-mode id and
    /// which the composite-effect id is unknown. They stay 0, the only value that claims nothing.
    /// </summary>
    [Fact]
    public void TheUnarmedGroupGuessesNothingAboutTheTwoUndecodedFields()
    {
        Assert.All(UnarmedFireGroup.Group.Modes, mode =>
        {
            Assert.Equal((byte)0, mode.Flags);
            Assert.Equal(0u, mode.EffectId);
        });
    }
}
