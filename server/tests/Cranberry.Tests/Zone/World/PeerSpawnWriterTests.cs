using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.World;

/// <summary>
/// The peer wire: <c>d5 AddLightweightPc</c>, the <c>0x78</c> pose relay, <c>0f 45</c>'s request and
/// <c>0f 01</c>'s despawn. docs/100 is the derivation.
///
/// <para>
/// <b>What these prove.</b> That the writer agrees with the read order recovered from
/// <c>FUN_140a2d880</c> and <c>FUN_140a600b0</c>, and — for <c>d5</c> especially — that the length
/// helper agrees with the writer, because the dispatcher applies the record only when the buffer is
/// consumed <em>exactly</em>. They prove nothing about the client: no second player has connected.
/// </para>
/// </summary>
public sealed class PeerSpawnWriterTests
{
    private const ulong PeerGuid = 0x0102_0304_0506_0708;

    private static PeerCharacterRecord Peer(uint transientId = 16) => new()
    {
        Guid = PeerGuid,
        TransientId = transientId,
        ModelId = 9240,
        Position = new Vector3(1f, 2f, 3f),
        Rotation = new Vector4(0f, 0f, 0f, 1f),
    };

    // ------------------------------------------------------------------ d5 AddLightweightPc

    /// <summary>
    /// The whole record, field by field, in <c>FUN_140a2d880</c>'s read order. If this ever needs
    /// editing, re-read the dump first: a record one byte off is dropped in silence by
    /// <c>FUN_140af3950</c> case <c>0xd5</c>, with no client-side diagnostic at all.
    /// </summary>
    [Fact]
    public void TheSpawnRecordIsExactlyTheRecoveredReadOrder()
    {
        byte[] wire = PeerSpawnWriter.AddLightweightPc(Peer(transientId: 16));

        byte[] expected =
        [
            0xd5,                                               // rec+0x08 opcode
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,     // rec+0x10 character guid
            0x40,                                               // rec+0x18 varint 16 = 16 << 2

            0x00, 0x00, 0x00, 0x00,                             // rec+0x128 identity value0
            0x00, 0x00, 0x00, 0x00,                             // rec+0x12c identity value1
            0x00, 0x00, 0x00, 0x00,                             // rec+0x130 identity value2
            0x00, 0x00, 0x00, 0x00,                             // rec+0x20  identity name (empty)
            0x00, 0x00, 0x00, 0x00,                             // rec+0x50  identity text1
            0x00, 0x00, 0x00, 0x00,                             // rec+0xc0  identity text2
            0x00, 0x00, 0x00, 0x00,                             // rec+0xf0  identity text3
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // rec+0x120 identity value3

            0x00,                                               // rec+0x138 i8 [U]
            0x18, 0x24, 0x00, 0x00,                             // rec+0x13c model id 9240
            0x00, 0x00, 0x00, 0x00,                             // rec+0x140 [U] -> vtable[0x110]

            0x00, 0x00, 0x80, 0x3f,                             // rec+0x144 position.x = 1
            0x00, 0x00, 0x00, 0x40,                             //           position.y = 2
            0x00, 0x00, 0x40, 0x40,                             // rec+0x14c position.z = 3; no wire W

            0x00, 0x00, 0x00, 0x00,                             // rec+0x150 rotation.x
            0x00, 0x00, 0x00, 0x00,                             //           rotation.y
            0x00, 0x00, 0x00, 0x00,                             //           rotation.z
            0x00, 0x00, 0x80, 0x3f,                             //           rotation.w = 1

            0x00, 0x00, 0x00, 0x00,                             // rec+0x160 [U]
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // rec+0x168 mount guid
            0x00, 0x00, 0x00, 0x00,                             // rec+0x170 mount seat [I]
            0x00, 0x00, 0x00, 0x00,                             // rec+0x174 [U]
            0x00,                                               // rec+0x178 nameplate byte [I]
            0x00, 0x00, 0x00, 0x00,                             // rec+0x17c [U]
            0x00, 0x00, 0x00, 0x00,                             // rec+0x180 [U]
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // rec+0x188 [U]
            0x00, 0x00, 0x00, 0x00,                             // rec+0x190 [U]
            0x00,                                               // rec+0x194 spawn flags
        ];

        Assert.Equal(expected, wire);
    }

    [Fact]
    public void TheMinimalRecordIsOneHundredAndTwentyFiveBytes()
    {
        byte[] wire = PeerSpawnWriter.AddLightweightPc(Peer());

        Assert.Equal(PeerCharacterRecord.MinimalLength, wire.Length);
        Assert.Equal(125, wire.Length);
    }

    /// <summary>
    /// FUN_140a2d880 reads three floats into +0x144, then four into +0x150. The handler's
    /// synthesized position W is not a wire field: including it shifts rotation and leaves four
    /// trailing bytes, which causes FUN_140af3950's exact-length gate to reject the entire spawn.
    /// Offsets here are counted from that native read sequence, independently of the writer.
    /// </summary>
    [Fact]
    public void ThreePositionFloatsAreImmediatelyFollowedByRotationAndTheNativeTail()
    {
        byte[] wire = PeerSpawnWriter.AddLightweightPc(Peer() with
        {
            Rotation = new Vector4(0.125f, 0.25f, 0.5f, 0.75f),
            Field160 = 0x1234_5678,
            SpawnFlags = 0xa5,
        });

        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(55)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(59)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(63)));
        Assert.Equal(0.125f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(67)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(71)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(75)));
        Assert.Equal(0.75f, BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(79)));
        Assert.Equal(0x1234_5678u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(83)));
        Assert.Equal(0xa5, wire[124]);
        Assert.Equal(125, wire.Length);
    }

    /// <summary>
    /// <c>d5</c> is exact-length gated, so the length helper is a contract. Both variable parts —
    /// the transient varint and the four identity strings — have to move it.
    /// </summary>
    [Theory]
    [InlineData(16u)]      // one varint byte
    [InlineData(64u)]      // two
    [InlineData(16_384u)]  // three
    [InlineData(4_194_304u)] // four
    public void TheLengthHelperAgreesWithTheWriterAtEveryVarintWidth(uint transientId)
    {
        PeerCharacterRecord peer = Peer(transientId) with
        {
            Identity = new SelfIdentity { Name = "Cranberry", Text2 = "Z2" },
        };

        Assert.Equal(
            PeerSpawnWriter.AddLightweightPcLength(peer),
            PeerSpawnWriter.AddLightweightPc(peer).Length);
    }

    /// <summary>
    /// The identity sub-record is the self record's, through the same client reader
    /// <c>FUN_140a40000</c>. Writing it here with <c>SelfIdentity</c> rather than a private copy is
    /// the point: if the self record's block ever changes, the peer's changes with it.
    /// </summary>
    [Fact]
    public void TheIdentityBlockIsTheSelfRecordsOwn()
    {
        var identity = new SelfIdentity
        {
            Value0 = 1,
            Value1 = 2,
            Value2 = 3,
            Name = "Ab",
            Value3 = 0x1122_3344_5566_7788,
        };

        byte[] wire = PeerSpawnWriter.AddLightweightPc(Peer() with { Identity = identity });

        // The block starts after the opcode, the guid and the one-byte transient varint.
        ReadOnlySpan<byte> block = wire.AsSpan(10);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(block));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(block[4..]));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(block[8..]));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(block[12..]));   // "Ab" length
        Assert.Equal((byte)'A', block[16]);
        Assert.Equal((byte)'b', block[17]);

        // And the same eight fields, written by SelfRecordCodec, are byte-identical.
        using var selfWriter = new PacketWriter(64);
        selfWriter.WriteUInt32(identity.Value0);
        selfWriter.WriteUInt32(identity.Value1);
        selfWriter.WriteUInt32(identity.Value2);
        selfWriter.WriteString(identity.Name);
        selfWriter.WriteString(identity.Text1);
        selfWriter.WriteString(identity.Text2);
        selfWriter.WriteString(identity.Text3);
        selfWriter.WriteUInt64(identity.Value3);
        Assert.Equal(selfWriter.Written.ToArray(), wire.AsSpan(10, selfWriter.Position).ToArray());
    }

    /// <summary>
    /// The spawn-flag byte is the record's last byte, whatever the strings did to the length. Every
    /// bit in it has a proven effect (docs/100 §2c), so it is the one tail field a caller may set
    /// with confidence.
    /// </summary>
    [Fact]
    public void TheSpawnFlagsAreTheFinalByte()
    {
        PeerCharacterRecord peer = Peer() with
        {
            Identity = new SelfIdentity { Name = "a long enough name to move the tail" },
            SpawnFlags = (byte)(PeerSpawnFlags.Bit04 | PeerSpawnFlags.Bit40),
        };

        byte[] wire = PeerSpawnWriter.AddLightweightPc(peer);

        Assert.Equal(0x44, wire[^1]);
    }

    // ------------------------------------------------------------------ d9 LightweightToFullPc

    [Fact]
    public void TheFullPcPromotionCarriesTheActorsPositionAndRotation()
    {
        Assert.False(PeerSpawnWriter.FullPcNotYetDerived);
        var peer = Peer();
        byte[] packet = PeerSpawnWriter.LightweightToFullPc(peer);
        Assert.Equal(0xd9, packet[0]);
        int blobLength = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(2));
        var pose = ClientMovementUpdate.Parse(packet.AsSpan(6 + blobLength, packet.Length - 6 - blobLength - 18));
        Assert.InRange(System.Numerics.Vector3.Distance(peer.Position, pose.Position!.Value), 0, 0.02f);
        Assert.Equal(peer.Field178, pose.State);
        Assert.Equal(0x0f, packet[^18]);
        Assert.Equal(0x40, packet[^17]);
        Assert.Equal(peer.Guid, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(packet.Length - 16)));
    }

    [Fact]
    public void TheFullPcPrefixIsTheProvenSixBytes()
    {
        byte[] prefix = PeerSpawnWriter.LightweightToFullPcPrefix(blobLength: 144);

        Assert.Equal(new byte[] { 0xd9, 0x00, 0x90, 0x00, 0x00, 0x00 }, prefix);
        Assert.Equal(0x01, PeerSpawnWriter.LightweightToFullPcPrefix(0, compressed: true)[1]);
    }

    // ------------------------------------------------------------------ 0x78 relay

    /// <summary>
    /// The channel-2 body: a varint and the client's own record, copied. Never a re-encode — the
    /// pose is quantised at two decimal places, so a decode/re-encode round trip moves a peer for no
    /// reason and loses any field this server does not yet parse.
    /// </summary>
    [Fact]
    public void TheRelayBodyIsAVarintAndTheRecordVerbatim()
    {
        byte[] record = [0x02, 0x00, 0x11, 0x22, 0x33, 0x44, 0x00];

        byte[] body = PeerSpawnWriter.RelayBody(16, record);

        Assert.Equal(0x40, body[0]);                    // varint 16
        Assert.Equal(record, body[1..]);
        Assert.Equal(PeerSpawnWriter.RelayBodyLength(16, record.Length), body.Length);
    }

    /// <summary>
    /// The opcode-framed form is the same body with <c>0x78</c> in front — the framing the client
    /// reads through <c>FUN_140a600b0</c> when the packet arrives with an opcode instead of being
    /// implied by the channel.
    /// </summary>
    [Fact]
    public void TheOpcodeFramedRelayIsTheSameBodyBehindSeventyEight()
    {
        byte[] record = [0x02, 0x00, 0x11, 0x22, 0x33, 0x44, 0x00];

        byte[] framed = PeerSpawnWriter.PlayerUpdatePosition(16, record);
        byte[] bare = PeerSpawnWriter.RelayBody(16, record);

        Assert.Equal(ZoneOpcodes.PlayerUpdatePosition, framed[0]);
        Assert.Equal(bare, framed[1..]);
        Assert.Equal(PeerSpawnWriter.PlayerUpdatePositionLength(16, record.Length), framed.Length);
    }

    /// <summary>
    /// Relaying a peer's pose under the viewer's own transient id would move the viewer, not the
    /// peer. Both ids that mean "me" are refused.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(TransientIdTable.LocalPlayer)]
    public void APoseCannotBeRelayedOntoTheViewerItself(uint transientId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PeerSpawnWriter.RelayBody(transientId, [0x00]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PeerSpawnWriter.PlayerUpdatePosition(transientId, [0x00]));
    }

    // ------------------------------------------------------------------ 0f 45 and 0f 01

    /// <summary>
    /// <c>0f 45</c> names the character by its <b>guid</b>, not by the transient id: the family head
    /// is <c>0f | u8 sub | u64 guid</c> and the client resolves it through the guid-keyed entity map.
    /// </summary>
    [Fact]
    public void TheFullCharacterDataRequestCarriesAGuid()
    {
        byte[] request = [0x0f, 0x45, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01];

        Assert.True(PeerSpawnWriter.TryParseFullCharacterDataRequest(request, out ulong guid, out int trailing));
        Assert.Equal(PeerGuid, guid);
        Assert.Equal(0, trailing);
    }

    /// <summary>
    /// Whether the <c>0x45</c> class appends a body after the guid is [U] — the client has no
    /// inbound handler for it and no serialiser is in any dump — so a longer body is reported rather
    /// than assumed empty.
    /// </summary>
    [Fact]
    public void ALongerRequestIsAcceptedButItsTailIsReported()
    {
        byte[] request = [0x0f, 0x45, 1, 2, 3, 4, 5, 6, 7, 8, 0xaa, 0xbb];

        Assert.True(PeerSpawnWriter.TryParseFullCharacterDataRequest(request, out _, out int trailing));
        Assert.Equal(2, trailing);
    }

    [Theory]
    [InlineData(new byte[] { 0x0f, 0x45, 1, 2, 3 })]                    // truncated
    [InlineData(new byte[] { 0x0f, 0x44, 1, 2, 3, 4, 5, 6, 7, 8 })]     // a different sub
    [InlineData(new byte[] { 0x82, 0x45, 1, 2, 3, 4, 5, 6, 7, 8 })]     // a different family
    public void AnythingElseIsNotAFullCharacterDataRequest(byte[] body)
    {
        Assert.False(PeerSpawnWriter.TryParseFullCharacterDataRequest(body, out _, out _));
    }

    /// <summary>
    /// Leaving interest is the same twelve-byte <c>0f 01</c> that evicts a loot pile, with
    /// <c>effectFlag = 0</c> so there is no death presentation.
    /// </summary>
    [Fact]
    public void TheDespawnIsTheProvenRemovePlayer()
    {
        byte[] wire = PeerSpawnWriter.Despawn(PeerGuid);

        Assert.Equal(RemovePlayer.Length, wire.Length);
        Assert.Equal(
            new byte[] { 0x0f, 0x01, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, 0x00, 0x00 },
            wire);
    }
}
