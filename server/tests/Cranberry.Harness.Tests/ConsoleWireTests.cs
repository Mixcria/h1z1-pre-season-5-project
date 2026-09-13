using Cranberry.Harness.Protocol;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The harness's half of the developer-console wire: the packet it types as the client, and the
/// three surfaces it reads a reply off. Byte expectations are literals, not calls into the server's
/// writers, so a scenario that goes green is not going green against the same mistake twice.
/// </summary>
public sealed class ConsoleWireTests
{
    [Fact]
    public void ExecuteCommand_is_the_tunnel_header_then_09_42_hash_and_a_counted_string()
    {
        // 06 (tunnel to server, channel 0) | 09 42 00 | u32 hash | u32 len | utf8 — what the client
        // sends for "/m 3" (hash of "m" = 0x5a1fb42a).
        Assert.Equal(
            Convert.FromHexString("06" + "094200" + "2AB41F5A" + "01000000" + "33"),
            ZoneClientMessages.ExecuteCommand("m", "3"));

        Assert.Equal(ZoneClientMessages.ExecuteCommand(0x5a1fb42au, "3"), ZoneClientMessages.ExecuteCommand("m", "3"));
    }

    [Fact]
    public void ExecuteCommand_of_an_unknown_name_is_the_HELP_hash_with_no_arguments()
    {
        // The bytes the owner's own 1087 server logged for an unregistered /name
        // (out\devconsole-20260901\part1.md:177).
        Assert.Equal(
            Convert.FromHexString("06" + "094200" + "69DB1BD5" + "00000000"),
            ZoneClientMessages.ExecuteCommand(0xd51bdb69u));
    }

    [Fact]
    public void Spectate_is_the_noise_the_console_toggle_makes()
    {
        Assert.Equal(
            Convert.FromHexString("06" + "091005" + "0E000000" + "4F6273657276657243616D657261"),
            ZoneClientMessages.SpectateObserverCamera());
    }

    [Fact]
    public void Console_lines_are_read_off_all_three_surfaces()
    {
        Assert.Equal(
            new ConsoleLine("print", "hi"),
            ServerPackets.TryReadConsoleLine(Convert.FromHexString("060300" + "02000000" + "6869" + "00" + "00000000")));

        Assert.Equal(
            new ConsoleLine("chat1", "hi"),
            ServerPackets.TryReadConsoleLine(
                Convert.FromHexString("060500" + "02000000" + "6869" + "00000000" + "FFFFFF00" + "00000000" + "00" + "01")));

        // Same packet, trailing byte 0: a chat line the console pane never prints
        // (dispatcher FUN_1412568e0:775-777).
        Assert.Equal(
            new ConsoleLine("chat0", "hi"),
            ServerPackets.TryReadConsoleLine(
                Convert.FromHexString("060500" + "02000000" + "6869" + "00000000" + "FFFFFF00" + "00000000" + "00" + "00")));

        Assert.Equal(
            new ConsoleLine("alert", "MENU"),
            ServerPackets.TryReadConsoleLine(Convert.FromHexString("113100" + "04000000" + "4D454E55")));
    }

    [Fact]
    public void A_packet_that_is_not_a_console_line_decodes_to_null()
    {
        Assert.Null(ServerPackets.TryReadConsoleLine(Convert.FromHexString("094000" + "01000000" + "6D")));
        Assert.Null(ServerPackets.TryReadConsoleLine(Convert.FromHexString("0603")));
        Assert.Null(ServerPackets.TryReadConsoleLine(Convert.FromHexString("060300" + "FF000000" + "6869")));
    }

    [Fact]
    public void The_console_packets_are_named_in_a_failure_report()
    {
        Assert.Equal(
            "ch0 CommandBase::Command.AddWorldCommand",
            ObservedPacket.ParseGateway(Convert.FromHexString("05" + "094000" + "01000000" + "6D")).Name);

        Assert.Equal(
            "ch0 ChatBase::ConsolePrint",
            ObservedPacket.ParseGateway(
                Convert.FromHexString("05" + "060300" + "02000000" + "6869" + "00" + "00000000")).Name);

        Assert.Equal(
            "ch0 CommandBase::Command.ExecuteCommand",
            ObservedPacket.ParseGateway(ZoneClientMessages.ExecuteCommand("m", "3")).Name);
    }
}
