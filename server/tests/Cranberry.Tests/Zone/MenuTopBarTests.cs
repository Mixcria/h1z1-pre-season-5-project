using System.Net;
using System.Text;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

/// <summary>
/// docs/105 §10 — the menu top bar (<c>Experience.SetExperience</c> +
/// <c>Currency.SetAccountCurrencyRecord</c>) and the MOTD panel, MENU-RETAIL-GAP rows U-3 and U-4.
/// <para>
/// The layouts are the August client's own (<c>FUN_140cfae20</c> / <c>FUN_140cf69c0</c> /
/// <c>FUN_140a39740</c>, <c>FUN_140cd39c0</c>, <c>FUN_140a62ec0</c>). The two 1087 payloads the
/// owner's own decodes carry are used here only as a CROSS-CHECK: they must round-trip through
/// Cranberry's 1148 writer with nothing but the base byte changed, which is what proves the
/// derivation and the −1 base shift at the same time.
/// </para>
/// </summary>
public sealed class MenuTopBarTests
{
    /// <summary>
    /// <c>Experience::cExperiencePacketIdSetExperience</c> as the friend server sent it in menu
    /// session <c>1118:62892</c>, 55 B, five times — a fresh-looking account: XP 0, rank 1.
    /// Source: <c>C:\Project\out\ingest-admin-20260822-part1\ops\
    /// Experience__cExperiencePacketIdSetExperience.txt</c> (D53, values only).
    /// </summary>
    private const string CaptureExperience1087 =
        "88010300000002000000000000000b02000001000000000000000100000001020000"
            + "000000000b0000000c0000000d0000000e00000001";

    /// <summary>
    /// The same packet from his OTHER session, <c>1118:64205</c>: XP 31,662, rank 6. Quoted from
    /// the owner's own Z1 (<c>C:\Z1\Server\Zone\ZoneCaptureParity.cs:230</c>, D53 — the value
    /// crosses, the file does not). Only three words differ from the row above, which is what
    /// identifies which words are the XP total and the rank.
    /// </summary>
    private const string CaptureExperience1087Ranked =
        "88010300000002000000ae7b00000b02000006000000100000000100000001020000"
            + "000000000b0000000c0000000d0000000e00000001";

    /// <summary>
    /// <c>Currency::cCurrencyPacketIdSetAccountCurrencyRecord</c>, 14 B at 1087: currency 1
    /// (Scrap) = 52,818, then a third word the August parser does not read.
    /// </summary>
    private const string CaptureCurrency1087 = "ac030100000052ce000000000000";

    private static string Hex(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return Convert.ToHexString(writer.Written).ToLowerInvariant();
    }

    // -----------------------------------------------------------------------------------------
    // U-3a — Experience.SetExperience (87 01)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultExperiencePacketIsTheClientsOwnFreshAccountFiftyFiveBytes()
    {
        // XP 0 and rank 1 are what FUN_140cfa570 writes into a record it has to create
        // ({key, 0, 0, 1, 0, 0, 0}), and what MENU-RETAIL-GAP U-3 says retail draws unaided.
        string hex = Hex(writer => new SetExperience().WriteTo(writer));
        Assert.Equal(SetExperience.Length, hex.Length / 2);
        Assert.Equal(
            "87010300000002000000000000000b02000001000000000000000100000001020000"
                + "000000000b0000000000803f0d0000000e00000001",
            hex);
    }

    [Fact]
    public void TheCapturesOwnExperienceBodyRoundTripsWithOnlyTheBaseByteChanged()
    {
        // The whole point of the derivation: read the 1087 payload field for field with the layout
        // taken out of the 1148 binary, feed those values back into Cranberry's writer, and get the
        // same 55 bytes with 0x88 -> 0x87. Nothing else in the body moves between the two builds.
        string hex = Hex(writer => new SetExperience
        {
            Flags = 3u,
            RecordId = 2u,
            Experience = 0u,
            Word2 = 523u,
            Rank = 1u,
            Word4 = 0u,
            Word6 = 1u,
            Word5 = 513u,
            Word30 = 11u,
            ExperienceRate = BitConverter.Int32BitsToSingle(12),
            Word38 = 13u,
            Word3c = 14u,
            Word40 = true,
        }.WriteTo(writer));

        Assert.Equal("87" + CaptureExperience1087[2..], hex);
    }

    [Fact]
    public void TheSecondCaptureSessionMovesExactlyTheXpTheRankAndOneUnsettledWord()
    {
        // 1118:62892 -> XP 0 / rank 1 / word4 0; 1118:64205 -> XP 31,662 / rank 6 / word4 16.
        // Three words move together across two sessions of the same server; every other word is
        // byte-identical. That is what promotes rec[1] and rec[3] from "plausible" to named.
        string hex = Hex(writer => new SetExperience
        {
            Experience = 31_662u,
            Rank = 6u,
            Word4 = 16u,
            ExperienceRate = BitConverter.Int32BitsToSingle(12),
        }.WriteTo(writer));

        Assert.Equal("87" + CaptureExperience1087Ranked[2..], hex);

        // And the two capture rows really do differ in only those three words.
        byte[] plain = Convert.FromHexString(CaptureExperience1087);
        byte[] ranked = Convert.FromHexString(CaptureExperience1087Ranked);
        Assert.Equal(plain.Length, ranked.Length);
        int[] differing = [.. Enumerable.Range(0, plain.Length).Where(i => plain[i] != ranked[i])];
        // XP at body offset 10..13, rank at 18..21, word4 at 22..25 (the header is two bytes).
        Assert.All(differing, i => Assert.InRange(i, 10, 25));
    }

    [Fact]
    public void TheExperienceRateIsWrittenAsAFloatNotAnInteger()
    {
        // FUN_140cfdca0 takes obj+0x34 in xmm2 and computes (int)((float)n * rate). The capture's
        // server put the integer 12 there, which is a denormal; Cranberry writes a neutral 1.0.
        byte[] body = Convert.FromHexString(Hex(writer => new SetExperience().WriteTo(writer)));
        float rate = BitConverter.ToSingle(body.AsSpan(2 + 10 * 4));
        Assert.Equal(1f, rate);
        Assert.Equal("0000803f", Convert.ToHexString(body.AsSpan(2 + 10 * 4, 4)).ToLowerInvariant());
    }

    // -----------------------------------------------------------------------------------------
    // U-3b — Currency.SetAccountCurrencyRecord (ab 03)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ACurrencyRecordIsTenBytesAndCarriesTheIdThenTheAmount()
    {
        Assert.Equal(
            "ab030100000000000000",
            Hex(writer => new SetAccountCurrencyRecord(SetAccountCurrencyRecord.Scrap, 0).WriteTo(writer)));
        Assert.Equal(
            SetAccountCurrencyRecord.Length,
            Hex(writer => new SetAccountCurrencyRecord(4, 0).WriteTo(writer)).Length / 2);
    }

    [Fact]
    public void TheCapturesCurrencyRowIsTheSameTwoWordsWithoutTheThirdTheAugustParserIgnores()
    {
        // FUN_140cd39c0 reads exactly two u32s after the two header bytes. The 1087 payload has a
        // third word (zero); Cranberry does not write one, because the August parser never reads it.
        string hex = Hex(writer =>
            new SetAccountCurrencyRecord(SetAccountCurrencyRecord.Scrap, 52_818u).WriteTo(writer));
        Assert.Equal("ab" + CaptureCurrency1087[2..(SetAccountCurrencyRecord.Length * 2)], hex);
        Assert.Equal(14, CaptureCurrency1087.Length / 2);
    }

    [Fact]
    public void TheDefaultTopBarSendsAZeroRowForEachOfTheFourCurrenciesTheBarDraws()
    {
        // docs/113 (AUDIT-bounty G3): this was three rows until the bounty lane. Credits (id 6) is
        // one of the three antes the Bounty screen offers and Cranberry had never sent it anywhere,
        // so every ante button was unusable by construction.
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
        Assert.All(rows, row => Assert.Equal(0u, row.Amount));
    }

    // -----------------------------------------------------------------------------------------
    // U-4 — the MOTD (0x32)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheMotdIsTheOpcodeThenTwoCountedStrings()
    {
        string hex = Hex(writer => new MessageOfTheDay("hi").WriteTo(writer));
        Assert.Equal("3200000000" + "02000000" + "6869", hex);
    }

    [Fact]
    public void TheDefaultMotdCarriesTheProjectNameAndTheClientBuildAndAnEmptyTitle()
    {
        MessageOfTheDay motd = MenuTopBarOptions.Default.MotdPacket();
        Assert.Equal(string.Empty, motd.Title);
        Assert.Contains("Cranberry", motd.Message, StringComparison.Ordinal);
        Assert.Contains("0.0.118.208059", motd.Message, StringComparison.Ordinal);

        byte[] body = Convert.FromHexString(Hex(writer => motd.WriteTo(writer)));
        Assert.Equal(ZoneOpcodes.MOTD, body[0]);
        // An empty title is deliberate: FUN_141072ce0 substitutes the client's own
        // "MessageOfTheDay" heading for it.
        Assert.Equal(0, BitConverter.ToInt32(body.AsSpan(1)));
        int length = BitConverter.ToInt32(body.AsSpan(5));
        Assert.Equal(Encoding.UTF8.GetByteCount(motd.Message), length);
        Assert.Equal(1 + 4 + 4 + length, body.Length);          // strict parser: no padding
    }

    // -----------------------------------------------------------------------------------------
    // The live session — what actually goes out at the menu ClientIsReady, and the two reverts
    // -----------------------------------------------------------------------------------------

    private sealed class Recorder : IPacketRecorder
    {
        public List<byte[]> Sent { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c")
            {
                Sent.Add(bytes.ToArray());
            }
        }

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

    /// <summary>Logs a fake link in, then answers ClientIsReady; returns just that burst.</summary>
    private static byte[][] MenuReadyBurst(ZoneOptions options, out int loginBurst)
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new Recorder();
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

        using (var login = new PacketWriter())
        {
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(admission.Guid);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            service.OnMessage(connection, login.Written.ToArray());
        }

        loginBurst = recorder.Sent.Count;
        using (var ready = new PacketWriter())
        {
            ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            ready.WriteByte(ZoneOpcodes.ClientIsReady);
            service.OnMessage(connection, ready.Written.ToArray());
        }

        return [.. recorder.Sent.Skip(loginBurst)];
    }

    private static byte[][] Family(byte[][] burst, byte opcode) =>
        [.. burst.Where(packet => packet.Length > 1 && packet[1] == opcode)];

    [Fact]
    public void TheMenuClientIsReadySendsTheTopBarAndTheMotdOnce()
    {
        byte[][] burst = MenuReadyBurst(new ZoneOptions(), out _);

        byte[][] progression = Family(burst, ZoneOpcodes.ExperienceBase);
        Assert.Equal(2, progression.Length);
        Assert.Equal(2, progression[0][2]); // Rank thresholds precede the account snapshot.
        byte[] experience = progression[1];
        Assert.Equal(0x05, experience[0]);
        Assert.Equal(SetExperience.SubOpcode, experience[2]);
        Assert.Equal(1 + SetExperience.Length, experience.Length);
        // gateway byte, base, sub, then flags | recordId | xp | word2 | rank | …
        Assert.Equal(0u, BitConverter.ToUInt32(experience.AsSpan(3 + 4 + 4)));           // xp
        Assert.Equal(1u, BitConverter.ToUInt32(experience.AsSpan(3 + 4 + 4 + 4 + 4)));   // rank

        // Four since docs/113 (AUDIT-bounty G3): Credits (id 6) is one of the three antes the
        // Bounty screen offers and Cranberry had never sent it anywhere.
        byte[][] currency = Family(burst, ZoneOpcodes.CurrencyBase);
        Assert.Equal(4, currency.Length);
        Assert.All(currency, row => Assert.Equal(1 + SetAccountCurrencyRecord.Length, row.Length));
        Assert.All(currency, row => Assert.Equal(SetAccountCurrencyRecord.SubOpcode, row[2]));
        Assert.Equal(
            [
                SetAccountCurrencyRecord.Scrap,
                SetAccountCurrencyRecord.Crowns,
                SetAccountCurrencyRecord.Skulls,
                SetAccountCurrencyRecord.Credits,
            ],
            currency.Select(row => BitConverter.ToUInt32(row.AsSpan(3))));
        Assert.All(currency, row => Assert.Equal(0u, BitConverter.ToUInt32(row.AsSpan(7))));

        byte[] motd = Assert.Single(Family(burst, ZoneOpcodes.MOTD));
        Assert.Equal(
            Hex(writer => MenuTopBarOptions.Default.MotdPacket().WriteTo(writer)),
            Convert.ToHexString(motd.AsSpan(1)).ToLowerInvariant());
    }

    [Fact]
    public void TheTwoSwitchesSilenceTheirOwnHalfAndNothingElse()
    {
        byte[][] noTopBar = MenuReadyBurst(
            new ZoneOptions { MenuTopBar = MenuTopBarOptions.Default with { SendTopBar = false } }, out _);
        Assert.Empty(Family(noTopBar, ZoneOpcodes.ExperienceBase));
        Assert.Empty(Family(noTopBar, ZoneOpcodes.CurrencyBase));
        Assert.Single(Family(noTopBar, ZoneOpcodes.MOTD));

        byte[][] noMotd = MenuReadyBurst(
            new ZoneOptions { MenuTopBar = MenuTopBarOptions.Default with { SendMotd = false } }, out _);
        Assert.Equal(2, Family(noMotd, ZoneOpcodes.ExperienceBase).Length);
        Assert.Equal(4, Family(noMotd, ZoneOpcodes.CurrencyBase).Length);
        Assert.Empty(Family(noMotd, ZoneOpcodes.MOTD));

        byte[][] neither = MenuReadyBurst(
            new ZoneOptions
            {
                MenuTopBar = MenuTopBarOptions.Default with { SendTopBar = false, SendMotd = false },
            },
            out _);
        Assert.Empty(Family(neither, ZoneOpcodes.ExperienceBase));
        Assert.Empty(Family(neither, ZoneOpcodes.CurrencyBase));
        Assert.Empty(Family(neither, ZoneOpcodes.MOTD));
    }

    [Fact]
    public void AnEmptyMotdTextIsNeverSentBecauseItWouldDeleteTheRow()
    {
        // FUN_141072ce0 removes the entry when the message is empty, so "" is not a way to send a
        // blank MOTD — it is a way to unsend one. The sender treats it as off.
        byte[][] burst = MenuReadyBurst(
            new ZoneOptions
            {
                MenuTopBar = MenuTopBarOptions.Default with { MotdText = string.Empty },
            },
            out _);
        Assert.Empty(Family(burst, ZoneOpcodes.MOTD));
    }

    [Fact]
    public void TheProgressionAndMotdPacketsDoNotTouchTheLoginBurst()
    {
        // The login burst has fourteen packets, including the KOTK environment update
        // (MenuViewSessionTests pins the same number for the camera table).
        _ = MenuReadyBurst(new ZoneOptions(), out int loginBurst);
        Assert.Equal(14, loginBurst);

        // MOTD stays on the menu arm; progression is also initialized when zoning into a match.
        byte[][] burst = MenuReadyBurst(new ZoneOptions(), out _);
        Assert.NotEmpty(Family(burst, ZoneOpcodes.MOTD));
    }

    [Fact]
    public void TheEnvironmentSwitchesAreNamedAsTheDocumentationSays()
    {
        Assert.Equal("CRANBERRY_MENU_TOPBAR", MenuTopBarOptions.TopBarVariable);
        Assert.Equal("CRANBERRY_MENU_MOTD", MenuTopBarOptions.MotdVariable);
        Assert.Equal("CRANBERRY_MENU_MOTD_TEXT", MenuTopBarOptions.MotdTextVariable);
        Assert.Equal("CRANBERRY_MENU_TOPBAR_XP", MenuTopBarOptions.ExperienceVariable);
        Assert.Equal("CRANBERRY_MENU_TOPBAR_RANK", MenuTopBarOptions.RankVariable);
        Assert.Equal("CRANBERRY_MENU_TOPBAR_SCRAP", MenuTopBarOptions.ScrapVariable);

        Assert.True(MenuTopBarOptions.Default.SendTopBar);
        Assert.True(MenuTopBarOptions.Default.SendMotd);
        Assert.Contains("CRANBERRY_MENU_TOPBAR=0", MenuTopBarOptions.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("CRANBERRY_MENU_MOTD=0", MenuTopBarOptions.Default.Describe(), StringComparison.Ordinal);
    }
}
