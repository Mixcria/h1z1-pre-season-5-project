using System.Net;
using System.Numerics;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Zone.Movement;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone;

/// <summary>
/// The wave-4 <b>integration</b> surface: the parts of docs/45-50 that live in
/// <c>ZoneService</c>, <c>ZoneOptions</c> and <c>Program.cs</c>, and which therefore have no lane
/// test of their own. Every lane pinned its own library; nothing pinned the wiring, and the wiring
/// is where docs/32's two regressions actually happened.
/// <para>
/// These are <b>send-side</b> assertions (docs/32, D29): they prove what the server emits and
/// nothing at all about what the client does with it.
/// </para>
/// </summary>
public class Wave4WiringTests
{
    private sealed class RecordingRecorder : IPacketRecorder
    {
        public List<(string Direction, byte[] Bytes)> Messages { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) =>
            Messages.Add((direction, bytes.ToArray()));

        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
        {
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder)
        Admit(ZoneOptions options)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options);
        var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555),
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);

        using var writer = new PacketWriter();
        writer.WriteByte(GatewayLoginRequest.Opcode);
        writer.WriteUInt64(admission.Guid);
        writer.WriteString(admission.Ticket);
        writer.WriteString(GatewayLoginRequest.AugustProtocol);
        writer.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, writer.Written.ToArray());
        return (service, connection, recorder);
    }

    private static byte[][] Sent(RecordingRecorder recorder, int skip = 0) =>
        [.. recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes).Skip(skip)];

    /// <summary>The 19-byte request the August client actually sent, three times, in one session.</summary>
    private static void SendInteractionString(ZoneService service, SoeConnection connection, ulong targetGuid)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        packet.WriteByte(ZoneOpcodes.CommandBase);
        packet.WriteUInt16(InteractionStringRequest.SubOpcode);
        packet.WriteUInt64(targetGuid);
        packet.WriteUInt32(0);
        packet.WriteUInt32(0);
        service.OnMessage(connection, packet.Written.ToArray());
    }

    private static bool IsInteractionStringReply(byte[] packet) =>
        packet.Length >= 4
        && packet[1] == ZoneOpcodes.CommandBase
        && BitConverter.ToUInt16(packet, 2) == InteractionStringRequest.SubOpcode;

    // ---------------------------------------------------------------- docs/47 I2, the [F] label

    /// <summary>
    /// docs/47 §4: <c>Command.InteractionString (09 2d)</c> is what carries the prompt's TEXT, and
    /// Cranberry had never answered it — three requests sat unhandled in
    /// <c>host-20260829-220829.log</c>. The reply must echo the guid the request named, or
    /// <c>FUN_14129e7c0</c>'s <c>replyGuid == ui-&gt;+0x1a8</c> test drops it.
    /// </summary>
    [Fact]
    public void AnInteractionStringRequestIsAnsweredAndEchoesItsOwnGuid()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions());
        int before = Sent(recorder).Length;

        const ulong Target = 0x2000_0000_0000_0007UL;
        SendInteractionString(service, connection, Target);

        byte[] reply = Assert.Single(Sent(recorder, before), IsInteractionStringReply);
        // + 1 for the gateway tunnel header byte the recorder sees.
        Assert.Equal(InteractionStringReply.MinimalLength + 1, reply.Length);
        Assert.Equal(Target, BitConverter.ToUInt64(reply, 4));
    }

    /// <summary>
    /// An unknown guid is still answered. <c>FUN_14140c480</c> runs either way, and an unanswered
    /// request is one the client re-asks once a second for the rest of the match (docs/47 §I2).
    /// </summary>
    [Fact]
    public void AnUnknownGuidIsAnsweredWithStringIdZeroRatherThanIgnored()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions());
        int before = Sent(recorder).Length;

        SendInteractionString(service, connection, 0xDEAD_BEEF_0000_0001UL);

        byte[] reply = Assert.Single(Sent(recorder, before), IsInteractionStringReply);
        Assert.Equal(InteractionPromptStrings.None, BitConverter.ToUInt32(reply, 12));
    }

    /// <summary>
    /// docs/47 §7 q1 is settled: the reply the server puts on the wire carries the string id in the
    /// FIRST of the two trailing words. Lane 0D deleted <c>CRANBERRY_INTERACTION_STRING_ORDER</c>
    /// and the alternative branch with it — the other order wrote a locale id into the entry-count
    /// word and walked the client's 32-byte-stride array off the end of the buffer — so what this
    /// pins now is that there is exactly one order reaching the wire.
    /// </summary>
    [Fact]
    public void TheDerivedFieldOrderReachesTheWire()
    {
        const ulong Target = 0x2000_0000_0000_0009UL;

        var (service, connection, recorder) = Admit(new ZoneOptions());
        int before = Sent(recorder).Length;
        SendInteractionString(service, connection, Target);
        byte[] bytes = Assert.Single(Sent(recorder, before), IsInteractionStringReply);

        // The recorded frame carries the tunnel prefix; the reply body starts one byte in, so the
        // string id is at 12 and the entry count at 16 — the same offsets the sweep test used.
        Assert.Equal(InteractionStringReply.MinimalLength + 1, bytes.Length);
        Assert.Equal(InteractionPromptStrings.None, BitConverter.ToUInt32(bytes, 12));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 16));
    }

    /// <summary>
    /// <c>InteractionStringRequest.Matches</c> tests the sub-opcode, so the new case cannot swallow
    /// <c>09 07</c> InteractRequest, <c>09 15</c> PlayerSelect or <c>09 08</c> InteractCancel — the
    /// three Command packets the live pickup path depends on.
    /// </summary>
    [Theory]
    [InlineData(0x0007)]
    [InlineData(0x0015)]
    [InlineData(0x0008)]
    public void TheNewCaseNeverAnswersAnotherCommandPacket(int subOpcode)
    {
        var (service, connection, recorder) = Admit(new ZoneOptions());
        int before = Sent(recorder).Length;

        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        packet.WriteByte(ZoneOpcodes.CommandBase);
        packet.WriteUInt16((ushort)subOpcode);
        packet.WriteUInt64(0x2000_0000_0000_0001UL);
        service.OnMessage(connection, packet.Written.ToArray());

        Assert.DoesNotContain(Sent(recorder, before), IsInteractionStringReply);
    }

    // ------------------------------------------------------------- docs/47 I1b, the door pose

    /// <summary>
    /// docs/47 §3b proves both Euler packings ask for a rotation about the world X axis plus a scale
    /// of <c>yaw² + 1</c>. The writer substitutes regardless, so this pins the second half: the
    /// shipped default no longer <em>asks</em> for one, which is what stops the host log lying about
    /// what went out.
    /// </summary>
    [Fact]
    public void TheShippedDoorRotationIsNoLongerAProvenImpossiblePacking()
    {
        DoorRotation shipped = new ZoneOptions().DoorRotation;

        Assert.Equal(DoorRotation.QuaternionYUp, shipped);
        Assert.False(DoorRotationPacking.IsProvenImpossible(shipped));
        Assert.Equal(shipped, DoorRotationPacking.ForWire(shipped));
    }

    /// <summary>
    /// docs/47 §I3: a re-stream is worthless without a recurring trigger — <c>ArmGroundLoot</c> runs
    /// exactly once, at the parachute landing, which is precisely why the wave-3 latch looked
    /// harmless. The pump interval is the trigger, and <c>0</c> is the one-value rollback to the
    /// wave-3 behaviour (`CRANBERRY_DOOR_RESTREAM_MS`).
    /// </summary>
    [Fact]
    public void DoorsHaveARecurringRestreamTriggerWithAOneValueRollback()
    {
        Assert.True(new ZoneOptions().DoorRestreamIntervalMs > 0);
        Assert.Equal(0, new ZoneOptions { DoorRestreamIntervalMs = 0 }.DoorRestreamIntervalMs);

        // The predicate the pump asks is the lane's, and it is false until the player has moved
        // half a radius — so a standing player costs one distance test per tick and no packets.
        var doors = new MatchDoors(Z2Doors.LoadDefault(), new MatchDoorOptions());
        var spawned = new List<DoorInstance>();
        Assert.True(doors.ShouldRestream(new Vector3(348f, 32.5f, 130.8f), 60f));
        doors.RegisterNear(new Vector3(348f, 32.5f, 130.8f), 60f, 48, spawned);
        Assert.False(doors.ShouldRestream(new Vector3(348f, 32.5f, 130.8f), 60f));
        Assert.True(doors.ShouldRestream(new Vector3(348f + 61f, 32.5f, 130.8f), 60f));
    }

    // ------------------------------------------------------------------ docs/48, the random drop

    /// <summary>
    /// docs/48: the drop dataset is forced at boot, never inside the countdown closure — its anchor
    /// pass over 144,365 area-tagged markers is ~720 ms in Debug and would otherwise run on the
    /// single listener thread at the worst possible moment.
    /// </summary>
    [Fact]
    public void TheDropDatasetIsBuiltAtBootAndReportsItsDerivedCensus()
    {
        var (service, _, _) = Admit(new ZoneOptions { SendDoors = false, SendVehicles = false });

        string[] lines = service.PreloadWorldData();

        string drop = Assert.Single(lines, line => line.StartsWith("drop data:", StringComparison.Ordinal));
        Assert.Contains("92 named Z2 place(s)", drop, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", drop, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="ZoneOptions.MatchDropSpawn"/> is deliberately KEPT (docs/48 §5.6): it is the
    /// degrade target when the placement file is unreadable, and the burst centre before the client
    /// has a pose. Turning the random drop off must restore the wave-3 behaviour exactly.
    /// </summary>
    [Fact]
    public void TheRandomDropIsOnByDefaultAndTheFixedSpawnSurvivesAsTheFallback()
    {
        var options = new ZoneOptions();

        Assert.True(options.Drop.Enabled);
        Assert.Equal(850f, options.Drop.SkySpawnAltitude);
        Assert.Equal(new Vector4(348.0f, 32.5f, 130.8f, 1), options.MatchDropSpawn);
        Assert.Equal(0UL, options.MatchSeed);   // 0 = draw a fresh seed per match
    }

    /// <summary>
    /// docs/48 §5.1: one seed reproduces the whole match, so every subsystem's stream must be a pure
    /// function of it. That is what makes <c>CRANBERRY_MATCH_SEED</c> replay the drop <em>and</em>
    /// the gas circles rather than one of them.
    /// </summary>
    [Fact]
    public void OneMatchSeedDeterminesBothTheDropAndTheGas()
    {
        const ulong Seed = 0x0123_4567_89AB_CDEFUL;

        Assert.Equal(MatchSeeds.For(Seed, MatchSeeds.GasSalt), MatchSeeds.For(Seed, MatchSeeds.GasSalt));
        Assert.NotEqual(MatchSeeds.For(Seed, MatchSeeds.GasSalt), MatchSeeds.For(Seed, MatchSeeds.DropSalt));
        Assert.Equal(Seed, MatchSeeds.Draw(Seed, sessionGuid: 0x1001, utcTicks: 12345));
        Assert.NotEqual(0UL, MatchSeeds.Draw(0, sessionGuid: 0x1001, utcTicks: 12345));
    }

    // ----------------------------------------------------------------- docs/49, movement tuning

    /// <summary>
    /// docs/49 §I3: <c>MovementTuning.Aug2017Default</c> must be the same <em>instance</em> as
    /// <see cref="ZoneOptions.Movement"/>'s default, because <c>SendMovementStats</c> short-circuits
    /// on <c>ReferenceEquals</c>. An equal-but-distinct default would call <c>SetProfile</c>, clear
    /// <c>StatsDelivered</c> and re-arm the <c>0f 40</c> burst on every resync.
    /// </summary>
    [Fact]
    public void TheDefaultMovementProfileSurvivesTheHostWiringByReference()
    {
        MovementProfile fromEnvironment = MovementTuning.FromEnvironment(_ => null, out string? note);

        Assert.Null(note);
        Assert.Same(new ZoneOptions().Movement, fromEnvironment);
        Assert.Same(MovementProfile.Default, new ZoneOptions { Movement = fromEnvironment }.Movement);
    }

    /// <summary>A launch-script typo is ignored with a note; it must never be able to stop the host.</summary>
    [Fact]
    public void AnUnparsableMovementOverrideIsIgnoredWithANoteRatherThanThrown()
    {
        MovementProfile profile = MovementTuning.FromEnvironment(
            name => name == "CRANBERRY_MOVE_SPRINT" ? "1,45" : null,
            out string? note);

        Assert.NotNull(note);
        Assert.Contains("CRANBERRY_MOVE_SPRINT", note, StringComparison.Ordinal);
        Assert.Equal(MovementProfile.Default.SprintSpeedModifier, profile.SprintSpeedModifier);
    }
}
