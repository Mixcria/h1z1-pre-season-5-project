using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchEndgame;

// Frozen vectors of the 0xce subs an ending match sends. Every handler cited here is exact-length
// gated, so each Length is load-bearing: one byte either way and the client drops the packet in
// silence and shows nothing at all.
public sealed class EndgamePacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void ShowVictoryScreenIsSevenBytesWithAUInt32()
    {
        // ce 18 00 | u32 (FUN_140bbbbc0:18-42; the cursor is an undefined4* and the final read is
        // an undefined4, so this is 7 bytes and NOT docs/15 §7's 4).
        var packet = new ShowVictoryScreen();
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(ShowVictoryScreen.Length, bytes.Length);
        Assert.Equal(7, ShowVictoryScreen.Length);
        Assert.Equal(Convert.FromHexString("CE1800" + "00000000"), bytes);
    }

    [Fact]
    public void ShowVictoryScreenCarriesTheValueItIsGiven()
    {
        // Nothing in the client reads it — it is passed through as the event payload — but a
        // writer that silently dropped it would hide that fact rather than record it.
        byte[] bytes = Bytes(new ShowVictoryScreen(0x01020304).WriteTo);
        Assert.Equal(0x01020304u, BitConverter.ToUInt32(bytes, 3));
    }

    [Fact]
    public void LeaveMatchIsHeaderOnly()
    {
        // ce 1b 00, and FUN_140bbbce0:37 requires end - cursor < 1: any payload byte kills it.
        byte[] bytes = Bytes(new LeaveMatch().WriteTo);
        Assert.Equal(LeaveMatch.Length, bytes.Length);
        Assert.Equal(3, LeaveMatch.Length);
        Assert.Equal(Convert.FromHexString("CE1B00"), bytes);
    }

    [Fact]
    public void LeaveMatchIsTheExistingMatchFlowWriter()
    {
        // Reuse rather than a second copy of three bytes: GameModeHud.WriteLeaveMatch is the one
        // that already existed.
        Assert.Equal(Bytes(GameModeHud.WriteLeaveMatch), Bytes(new LeaveMatch().WriteTo));
    }

    [Fact]
    public void DeathTimerIsSevenBytesWithAUInt32()
    {
        // ce 12 00 | u32 (FUN_140bb6b50:24-46, then the "DEATH_TIMER" audio parameter at :49-50).
        byte[] bytes = Bytes(new DeathTimer(30).WriteTo);
        Assert.Equal(DeathTimer.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString("CE1200" + "1E000000"), bytes);
    }

    [Fact]
    public void AliveCountIsSevenBytesAndTheExistingWriter()
    {
        // ce 09 00 | i32 (FUN_140bba510:70-113).
        byte[] bytes = Bytes(w => EndgamePackets.WriteAliveCount(w, 12));
        Assert.Equal(EndgamePackets.AliveCountLength, bytes.Length);
        Assert.Equal(Convert.FromHexString("CE0900" + "0C000000"), bytes);
        Assert.Equal(Bytes(w => GameModeHud.WritePlayersRemaining(w, 12)), bytes);
    }

    [Fact]
    public void AliveCountOfMinusOneHidesTheCounter()
    {
        byte[] bytes = Bytes(w => EndgamePackets.WriteAliveCount(w, -1));
        Assert.Equal(Convert.FromHexString("CE0900" + "FFFFFFFF"), bytes);
    }

    [Fact]
    public void WinnerDeathInfoIsRankZeroAndCause48()
    {
        // ce 04 00 | i32 rank; u8 draw; str killer; u32 field4; u32 sourceId; u32 cause
        // (FUN_140bb0cc0, exact-length gated at FUN_140bbb120:70). rank 0 draws "#1" and cause
        // 0x48 resolves UI.Results.Rank.EndOfMatchWinner = 12600 "You won!".
        GasPackets.DeathInfo winner = EndgamePackets.Winner();
        byte[] bytes = Bytes(winner.WriteTo);

        Assert.Equal(GasPackets.DeathInfo.BaseLength, bytes.Length);
        Assert.Equal(24, winner.Length);
        Assert.Equal(
            Convert.FromHexString(
                "CE0400"
                + "00000000"      // rank 0 -> the client draws #1
                + "00"            // draw
                + "00000000"      // killer name, empty
                + "00000000"      // field4
                + "00000000"      // sourceId 0 -> the name comes from the cause
                + "48000000"),    // EndOfMatchWinner
            bytes);
    }

    [Fact]
    public void KilledByPlayerCarriesTheKillerNameAndTheKillConditionCause()
    {
        GasPackets.DeathInfo death = EndgamePackets.KilledByPlayer(4, "ab", killerHealth: 76);
        byte[] bytes = Bytes(death.WriteTo);

        Assert.Equal(GasPackets.DeathInfo.BaseLength + 2, bytes.Length);
        Assert.Equal(bytes.Length, death.Length);
        Assert.Equal(
            Convert.FromHexString(
                "CE0400"
                + "04000000"                                    // rank 4 -> "#5"
                + "00"
                + "02000000" + Convert.ToHexString(Encoding.UTF8.GetBytes("ab"))
                + "4C000000"                                    // field4 = the killer's health
                + "00000000"
                + "4D000000"),                                  // GameModeKillCondition
            bytes);
    }

    [Fact]
    public void EnvironmentalDeathHasNoKillerAndNoSource()
    {
        GasPackets.DeathInfo death = EndgamePackets.Environmental(2, DeathCauseCode.Gas);
        byte[] bytes = Bytes(death.WriteTo);

        Assert.Equal(GasPackets.DeathInfo.BaseLength, bytes.Length);
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 8));   // empty name
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 16));  // sourceId
        Assert.Equal((uint)DeathCauseCode.Gas, BitConverter.ToUInt32(bytes, 20));
    }

    [Fact]
    public void EnvironmentalDeathIsByteIdenticalToTheShippedGasDeath()
    {
        // The gas lane already sends this exact packet through GasPackets.DeathInfo.Gas. If the
        // generalisation produced different bytes it would be a duplicate writer, not a reuse.
        Assert.Equal(
            Bytes(GasPackets.DeathInfo.Gas(rankIndex: 0).WriteTo),
            Bytes(EndgamePackets.Environmental(0, DeathCauseCode.Gas).WriteTo));
    }

    [Fact]
    public void SurvivorDefaultsToEndOfMatchRemaining()
    {
        byte[] bytes = Bytes(EndgamePackets.Survivor(3).WriteTo);
        Assert.Equal((uint)DeathCauseCode.EndOfMatchRemaining, BitConverter.ToUInt32(bytes, 20));
        Assert.Equal(3, BitConverter.ToInt32(bytes, 3));
    }

    [Fact]
    public void DrawSetsTheUiBoolean()
    {
        // ce 04's +0x1c (FUN_140bbb120:84-87) — S4 row E6.
        byte[] bytes = Bytes(EndgamePackets.Draw(1, DeathCauseCode.GameModeKillCondition).WriteTo);
        Assert.Equal(0x01, bytes[7]);
        Assert.Equal(0x00, Bytes(EndgamePackets.Environmental(1, DeathCauseCode.Gas).WriteTo)[7]);
    }

    [Fact]
    public void NegativeRanksAreClampedToTheZeroBasedFloor()
    {
        // The client draws rank + 1 unconditionally, so a negative rank would render "#0" or less.
        Assert.Equal(0, EndgamePackets.Environmental(-5, DeathCauseCode.Gas).Rank);
        Assert.Equal(0, EndgamePackets.KilledByPlayer(-1, "x").Rank);
        Assert.Equal(0, EndgamePackets.Survivor(-2).Rank);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void ShowVictoryScreenIsAlwaysSevenBytes(uint value) =>
        Assert.Equal(ShowVictoryScreen.Length, Bytes(new ShowVictoryScreen(value).WriteTo).Length);

    [Theory]
    [InlineData(0u)]
    [InlineData(30u)]
    [InlineData(uint.MaxValue)]
    public void DeathTimerIsAlwaysSevenBytes(uint value) =>
        Assert.Equal(DeathTimer.Length, Bytes(new DeathTimer(value).WriteTo).Length);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(150)]
    public void AliveCountIsAlwaysSevenBytes(int alive) =>
        Assert.Equal(EndgamePackets.AliveCountLength, Bytes(w => EndgamePackets.WriteAliveCount(w, alive)).Length);

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a longer killer name")]
    public void DeathInfoLengthIsTwentyFourPlusTheNameBytes(string killer)
    {
        GasPackets.DeathInfo death = EndgamePackets.KilledByPlayer(0, killer);
        int expected = GasPackets.DeathInfo.BaseLength + Encoding.UTF8.GetByteCount(killer);
        Assert.Equal(expected, death.Length);
        Assert.Equal(expected, Bytes(death.WriteTo).Length);
    }

    [Fact]
    public void EndgameSubOpcodesAreTheDispatchersOwn()
    {
        // FUN_140bba510's switch: case 0x18 -> FUN_140bbbbc0, case 0x1b -> FUN_140bbbce0,
        // case 0x12 -> FUN_140bb6b50.
        Assert.Equal(0xce, EndgamePackets.Opcode);
        Assert.Equal(0x0018, ShowVictoryScreen.SubOpcode);
        Assert.Equal(0x001b, LeaveMatch.SubOpcode);
        Assert.Equal(0x0012, DeathTimer.SubOpcode);
    }
}
