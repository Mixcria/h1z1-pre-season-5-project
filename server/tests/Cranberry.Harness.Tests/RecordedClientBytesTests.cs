using Cranberry.Harness.Protocol;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The builders must produce the bytes the real client produced. Every expectation below is a
/// verbatim <c>c2s</c> line from <c>wire-20260829-184346.txt</c> (docs/71). If a builder drifts,
/// the harness stops imitating the client and every scenario that uses it silently stops meaning
/// anything — so this file is the harness's own regression guard.
/// </summary>
public sealed class RecordedClientBytesTests
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void ClientIsReady_is_two_bytes_on_channel_zero() =>
        Assert.Equal("0604", Hex(ZoneClientMessages.ClientIsReady()));

    [Fact]
    public void ClientFinishedLoading_carries_its_tail_byte()
    {
        Assert.Equal("060200", Hex(ZoneClientMessages.ClientFinishedLoading(0)));
        Assert.Equal("060201", Hex(ZoneClientMessages.ClientFinishedLoading(1)));
    }

    [Fact]
    public void SetLocale_goes_out_on_channel_one() =>
        Assert.Equal("263305000000656E5F5553", Hex(ZoneClientMessages.SetLocale()));

    [Fact]
    public void ClientInitializationDetails_and_the_two_probes_match()
    {
        Assert.Equal("067100000000", Hex(ZoneClientMessages.ClientInitializationDetails()));
        Assert.Equal("2697", Hex(ZoneClientMessages.GetContinentBattleInfo()));
        Assert.Equal("06B1", Hex(ZoneClientMessages.GetRewardBuffInfo()));
    }

    [Fact]
    public void Unregistered_0x57_has_the_recorded_shape()
    {
        byte[] message = ZoneClientMessages.Unregistered0x57(0x25311B42);
        Assert.Equal("0657421B312501000000000000000000000000000000", Hex(message));
        Assert.Equal(22, message.Length);
    }

    [Fact]
    public void LobbyGameDefinition_request_is_the_channel_one_four_byte_form() =>
        Assert.Equal("26410100", Hex(ZoneClientMessages.LobbyGameDefinitionRequest()));

    [Fact]
    public void UpdateBattlEyeRegistration_matches() =>
        Assert.Equal("06115000", Hex(ZoneClientMessages.UpdateBattlEyeRegistration()));

    [Fact]
    public void The_loading_screen_close_is_the_recorded_window_event() =>
        Assert.Equal(
            "269A05130000004C6F6164696E6753637265656E57696E646F7705000000636C6F736500000000",
            Hex(ZoneClientMessages.WindowEvent("LoadingScreenWindow", "close")));

    [Fact]
    public void WallOfData_counter_and_unregistered_F3_match()
    {
        Assert.Equal("269A0A0000000000000000", Hex(ZoneClientMessages.WallOfDataCounter()));
        Assert.Equal("26F30A00", Hex(ZoneClientMessages.Unregistered0xF3()));
    }

    [Fact]
    public void MatchHistory_requests_differ_only_in_their_index_byte()
    {
        Assert.Equal("266706000000000000000000000000000000000100000000000000", Hex(ZoneClientMessages.MatchHistoryRequest(1)));
        Assert.Equal("266706000000000000000000000000000000000200000000000000", Hex(ZoneClientMessages.MatchHistoryRequest(2)));
        Assert.Equal("266706000000000000000000000000000000000300000000000000", Hex(ZoneClientMessages.MatchHistoryRequest(3)));
    }

    [Fact]
    public void StaticView_request_names_the_camera() =>
        Assert.Equal("06E901000B0000006B6F746B64656661756C74", Hex(ZoneClientMessages.StaticViewRequest("kotkdefault")));

    [Fact]
    public void The_five_InGamePurchase_requests_match_the_recorded_burst()
    {
        IReadOnlyList<byte[]> burst = ZoneClientMessages.InGamePurchaseBurst();
        Assert.Equal("26270A00", Hex(burst[0]));
        Assert.Equal("26271A0005000000656E5F5553", Hex(burst[1]));
        Assert.Equal("2627150005000000656E5F5553", Hex(burst[2]));
        Assert.Equal("26270E0003000000555344", Hex(burst[3]));
        Assert.Equal("26270F0003000000555344", Hex(burst[4]));
    }

    [Fact]
    public void PlayerWorldTransferRequest_is_the_nineteen_byte_channel_one_form()
    {
        byte[] message = ZoneClientMessages.PlayerWorldTransferRequest();
        Assert.Equal("26EC0100000000000000000000000101000000", Hex(message));
        Assert.Equal(19, message.Length);
    }

    [Fact]
    public void The_two_transfer_requests_are_byte_identical() =>
        Assert.Equal(
            Hex(ZoneClientMessages.PlayerWorldTransferRequest()),
            Hex(ZoneClientMessages.PlayerWorldTransferRequest()));

    [Fact]
    public void KeepAlive_and_MonitorTimeDrift_match()
    {
        Assert.Equal("063B840D3802", Hex(ZoneClientMessages.KeepAlive(0x02380D84)));
        Assert.Equal("0611440000000000", Hex(ZoneClientMessages.MonitorTimeDrift()));
    }

    [Fact]
    public void Synchronization_is_fifty_bytes_in_the_recorded_shape()
    {
        byte[] message = ZoneClientMessages.Synchronization(0x02380D84, 0x6A931A77);
        Assert.Equal(50, message.Length);
        Assert.Equal(
            "068C840D380200000000840D380200000000771A936A00000000000000000000000000000000000000000000000000000000",
            Hex(message));
    }

    [Fact]
    public void GameTimeSync_is_fifteen_bytes_in_the_recorded_shape()
    {
        byte[] message = ZoneClientMessages.GameTimeSync(0x6A931A74);
        Assert.Equal(15, message.Length);
        Assert.Equal("061D741A936A000000000000000000", Hex(message));
    }

    [Fact]
    public void ClientMetrics_is_the_recorded_one_hundred_and_fifty_byte_block()
    {
        byte[] message = ZoneClientMessages.ClientMetrics();
        Assert.Equal(150, message.Length);
        Assert.StartsWith("064401000000", Hex(message), StringComparison.Ordinal);
    }

    [Fact]
    public void The_teleport_ack_and_the_vehicle_messages_match()
    {
        Assert.Equal("06E80200", Hex(ZoneClientMessages.SynchronizedTeleportAck()));
        Assert.Equal("0688180000000000000000", Hex(ZoneClientMessages.VehicleDismiss()));
        Assert.Equal("068827032000000000000005", Hex(ZoneClientMessages.VehicleCurrentMoveMode(0x2003, 5)));
    }

    [Fact]
    public void The_AutoMount_echo_is_the_servers_packet_with_exactly_two_edits()
    {
        // 05 88 19 03 20 00 00 00 00 00 00 01 00 00 00 00  ->  header 05->06, byte 11 01->00.
        byte[] fromServer = Convert.FromHexString("05881903200000000000000100000000");
        byte[] echo = ZoneClientMessages.VehicleAutoMountEcho(fromServer);

        Assert.Equal("06881903200000000000000000000000", Hex(echo));
        Assert.Equal(fromServer.Length, echo.Length);
        for (int i = 0; i < echo.Length; i++)
        {
            bool edited = i is 0 or 11;
            Assert.Equal(edited, fromServer[i] != echo[i]);
        }
    }

    [Fact]
    public void The_loot_loop_messages_match_their_recorded_lengths()
    {
        Assert.Equal("060F450100000000000020", Hex(ZoneClientMessages.FullCharacterDataRequest(0x2000000000000001)));
        Assert.Equal(11, ZoneClientMessages.FullCharacterDataRequest(1).Length);
        Assert.Equal(20, ZoneClientMessages.PlayerSelect(0x1003, 0x2001).Length);
        Assert.Equal(29, ZoneClientMessages.InteractRequest(0x2001, 1f, 2f, 3f).Length);
        Assert.Equal("06090800", Hex(ZoneClientMessages.InteractCancel()));
        Assert.Equal("06091600", Hex(ZoneClientMessages.FreeInteractionNpc()));
    }

    [Fact]
    public void Logout_messages_match()
    {
        Assert.Equal("06FAC0000000", Hex(ZoneClientMessages.PlayLength(192)));
        Assert.Equal("0607", Hex(ZoneClientMessages.ClientLogout()));
    }

    [Fact]
    public void The_collision_damage_packet_is_forty_five_bytes() =>
        Assert.Equal(45, ZoneClientMessages.CollisionDamage(0x1003, 0x40, 1f, 2f, 3f).Length);

    [Fact]
    public void The_gateway_login_request_matches_the_recorded_seventy_eight_bytes()
    {
        byte[] message = GatewayWire.LoginRequest(
            0x1003, "72D5949AF1C0D4BE8F6D78DD", AugustClient.Protocol, AugustClient.Version);
        Assert.Equal(78, message.Length);
        Assert.Equal(
            "0103100000000000001800000037324435393439414631433044344245384636443738444413000000"
            + "436C69656E7450726F746F636F6C5F313134380E000000302E302E3131382E323038303539",
            Hex(message));
    }

    [Fact]
    public void The_login_context_replayed_from_the_capture_is_the_recorded_ninety_seven_bytes()
    {
        byte[] context = AugustLoginContext.Bytes;
        Assert.Equal(97, context.Length);
        Assert.Equal(
            "05000000656E5F757308000000000000000000000000000000000000000000010000003000000000020000"
            + "000F00000057696E646F777320313020486F6D6503000000362E320E000000302E302E3131382E3230383035"
            + "39040000004C69766501",
            Hex(context));
    }

    [Fact]
    public void The_first_zoning_movement_packet_is_the_recorded_forty_three_byte_one()
    {
        byte[] packet = MovementReplay.FirstZoningPacket();
        Assert.Equal(0x46, packet[0]);
        Assert.Equal(2, packet[0] >> 5);
        Assert.Equal(43, packet.Length);
    }

    [Fact]
    public void Every_client_message_lands_on_the_channel_docs71_assigns_it()
    {
        AssertChannel(0, ZoneClientMessages.ClientIsReady());
        AssertChannel(0, ZoneClientMessages.ClientFinishedLoading());
        AssertChannel(0, ZoneClientMessages.GameTimeSync(0));
        AssertChannel(0, ZoneClientMessages.KeepAlive(0));
        AssertChannel(0, ZoneClientMessages.Synchronization(0, 0));
        AssertChannel(0, ZoneClientMessages.InteractCancel());
        AssertChannel(0, ZoneClientMessages.ClientLogout());
        AssertChannel(0, ZoneClientMessages.PlayLength(1));
        AssertChannel(0, ZoneClientMessages.StaticViewRequest("kotkdefault"));

        AssertChannel(1, ZoneClientMessages.SetLocale());
        AssertChannel(1, ZoneClientMessages.GetContinentBattleInfo());
        AssertChannel(1, ZoneClientMessages.LobbyGameDefinitionRequest());
        AssertChannel(1, ZoneClientMessages.MatchHistoryRequest(1));
        AssertChannel(1, ZoneClientMessages.PlayerWorldTransferRequest());
        AssertChannel(1, ZoneClientMessages.VoiceBase());
        AssertChannel(1, ZoneClientMessages.WindowEvent("HudWindow", "open"));
        foreach (byte[] purchase in ZoneClientMessages.InGamePurchaseBurst())
        {
            AssertChannel(1, purchase);
        }
    }

    private static void AssertChannel(byte expected, byte[] message)
    {
        (byte opcode, byte channel) = GatewayWire.SplitHeader(message[0]);
        Assert.Equal(GatewayWire.OpcodeTunnelToServer, opcode);
        Assert.Equal(expected, channel);
    }
}
