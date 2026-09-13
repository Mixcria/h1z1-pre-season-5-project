using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    [Theory]
    [InlineData(DamageCause.Bullet)]
    [InlineData(DamageCause.Falling)]
    [InlineData(DamageCause.Melee)]
    [InlineData(DamageCause.Vehicle)]
    [InlineData(DamageCause.ToxicGas)]
    [InlineData(DamageCause.BombingRun)]
    public void LobbyBlocksAllDamageButTheSameLivingPlayerCanTakeWorldDamage(DamageCause cause)
    {
        using var f = new Fixture();
        var player = f.Add(1, Vector3.Zero);
        var state = player.Tag!;
        var matchProperty = state.GetType().GetProperty("Match")!;
        matchProperty.SetValue(state, Enum.Parse(matchProperty.PropertyType, "Lobby"));
        int mark = f.Recorder.Sent.Count;
        f.Service.ForTest(player).Damage(2500, cause);
        Call(f.Service, "ApplyGasDamage", player, state, 2500u);
        Assert.Equal(10000u, f.Health(player));
        Assert.Equal(mark, f.Recorder.Sent.Count);

        f.Service.ForTest(player).EnterMatch();
        f.Service.ForTest(player).Damage(2500, cause);
        Assert.Equal(7500u, f.Health(player));
    }

    [Fact]
    public void GodModeGuardsEveryHealthPathAndDisablingItRestoresDamage()
    {
        using var f = new Fixture();
        var player = f.Add(1, Vector3.Zero);
        var console = Get<ConsoleSession>(player.Tag!, "DevConsole");
        console.Invulnerable = true;
        foreach (DamageCause cause in Enum.GetValues<DamageCause>())
            f.Service.ForTest(player).Damage(1000, cause);
        Call(f.Service, "ApplyGasDamage", player, player.Tag, 1000u);
        Assert.Equal(10000u, f.Health(player));
        console.Invulnerable = false;
        f.Service.ForTest(player).Damage(1000, DamageCause.Falling);
        Assert.Equal(9000u, f.Health(player));
    }

    [Theory]
    [InlineData(0f, 1.5f, 0f, true)]
    [InlineData(0f, -1.5f, 0f, false)]
    [InlineData(1.5f, 0f, 0f, false)]
    [InlineData(1.5f, 0f, 1.57079637f, true)]
    [InlineData(0f, 2.1f, 0f, false)]
    public void PlayerPunchUsesCloseReachAndCurrentFacing(float x, float z, float yaw, bool hit)
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero);
        var b = f.Add(2, new(x, 0, z));
        f.SeeEveryone();
        Face(a, yaw);
        Punch(f, a, 1000);
        Assert.Equal(hit ? 9000u : 10000u, f.Health(b));
        Assert.Equal(10000u, f.Health(a));
    }

    [Fact]
    public void PlayerPunchCannotCrossMatchesOrDamageLobbyPlayers()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero);
        var b = f.Add(2, new(0, 0, 1.5f), matchId: 2);
        var c = f.Add(3, new(0, 0, 1.8f));
        var property = c.Tag!.GetType().GetProperty("Match")!;
        property.SetValue(c.Tag, Enum.Parse(property.PropertyType, "Lobby"));
        f.SeeEveryone();
        Face(a, 0);
        Punch(f, a, 1000);
        Assert.Equal(10000u, f.Health(b));
        Assert.Equal(10000u, f.Health(c));
    }

    [Fact]
    public void PunchChoosesOneNearestTargetAcrossPlayersAndDummies()
    {
        using var f = new Fixture();
        var a = f.Add(1, Vector3.Zero);
        var b = f.Add(2, new(0, 0, 1));
        f.SeeEveryone();
        Face(a, 0);
        var dummy = Get<SessionCombat>(a.Tag!, "Combat").Targets.Spawn(Vector3.Zero, 0, 1, 1.5f, 10000)[0];
        Punch(f, a, 1000);
        Assert.Equal(9000u, f.Health(b));
        Assert.Equal(10000, dummy.Health);
        f.Move(b, new(0, 0, 1.8f));
        Punch(f, a, 2000);
        Assert.Equal(9000u, f.Health(b));
        Assert.Equal(9000, dummy.Health);
    }

    [Fact]
    public void ReportedFallDamagesReleasedWorldPlayerOncePerImpactPeak()
    {
        using var f = new Fixture();
        var player = f.Add(1, Vector3.Zero);
        Set(player.Tag!, "Authenticated", true);
        Set(player.Tag!, "Released", true);
        Set(player.Tag!, "ReleasedAtMs", Environment.TickCount64 - 10000);
        var session = f.Service.ForVehicleTest(player);
        foreach (uint damage in new uint[] { 1000, 2500, 2500, 2000 })
        {
            using var packet = new PacketWriter();
            new CollisionDamageReport(1, 1, damage, CollisionDamageCause.FallDamage, Vector3.Zero).WriteTo(packet);
            session.Deliver(packet.Written);
        }
        Assert.Equal(7500u, f.Health(player));
    }

    private static void Face(SoeConnection player, float heading)
    {
        using var packet = new PacketWriter();
        packet.WriteUInt16((ushort)MovementFieldMask.Orientation);
        packet.WriteUInt32(1);
        packet.WriteByte(0);
        packet.WriteSingle(heading);
        Get<SessionMovementState>(player.Tag!, "Movement").ApplyPlayer(ClientMovementUpdate.Parse(packet.Written));
    }

    private static void Punch(Fixture fixture, SoeConnection player, long now)
    {
        object state = player.Tag!;
        Call(fixture.Service, "PrepareMeleeContext", state);
        WeaponFireArm.Handle(Get<SessionCombat>(state, "Combat"),
            ShootingPacketBuilder.FireStateUpdate(0x3100000000000001, 17), CombatOptions.Default,
            85, Get<SessionMovementState>(state, "Movement").Player!.Position!.Value, now,
            Get<List<WeaponArmResult>>(state, "WeaponArmResults"));
        Call(fixture.Service, "DrainCombatArm", player, state);
    }
}

public sealed class MeleeContactTests
{
    [Theory]
    [InlineData(0f, 1.5f, 0f, true)]
    [InlineData(0f, -1.5f, 0f, false)]
    [InlineData(1.5f, 0f, 0f, false)]
    [InlineData(0f, 2.01f, 0f, false)]
    [InlineData(1.5f, 0f, 1.57079637f, true)]
    public void DummyPunchObeysTheSameContactGate(float x, float z, float heading, bool hit)
    {
        var session = new SessionCombat { MeleeHeading = heading };
        var dummy = session.Targets.Spawn(new(x, 0, z), 0, 1, 0, 10000)[0];
        MeleeArm.ResolveTriggerSwing(session, CombatOptions.Default, 85, 1, Vector3.Zero, 1000, 1000);
        Assert.Equal(hit ? 9000 : 10000, dummy.Health);
    }

    [Fact]
    public void DuplicateTriggersAndAbilityCopiesCannotMultiplyAPunch()
    {
        var session = new SessionCombat { MeleeHeading = 0 };
        var dummy = session.Targets.Spawn(Vector3.Zero, 0, 1, 1.5f, 10000)[0];
        MeleeArm.ResolveTriggerSwing(session, CombatOptions.Default, 85, 1, Vector3.Zero, 1000, 1000);
        foreach (long now in new long[] { 1000, 1001, 1010, 1499 })
            MeleeArm.ResolveTriggerSwing(session, CombatOptions.Default, 85, 1, Vector3.Zero, now, (uint)now);
        var results = new List<WeaponArmResult>();
        foreach (byte sub in new byte[] { 1, 2 })
        {
            using var packet = new PacketWriter();
            packet.WriteByte(0xa0); packet.WriteByte(sub);
            packet.WriteUInt32(1); packet.WriteUInt32(1); packet.WriteUInt32(1111157);
            packet.WriteString("HEAD");
            MeleeArm.Handle(session, packet.Written, CombatOptions.Default, 85, Vector3.Zero, 2000, results);
        }
        Assert.Equal(9000, dummy.Health);
        Assert.Equal(1, session.MeleeSwings);
        MeleeArm.ResolveTriggerSwing(session, CombatOptions.Default, 85, 1, Vector3.Zero, 2500, 2500);
        Assert.Equal(8000, dummy.Health);
    }

    [Fact]
    public void MissingHeadingOrNonFiniteGeometryCannotHit()
    {
        Assert.False(MeleeArm.CanReach(Vector3.Zero, null, Vector3.UnitZ));
        Assert.False(MeleeArm.CanReach(Vector3.Zero, float.NaN, Vector3.UnitZ));
        Assert.False(MeleeArm.CanReach(Vector3.Zero, 0, new(0, float.NaN, 1)));
        Assert.False(MeleeArm.CanReach(Vector3.Zero, 0, Vector3.Zero));
    }
}
