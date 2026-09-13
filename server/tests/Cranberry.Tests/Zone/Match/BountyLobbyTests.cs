using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Match;

// MatchLobby, not Match: a test namespace called Match would shadow the Cranberry.Zone.World.Match
// TYPE inside Cranberry.Tests.Zone.World, which is why MatchDrop and MatchEndgame are named that way.
namespace Cranberry.Tests.Zone.MatchLobby;

/// <summary>
/// docs/113 - the pre-game lobby at Fort Destiny and the Bounty screen it exists for.
///
/// <para>
/// Three kinds of test, in this order: the byte-exact writers and the one reader (the layouts are
/// the August client's own parsers, so a wrong byte here is a wrong byte on the wire); the option
/// arithmetic (which banners fit inside which lobby); and one fake-session walk from arrival to
/// StartMatch that proves the ORDER - currency, then the payout tables, then the labelled
/// countdown, then the banners - and that with the drop-open suppression on the whole bounty burst
/// lands in the lobby with nothing bounty-shaped riding the drop (docs/113 addendum, D254 revised).
/// </para>
/// <para><b>Send-side only, per D29:</b> this proves what the server sent, never what the client did.</para>
/// </summary>
public sealed class BountyLobbyTests
{
    private static string Hex(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return Convert.ToHexString(writer.Written).ToLowerInvariant();
    }

    // -----------------------------------------------------------------------------------------
    // 67 0d MatchBounty - FUN_1413e9db0 / FUN_141655cf0
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheBountyStateIsTenBytesAndCarriesTheAmountThenTheType()
    {
        Assert.Equal(
            "670d" + "00000000" + "00000000",
            Hex(writer => MatchBountyState.None.WriteTo(writer)));
        Assert.Equal(
            "670d" + "f4010000" + "04000000",
            Hex(writer => new MatchBountyState(500u, 4u).WriteTo(writer)));
        Assert.Equal(
            MatchBountyState.Length,
            Hex(writer => MatchBountyState.None.WriteTo(writer)).Length / 2);
    }

    // -----------------------------------------------------------------------------------------
    // 67 0e MatchBountyTables - FUN_1413eafe0 / FUN_140007d9c / FUN_1416576d0
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheBountyTablesPacketIsHeaderThenTwoCountedBlocks()
    {
        var tables = new MatchBountyTables(
            PlayerCount: 1u,
            BountyCount: 0u,
            Skulls: new BountyPayoutTable([500u, 300u]),
            Credits: new BountyPayoutTable([100u]));

        Assert.Equal(
            "670e"                                   // base, u8 sub
                + "01000000" + "00000000"            // PlayerCount, BountyCount
                + "00000000" + "00000000"            // skulls block: the two words the handler never reads
                + "02000000" + "f4010000" + "2c010000"
                + "00000000" + "00000000"            // credits block
                + "01000000" + "64000000",
            Hex(writer => tables.WriteTo(writer)));

        // 10 header + (12 + 8) + (12 + 4)
        Assert.Equal(46, tables.Length);
        Assert.Equal(tables.Length, Hex(writer => tables.WriteTo(writer)).Length / 2);
    }

    [Fact]
    public void AnEmptyPayoutTableIsTwelveBytesOfZeroes()
    {
        Assert.Equal(12, BountyPayoutTable.Empty.Length);
        Assert.Equal(
            "000000000000000000000000",
            Hex(writer => BountyPayoutTable.Empty.WriteTo(writer)));
    }

    [Fact]
    public void TheShippedLaddersAreTenPlacesEachAndTheCreditsTopIsTheClientsOwnFreeCurrencyCap()
    {
        Assert.Equal(10, BountyOptions.DesignSkullPayouts.Count);
        Assert.Equal(10, BountyOptions.DesignCreditPayouts.Count);

        // A ladder must never pay a lower place more than a higher one.
        Assert.Equal(
            [.. BountyOptions.DesignSkullPayouts.OrderByDescending(x => x)],
            BountyOptions.DesignSkullPayouts);
        Assert.Equal(
            [.. BountyOptions.DesignCreditPayouts.OrderByDescending(x => x)],
            BountyOptions.DesignCreditPayouts);

        // S4 row M11: the client's own Match.MaxFreeCurrency is 100.
        Assert.Equal(100u, BountyOptions.DesignCreditPayouts[0]);
    }

    // -----------------------------------------------------------------------------------------
    // 67 0c SelectBounty (c2s) - the needle that settled AUDIT-bounty U-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheAnteRequestIsSixBytesAndIsSubZeroCNotThirteenOrOneD()
    {
        Assert.Equal(0x0c, SelectBountyRequest.SubOpcode);
        Assert.Equal(6, SelectBountyRequest.Length);
        Assert.Equal("670c04000000", Hex(writer => new SelectBountyRequest(4u).WriteTo(writer)));
    }

    [Fact]
    public void TheAnteRequestRoundTripsThroughItsOwnReader()
    {
        byte[] wire = Convert.FromHexString("670c06000000");
        Assert.True(SelectBountyRequest.TryParse(wire, out SelectBountyRequest? request));
        Assert.NotNull(request);
        Assert.Equal(6u, request!.BountyType);
    }

    [Theory]
    [InlineData("670c0400")]        // short
    [InlineData("670d04000000")]    // the s2c sub, not this one
    [InlineData("671304000000")]    // AUDIT-bounty's first candidate
    [InlineData("671d04000000")]    // AUDIT-bounty's second candidate
    public void AnythingThatIsNotSixSevenZeroCIsRefusedRatherThanGuessedAt(string hex)
    {
        Assert.False(SelectBountyRequest.TryParse(Convert.FromHexString(hex), out SelectBountyRequest? request));
        Assert.Null(request);
    }

    // -----------------------------------------------------------------------------------------
    // ce 14 - the "Match starts in N seconds." banner
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheBannerIsTwoLocaleKeysThenTheCountAndIsThirtySevenBytesWithTheRetailPair()
    {
        string hex = Hex(writer => GameModeHud.WriteMatchStartBanner(writer, 60u));

        Assert.Equal(
            "ce1400"
                + "0d000000" + Convert.ToHexString("BR.MatchStart"u8).ToLowerInvariant()
                + "09000000" + Convert.ToHexString("BR.Second"u8).ToLowerInvariant()
                + "3c000000",
            hex);

        // Z1's own cf 14 writer calls this trio 37 bytes; the August base is 0xce and nothing else
        // moved (the -1 base shift of the 1087 -> 1148 bridge).
        Assert.Equal(37, hex.Length / 2);
    }

    [Fact]
    public void TheBannerKeysAreTheClientsOwnCodeStringMappingNames()
    {
        Assert.Equal("BR.MatchStart", GameModeHud.MatchStartBannerKey);
        Assert.Equal("BR.Second", GameModeHud.SecondBannerKey);
    }

    // -----------------------------------------------------------------------------------------
    // The lobby table
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheShippedLobbyIsTheZ1RetailRow()
    {
        LobbyOptions lobby = LobbyOptions.Default;
        Assert.Equal(120_000u, lobby.CountdownMs);
        Assert.Equal(1, lobby.MinPlayers);
        Assert.Equal([60u, 30u, 10u], lobby.BannerSeconds);
        Assert.True(lobby.Banners);
        Assert.True(lobby.Labels);

        // D249: arrival, not a blind timer - with the pre-D249 number kept as the safety net.
        Assert.Equal(0, lobby.ArmMs);
        Assert.Equal(15_000, lobby.FallbackArmMs);
    }

    [Fact]
    public void AShortLobbyFiresOnlyTheBannersThatFitInsideIt()
    {
        Assert.Equal(
            [60u, 30u, 10u],
            LobbyOptions.Default.ApplicableBannerSeconds(120_000u));

        // The exact failure Z1's own comment names: a 6 s lobby must not announce 60 and 30 in the
        // same tick.
        Assert.Empty(LobbyOptions.Default.ApplicableBannerSeconds(6_000u));
        Assert.Equal([10u], LobbyOptions.Default.ApplicableBannerSeconds(20_000u));

        // A banner exactly as long as the lobby would fire at t = 0, which is not a countdown.
        Assert.Empty(LobbyOptions.Default.ApplicableBannerSeconds(10_000u));
    }

    [Fact]
    public void TurningTheBannersOffArmsNone()
    {
        LobbyOptions off = LobbyOptions.Default with { Banners = false };
        Assert.Empty(off.ApplicableBannerSeconds(120_000u));
    }

    [Fact]
    public void TheLabelsAreTheGeneratedOnesAndNotTypedHere()
    {
        Assert.Equal(13198u, AugustStrings.HudLabels.WaitingForPlayers);
        Assert.Equal(13356u, AugustStrings.HudLabels.StartingMatch);
        Assert.Equal("Waiting for players...", AugustStrings.HudLabels.WaitingForPlayersText);
    }

    // -----------------------------------------------------------------------------------------
    // The currency rows
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheTopBarNowCarriesFourCurrenciesAndCreditsIsIdSix()
    {
        SetAccountCurrencyRecord[] rows = [.. MenuTopBarOptions.Default.CurrencyRecords()];
        Assert.Equal(4, rows.Length);
        Assert.Equal(
            [
                SetAccountCurrencyRecord.Scrap,
                SetAccountCurrencyRecord.Crowns,
                SetAccountCurrencyRecord.Skulls,
                SetAccountCurrencyRecord.Credits,
            ],
            rows.Select(row => row.CurrencyId));
        Assert.Equal(6u, SetAccountCurrencyRecord.Credits);

        // D255: a fresh account is zero on every one of them.
        Assert.All(rows, row => Assert.Equal(0u, row.Amount));
    }

    [Fact]
    public void TheThreeBalanceLeversMoveTheRowsTheBountyScreenSpends()
    {
        MenuTopBarOptions options = MenuTopBarOptions.FromEnvironment(name => name switch
        {
            MenuTopBarOptions.CrownsVariable => "2500",
            MenuTopBarOptions.SkullsVariable => "1000",
            MenuTopBarOptions.CreditsVariable => "500",
            _ => null,
        });

        Assert.Equal(2500u, options.Crowns);
        Assert.Equal(1000u, options.Skulls);
        Assert.Equal(500u, options.Credits);
    }

    // -----------------------------------------------------------------------------------------
    // The ante table
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void EveryShippedAnteNamesACurrencyTheTopBarSends()
    {
        foreach (BountyAnte ante in BountyOptions.Default.Antes)
        {
            if (ante.CurrencyId == 0)
            {
                Assert.Equal(0u, ante.Amount);
                continue;
            }

            Assert.Contains(
                ante.CurrencyId,
                MenuTopBarOptions.Default.CurrencyRecords().Select(row => row.CurrencyId));
            Assert.True(ante.Amount > 0);
        }
    }

    [Fact]
    public void AnUnmappedAnteTypeIsRefused()
    {
        Assert.False(BountyOptions.Default.TryGetAnte(0xdead_beef, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => BountyOptions.Default.AnteFor(0xdead_beef));
    }

    // -----------------------------------------------------------------------------------------
    // The fake-session walk: arrival -> currency + 67 0e/0d + labels -> banners -> StartMatch
    // -----------------------------------------------------------------------------------------

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

    private const ulong Self = 0x1001;

    private static ZoneOptions Fast() => new()
    {
        // No PLAY click, and a lobby short enough that one banner step still fits.
        AutoMatchMs = 1,
        LobbyCountdownMs = 20_000,
        Lobby = LobbyOptions.Default,
        MenuTopBar = MenuTopBarOptions.Default with { Crowns = 2_000u, Credits = 250u },

        // Everything the walk does not grade, off.
        EnableGas = false,
        SendDoors = false,
        SendVehicles = false,
        SendContainers = false,
        GroundLootRadius = 0f,
        DevGroundLootMs = 0,
    };

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending) World(ZoneOptions options)
    {
        var pending = new ConcurrentQueue<Action>();
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            Self, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options)
        {
            Post = pending.Enqueue,
        };

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
        return (service, connection, recorder, pending);
    }

    private static void FromClient(ZoneService service, SoeConnection connection, Action<PacketWriter> body)
    {
        using var packet = new PacketWriter();
        packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        body(packet);
        service.OnMessage(connection, packet.Written.ToArray());
    }

    private static void ClientIsReady(ZoneService service, SoeConnection connection) =>
        FromClient(service, connection, w => w.WriteByte(ZoneOpcodes.ClientIsReady));

    private static void ClientFinishedLoading(ZoneService service, SoeConnection connection) =>
        FromClient(service, connection, w =>
        {
            w.WriteByte(ZoneOpcodes.ClientFinishedLoading);
            w.WriteByte(0);
        });

    private static void SelectBounty(ZoneService service, SoeConnection connection, uint type) =>
        FromClient(service, connection, w => new SelectBountyRequest(type).WriteTo(w));

    private static byte[][] Sent(RecordingRecorder recorder) =>
        [.. recorder.Messages.Where(m => m.Direction == "s2c").Select(m => m.Bytes)];

    /// <summary>
    /// Only what the server sent AFTER the match-zoning burst. The LoginZone menu sends its own
    /// <c>ab 03</c> rows (docs/105 §10) and they would otherwise be counted as the lobby's.
    /// </summary>
    private static byte[][] SentInTheMatch(RecordingRecorder recorder)
    {
        byte[][] all = Sent(recorder);
        int zoning = Array.FindLastIndex(all, IsClientBeginZoning);
        Assert.True(zoning >= 0, "the match-zoning burst was never sent");
        return [.. all.Skip(zoning)];
    }

    /// <summary>The zone payload of a tunnelled s2c packet (the gateway header byte removed).</summary>
    private static ReadOnlySpan<byte> Body(byte[] packet) => packet.AsSpan(1);

    private static bool Is(byte[] packet, byte opcode, byte sub) =>
        packet.Length >= 3 && packet[1] == opcode && packet[2] == sub;

    private static bool IsCountdown(byte[] packet) =>
        packet.Length >= 4 && packet[1] == GameModeHud.Opcode
            && BitConverter.ToUInt16(packet, 2) == 0x000f;

    private static bool IsBanner(byte[] packet) =>
        packet.Length >= 4 && packet[1] == GameModeHud.Opcode
            && BitConverter.ToUInt16(packet, 2) == 0x0014;

    private static bool IsStartMatch(byte[] packet) =>
        packet.Length >= 4 && packet[1] == GameModeHud.Opcode
            && BitConverter.ToUInt16(packet, 2) == 0x0016;

    private static bool IsClientBeginZoning(byte[] packet) =>
        packet.Length >= 2 && packet[1] == ZoneOpcodes.ClientBeginZoning;

    private static void PumpUntil(ConcurrentQueue<Action> pending, Func<bool> done, string what)
    {
        for (int guard = 0; guard < 200 && !done(); guard++)
        {
            if (!SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000))
            {
                break;
            }

            while (pending.TryDequeue(out Action? work))
            {
                work!();
            }
        }

        Assert.True(done(), $"{what} never happened");
    }

    /// <summary>Login, auto-match, the zoning burst, and the client's own arrival packets.</summary>
    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending) ArriveAtFortDestiny(ZoneOptions options)
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World(options);

        ClientIsReady(service, connection);
        PumpUntil(pending, () => Sent(recorder).Any(IsClientBeginZoning), "the zoning burst");

        // Arrival: the client's second ClientIsReady, then the packet that says the Fort Destiny
        // world is built. D249 makes the SECOND one the lobby's arm.
        ClientIsReady(service, connection);
        ClientFinishedLoading(service, connection);
        return (service, connection, recorder, pending);
    }

    [Fact]
    public void ArrivalAtFortDestinySendsTheCurrencyThenTheBountyTablesThenTheLabelledCountdown()
    {
        (_, _, RecordingRecorder recorder, _) = ArriveAtFortDestiny(Fast());

        byte[][] sent = SentInTheMatch(recorder);
        int firstCurrency = Array.FindIndex(
            sent, p => Is(p, SetAccountCurrencyRecord.Opcode, SetAccountCurrencyRecord.SubOpcode));
        int tables = Array.FindIndex(sent, p => Is(p, MatchBountyTables.Opcode, MatchBountyTables.SubOpcode));
        int state = Array.FindIndex(sent, p => Is(p, MatchBountyState.Opcode, MatchBountyState.SubOpcode));
        int countdown = Array.FindIndex(sent, IsCountdown);

        Assert.True(countdown >= 0, "no ce 0f countdown was sent at arrival");
        Assert.True(firstCurrency >= 0 && firstCurrency < countdown, "balances must arrive before the lobby countdown");
        Assert.True(tables > firstCurrency, "67 0e must follow the balances it is spent from");
        Assert.True(state > tables, "67 0d must be the last thing written into MatchBounty");
        Assert.True(countdown > state, "the complete backing offer must precede the countdown");
        Assert.Contains(sent.Take(countdown), p => Body(p).SequenceEqual(new byte[] { 0xce, 0x15, 0, 1 }));

        // Four balance rows, Credits (6) among them.
        byte[][] currency =
            [.. sent.Where(p => Is(p, SetAccountCurrencyRecord.Opcode, SetAccountCurrencyRecord.SubOpcode))];
        Assert.Equal(4, currency.Length);
        Assert.Contains(currency, p => BitConverter.ToUInt32(Body(p)[2..6]) == SetAccountCurrencyRecord.Credits);

        // The countdown is labelled - at or above the minimum population, with 13356.
        Assert.Equal(
            AugustStrings.HudLabels.StartingMatch,
            BitConverter.ToUInt32(Body(sent[countdown])[11..15]));
    }

    [Fact]
    public void TheLobbyRunsItsBannersAndThenStartsTheMatch()
    {
        (_, _, RecordingRecorder recorder, ConcurrentQueue<Action> pending) =
            ArriveAtFortDestiny(Fast());

        PumpUntil(pending, () => Sent(recorder).Any(IsStartMatch), "StartMatch");

        byte[][] sent = Sent(recorder);
        byte[][] banners = [.. sent.Where(IsBanner)];

        // A 20 s lobby fits exactly one of the 60 / 30 / 10 steps.
        Assert.Single(banners);
        Assert.Equal(10u, BitConverter.ToUInt32(banners[0].AsSpan()[^4..]));

        // ...and every banner is behind StartMatch.
        Assert.True(
            Array.FindIndex(sent, IsBanner) < Array.FindIndex(sent, IsStartMatch),
            "a banner was sent after the match had already started");
    }

    [Fact]
    public void TheWholeBountyBurstIsSentInTheLobbyAndNothingBountyShapedRidesTheDrop()
    {
        // The offer closes and IsInBox clears before StartMatch. The default sends the
        // backed state and payout tables only in pregame; UI timing needs a client witness.
        (_, _, RecordingRecorder recorder, ConcurrentQueue<Action> pending) =
            ArriveAtFortDestiny(Fast());

        PumpUntil(pending, () => Sent(recorder).Any(IsStartMatch), "StartMatch");

        byte[][] sent = Sent(recorder);
        int start = Array.FindIndex(sent, IsStartMatch);
        int tables = Array.FindIndex(sent, p => Is(p, MatchBountyTables.Opcode, MatchBountyTables.SubOpcode));
        int lastState = Array.FindLastIndex(
            sent, p => Is(p, MatchBountyState.Opcode, MatchBountyState.SubOpcode));

        // The burst went out in the lobby - before StartMatch, not after.
        Assert.True(tables >= 0 && tables < start, "67 0e must be sent in the lobby, before StartMatch");
        Assert.True(lastState >= 0 && lastState < start, "67 0d must be sent in the lobby, before StartMatch");

        // ...and nothing bounty-shaped follows the drop.
        for (int i = start; i < sent.Length; i++)
        {
            Assert.False(
                Is(sent[i], MatchBountyState.Opcode, MatchBountyState.SubOpcode)
                    || Is(sent[i], MatchBountyTables.Opcode, MatchBountyTables.SubOpcode),
                "a bounty packet was sent at or after StartMatch with the suppression on");
        }
    }

    [Fact]
    public void TurningTheSuppressionOffRestoresTheOldPreStartMatchRestate()
    {
        // OFF brings back the discredited D254 pre-StartMatch 67 0d re-state, kept only for A/B.
        (_, _, RecordingRecorder recorder, ConcurrentQueue<Action> pending) =
            ArriveAtFortDestiny(Fast() with
            {
                Bounty = BountyOptions.Default with { SuppressDropOpen = false },
            });

        PumpUntil(pending, () => Sent(recorder).Any(IsStartMatch), "StartMatch");

        byte[][] sent = Sent(recorder);
        int start = Array.FindIndex(sent, IsStartMatch);
        int lastState = Array.FindLastIndex(
            sent, p => Is(p, MatchBountyState.Opcode, MatchBountyState.SubOpcode));

        // The re-state is the last packet of the lobby: only the SynchronizedTeleport sits between
        // it and StartMatch.
        Assert.True(lastState >= 0 && lastState < start, "no 67 0d re-state before ce 16 StartMatch");
        Assert.True(start - lastState <= 2, $"67 0d is {start - lastState} packets before StartMatch");
    }

    [Fact]
    public void TurningTheWholeLaneOffPutsNoBountyByteOnTheWire()
    {
        (_, _, RecordingRecorder recorder, _) = ArriveAtFortDestiny(Fast() with
        {
            Bounty = new BountyOptions { Enabled = false, Currency = false },
        });

        byte[][] sent = SentInTheMatch(recorder);
        Assert.DoesNotContain(sent, p => Is(p, MatchBountyTables.Opcode, MatchBountyTables.SubOpcode));
        Assert.DoesNotContain(sent, p => Is(p, MatchBountyState.Opcode, MatchBountyState.SubOpcode));
        Assert.DoesNotContain(
            sent, p => Is(p, SetAccountCurrencyRecord.Opcode, SetAccountCurrencyRecord.SubOpcode));
    }

    // -----------------------------------------------------------------------------------------
    // The ante round-trip
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ConfirmingAnAnteDebitsTheBalanceResendsItAndEchoesTheBackedState()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder, _) =
            ArriveAtFortDestiny(Fast());

        int before = Sent(recorder).Length;
        SelectBounty(service, connection, 1u);   // Crowns, the hard ante

        byte[][] answer = [.. Sent(recorder).Skip(before)];
        Assert.True(answer.Length >= 2);

        // The debit: 2,000 Crowns less the 500 ante.
        Assert.True(Is(answer[0], SetAccountCurrencyRecord.Opcode, SetAccountCurrencyRecord.SubOpcode));
        Assert.Equal(SetAccountCurrencyRecord.Crowns, BitConverter.ToUInt32(Body(answer[0])[2..6]));
        Assert.Equal(1_500u, BitConverter.ToUInt32(Body(answer[0])[6..10]));

        // The echo: BOUNTY BACKED, with the type the client asked for.
        Assert.True(Is(answer[1], MatchBountyState.Opcode, MatchBountyState.SubOpcode));
        Assert.Equal(500u, BitConverter.ToUInt32(Body(answer[1])[2..6]));
        Assert.Equal(1u, BitConverter.ToUInt32(Body(answer[1])[6..10]));
    }

    [Fact]
    public void ARepeatedBackingRestatesTheOriginalAnteWithoutChargingAgain()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder, _) =
            ArriveAtFortDestiny(Fast() with
            {
                MenuTopBar = MenuTopBarOptions.Default with { Credits = 100u },
            });

        SelectBounty(service, connection, 3u);   // affordable
        int before = Sent(recorder).Length;
        SelectBounty(service, connection, 1u);   // cannot change an already accepted ante

        byte[][] answer = [.. Sent(recorder).Skip(before)];
        byte[] echo = answer.Last(p => Is(p, MatchBountyState.Opcode, MatchBountyState.SubOpcode));
        Assert.DoesNotContain(answer, p => Is(p, SetAccountCurrencyRecord.Opcode, SetAccountCurrencyRecord.SubOpcode));

        // The state is the FIRST ante's, unchanged - the click was answered, not dropped.
        Assert.Equal(100u, BitConverter.ToUInt32(Body(echo)[2..6]));
        Assert.Equal(3u, BitConverter.ToUInt32(Body(echo)[6..10]));
    }

    [Fact]
    public void InvalidOptionZeroDoesNotBackOrSpendCurrency()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder, _) =
            ArriveAtFortDestiny(Fast());

        int before = Sent(recorder).Length;
        SelectBounty(service, connection, 0u);

        byte[][] answer = [.. Sent(recorder).Skip(before)];
        Assert.Single(answer);
        Assert.True(Is(answer[0], MatchBountyState.Opcode, MatchBountyState.SubOpcode));
        Assert.Equal(0u, BitConverter.ToUInt32(Body(answer[0])[2..6]));
    }

    [Fact]
    public void AConfirmedAnteIsTheLastBackedStateBeforeStartMatch()
    {
        // With the suppression on (default) there is no drop-time re-state, so the last 67 0d before
        // StartMatch is the ante echo itself - which still carries what the owner backed in the lobby.
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = ArriveAtFortDestiny(Fast());

        SelectBounty(service, connection, 1u);
        PumpUntil(pending, () => Sent(recorder).Any(IsStartMatch), "StartMatch");

        byte[][] sent = Sent(recorder);
        int start = Array.FindIndex(sent, IsStartMatch);
        byte[] backed = sent
            .Take(start)
            .Last(p => Is(p, MatchBountyState.Opcode, MatchBountyState.SubOpcode));

        Assert.Equal(500u, BitConverter.ToUInt32(Body(backed)[2..6]));
        Assert.Equal(1u, BitConverter.ToUInt32(Body(backed)[6..10]));
    }
}
