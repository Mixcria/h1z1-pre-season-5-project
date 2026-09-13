using System.Numerics;
using System.Reflection;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    private static void SendNativeGoto(ZoneService service, SoeConnection connection,
        ulong guid = 0, string name = "", uint definition = 0, bool waypoint = false)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        writer.WriteByte(9);
        writer.WriteUInt16(waypoint ? NativeGotoRequest.WaypointSub : NativeGotoRequest.GotoSub);
        writer.WriteUInt64(guid);
        if (!waypoint) writer.WriteUInt32(definition);
        writer.WriteString(name);
        if (!waypoint) writer.WriteUInt64(0);
        service.OnMessage(connection, writer.Written.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeGotoReachesASpawnedNpcByGuidOrPartialName(bool byName)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var target = Assert.Single(Member<SessionCombat>(connection, "Combat").Targets.Spawn(
            new Vector3(200, 40, 300), 0, 1, 10, 10000));
        target.Name = "Native Target";
        int before = SentCount(recorder);
        SendNativeGoto(service, connection, byName ? 0 : target.WorldGuid, byName ? "nAtIvE" : "");
        Assert.Equal(target.Position, Member<SessionMovementState>(connection, "Movement").Player!.Position);
        Assert.Equal(new Vector3(100, 20, 100), Member<ConsoleSession>(connection, "DevConsole").LastPosition);
        Assert.Contains(ConsoleLines(recorder, before), line => line.StartsWith("+ teleported"));
        Assert.Single(Sent(recorder, before), packet => packet.Length > 3 && packet[1] == 0x11 && packet[2] == 0x0a);
    }

    [Fact]
    public void NativeGotoOnlyResolvesPlayersInTheSameAdmittedMatchAndWorld()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var (_, peer, _) = DevelopmentMatch();
        SetSessionMember(peer, "Guid", 0x2002UL);
        SetSessionMember(peer, "CharacterName", "Alice");
        var admission = new MatchAdmissionContext(44, MatchQueueKind.Public, MatchMode.Solo);
        SetSessionMember(connection, "BountyAdmission", admission);
        SetSessionMember(peer, "BountyAdmission", admission);
        SetSessionMember(connection, "BountyWorldId", 1u);
        SetSessionMember(peer, "BountyWorldId", 2u);
        Member<SessionMovementState>(peer, "Movement").PinPlayer(new Vector3(500, 60, 700));
        typeof(ZoneService).GetMethod("RegisterThrowableSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [peer, peer.Tag]);
        int before = SentCount(recorder);
        SendNativeGoto(service, connection, name: "Alice");
        Assert.Contains(ConsoleLines(recorder, before), line => line.Contains("not found"));
        Assert.Equal(new Vector3(100, 20, 100), Member<SessionMovementState>(connection, "Movement").Player!.Position);
        SetSessionMember(peer, "BountyWorldId", 1u);
        SendNativeGoto(service, connection, name: "aLi");
        Assert.Equal(new Vector3(500, 60, 700), Member<SessionMovementState>(connection, "Movement").Player!.Position);
    }

    [Theory]
    [InlineData("waypoint")]
    [InlineData("definition")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("tier")]
    [InlineData("menu")]
    [InlineData("chute")]
    public void NativeGotoUnsupportedTargetsAndPermissionGatesDoNotTeleport(string reason)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var targets = Member<SessionCombat>(connection, "Combat").Targets.Spawn(
            new Vector3(200, 40, 300), 0, reason == "ambiguous" ? 2 : 1, 10, 10000);
        if (reason == "tier") Member<ConsoleSession>(connection, "DevConsole").TierOverride = ConsoleTier.Player;
        if (reason == "menu") EnterStep(connection, "Menu");
        if (reason == "chute") SetSessionMember(connection, "ChuteGuid", 0x999UL);
        int before = SentCount(recorder);
        SendNativeGoto(service, connection,
            guid: reason is "missing" ? 0xABCUL : reason == "ambiguous" ? 0 : targets[0].WorldGuid,
            name: reason == "ambiguous" ? "Practice" : "",
            definition: reason == "definition" ? 1234u : 0,
            waypoint: reason == "waypoint");
        Assert.Equal(new Vector3(100, 20, 100), Member<SessionMovementState>(connection, "Movement").Player!.Position);
        if (reason == "tier") Assert.Empty(Sent(recorder, before));
        else Assert.Single(ConsoleLines(recorder, before));
        Assert.All(Sent(recorder, before), packet => Assert.True(IsConsolePrint(packet)));
    }
}
