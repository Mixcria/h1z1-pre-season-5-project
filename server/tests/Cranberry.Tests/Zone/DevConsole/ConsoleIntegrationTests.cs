using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The console through a real <see cref="ZoneService"/>: bytes in on the gateway tunnel, bytes out
/// on channel 0.
/// <para>
/// Everything below the hook has its own unit tests (the hash, the packet layouts, the parser, the
/// registry, the gates, the menu state machine, the renderer). What only an integration test can
/// buy is the half that is <b>about ZoneService</b>: that the <c>AddWorldCommand</c> burst leaves
/// synchronously inside the <c>ClientIsReady</c> arm and not on a timer (D178), that a typed
/// command reaches a real backend and changes real server state, that
/// <c>CRANBERRY_CONSOLE=0</c> restores the pre-wave behaviour byte for byte, and that nothing a
/// backend can do takes the listener thread down.
/// </para>
/// <para>
/// <b>Send-side only, as docs/32 requires.</b> A passing assertion here proves what the server
/// emitted and nothing about what the August client draws with it: no console line has ever been
/// on a wire (R3 §2.2, zero <c>09 42</c> in twenty captures). The live recipe in docs/103 §6 is
/// what closes that gap.
/// </para>
/// </summary>
public partial class ConsoleIntegrationTests
{
    [Fact]
    public void GiveMedkitUsesNormalPickupAndAssignsTheMedicalQuickSlot()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            Console = TestConsole with { ModMenuEnabled = false },
        });
        SendClientIsReady(service, connection);
        var session = service.ForVehicleTest(connection);
        session.EnterMatchWithCar();
        Assert.True(session.Exit());
        var movement = (SessionMovementState)connection.Tag!.GetType()
            .GetProperty("Movement")!.GetValue(connection.Tag)!;
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        movement.PinPlayer(new System.Numerics.Vector3(100, 20, 100));
        int before = SentCount(recorder);

        SendExecuteCommand(service, connection, "give", "medkit");

        var medkit = Assert.Single(session.Inventory!.Items.Values, item => item.DefinitionId == 2424);
        Assert.Equal(41u, medkit.LoadoutSlotId);
        Assert.Contains(ConsoleLines(recorder, before), line => line.Contains("gave", StringComparison.OrdinalIgnoreCase));
    }

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

    /// <summary>
    /// The console options every test starts from: the rate limit off, because two console inputs
    /// in the same millisecond are exactly what a test does and what the 50 ms floor exists to
    /// drop; and the refusal log pointed at a path that does not exist, so a real
    /// <c>AdminCommands.log</c> on the machine running the suite can never grey a leaf.
    /// </summary>
    private static ConsoleOptions TestConsole => ConsoleOptions.Default with
    {
        ModMenuEnabled = true,
        SelfFlagOpensConsole = false,
        RateLimitMs = 0,
        ClientLogsPath = Path.Combine(Path.GetTempPath(), "cranberry-console-tests-no-such-directory"),
    };

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder)
        Admit(ZoneOptions options, Action<Action>? post = null, string accountId = "", string? localOwnerAccountId = null)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270, accountId: accountId);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options)
        { Post = post, LocalOwnerAccountId = localOwnerAccountId };
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

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    /// <summary>The bytes the August client sends for a typed line: tunnel, then <c>09 42 00</c>.</summary>
    private static void SendExecuteCommand(
        ZoneService service,
        SoeConnection connection,
        string name,
        string arguments = "")
    {
        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        packet.WriteByte(ZoneOpcodes.CommandBase);
        packet.WriteUInt16(0x0042);
        packet.WriteUInt32(CommandHash.Compute(name));
        packet.WriteString(arguments);
        service.OnMessage(connection, packet.Written.ToArray());
    }

    private static void SendRawHash(ZoneService service, SoeConnection connection, uint hash)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        packet.WriteByte(ZoneOpcodes.CommandBase);
        packet.WriteUInt16(0x0042);
        packet.WriteUInt32(hash);
        packet.WriteString(string.Empty);
        service.OnMessage(connection, packet.Written.ToArray());
    }

    private static byte[][] Sent(RecordingRecorder recorder, int skip = 0) =>
        [.. recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes).Skip(skip)];

    private static int SentCount(RecordingRecorder recorder) =>
        recorder.Messages.Count(m => m.Direction == "s2c");

    /// <summary>Payload of a tunnelled s2c packet: everything after the one-byte gateway header.</summary>
    private static ReadOnlySpan<byte> Payload(byte[] packet) => packet.AsSpan(1);

    private static bool IsAddWorldCommand(byte[] packet) =>
        packet.Length >= 4 && packet[1] == 0x09 && packet[2] == 0x40 && packet[3] == 0x00;

    private static bool IsConsolePrint(byte[] packet) =>
        packet.Length >= 4 && packet[1] == 0x06 && packet[2] == 0x03 && packet[3] == 0x00;

    private static bool IsChatText(byte[] packet) =>
        packet.Length >= 4 && packet[1] == 0x06 && packet[2] == 0x05 && packet[3] == 0x00;

    private static bool IsTextAlert(byte[] packet) =>
        packet.Length >= 4 && packet[1] == 0x11 && packet[2] == 0x31 && packet[3] == 0x00;

    private static bool IsSelfRecord(byte[] packet) =>
        packet.Length >= 2 && packet[1] == SendSelfToClient.Opcode;

    /// <summary>This link's vehicle fleet, or null while the landing burst has not planned one.</summary>
    private static object? Fleet(SoeConnection connection) =>
        connection.Tag!.GetType().GetProperty("Fleet")!.GetValue(connection.Tag!);

    /// <summary>The text of a <c>06 03</c> console line.</summary>
    private static string PrintText(byte[] packet)
    {
        var reader = new PacketReader(Payload(packet));
        reader.ReadByte();
        reader.ReadUInt16();
        return reader.ReadString();
    }

    private static IReadOnlyList<string> ConsoleLines(RecordingRecorder recorder, int skip = 0) =>
        [.. Sent(recorder, skip).Where(IsConsolePrint).Select(PrintText)];

    /// <summary>
    /// Sets a private member of the per-link session. The session type is deliberately nested and
    /// private to <see cref="ZoneService"/>; the alternative to reflection here is a production-only
    /// test hook, which <c>ZoneIntegrationTests</c> already refuses for the same reason.
    /// </summary>
    private static void SetSessionMember(SoeConnection connection, string property, object value)
    {
        object state = connection.Tag!;
        System.Reflection.PropertyInfo? member = state.GetType().GetProperty(property);
        Assert.NotNull(member);
        member!.SetValue(state, value);
    }

    /// <summary>Puts the session in the match step named, without driving 35 s of match flow.</summary>
    private static void EnterStep(SoeConnection connection, string step)
    {
        object state = connection.Tag!;
        System.Reflection.PropertyInfo member = state.GetType().GetProperty("Match")!;
        member.SetValue(state, Enum.Parse(member.PropertyType, step));
    }

    private static ConsoleSession Session(SoeConnection connection) =>
        (ConsoleSession)connection.Tag!.GetType().GetProperty("DevConsole")!.GetValue(connection.Tag!)!;

    // --- the burst ------------------------------------------------------------------------------

    [Fact]
    public void TheNameBurstGoesOutSynchronouslyInsideTheClientIsReadyArm()
    {
        var pending = new ConcurrentQueue<Action>();
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole }, pending.Enqueue);

        int beforeReady = SentCount(recorder);
        Assert.DoesNotContain(Sent(recorder), IsAddWorldCommand);

        SendClientIsReady(service, connection);

        // The whole point of D178: by the time OnMessage returns the burst is already recorded, and
        // nothing was enqueued to make it happen later.
        byte[][] afterReady = Sent(recorder, beforeReady);
        int expected = CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }).Names.Count;
        Assert.Equal(expected, afterReady.Count(IsAddWorldCommand));
        Assert.True(pending.IsEmpty, "the console must not defer its burst");
    }

    [Fact]
    public void TheBurstNamesAreTheRegistrysNamesInRegistrationOrder()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        List<string> names = [];
        foreach (byte[] packet in Sent(recorder).Where(IsAddWorldCommand))
        {
            var reader = new PacketReader(Payload(packet));
            reader.ReadByte();
            reader.ReadUInt16();
            names.Add(reader.ReadString());
        }

        Assert.Equal(CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }).Names, names);
    }

    [Fact]
    public void EachZoneSendsTheBurstAgainAtTheZoningReadyAndOnceDoesNot()
    {
        // "Each zone" is literal: the burst rides the appearance-resync block, which the zone
        // transfer re-opens (EnterMatch clears AppearanceReadySent). A second ClientIsReady in the
        // SAME zone re-sends nothing, which is the behaviour the client wants anyway - a re-sent
        // name is a silent early return in FUN_141280280, not a second alias.
        (ZoneService each, SoeConnection eachLink, RecordingRecorder eachRecorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(each, eachLink);
        int first = Sent(eachRecorder).Count(IsAddWorldCommand);
        Assert.Equal(CommandCatalog.Build(ConsoleOptions.Default with { ModMenuEnabled = true }).Names.Count, first);

        SendClientIsReady(each, eachLink);
        Assert.Equal(first, Sent(eachRecorder).Count(IsAddWorldCommand));

        ArmZoningReady(eachLink);
        SendClientIsReady(each, eachLink);
        Assert.Equal(first * 2, Sent(eachRecorder).Count(IsAddWorldCommand));

        (ZoneService once, SoeConnection onceLink, RecordingRecorder onceRecorder) =
            Admit(new ZoneOptions
            {
                Console = TestConsole with { Register = ConsoleRegisterPolicy.Once },
            });
        SendClientIsReady(once, onceLink);
        int only = Sent(onceRecorder).Count(IsAddWorldCommand);
        ArmZoningReady(onceLink);
        SendClientIsReady(once, onceLink);
        Assert.Equal(only, Sent(onceRecorder).Count(IsAddWorldCommand));
        Assert.Equal(first, only);
    }

    /// <summary>
    /// Puts the session where the zoning <c>ClientIsReady</c> finds it: in the Zoning step with the
    /// appearance resync due again, exactly as <c>EnterMatch</c>'s own 500 ms closure leaves it.
    /// </summary>
    private static void ArmZoningReady(SoeConnection connection)
    {
        EnterStep(connection, "Zoning");
        SetSessionMember(connection, "AppearanceReadySent", false);
    }

    [Fact]
    public void RegisterOffSendsNoNamesAtAll()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions
            {
                Console = TestConsole with { Register = ConsoleRegisterPolicy.Off },
            });
        SendClientIsReady(service, connection);

        Assert.DoesNotContain(Sent(recorder), IsAddWorldCommand);
    }

    [Fact]
    public void TheReadyLineGoesOutOncePerLink()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });

        SendClientIsReady(service, connection);
        SendClientIsReady(service, connection);

        Assert.Equal(1, ConsoleLines(recorder).Count(line => line == ConsoleEngine.ReadyLine));
    }

    // --- the revert -----------------------------------------------------------------------------

    [Fact]
    public void CranberryConsoleZeroLeavesTheBytesUnanswered()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole with { Enabled = false } });

        SendClientIsReady(service, connection);
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "where");

        Assert.DoesNotContain(Sent(recorder), IsAddWorldCommand);
        Assert.DoesNotContain(Sent(recorder, before), IsConsolePrint);
    }

    // --- typed commands -------------------------------------------------------------------------

    [Fact]
    public void ATypedWhereAnswersWithExactlyOneConsoleLine()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "where");

        IReadOnlyList<string> lines = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        string only = Assert.Single(lines);

        // No channel-2 record has arrived in this test, so the honest answer is the "-" line; the
        // point being pinned is that there is exactly one of them and it is never silence.
        Assert.StartsWith("- no position yet", only, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownHashAnswersWithHelpPageOne()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendRawHash(service, connection, CommandHash.Help);

        IReadOnlyList<string> lines = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.NotEmpty(lines);
        Assert.Equal(HelpFormatter.CatchAllNote, lines[0]);
        Assert.Contains(lines, line => line.StartsWith("Cranberry console --", StringComparison.Ordinal));
    }

    [Fact]
    public void AHashNoOneRegisteredIsAnsweredNotDropped()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendRawHash(service, connection, 0xdead_beef);

        string only = Assert.Single([.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)]);
        Assert.StartsWith("? unknown command 0xdeadbeef", only, StringComparison.Ordinal);
    }

    [Fact]
    public void TeleportEmitsOneUpdateLocation()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "tp", "1 2 3");

        byte[][] after = Sent(recorder, before);
        byte[] update = Assert.Single(
            after, p => p.Length >= 4 && p[1] == 0x11 && p[2] == 0x0a && p[3] == 0x00);

        var reader = new PacketReader(Payload(update));
        reader.ReadByte();
        reader.ReadUInt16();
        Assert.Equal(1f, reader.ReadSingle());
        Assert.Equal(2f, reader.ReadSingle());
        Assert.Equal(3f, reader.ReadSingle());

        Assert.Contains(
            after.Where(IsConsolePrint).Select(PrintText),
            line => line.StartsWith("+ teleported to 1.0 2.0 3.0", StringComparison.Ordinal));
    }

    [Fact]
    public void HurtAndHealMoveTheHitpointsPacket()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);
        EnterStep(connection, "InMatch");
        SetSessionMember(connection, "Hitpoints", 10_000u);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "hurt", "100");

        byte[] hurt = Assert.Single(Sent(recorder, before), IsHitpoints);
        Assert.Equal(9_900u, HitpointsValue(hurt));
        byte[] hurtResource = Assert.Single(Sent(recorder, before), IsHealthResource);
        Assert.Equal(9900u, BitConverter.ToUInt32(hurtResource, 23));
        Assert.Equal(10000u, BitConverter.ToUInt32(hurtResource, 27));

        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "heal");
        byte[] heal = Assert.Single(Sent(recorder, before), IsHitpoints);
        Assert.Equal(10_000u, HitpointsValue(heal));
        byte[] healResource = Assert.Single(Sent(recorder, before), IsHealthResource);
        Assert.Equal(10000u, BitConverter.ToUInt32(healResource, 23));
        Assert.Equal(9900u, BitConverter.ToUInt32(healResource, 27));
    }

    [Fact]
    public void GodModeAbsorbsTheDamageAndSendsNoHitpoints()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);
        EnterStep(connection, "InMatch");
        SetSessionMember(connection, "Hitpoints", 10_000u);

        SendExecuteCommand(service, connection, "godmode", "on");
        Assert.True(Session(connection).Invulnerable);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "hurt", "2500");

        Assert.DoesNotContain(Sent(recorder, before), IsHitpoints);
        Assert.DoesNotContain(Sent(recorder, before), IsHealthResource);
        Assert.Contains(
            Sent(recorder, before).Where(IsConsolePrint).Select(PrintText),
            line => line.Contains("god mode absorbed", StringComparison.Ordinal));

        SendExecuteCommand(service, connection, "godmode", "off");
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "hurt", "2500");
        Assert.Single(Sent(recorder, before), IsHitpoints);
    }

    private static bool IsHitpoints(byte[] packet) =>
        packet.Length >= 4 && packet[1] == 0x11 && packet[2] == 0x01 && packet[3] == 0x00;

    private static bool IsHealthResource(byte[] packet) => packet.Length == 102 && packet[1] == 0x8d
        && packet[6] == 3 && BitConverter.ToUInt32(packet, 15) == 1;

    private static uint HitpointsValue(byte[] packet)
    {
        var reader = new PacketReader(Payload(packet));
        reader.ReadByte();
        reader.ReadUInt16();
        return reader.ReadUInt32();
    }

    // --- gates ----------------------------------------------------------------------------------

    [Fact]
    public void APhaseGatedCommandAnswersOneRefusalAndDoesNotThrow()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);

        // /enter in the Menu step: there is no fleet, no position and no vehicle. It must answer
        // one line, and OnMessage must return normally - the round-21 defect was a backend
        // exception disappearing into SoeListener and the console simply going quiet.
        SendExecuteCommand(service, connection, "enter");

        string only = Assert.Single([.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)]);
        Assert.StartsWith("! not now: Menu (needs InMatch)", only, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerCannotUseTheDevelopmentCommandChannel()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole with { LocalIsOwner = false } });
        SendClientIsReady(service, connection);
        EnterStep(connection, "InMatch");

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "kill");

        Assert.Empty(Sent(recorder, before));
    }

    [Fact]
    public void AnUnknownKitListsValidKitsWithoutGrantingAnything()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);
        EnterStep(connection, "InMatch");

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "kit", "ar15");

        string only = Assert.Single([.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)]);
        Assert.StartsWith("? /kit", only, StringComparison.Ordinal);
        Assert.Contains("kits: pvp, guns, ammo, meds, armour, throwables", only, StringComparison.Ordinal);
    }

    // --- the toggle's noise ---------------------------------------------------------------------

    [Fact]
    public void TheConsoleTogglesSpectatePacketIsSwallowed()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);

        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        packet.WriteByte(ZoneOpcodes.CommandBase);
        packet.WriteByte(0x10);
        packet.WriteByte(0x05);
        packet.WriteString("ObserverCamera");
        service.OnMessage(connection, packet.Written.ToArray());

        Assert.Empty(Sent(recorder, before));
    }

    // --- the menu -------------------------------------------------------------------------------

    [Fact]
    public void TheMenuOpensNavigatesAndRunsALeaf()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "m");
        IReadOnlyList<string> root = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.Contains(root, line => line.Contains("CRANBERRY CONSOLE", StringComparison.Ordinal));
        Assert.Contains(root, line => line.Contains("Player", StringComparison.Ordinal));

        // /m 1 walks into Player; the cursor lands on God mode, and /s flips it.
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "m", "1");
        IReadOnlyList<string> player = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.Contains(player, line => line.Contains("root > Player", StringComparison.Ordinal));
        Assert.Contains(player, line => line.Contains("God mode", StringComparison.Ordinal));

        Assert.False(Session(connection).Invulnerable);
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "s");

        // The command runs, the reply is drawn, and the frame under it shows the NEW state - which
        // is only true because MenuState.Apply reports "redraw" and the engine renders afterwards.
        Assert.True(Session(connection).Invulnerable);
        IReadOnlyList<string> flipped = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.Contains(flipped, line => line.StartsWith("+ God mode [ON]", StringComparison.Ordinal));
        Assert.Contains(flipped, line => line.Contains("[ON]", StringComparison.Ordinal) && line.StartsWith("|", StringComparison.Ordinal));

        // /q closes and prints the status ticker of the toggles that are on.
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "q");
        Assert.Contains(
            Sent(recorder, before).Where(IsConsolePrint).Select(PrintText),
            line => line.StartsWith("* GOD", StringComparison.Ordinal));
    }

    [Fact]
    public void AMenuJumpRunsALeafWithoutOpeningTheMenu()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "m", "player godmode on");

        IReadOnlyList<string> lines = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.True(Session(connection).Invulnerable);
        Assert.Contains(lines, line => line.StartsWith("+ God mode [ON]", StringComparison.Ordinal));
    }

    [Fact]
    public void ANoLongerReachableMenuKeyAnswersRatherThanGoingQuiet()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "d");

        string only = Assert.Single([.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)]);
        Assert.Contains("menu", only, StringComparison.OrdinalIgnoreCase);
    }

    // --- surfaces -------------------------------------------------------------------------------

    [Fact]
    public void TheSurfaceSwitchChangesTheReplyBytes()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        SendExecuteCommand(service, connection, "surface", "chat");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "where");

        byte[] line = Assert.Single(Sent(recorder, before), IsChatText);
        Assert.DoesNotContain(Sent(recorder, before), IsConsolePrint);
        Assert.Equal(1, line[^1]);

        SendExecuteCommand(service, connection, "surface", "chat0");
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "where");
        Assert.Equal(0, Assert.Single(Sent(recorder, before), IsChatText)[^1]);
    }

    [Fact]
    public void TheSurfaceProbeTouchesAllFourCarriers()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "surface", "probe");

        byte[][] after = Sent(recorder, before);
        IReadOnlyList<byte[]> chat = [.. after.Where(IsChatText)];
        Assert.Equal(2, chat.Count);
        Assert.Equal(1, chat[0][^1]);
        Assert.Equal(0, chat[1][^1]);
        Assert.Single(after, IsTextAlert);

        // Two 06 03: the [print] probe, and the summary line on the session's own surface, which is
        // print until /surface says otherwise. A deliberate deviation from design §5.5's "one" -
        // the summary is what tells the owner which surface he is reading when only one arrives.
        IReadOnlyList<string> prints = [.. after.Where(IsConsolePrint).Select(PrintText)];
        Assert.Equal(2, prints.Count);
        Assert.StartsWith("[print] Cranberry probe 1/4", prints[0], StringComparison.Ordinal);
        Assert.StartsWith("* probe sent on print, chat1, chat0, alert", prints[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnnounceIsOneBanner()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "announce", "Hello There");

        byte[] banner = Assert.Single(Sent(recorder, before), IsTextAlert);
        var reader = new PacketReader(Payload(banner));
        reader.ReadByte();
        reader.ReadUInt16();

        // KeepCase: the owner's own lesson - a banner must not be shouted back in lower case.
        Assert.Equal("Hello There", reader.ReadString());
    }

    // --- the surfaces the world reaches ---------------------------------------------------------

    [Fact]
    public void InfoAndMatchStatusAnswerWithoutAMatch()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "info");
        IReadOnlyList<string> info = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.Contains(info, line => line.Contains("CRANBERRY", StringComparison.Ordinal));
        Assert.Contains(info, line => line.Contains("1148 / 0.0.118.208059", StringComparison.Ordinal));

        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "match", "status");
        IReadOnlyList<string> status = [.. Sent(recorder, before).Where(IsConsolePrint).Select(PrintText)];
        Assert.Contains(status, line => line.Contains("gas not running", StringComparison.Ordinal));
    }

    [Fact]
    public void MatchStartFromTheMenuStepZonesIn()
    {
        var pending = new ConcurrentQueue<Action>();
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole }, pending.Enqueue);
        SendClientIsReady(service, connection);

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "startmatch");

        Assert.Contains(
            Sent(recorder, before).Where(IsConsolePrint).Select(PrintText),
            line => line.StartsWith("+ match starting", StringComparison.Ordinal));

        // EnterMatch arms its own 500 ms closure before it zones, exactly as the PLAY button's
        // path does; running it is the proof the console reached the real match flow and not a stub.
        Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000), "EnterMatch never deferred its zoning");
        Assert.True(pending.TryDequeue(out Action? zoning));
        zoning!();

        // ClientBeginZoning (0x0b) is the first packet of the zoning burst.
        Assert.Contains(Sent(recorder, before), packet => packet.Length >= 2 && packet[1] == 0x0b);
    }

    // --- vehicles (review-1 M2) -----------------------------------------------------------------

    [Fact]
    public void CarWithoutAPlayerPositionDoesNotCreateTheParkingPlan()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);

        // Commands must validate the position before constructing any fleet.
        EnterStep(connection, "InMatch");

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "car", "atv");

        string only = Assert.Single(ConsoleLines(recorder, before));
        Assert.StartsWith("- no position yet", only, StringComparison.Ordinal);
        Assert.Null(Fleet(connection));
    }

    [Fact]
    public void CarStillOwnsTheFleetWhenNoLandingBurstWillEverPlanOne()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole, SendVehicles = false });
        SendClientIsReady(service, connection);
        EnterStep(connection, "InMatch");

        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "car", "atv");

        // With the burst switched off there is no plan to lose, so the guard stands aside and the
        // command reaches its next real requirement - a channel-2 position this test never sends.
        string only = Assert.Single(ConsoleLines(recorder, before));
        Assert.StartsWith("- no position yet", only, StringComparison.Ordinal);
    }

    // --- /win: Ui.ExecuteScript through the real service (R6 recommendation (a)) -----------------

    private static bool IsUiExecuteScript(byte[] packet) =>
        packet.Length >= 3 && packet[1] == 0x1A && packet[2] == 0x07;

    [Fact]
    public void ATypedWinPutsExactlyOneUiExecuteScriptOnChannelZero()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);
        int before = SentCount(recorder);

        SendExecuteCommand(service, connection, "win", "inventory");

        byte[] packet = Assert.Single(Sent(recorder, before), IsUiExecuteScript);

        // 1a 07 | u32 24 | "HudHandler.ShowInventory" | u32 0 - 34 bytes, no third header byte.
        Assert.Equal(
            Convert.FromHexString(
                "1A07" + "18000000" + "48756448616E646C65722E53686F77496E76656E746F7279" + "00000000"),
            Payload(packet).ToArray());
        Assert.Equal(34, Payload(packet).Length);

        string line = Assert.Single(ConsoleLines(recorder, before));
        Assert.Equal(
            "+ sent Ui.ExecuteScript HudHandler.ShowInventory (34 bytes) - "
            + "the client does not acknowledge; watch the screen",
            line);
    }

    [Fact]
    public void WinRawRefusesTheNamesThatWouldEndTheSession()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole });
        SendClientIsReady(service, connection);
        Session(connection).Settings = Session(connection).Settings with { Confirms = false };
        int before = SentCount(recorder);

        SendExecuteCommand(service, connection, "win", "raw Ui.Logout");
        SendExecuteCommand(service, connection, "win", "raw Ui.Quit");

        Assert.DoesNotContain(Sent(recorder, before), IsUiExecuteScript);
        Assert.All(ConsoleLines(recorder, before), l => Assert.StartsWith("! refused --", l, StringComparison.Ordinal));
    }

    [Fact]
    public void WinProbeSendsItsFirstStepNowAndDefersTheRest()
    {
        var pending = new ConcurrentQueue<Action>();
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder) =
            Admit(new ZoneOptions { Console = TestConsole }, pending.Enqueue);
        SendClientIsReady(service, connection);
        int before = SentCount(recorder);

        SendExecuteCommand(service, connection, "win", "probe");

        // Only the positive control is on the wire when OnMessage returns; the other three are on
        // the server's own Later, which is why the owner can tell the four sends apart.
        byte[] first = Assert.Single(Sent(recorder, before), IsUiExecuteScript);
        Assert.Equal(
            Convert.FromHexString(
                "1A07" + "1C000000" + "47616D654576656E74732E4F6E496E76656E746F7279546F67676C65" + "00000000"),
            Payload(first).ToArray());
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("1/4", StringComparison.Ordinal));
    }

    // --- Door A (review-1 M1) -------------------------------------------------------------------

    [Fact]
    public void TheSelfFlagSwitchFlipsExactlyOneByteOfTheSelfRecord()
    {
        (_, _, RecordingRecorder off) = Admit(new ZoneOptions { Console = TestConsole });
        (_, _, RecordingRecorder on) = Admit(new ZoneOptions
        {
            Console = TestConsole with { SelfFlagOpensConsole = true },
        });

        byte[] without = Assert.Single(Sent(off), IsSelfRecord);
        byte[] with = Assert.Single(Sent(on), IsSelfRecord);

        // One bool inside a fixed layout: the record the login depends on is the same length and
        // the same bytes but for FlagI at +0x106b9. That is the whole of Door A on the wire.
        Assert.Equal(without.Length, with.Length);
        int at = Assert.Single([.. Enumerable.Range(0, with.Length).Where(i => with[i] != without[i])]);
        Assert.Equal(0, without[at]);
        Assert.Equal(1, with[at]);
    }

    [Fact]
    public void TheSelfFlagStaysOffForALinkThatIsNotOwner()
    {
        (_, _, RecordingRecorder off) = Admit(new ZoneOptions { Console = TestConsole });
        (_, _, RecordingRecorder tester) = Admit(new ZoneOptions
        {
            Console = TestConsole with { SelfFlagOpensConsole = true, LocalIsOwner = false },
        });

        Assert.Equal(
            Assert.Single(Sent(off), IsSelfRecord),
            Assert.Single(Sent(tester), IsSelfRecord));
    }
}
