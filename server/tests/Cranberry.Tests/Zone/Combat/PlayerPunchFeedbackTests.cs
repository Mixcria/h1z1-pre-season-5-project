using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

public sealed partial class LivePlayerCombatTests
{
    private static void SetCombatPhase(SoeConnection player, string phase)
    {
        var property = player.Tag!.GetType().GetProperty("Match")!;
        property.SetValue(player.Tag, Enum.Parse(property.PropertyType, phase));
    }

    private static void DeliverPunch(Fixture fixture, SoeConnection player, ulong weaponGuid = 1)
    {
        Set(player.Tag!, "Authenticated", true);
        var packet = ShootingPacketBuilder.FireStateUpdate(weaponGuid, 17);
        fixture.Service.OnMessage(player,
            [new GatewayHeader(GatewayTunnelFromClient.Opcode, 1).ToByte(), .. packet]);
    }

    private static bool IsPunchReaction(byte[] packet) =>
        packet.Length > 12 && packet[1] == 0x0f && packet[2] == 4;

    private static bool IsPunchMarker(byte[] packet) =>
        packet.Length == WeaponHitFeedback.Length + 1 && packet[1] == 0x1a && packet[2] == 0x10;

    private static bool IsHitSound(byte[] packet) =>
        packet.Length > 2 && packet[1] == 0xdc && packet[2] == 3;

    private static bool IsPunchReticleFeedback(byte[] packet) =>
        packet.Length > 2 && packet[1] == 0x1a && packet[2] is 0x10 or 0x1c;

    [Theory]
    [InlineData("InMatch", false, true, true, 9000u)]
    [InlineData("Lobby", false, true, true, 10000u)]
    [InlineData("Lobby", false, true, false, 10000u)]
    [InlineData("Lobby", true, true, true, 10000u)]
    [InlineData("Lobby", false, false, true, 10000u)]
    [InlineData("InMatch", true, true, true, 10000u)]
    [InlineData("InMatch", false, false, true, 10000u)]
    [InlineData("InMatch", false, true, false, 9000u)]
    public void NativePlayerPunchReactsForViewersAndSoundsOnlyForAttackerWithoutBypassingHealthPolicy(
        string phase, bool godMode, bool damageEnabled, bool markersEnabled, uint expectedHealth)
    {
        using var f = new Fixture(new ZoneOptions
        {
            Combat = CombatOptions.Default with { EnableCombatDamage = damageEnabled, SendHitMarker = markersEnabled }
        });
        var attacker = f.Add(1, Vector3.Zero);
        var victim = f.Add(2, new(0, 0, 1.5f));
        var observer = f.Add(3, new(5, 0, 0));
        var otherMatch = f.Add(4, new(0, 0, 1.5f), matchId: 2);
        var distant = f.Add(5, new(ObserverView.PlayerLeaveMetres + 100, 0, 0));
        foreach (var player in f.Connections) SetCombatPhase(player, phase);
        Get<ConsoleSession>(victim.Tag!, "DevConsole").Invulnerable = godMode;
        Face(attacker, 0);
        Face(victim, MathF.PI);
        f.SeeEveryone();
        int start = f.Recorder.Routed.Count;
        DeliverPunch(f, attacker);

        Assert.Equal(expectedHealth, f.Health(victim));
        Assert.Equal(10000u, f.Health(attacker));
        Assert.Equal(10000u, f.Health(otherMatch));
        var packets = f.Recorder.Routed.Skip(start).ToArray();
        var reactions = packets.Where(p => IsPunchReaction(p.Packet)).ToArray();
        Assert.Equal(new ulong[] { 1, 2, 3 }, reactions.Select(r => Get<ulong>(r.Connection.Tag!, "Guid")).Order());
        foreach (var viewer in new[] { attacker, victim, observer })
        {
            var bytes = Assert.Single(reactions, r => r.Connection == viewer).Packet;
            var reader = new PacketReader(bytes.AsSpan(3));
            Assert.Equal(2ul, reader.ReadUInt64());
            Assert.Equal(6, reader.ReadUInt16());
            Assert.Equal("Flinch", System.Text.Encoding.ASCII.GetString(reader.ReadBytes(6)));
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(200u, reader.ReadUInt32());
            Assert.Equal(15, reader.ReadUInt16());
            Assert.Equal("FlinchDirection", System.Text.Encoding.ASCII.GetString(reader.ReadBytes(15)));
            Assert.Equal(0, reader.ReadByte());
            Assert.InRange(reader.ReadSingle(), -0.01f, 0.01f);
            Assert.True(reader.AtEnd);
        }
        Assert.DoesNotContain(reactions, r => r.Connection == otherMatch || r.Connection == distant);
        if (phase == "Lobby")
        {
            Assert.DoesNotContain(packets, p => IsPunchReticleFeedback(p.Packet));
            var audio = Assert.Single(packets, p => IsHitSound(p.Packet));
            Assert.Same(attacker, audio.Connection);
            var reader = new PacketReader(audio.Packet.AsSpan(3));
            Assert.Equal("PLAY_MELEE_ENEMY_HUMAN", reader.ReadString());
            Assert.True(reader.AtEnd);
            Assert.DoesNotContain(packets, p => p.Packet.Length > 3 && p.Packet[1] == 0x11
                && p.Packet[2] == 0x1e && p.Packet[3] == 0); // No incoming-damage effects.
        }
        else
        {
            if (markersEnabled)
            {
                var marker = Assert.Single(packets, p => IsPunchMarker(p.Packet));
                Assert.Same(attacker, marker.Connection);
                Assert.Equal(10000u - expectedHealth, BitConverter.ToUInt32(marker.Packet, 3));
                Assert.Equal(0x09, marker.Packet[7]); // Native melee impact, with audio enabled.
            }
            else Assert.DoesNotContain(packets, p => IsPunchReticleFeedback(p.Packet));
            Assert.DoesNotContain(packets, p => IsHitSound(p.Packet));
        }
        var combat = Get<SessionCombat>(attacker.Tag!, "Combat").Shooter;
        Assert.Equal((long)(10000u - expectedHealth), combat.DamageDealt);
        Assert.Equal(expectedHealth == 10000 ? 0 : 1, combat.HitsRegistered);

        int afterContact = f.Recorder.Routed.Count;
        DeliverPunch(f, attacker);
        // The ability copy for the same swing cannot add another hit or another sound.
        f.Service.OnMessage(attacker, [6, .. Convert.FromHexString("A0030400000075F4100003000000")]);
        Assert.DoesNotContain(f.Recorder.Routed.Skip(afterContact), p =>
            IsPunchReticleFeedback(p.Packet) || IsPunchReaction(p.Packet) || IsHitSound(p.Packet));
        Assert.Equal(expectedHealth, f.Health(victim));
    }

    [Theory]
    [InlineData("InMatch", "behind")]
    [InlineData("InMatch", "range")]
    [InlineData("InMatch", "different-match")]
    [InlineData("InMatch", "different-phase")]
    [InlineData("InMatch", "dead-victim")]
    [InlineData("InMatch", "dead-attacker")]
    [InlineData("InMatch", "unseen")]
    [InlineData("InMatch", "zoning")]
    [InlineData("Lobby", "behind")]
    [InlineData("Lobby", "range")]
    [InlineData("Lobby", "different-match")]
    [InlineData("Lobby", "different-phase")]
    [InlineData("Lobby", "dead-victim")]
    [InlineData("Lobby", "dead-attacker")]
    [InlineData("Lobby", "unseen")]
    [InlineData("Lobby", "zoning")]
    public void RejectedPlayerPunchDoesNotAnimateOrPlayAHitSound(string phase, string reason)
    {
        using var f = new Fixture();
        var attacker = f.Add(1, Vector3.Zero);
        var victim = f.Add(2, new(0, 0, 1.5f), matchId: reason == "different-match" ? 2ul : 1ul);
        SetCombatPhase(attacker, phase);
        SetCombatPhase(victim, phase);
        Face(attacker, reason == "behind" ? MathF.PI : 0);
        if (reason == "range") f.Move(victim, new(0, 0, 2.1f));
        if (reason == "different-phase") SetCombatPhase(victim, phase == "Lobby" ? "InMatch" : "Lobby");
        if (reason == "zoning") SetCombatPhase(attacker, "Zoning");
        if (reason == "dead-victim") Set(victim.Tag!, "DeathSent", true);
        if (reason == "dead-attacker") Set(attacker.Tag!, "DeathSent", true);
        if (reason != "unseen") f.SeeEveryone();
        int start = f.Recorder.Routed.Count;
        DeliverPunch(f, attacker);
        Assert.Equal(10000u, f.Health(victim));
        Assert.DoesNotContain(f.Recorder.Routed.Skip(start), p =>
            IsPunchReticleFeedback(p.Packet) || IsPunchReaction(p.Packet) || IsHitSound(p.Packet));
    }
}
