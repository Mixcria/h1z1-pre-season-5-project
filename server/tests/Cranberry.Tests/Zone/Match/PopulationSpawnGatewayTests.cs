using System.Numerics;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed partial class WorldModeRoutingTests
{
    [Fact]
    public void OpeningGasCannotDamageTheStaleLobbyPoseBeforeTeleportReady()
    {
        var f = new Fixture(enableGas: true);
        var player = f.Player();
        RegisterAccounts(f, player);
        f.Transfer(player, 1); f.Transfer(player, 1);
        var phase = player.Tag!.GetType().GetProperty("Match")!;
        phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Lobby"));
        Call(f.Service, "BeginMatchDrop", player, player.Tag);
        Assert.Equal("Dropping", Get<object>(player.Tag!, "Match").ToString());
        Assert.False(Get<bool>(player.Tag!, "MountRequested"));
        var gas = Get<GasController>(player.Tag!, "Gas");
        Assert.Equal(0, gas.Schedule!.RingLiveFromMs);
        uint before = Get<uint>(player.Tag!, "Hitpoints");
        gas.Start(Environment.TickCount64 - 2000, gas.Schedule.Seed);
        Call(f.Service, "PumpGas", player, player.Tag);
        Assert.Equal(before, Get<uint>(player.Tag!, "Hitpoints"));
    }

    [Fact]
    public void DynamicGasAndDropPacketsShareTheFrozenTenPlayerPlan()
    {
        var f = new Fixture(enableGas: true);
        var players = Enumerable.Range(0, 10).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, players);
        foreach (var player in players)
        {
            f.Transfer(player, 1); f.Transfer(player, 1);
            var phase = player.Tag!.GetType().GetProperty("Match")!;
            phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Lobby"));
        }
        foreach (var player in players.Reverse()) Call(f.Service, "BeginMatchDrop", player, player.Tag);
        var first = Get<GasController>(players[0].Tag!, "Gas");
        Assert.Equal(625f, first.Schedule!.Phase(1).Target.Radius);
        Assert.Equal(1000f, first.Schedule.InitialCircle.Radius);
        foreach (var player in players)
        {
            var gas = Get<GasController>(player.Tag!, "Gas");
            Assert.Equal(first.StartMs, gas.StartMs);
            Assert.Equal(first.Schedule.Phases, gas.Schedule!.Phases);
            Vector4 drop = Get<Vector4>(player.Tag!, "Drop");
            Assert.True(gas.Schedule.InitialCircle.Contains(new Vector3(drop.X, drop.Y, drop.Z)));
            byte[] opening = f.Recorded.Last(p => p.Connection == player && p.Bytes.Length == 32
                && p.Bytes[1] == 0xce && p.Bytes[2] == 1).Bytes;
            Assert.Equal(1000f, BitConverter.ToSingle(opening, 20));
            Assert.Equal(gas.Schedule.InitialCircle.Centre.X, BitConverter.ToSingle(opening, 4));
            Assert.Equal(gas.Schedule.InitialCircle.Centre.Z, BitConverter.ToSingle(opening, 12));
        }
    }

    [Fact]
    public void DeparturesDuringDropDoNotResizeGasAndAnotherWorldHasItsOwnPlan()
    {
        var f = new Fixture(enableGas: true);
        var solo = Enumerable.Range(0, 11).Select(_ => f.Player()).ToArray();
        var duos = Enumerable.Range(0, 4).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, solo.Concat(duos).ToArray());
        foreach (var player in solo.Concat(duos))
        {
            uint world = solo.Contains(player) ? 1u : 6u;
            f.Transfer(player, world); f.Transfer(player, world);
            var phase = player.Tag!.GetType().GetProperty("Match")!;
            phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Lobby"));
        }
        Call(f.Service, "BeginMatchDrop", solo[0], solo[0].Tag);
        var first = Get<GasController>(solo[0].Tag!, "Gas");
        Assert.Equal(900f, first.Schedule!.Phase(1).Target.Radius);
        Call(f.Service, "LeaveSharedLoot", solo[^1].Tag);
        foreach (var player in solo.Skip(1).SkipLast(1)) Call(f.Service, "BeginMatchDrop", player, player.Tag);
        foreach (var player in solo.SkipLast(1))
        {
            var gas = Get<GasController>(player.Tag!, "Gas");
            Assert.Equal(first.Schedule.Phases, gas.Schedule!.Phases);
            Assert.Equal(first.StartMs, gas.StartMs);
        }
        foreach (var player in duos) Call(f.Service, "BeginMatchDrop", player, player.Tag);
        var other = Get<GasController>(duos[0].Tag!, "Gas");
        Assert.Equal(625f, other.Schedule!.Phase(1).Target.Radius);
        Assert.NotEqual(first.Schedule.Seed, other.Schedule.Seed);
    }

    [Theory]
    [InlineData(1u, 2, 1)]
    [InlineData(6u, 4, 2)]
    [InlineData(7u, 10, 5)]
    public void MatchStartUsesSharedRosterAndDistinctCanopies(uint world, int count, int size)
    {
        var f = new Fixture();
        var players = Enumerable.Range(0, count).Select(_ => f.Player()).ToArray();
        RegisterAccounts(f, players);
        foreach (var player in players)
        {
            f.Transfer(player, world); f.Transfer(player, world);
            var phase = player.Tag!.GetType().GetProperty("Match")!;
            phase.SetValue(player.Tag, Enum.Parse(phase.PropertyType, "Lobby"));
        }
        Assert.Equal(count, players.Select(p => Get<MatchAdmissionContext>(p.Tag!, "BountyAdmission").MatchId).GroupBy(id => id).Single().Count());
        foreach (var player in players.Reverse()) Call(f.Service, "BeginMatchDrop", player, player.Tag);
        var drops = players.Select(p => Get<Vector4>(p.Tag!, "Drop")).ToArray();
        Assert.Equal(count, drops.Distinct().Count());
        Assert.All(players, player => Assert.Equal("Dropping", Get<object>(player.Tag!, "Match").ToString()));
        foreach (var player in players)
        {
            // This is the air-spawn packet from BeginMatchDrop, before any chute handshake.
            byte[] packet = f.Recorded.Last(p => p.Connection == player && p.Bytes.Length > 19
                && p.Bytes[1] == 0x11 && p.Bytes[2] == 0x0a && p.Bytes[3] == 0).Bytes;
            Assert.Equal(850f, BitConverter.ToSingle(packet, 8));
        }
        for (int i = 1; i < count; i++)
        {
            float distance = Vector4.Distance(drops[0], drops[i]);
            Assert.InRange(distance, i < size ? 25f : 250f, i < size ? 49f : 1050f);
        }
    }

    [Fact]
    public void LobbySelectionVariesOnReentryAndIsRetainedForConsumers()
    {
        var f = new Fixture();
        var player = f.Player();
        Call(f.Service, "ChooseStagingPosition", player.Tag);
        var first = Get<Vector4>(player.Tag!, "StagingPosition");
        Assert.Contains(first, LobbySpawnPlanner.Points.ToArray());
        Assert.Equal(first, Call(f.Service, "StagingPosition", player.Tag));
        Call(f.Service, "ChooseStagingPosition", player.Tag);
        Assert.NotEqual(first, Get<Vector4>(player.Tag!, "StagingPosition"));
    }
}
