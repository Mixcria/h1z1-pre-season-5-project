using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

// Frozen vectors of the match-flow packets (docs/11-match-flow-map.md).
public sealed class MatchFlowPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void QueueUpdateGameModeIs59Bytes()
    {
        byte[] bytes = Bytes(w => new QueueUpdateGameMode(Position: 1, GameMode: 13).WriteTo(w));
        Assert.Equal(59, bytes.Length);
        Assert.Equal(Convert.FromHexString("A608" + "01000000" + "0D000000" + "0D000000" + "0D000000" + "0D000000" + "01"), bytes[..23]);
        Assert.All(Enumerable.Range(0, 9), i => Assert.Equal(13u, BitConverter.ToUInt32(bytes, 23 + 4 * i)));
    }

    [Fact]
    public void QueueExitAndTransferReplyMatchTheParsers()
    {
        Assert.Equal(Convert.FromHexString("A604" + "0A000000" + "0110000000000000" + "00"), Bytes(w => new QueueExit(10, 0x1001).WriteTo(w)));
        Assert.Equal(Convert.FromHexString("ED" + "00" + "02000000"), Bytes(w => new PlayerWorldTransferReply(2).WriteTo(w)));
    }

    [Fact]
    public void GameModeHudPacketsHaveTheDerivedShapes()
    {
        Assert.Equal(Convert.FromHexString("CE1000" + "0D000000" + "01000000" + "01000000" + "00000000"), Bytes(w => GameModeHud.WriteModeTrio(w)));
        Assert.Equal(Convert.FromHexString("CE0900" + "FFFFFFFF"), Bytes(w => GameModeHud.WritePlayersRemaining(w, -1)));
        Assert.Equal(Convert.FromHexString("CE0F00" + "00000000" + "60EA0000" + "00000000" + "00000000"), Bytes(w => GameModeHud.WriteCountdown(w, 60000)));
        Assert.Equal(
            Convert.FromHexString("CE0F00" + "00000000" + "204E0000" + "2C340000" + "00000000"),
            Bytes(w => GameModeHud.WriteCountdown(w, 20000, GameModeHud.StartingMatchLabelId)));
        Assert.Equal(
            Convert.FromHexString("CE0F00" + "00000000" + "C0D40100" + "49370000" + "00000000"),
            Bytes(w => GameModeHud.WriteCountdown(w, 120000, GameModeHud.RevealingSafeZoneLabelId)));
        Assert.Equal(Convert.FromHexString("CE150000"), Bytes(w => GameModeHud.WriteInMatchState(w, false)));
        Assert.Equal(Convert.FromHexString("CE150001"), Bytes(w => GameModeHud.WriteInMatchState(w, true)));
        Assert.Equal(Convert.FromHexString("CE1600" + "01000000"), Bytes(w => GameModeHud.WriteStartMatch(w)));
        Assert.Equal(Convert.FromHexString("CE1B00"), Bytes(w => GameModeHud.WriteLeaveMatch(w)));
        Assert.Equal(23, Bytes(w => GameModeHud.WriteSafeZone(w, new Vector4(0, 0, 0, 1), 100f)).Length);
    }

    [Fact]
    public void SynchronizedTeleportAndUpdateLocationMatchTheParsers()
    {
        Assert.Equal(Convert.FromHexString("E80100"), Bytes(w => new SynchronizedTeleport(SynchronizedTeleport.Start).WriteTo(w)));
        Assert.Equal(Convert.FromHexString("E80400"), Bytes(w => new SynchronizedTeleport(SynchronizedTeleport.Release).WriteTo(w)));

        byte[] location = Bytes(w => new UpdateLocation(new Vector4(1, 2, 3, 1), new Vector4(0, 0, 0, 1), Apply: true, WaitForTeleport: true).WriteTo(w));
        Assert.Equal(38, location.Length);
        Assert.Equal(Convert.FromHexString("110A00" + "0000803F" + "00000040" + "00004040" + "0000803F"), location[..19]);
        Assert.Equal(Convert.FromHexString("01" + "00" + "01"), location[35..]);
    }

    [Fact]
    public void MountPacketsMatchTheParsers()
    {
        Assert.Equal(Convert.FromHexString("8819" + "0110000000000000" + "01" + "00000000"), Bytes(w => new VehicleAutoMount(0x1001).WriteTo(w)));
        byte[] mount = Bytes(w => new MountResponse(Rider: 0x1001, Mount: 0x2001).WriteTo(w));
        // 2 + 8 + 8 + 4*4 + identity (12 + 4 strings * 4 + 8) + str 4 = 74
        Assert.Equal(74, mount.Length);
        Assert.Equal(Convert.FromHexString("7002" + "0110000000000000" + "0120000000000000" + "00000000" + "01000000" + "01000000" + "00000000"), mount[..34]);
    }

    [Fact]
    public void ClientBeginZoningCarriesVec4sWeatherAndTheCapturedTail()
    {
        byte[] bytes = Bytes(w => new ClientBeginZoning("Z2", new Vector4(1, 2, 3, 1), new Vector4(0, 0, 0, 1)).WriteTo(w));
        // 1 + (4+2) + 4 + 16 + 16 + 152 (ClearDay weather struct) + 1 + 24 + 8 + 3 = 231
        Assert.Equal(231, bytes.Length);
        Assert.Equal(Convert.FromHexString("0B" + "02000000" + "5A32" + "04000000" + "0000803F" + "00000040" + "00004040" + "0000803F"), bytes[..27]);
        Assert.Equal(Convert.FromHexString("05" + "05000000" + "00000000" + "02000000" + "00000000" + "00000000" + "00000000" + "0000000000000000" + "00" + "00" + "00"), bytes[195..]);
    }
}
