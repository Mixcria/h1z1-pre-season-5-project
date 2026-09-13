using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone;

/// <summary>
/// docs/105 §4 — the menu camera burst against a fake session: one <c>E9 01 00 str name</c> in,
/// exactly two packets out, in the order the 2026-08-22 admin capture sends them
/// (<c>UpdateLocation</c> first, then <c>StaticViewReply</c> is the capture's order; Cranberry
/// keeps its own reply-then-move order, which the client accepted live on 2026-08-29 — see
/// docs/105 §4 for why the difference is recorded rather than "fixed").
/// </summary>
public sealed class MenuViewSessionTests
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

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder) Admit(
        ZoneOptions options)
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

    private static byte[][] SendZone(
        ZoneService service, SoeConnection connection, RecordingRecorder recorder, byte[] body)
    {
        int before = recorder.Messages.Count(m => m.Direction == "s2c");
        using var request = new PacketWriter();
        request.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        foreach (byte b in body)
        {
            request.WriteByte(b);
        }

        service.OnMessage(connection, request.Written.ToArray());

        return [.. recorder.Messages
            .Where(m => m.Direction == "s2c")
            .Skip(before)
            .Select(m => m.Bytes)];
    }

    private static byte[][] RequestView(
        ZoneService service, SoeConnection connection, RecordingRecorder recorder, string view)
    {
        int before = recorder.Messages.Count(m => m.Direction == "s2c");
        using var request = new PacketWriter();
        request.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        request.WriteByte(ZoneOpcodes.StaticViewBase);
        request.WriteUInt16(1);
        request.WriteString(view);
        service.OnMessage(connection, request.Written.ToArray());

        return [.. recorder.Messages
            .Where(m => m.Direction == "s2c")
            .Skip(before)
            .Select(m => m.Bytes)];
    }

    [Fact]
    public void EveryMenuScreenMovesTheSubjectBeforeTheCameraReadsItsRotation()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions());

        // The order the August client itself walked on 2026-09-02 (host-20260902-212957.log :103,
        // :117), extended through the CHARACTER tree the retail footage shows.
        string[] walk =
        [
            "kotkdefault", "kotkgamemodes", "kotkcharacter", "kotkappearance",
            "kotkappearancegear", "kotkappearancegearhead", "kotkappearancegearchest",
            "kotkappearanceweapons", "kotkappearancevehiclesatv", "kotkappearanceemotes",
            "kotkcrates", "kotkstats", "kotkgrinder", "kotkdefault",
        ];

        foreach (string view in walk)
        {
            byte[][] sent = RequestView(service, connection, recorder, view);

            // docs/105 §9 (U-1): three now, not two — the capture's stance-0 follows the mark.
            Assert.Equal(3, sent.Length);

            // 1. the camera (e9 02 00, 52 bytes) — on the gateway tunnel, channel 0.
            Assert.Equal(0x05, sent[1][0]);
            Assert.Equal(ZoneOpcodes.StaticViewBase, sent[1][1]);
            Assert.Equal(2, sent[1][2]);
            Assert.Equal(1 + 52, sent[1].Length);

            // 2. the subject mark (11 0a 00, 38 bytes).
            Assert.Equal(0x05, sent[0][0]);
            Assert.Equal(ZoneOpcodes.ClientUpdateBase, sent[0][1]);
            Assert.Equal(0x0a, sent[0][2]);
            Assert.Equal(1 + 38, sent[0].Length);
            Assert.Equal("010000", Convert.ToHexString(sent[0].AsSpan(36)).ToLowerInvariant());

            // 3. the pose (0f 20, 14 bytes, stance 0) — the capture's 35-for-35 companion.
            Assert.Equal(0x05, sent[2][0]);
            Assert.Equal(WeaponStance.Opcode, sent[2][1]);
            Assert.Equal(WeaponStance.SubOpcode, sent[2][2]);
            Assert.Equal(1 + WeaponStance.Length, sent[2].Length);
            Assert.True(WeaponStance.TryParse(sent[2].AsSpan(1), out WeaponStance? stance));
            Assert.Equal(0u, stance!.Stance);
            Assert.Equal(0x1001ul, stance.CharacterGuid);
        }
    }

    [Fact]
    public void TheStanceSwitchTakesTheThirdPacketBackOff()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            MenuActor = MenuActorOptions.Default with { SendWeaponStanceOnViewChange = false },
        });

        Assert.Equal(2, RequestView(service, connection, recorder, "kotkdefault").Length);
        Assert.Equal(2, RequestView(service, connection, recorder, "kotkcharacter").Length);
    }

    [Fact]
    public void AnInheritedScreenGetsNoStanceEither()
    {
        // The capture answered all 35 of its requests, so it says nothing about an UNanswered view.
        // Cranberry ties the stance to the reply rather than to the request, which is the only
        // shape the evidence supports.
        var (service, connection, recorder) = Admit(new ZoneOptions());

        Assert.Empty(RequestView(service, connection, recorder, "kotksettings"));
    }

    [Fact]
    public void TheClientsOwnFreeInteractionNpcIsEchoedBackOneForOne()
    {
        // docs/105 §9 (U-1, second half). 35 c2s / 35 s2c in the capture's menu session and
        // 4 c2s / 4 s2c in its match session — one reply per request, identical three bytes.
        var (service, connection, recorder) = Admit(new ZoneOptions());

        for (int i = 0; i < 3; i++)
        {
            byte[][] sent = SendZone(service, connection, recorder, [0x09, 0x16, 0x00]);
            byte[] echo = Assert.Single(sent);
            Assert.Equal("05091600", Convert.ToHexString(echo).ToLowerInvariant());
        }
    }

    [Fact]
    public void TheEchoSwitchSilencesIt()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            MenuActor = MenuActorOptions.Default with { EchoFreeInteractionNpc = false },
        });

        Assert.Empty(SendZone(service, connection, recorder, [0x09, 0x16, 0x00]));
    }

    /// <summary>
    /// docs/105 §9 (U-5 and U-6). The self record in the login burst must carry the capture's own
    /// <c>kotkdefault</c> mark and an identity rotation — the two 16-byte words the friend server
    /// writes at blob offsets 136 and 152 of its own <c>SendSelfToClient</c>, except that the
    /// friend's position there is his staging spot (650.98, 148.47, −1611.41) while Cranberry
    /// spawns the actor where the first <c>UpdateLocation</c> is going to put it anyway.
    /// </summary>
    [Theory]
    // (−31.53, 506.42, 279.97, 1) then (0, 0, 0, 1): the mark from
    // `11 0a 00 713dfcc1 c335fd43 29fc8b43 0000803f …`, the capture's kotkdefault UpdateLocation.
    [InlineData(false, "713DFCC1C335FD4329FC8B430000803F0000000000000000000000000000803F")]
    // CRANBERRY_MENU_SPAWN_MARK=legacy: the pre-2026-09-03 Z of 279.72, 25 cm low.
    [InlineData(true, "713DFCC1C335FD4329DC8B430000803F0000000000000000000000000000803F")]
    public void TheSelfRecordSpawnsOnTheCapturesMarkWithIdentityRotation(bool legacy, string expected)
    {
        MenuActorOptions actor = legacy
            ? MenuActorOptions.Default with { SpawnPosition = MenuActorOptions.LegacyMenuMark }
            : MenuActorOptions.Default;
        var (_, _, recorder) = Admit(new ZoneOptions
        {
            MenuActor = actor,
            SpawnPosition = actor.SpawnPosition,
        });

        byte[] self = Assert.Single(recorder.Messages
            .Where(m => m.Direction == "s2c"
                && m.Bytes.Length > 2
                && m.Bytes[0] == 0x05
                && m.Bytes[1] == ZoneOpcodes.SendSelfToClient)
            .Select(m => m.Bytes));

        Assert.Contains(expected, Convert.ToHexString(self), StringComparison.Ordinal);
    }

    [Fact]
    public void TheScreensTheTableInheritsProduceNoBytesAtAll()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions());

        foreach (string view in MenuViewTable.InheritNames.Append("kotknosuchview"))
        {
            Assert.Empty(RequestView(service, connection, recorder, view));
        }
    }

    [Fact]
    public void ReferenceCoverageAnswersOnlyTheTwoPeriodShotsOnTheWire()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            MenuViews = MenuViewOptions.Default with { Coverage = MenuViewCoverage.Reference },
        });

        Assert.Equal(3, RequestView(service, connection, recorder, "kotkdefault").Length);
        Assert.Equal(3, RequestView(service, connection, recorder, "kotkgamemodes").Length);
        Assert.Empty(RequestView(service, connection, recorder, "kotkcharacter"));
        Assert.Empty(RequestView(service, connection, recorder, "kotkappearancegear"));
    }

    [Fact]
    public void TheOffSwitchSilencesEvenTheTwoPeriodShots()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            MenuViews = MenuViewOptions.Default with { Coverage = MenuViewCoverage.Off },
        });

        Assert.Empty(RequestView(service, connection, recorder, "kotkdefault"));
        Assert.Empty(RequestView(service, connection, recorder, "kotkcharacter"));
    }

    [Fact]
    public void TheMenuCameraTableAddsNothingToTheLoginBurst()
    {
        // Regression guard: the login burst is the owner's only confirmed path to PLAY and it stays
        // at fourteen packets, including the KOTK environment update, whatever this table holds.
        // ZoneBootstrapTests pins the same number; this asserts the option cannot move it.
        foreach (MenuViewCoverage coverage in Enum.GetValues<MenuViewCoverage>())
        {
            var (_, _, recorder) = Admit(new ZoneOptions
            {
                MenuViews = MenuViewOptions.Default with { Coverage = coverage },
            });
            Assert.Equal(14, recorder.Messages.Count(m => m.Direction == "s2c"));
        }
    }
}
