using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// <c>Command.InteractionString 09 2d</c> — docs/47 §4c–§4e. The client asks for the <c>[F]</c>
/// prompt's TEXT once a second while a target is bound, and Cranberry has never answered, which is
/// why the label has been blank for doors and for ground loot alike.
/// <para>
/// The request bytes are <b>[P-live]</b>, taken verbatim from
/// <c>logs/host-20260829-220829.log:793</c>. The reply's field roles are <b>[P-bin]</b> from
/// <c>FUN_14129e7c0</c>; the order of its two trailing <c>u32</c>s is <b>[lead]</b> and is the one
/// order pinned below. <b>Nothing here is
/// LIVE-VERIFIED.</b>
/// </para>
/// </summary>
public sealed class InteractionStringPacketTests
{
    /// <summary>
    /// The exact 19 bytes the August client sent three times in the 22:25 session:
    /// <c>092D0010000000000000200000000000000000</c>.
    /// </summary>
    private static readonly byte[] LiveRequest =
    [
        0x09, 0x2D, 0x00,
        0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private static byte[] Write(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void TheLiveRequestParsesToItsGroundLootGuid()
    {
        Assert.Equal(InteractionStringRequest.Length, LiveRequest.Length);
        Assert.True(InteractionStringRequest.Matches(LiveRequest));

        InteractionStringRequest request = InteractionStringRequest.Parse(LiveRequest);

        Assert.Equal(0x2000_0000_0000_0010UL, request.TargetGuid);
        Assert.Equal(0u, request.FirstWord);
        Assert.Equal(0u, request.SecondWord);
    }

    /// <summary>
    /// The dispatcher guard must not fire on the neighbouring Command subs the loot and door paths
    /// already own — <c>09 07</c>, <c>09 15</c>, <c>09 08</c> — nor on a truncated packet.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x09, 0x07, 0x00 })]
    [InlineData(new byte[] { 0x09, 0x15, 0x00 })]
    [InlineData(new byte[] { 0x09, 0x08, 0x00 })]
    [InlineData(new byte[] { 0x09, 0x2D, 0x00, 0x01 })]
    [InlineData(new byte[] { 0x0F, 0x2D, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void MatchesRejectsEverythingThatIsNotThisPacket(byte[] payload)
    {
        Assert.False(InteractionStringRequest.Matches(payload));
        Assert.False(InteractionStringRequest.TryParse(payload, out InteractionStringRequest? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void ParsingSomethingElseThrows()
    {
        byte[] wrong = [0x09, 0x07, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<PacketFormatException>(() => InteractionStringRequest.Parse(wrong));
    }

    /// <summary>
    /// The reply, byte by byte from the derivation rather than round-tripped through Cranberry's own
    /// reader: <c>09 2d 00 | u64 guid | u32 stringId | u32 entryCount</c>, 19 bytes.
    /// </summary>
    [Fact]
    public void TheMinimalReplyIsNineteenBytesEchoingTheRequestsGuid()
    {
        const ulong guid = 0x4400_0000_0000_0001UL;

        byte[] bytes = Write(
            InteractionStringReply.For(guid, InteractionTargetKind.ClosedDoor).WriteTo);

        Assert.Equal(InteractionStringReply.MinimalLength, bytes.Length);
        Assert.Equal(19, bytes.Length);
        Assert.Equal(0x09, bytes[0]);
        Assert.Equal(0x2D, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(guid, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(3)));
        Assert.Equal(InteractionPromptStrings.Open, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)));
    }

    /// <summary>
    /// docs/47 §7 q1 is settled and the alternative order was deleted with its switch in lane 0D:
    /// the string id is the FIRST of the two trailing words and the entry count the second, matching
    /// the client object's field offsets (<c>+0x28</c> before <c>+0x40</c>) and a SOE serialiser's
    /// declaration order. This pins that there is now exactly one order on the wire.
    /// </summary>
    [Fact]
    public void TheStringIdIsTheFirstOfTheTwoTrailingWords()
    {
        const ulong guid = 0x4400_0000_0000_002AUL;

        byte[] bytes = Write(InteractionStringReply
            .For(guid, InteractionTargetKind.GroundLoot)
            .WriteTo);

        Assert.Equal(InteractionStringReply.MinimalLength, bytes.Length);
        Assert.Equal(
            InteractionPromptStrings.PickUpTarget,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)));
    }

    /// <summary>
    /// An unrecognised guid still gets an answer. <c>FUN_14140c480</c> is called either way, and an
    /// answered request is one the client stops re-asking every second for the rest of the match.
    /// </summary>
    [Fact]
    public void AnUnknownTargetIsAnsweredWithStringIdZero()
    {
        InteractionStringReply reply =
            InteractionStringReply.For(0xDEAD_BEEFUL, InteractionTargetKind.Unknown);

        Assert.Equal(InteractionPromptStrings.None, reply.StringId);
        Assert.Equal(0, reply.EntryCount);
        Assert.Equal(19, Write(reply.WriteTo).Length);
    }

    /// <summary>
    /// The string ids recovered from the client's own locale (docs/47 §4e). A door's
    /// <c>NAME_ID</c> is 0, so a door must never be given a template carrying <c>[*target*]</c>:
    /// 12416 and 8922 are the only two that qualify.
    /// </summary>
    [Theory]
    [InlineData(InteractionTargetKind.ClosedDoor, 12416u)]
    [InlineData(InteractionTargetKind.OpenDoor, 8922u)]
    [InlineData(InteractionTargetKind.Gate, 1004u)]
    [InlineData(InteractionTargetKind.GroundLoot, 13338u)]
    [InlineData(InteractionTargetKind.Vehicle, 8882u)]
    [InlineData(InteractionTargetKind.Unknown, 0u)]
    public void EachTargetKindUsesItsRecoveredLocaleStringId(InteractionTargetKind kind, uint stringId)
    {
        Assert.Equal(stringId, InteractionPromptStrings.For(kind));
    }

    /// <summary>A door's prompt flips with its state, which is what makes the label read "Close Door".</summary>
    [Fact]
    public void ADoorsPromptFollowsItsOpenBit()
    {
        var dataset = new Lazy<Z2Doors>(Z2Doors.LoadDefault);
        var match = new MatchDoors(dataset.Value);
        DoorInstance door = match.Register(0);

        Assert.Equal(InteractionPromptStrings.Open, InteractionStringReply.ForDoor(door).StringId);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out DoorInstance? _));
        Assert.True(door.IsOpen);

        InteractionStringReply opened = InteractionStringReply.ForDoor(door);
        Assert.Equal(InteractionPromptStrings.CloseDoor, opened.StringId);
        Assert.Equal(door.WorldGuid, opened.TargetGuid);
    }

    /// <summary>
    /// The 32-byte interaction-list entry Cranberry does not need but has to be able to write, since
    /// it is the same array the radial wheel uses. The client skips an entry with an empty name or a
    /// non-positive range, so writing one is a silent no-op that still costs MTU: refuse instead.
    /// </summary>
    [Fact]
    public void AnInteractionListEntryIsWrittenAsStringThenRangeThenStringId()
    {
        var reply = new InteractionStringReply(
            0x4400_0000_0000_0001UL,
            InteractionPromptStrings.Open,
            [new InteractionStringEntry("Handle", 3.0f, InteractionPromptStrings.Open)]);

        byte[] bytes = Write(reply.WriteTo);

        Assert.Equal(1, reply.EntryCount);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(19)));   // "Handle".Length
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(29)));
        Assert.Equal(
            InteractionPromptStrings.Open,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(33)));
        Assert.Equal(37, bytes.Length);

        Assert.Throws<InvalidOperationException>(() => Write(new InteractionStringEntry(string.Empty, 3f, 1u).WriteTo));
        Assert.Throws<InvalidOperationException>(() => Write(new InteractionStringEntry("Handle", 0f, 1u).WriteTo));
    }

    /// <summary>
    /// The vehicle and loot paths share this reader, so the guid it hands back must be usable to
    /// resolve against a <see cref="MatchDoors"/> without a second parse.
    /// </summary>
    [Fact]
    public void ARequestNamingASpawnedDoorResolvesToThatDoor()
    {
        var dataset = new Lazy<Z2Doors>(Z2Doors.LoadDefault);
        var match = new MatchDoors(dataset.Value);
        var spawned = new List<DoorInstance>();
        match.RegisterNear(new Vector3(-111.91f, 33.40f, 258.82f), 60f, 48, spawned);

        Assert.NotEmpty(spawned);
        DoorInstance door = spawned[0];

        using var writer = new PacketWriter();
        writer.WriteByte(0x09);
        writer.WriteUInt16(InteractionStringRequest.SubOpcode);
        writer.WriteUInt64(door.WorldGuid);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);

        Assert.True(InteractionStringRequest.TryParse(writer.Written, out InteractionStringRequest? request));
        Assert.True(match.TryGet(request!.TargetGuid, out DoorInstance? resolved));
        Assert.Same(door, resolved);
    }
}
