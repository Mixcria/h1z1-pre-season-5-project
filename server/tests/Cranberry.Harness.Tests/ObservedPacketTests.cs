using Cranberry.Harness.Protocol;

namespace Cranberry.Harness.Tests;

public sealed class ObservedPacketTests
{
    [Theory]
    [InlineData(0x06, 6, 0)]
    [InlineData(0x26, 6, 1)]
    [InlineData(0x05, 5, 0)]
    [InlineData(0x46, 6, 2)]
    [InlineData(0x66, 6, 3)]
    [InlineData(0x01, 1, 0)]
    public void The_gateway_header_packs_a_five_bit_opcode_below_a_three_bit_channel(
        byte header, byte opcode, byte channel)
    {
        (byte parsedOpcode, byte parsedChannel) = GatewayWire.SplitHeader(header);
        Assert.Equal(opcode, parsedOpcode);
        Assert.Equal(channel, parsedChannel);
        Assert.Equal(header, GatewayWire.Header(opcode, channel));
    }

    [Fact]
    public void Channels_two_and_three_are_never_decoded_as_opcodes()
    {
        // Byte 1 of this recorded channel-2 packet is 0xFF, which as a base opcode would be
        // nonsense; decoding movement as opcodes manufactures ~4,900 phantom packets in one
        // session (docs/71 §1).
        ObservedPacket player = ObservedPacket.ParseGateway(MovementReplay.FirstZoningPacket());
        Assert.Equal(ObservedKind.MovementStream, player.Kind);
        Assert.Null(player.ZoneOpcode);
        Assert.Null(player.SubOpcodeU8);
        Assert.Null(player.SubOpcodeU16);
        Assert.Equal("ch2 PlayerMovement", player.Name);

        ObservedPacket managed = ObservedPacket.ParseGateway(
            Convert.FromHexString("66900801007EB63802000511"));
        Assert.Equal(ObservedKind.MovementStream, managed.Kind);
        Assert.Equal("ch3 ManagedMovement", managed.Name);
    }

    [Fact]
    public void A_tunnelled_zone_message_is_named_from_the_clients_own_registration_table()
    {
        ObservedPacket packet = ObservedPacket.ParseGateway(Convert.FromHexString("0604"));
        Assert.Equal(ObservedKind.ZoneTunnel, packet.Kind);
        Assert.Equal((byte?)0x04, packet.ZoneOpcode);
        Assert.Equal(0, packet.Channel);
        Assert.Equal("ch0 ClientIsReady", packet.Name);
    }

    [Fact]
    public void Sub_opcode_families_are_named_at_their_confirmed_widths()
    {
        Assert.Equal("ch0 CommandBase::PlayerSelect",
            ObservedPacket.ParseGateway(Convert.FromHexString("0609150003100000000000000120000000000000")).Name);
        Assert.Equal("ch0 CommandBase::InteractCancel",
            ObservedPacket.ParseGateway(Convert.FromHexString("06090800")).Name);
        Assert.Equal("ch0 CharacterBase::FullCharacterDataRequest",
            ObservedPacket.ParseGateway(Convert.FromHexString("060F450100000000000020")).Name);
        Assert.Equal("ch0 VehicleBase::VehicleAutoMount",
            ObservedPacket.ParseGateway(Convert.FromHexString("06881903200000000000000000000000")).Name);
        Assert.Equal("ch0 SynchronizedTeleportBase::ClientAck",
            ObservedPacket.ParseGateway(Convert.FromHexString("06E80200")).Name);
        Assert.Equal("ch1 WallOfDataBase::WindowEvent",
            ObservedPacket.ParseGateway(Convert.FromHexString("269A05130000004C6F6164696E6753637265656E57696E646F7705000000636C6F736500000000")).Name);
    }

    [Fact]
    public void An_unregistered_opcode_is_reported_as_unregistered_rather_than_guessed()
    {
        ObservedPacket packet = ObservedPacket.ParseGateway(
            Convert.FromHexString("0657421B312501000000000000000000000000000000"));
        Assert.Equal("ch0 unregistered 0x57", packet.Name);
        Assert.Equal((byte?)0x57, packet.ZoneOpcode);
    }

    [Fact]
    public void The_two_gateway_control_messages_are_recognised()
    {
        ObservedPacket request = ObservedPacket.ParseGateway(GatewayWire.LoginRequest(1, "t", "p", "v"));
        Assert.Equal(ObservedKind.GatewayControl, request.Kind);
        Assert.Equal("Gateway.LoginRequest", request.Name);

        ObservedPacket reply = ObservedPacket.ParseGateway([0x02, 0x01]);
        Assert.Equal("Gateway.LoginReply", reply.Name);
        Assert.True(GatewayWire.TryParseLoginReply([0x02, 0x01], out bool loggedIn));
        Assert.True(loggedIn);
        Assert.False(GatewayWire.TryParseLoginReply([0x06, 0x04], out _));
    }

    [Fact]
    public void Login_messages_are_named_by_their_own_opcode_table()
    {
        Assert.Equal("ServerListRequest", ObservedPacket.ParseLogin([0x0D]).Name);
        Assert.Equal("CharacterSelectInfoReply", ObservedPacket.ParseLogin([0x0C, 0]).Name);
        Assert.Equal("login 0x77", ObservedPacket.ParseLogin([0x77]).Name);
    }

    [Fact]
    public void An_empty_message_does_not_throw()
    {
        Assert.Equal(ObservedKind.Unknown, ObservedPacket.ParseGateway([]).Kind);
        Assert.Equal(ObservedKind.Login, ObservedPacket.ParseLogin([]).Kind);
    }

    [Fact]
    public void The_movement_replay_cycles_and_clones()
    {
        var replay = new MovementReplay([[1, 2], [3, 4]]);
        byte[] first = replay.Next();
        first[0] = 0xFF;
        replay.Rewind();
        Assert.Equal("0102", Convert.ToHexString(replay.Next()));
        Assert.Equal("0304", Convert.ToHexString(replay.Next()));
        Assert.Equal("0102", Convert.ToHexString(replay.Next()));
    }

    [Fact]
    public void A_movement_replay_needs_at_least_one_recorded_packet() =>
        Assert.Throws<ArgumentException>(() => new MovementReplay([]));

    [Fact]
    public void The_default_replay_pools_are_all_on_their_own_channel()
    {
        MovementReplay player = MovementReplay.PlayerMovement();
        for (int i = 0; i < player.Count; i++)
        {
            Assert.Equal(2, player.Next()[0] >> 5);
        }

        MovementReplay managed = MovementReplay.ManagedMovement();
        for (int i = 0; i < managed.Count; i++)
        {
            Assert.Equal(3, managed.Next()[0] >> 5);
        }
    }

    [Fact]
    public void The_gateway_handoff_parses_out_of_a_character_login_reply()
    {
        // Byte-for-byte the reply recorded at 18:44:13.027 in wire-20260829-184346.
        byte[] reply = Convert.FromHexString(
            "08031000000000000001000000000000000100000071000000A60D000000000F0000003132372E302E302E31"
            + "3A3230303433180000003732443539343941463143304434424538463644373844441000000066A2E4FA43DF"
            + "7A9AA8C47144D4722AF503000000031000000000000000000000000000000000000000000000000000000000"
            + "000000000000");

        CharacterLoginOutcome outcome = LoginWire.ParseCharacterLoginReply(reply);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0x1003UL, outcome.EntityKey);
        Assert.Equal(1UL, outcome.ServerId);

        GatewayHandoff handoff = outcome.Gateway!;
        Assert.Equal("127.0.0.1:20043", handoff.Address);
        Assert.Equal("72D5949AF1C0D4BE8F6D78DD", handoff.Ticket);
        Assert.Equal(16, handoff.Key.Length);
        Assert.True(handoff.UsesRc4);
        Assert.Equal(0x1003UL, handoff.Guid);
    }

    [Fact]
    public void The_roster_reply_parses_out_of_the_recorded_character_select_info()
    {
        byte[] reply = Convert.FromHexString(
            "0C0100000000010000000310000000000000010000000000000000000000000000000100000038000000030000"
            + "004D6978020100000000000000010000000E010000010000000E010000990200000100000000000000000000"
            + "000000000000000000");

        CharacterSelectInfo info = LoginWire.ParseCharacterSelectInfoReply(reply);
        Assert.Equal(1u, info.Status);
        RosterCharacter character = Assert.Single(info.Characters);
        Assert.Equal(0x1003UL, character.EntityKey);
        Assert.Equal(1UL, character.ServerId);
        Assert.Equal(RosterCharacter.StatusAvailable, character.Status);
        Assert.Equal(0x38, character.Payload.Length);
    }
}
