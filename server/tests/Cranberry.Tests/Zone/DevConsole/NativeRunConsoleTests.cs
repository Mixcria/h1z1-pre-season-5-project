using System.Buffers.Binary;
using System.Reflection;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    private static byte[] NativeRunPacket(float speed, byte opcode = ConsoleOpcodes.CommandBase)
    {
        var bytes = new byte[] { opcode, 0xc6, 0x04, 0, 0, 0, 0 };
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(3), speed);
        return bytes;
    }

    private static void SendNativeRun(ZoneService service, SoeConnection connection, byte[] payload)
    {
        var bytes = new byte[payload.Length + 1];
        bytes[0] = new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte();
        payload.CopyTo(bytes, 1);
        service.OnMessage(connection, bytes);
    }

    private static bool IsNativeRunReply(byte[] packet) => NativeRunCommand.Matches(Payload(packet));

    [Theory]
    [InlineData(40f, ConsoleOpcodes.CommandBase)]
    [InlineData(6.5f, ConsoleOpcodes.AdminBase)]
    public void NativeRunUsesAbsoluteMetresPerSecondAndTheOriginalReply(float speed, byte opcode)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendNativeRun(service, connection, NativeRunPacket(speed, opcode));
        Assert.Equal(speed, Member<PlayerMovementTracker>(connection, "MovementStats").Profile.MaxMovementSpeed);
        Assert.Equal(speed, Session(connection).NativeRunMetresPerSecond);
        Assert.Equal(1f, Session(connection).SpeedMultiplier);
        Assert.Contains(Sent(recorder, before), p => p[1] == 0x0f && p[2] == 0x40);
        Assert.Contains(Sent(recorder, before), p => p[1] == 0x11 && p[2] == 5);
        byte[] reply = Assert.Single(Sent(recorder, before), IsNativeRunReply);
        Assert.Equal(NativeRunPacket(speed), Payload(reply).ToArray());
    }

    [Fact]
    public void NativeRunDefaultRestoresNormalAndResetsBothServerAndClientOverride()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var tracker = Member<PlayerMovementTracker>(connection, "MovementStats");
        SendExecuteCommand(service, connection, "speed", "1");
        float normal = tracker.Profile.MaxMovementSpeed;
        SendNativeRun(service, connection, Convert.FromHexString("09C60400002042")); // /run 40
        int before = SentCount(recorder);
        SendNativeRun(service, connection, NativeRunPacket(0)); // /run default
        Assert.Equal(normal, tracker.Profile.MaxMovementSpeed);
        Assert.Null(Session(connection).NativeRunMetresPerSecond);
        Assert.Equal(1f, Session(connection).SpeedMultiplier);
        Assert.Equal(NativeRunPacket(0), Payload(Assert.Single(Sent(recorder, before), IsNativeRunReply)).ToArray());
    }

    [Fact]
    public void RepeatingNativeRunSpeedTogglesTheServerProfileWithTheOriginalClientAcknowledgement()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var tracker = Member<PlayerMovementTracker>(connection, "MovementStats");
        SendExecuteCommand(service, connection, "speed", "1");
        float normal = tracker.Profile.MaxMovementSpeed;
        SendNativeRun(service, connection, NativeRunPacket(40));
        int before = SentCount(recorder);

        SendNativeRun(service, connection, NativeRunPacket(40));

        Assert.Equal(normal, tracker.Profile.MaxMovementSpeed);
        Assert.Null(Session(connection).NativeRunMetresPerSecond);
        Assert.Equal(1f, Session(connection).SpeedMultiplier);
        Assert.Contains(ConsoleLines(recorder, before), line => line.Contains("restored to normal"));
        // The original client compares this echo with its current override and clears it.
        // A third identical request starts a new override on both sides.
        Assert.Equal(NativeRunPacket(40), Payload(Assert.Single(Sent(recorder, before), IsNativeRunReply)).ToArray());
        before = SentCount(recorder);
        SendNativeRun(service, connection, NativeRunPacket(40));
        Assert.Equal(40f, tracker.Profile.MaxMovementSpeed);
        Assert.Equal(40f, Session(connection).NativeRunMetresPerSecond);
        Assert.Equal(NativeRunPacket(40), Payload(Assert.Single(Sent(recorder, before), IsNativeRunReply)).ToArray());
    }

    [Fact]
    public void NativeRunRemainsAbsoluteWhenFootwearChangesAndCustomSpeedCanClearIt()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendNativeRun(service, connection, NativeRunPacket(40));
        var apply = typeof(ZoneService).GetMethod("ApplyConsoleSpeed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var changedFootwear = MovementProfile.Default with { MaxMovementSpeed = 8 };
        var profile = (MovementProfile)apply.Invoke(null, [connection.Tag, changedFootwear])!;
        Assert.Equal(40f, profile.MaxMovementSpeed);
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "speed", "2");
        Assert.Null(Session(connection).NativeRunMetresPerSecond);
        profile = (MovementProfile)apply.Invoke(null, [connection.Tag, changedFootwear])!;
        Assert.Equal(16f, profile.MaxMovementSpeed);
        Assert.Equal(NativeRunPacket(0), Payload(Assert.Single(Sent(recorder, before), IsNativeRunReply)).ToArray());
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("too-fast")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("player")]
    [InlineData("lobby")]
    public void NativeRunRefusesInvalidOrUnauthorizedRequestsWithoutChangingMovement(string variant)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        byte[] packet = NativeRunPacket(40);
        switch (variant)
        {
            case "negative": packet = NativeRunPacket(-1); break;
            case "nan": packet = NativeRunPacket(float.NaN); break;
            case "infinity": packet = NativeRunPacket(float.PositiveInfinity); break;
            case "too-fast": packet = NativeRunPacket(NativeRunCommand.MaximumMetresPerSecond + 1); break;
            case "short": packet = packet[..^1]; break;
            case "long": packet = [.. packet, 0]; break;
            case "player": Session(connection).TierOverride = ConsoleTier.Player; break;
            case "lobby": EnterStep(connection, "Lobby"); break;
        }
        var tracker = Member<PlayerMovementTracker>(connection, "MovementStats");
        float normal = tracker.Profile.MaxMovementSpeed;
        int before = SentCount(recorder);
        SendNativeRun(service, connection, packet);
        Assert.Equal(normal, tracker.Profile.MaxMovementSpeed);
        Assert.Null(Session(connection).NativeRunMetresPerSecond);
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
        if (variant == "player") Assert.Empty(Sent(recorder, before));
        else Assert.NotEmpty(ConsoleLines(recorder, before));
    }
}
