using Cranberry.Protocol;
using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// Frozen bytes of the developer-console wire. Every expectation is the layout the August client's
/// own parser reads, cited at the test; nothing here is copied from a schema, and nothing here has
/// been clicked yet — which is exactly why the bytes are pinned before the first click, so a failed
/// click blames the design and not a typo in a writer.
/// <para>
/// The one packet in this file that exists as <b>real captured bytes</b> is
/// <c>Command.ExecuteCommand</c> with the HELP hash, logged by the owner's own 1087 server
/// (R1 <c>part1.md:177</c>). Cranberry's own 135 wire captures contain no <c>09 42</c> at all — the
/// console has never been opened against this server (searched 2026-09-02; the five
/// <c>094200</c> byte runs in them all sit mid-payload inside channel-2 movement bundles).
/// </para>
/// </summary>
public sealed class ConsolePacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ---- s2c: Command.AddWorldCommand (09 40) -------------------------------------------------

    [Fact]
    public void AddWorldCommandIsThreeHeaderBytesAndOneString()
    {
        // 09 40 00 | u32 len | utf8 — FUN_14129ad10 case 0x40 parses one string and nothing else.
        Assert.Equal(
            Convert.FromHexString("094000" + "04000000" + "68656C70"),
            Bytes(w => new AddWorldCommand("help").WriteTo(w)));
    }

    [Fact]
    public void AddWorldCommandOfAOneLetterNameIsEightBytes()
    {
        byte[] bytes = Bytes(w => new AddWorldCommand("m").WriteTo(w));
        Assert.Equal(Convert.FromHexString("094000" + "01000000" + "6D"), bytes);
        Assert.Equal(new AddWorldCommand("m").Length, bytes.Length);
    }

    [Fact]
    public void AddWorldCommandRefusesAnEmptyName() =>
        Assert.Throws<ArgumentException>(() => new AddWorldCommand(""));

    [Fact]
    public void EveryCranberryNameSurvivesTheBurstUnchanged()
    {
        // The burst is one packet per name; each must come back out of its own bytes byte for byte,
        // because the client keys its per-connection table on the hash of exactly these bytes.
        foreach ((string name, uint hash) in ConsoleNameVectors.Appendix)
        {
            byte[] bytes = Bytes(w => new AddWorldCommand(name).WriteTo(w));
            Assert.Equal(name.Length + 7, bytes.Length);
            Assert.Equal(name, System.Text.Encoding.UTF8.GetString(bytes.AsSpan(7)));
            Assert.Equal(hash, CommandHash.Compute(name));
        }
    }

    // ---- s2c: the LINE surfaces ---------------------------------------------------------------

    [Fact]
    public void ConsolePrintIsFourteenBytesForTwoCharacters()
    {
        // 06 03 00 | u32 len | utf8 | u8 flag=0 | u32 code=0 (parser FUN_141251d90; flag 0 is the
        // PrintConsole arm, flag != 0 would be the admin-only AdminSystemMessage arm).
        byte[] bytes = Bytes(w => new ConsolePrint("hi").WriteTo(w));
        Assert.Equal(Convert.FromHexString("060300" + "02000000" + "6869" + "00" + "00000000"), bytes);
        Assert.Equal(14, bytes.Length);
        Assert.Equal(new ConsolePrint("hi").Length, bytes.Length);
    }

    [Fact]
    public void ConsolePrintWithARedCardSetsTheCodeTo0x40000()
    {
        // FUN_1412568e0:688-716 — code == 0x40000 raises SystemMessage(text, 0, "#FC0909") first.
        Assert.Equal(
            Convert.FromHexString("060300" + "02000000" + "6869" + "00" + "00000400"),
            Bytes(w => new ConsolePrint("hi", RedCard: true).WriteTo(w)));
    }

    [Fact]
    public void ConsolePrintRefusesAnEmptyLineBecauseTheClientDropsIt()
    {
        // FUN_141250c70 early-outs on an empty string, so a blank frame row is one space instead.
        Assert.Throws<ArgumentException>(() => new ConsolePrint(""));
        Assert.Equal(13, Bytes(w => new ConsolePrint(" ").WriteTo(w)).Length);
    }

    [Fact]
    public void ChatTextDefaultsToWhiteAndAlsoConsole()
    {
        // 06 05 00 | String8 | u32 a | u32 colour | u32 colour2 | u8 flag | u8 alsoConsole
        // (parser FUN_141251ba0; the console print at :775-777 happens only if the last byte is set).
        byte[] bytes = Bytes(w => new ChatText("Cranberry").WriteTo(w));
        Assert.Equal(
            Convert.FromHexString(
                "060500" + "09000000" + "4372616E6265727279"
                + "00000000" + "FFFFFF00" + "00000000" + "00" + "01"),
            bytes);
        Assert.Equal(30, bytes.Length);
        Assert.Equal(new ChatText("Cranberry").Length, bytes.Length);
    }

    [Fact]
    public void ChatTextWithoutAlsoConsoleIsTheOwners1087Bytes()
    {
        // The owner's 1087 server ends this packet with 00 00; the only difference from the default
        // is that last byte, and with it the console pane never sees the line.
        byte[] bytes = Bytes(w => new ChatText("Cranberry", AlsoConsole: false).WriteTo(w));
        Assert.Equal(
            Convert.FromHexString(
                "060500" + "09000000" + "4372616E6265727279"
                + "00000000" + "FFFFFF00" + "00000000" + "00" + "00"),
            bytes);
    }

    [Fact]
    public void ChatTextWritesTheColourLittleEndian()
    {
        // 0x0000ff00 — the client masks & 0xffffff, so the fourth byte is ignored, not forbidden.
        byte[] bytes = Bytes(w => new ChatText("x", Rgb: 0x00ff00).WriteTo(w));
        Assert.Equal(
            Convert.FromHexString("060500" + "01000000" + "78" + "00000000" + "00FF0000" + "00000000" + "00" + "01"),
            bytes);
    }

    [Fact]
    public void ChatTextRefusesAnEmptyLineAndTheLocaleKeyBranch()
    {
        Assert.Throws<ArgumentException>(() => new ChatText(""));
        Assert.Throws<ArgumentException>(() => new ChatText("##BR.Start"));
        _ = new ChatText("#1 alive");   // a single # is an ordinary line
    }

    // ---- s2c: the other surfaces --------------------------------------------------------------

    [Fact]
    public void SystemMessageCardCarriesTheColourIndex()
    {
        // 42 | u32 localeId | String8 | u32 type | u32 colourIdx (parser FUN_140a3ef50;
        // FUN_140aea220 maps 1 -> "0x00FF00").
        byte[] bytes = Bytes(w => new SystemMessageCard("saved", CardColour.Green).WriteTo(w));
        Assert.Equal(
            Convert.FromHexString("42" + "00000000" + "05000000" + "7361766564" + "00000000" + "01000000"),
            bytes);
        Assert.Equal(22, bytes.Length);
    }

    [Fact]
    public void HudTickerPutsTheSecondsBeforeTheLabel()
    {
        // 11 2f 00 | u32 localeId | u32 seconds | String8 (parser FUN_140a36640).
        Assert.Equal(
            Convert.FromHexString("112F00" + "00000000" + "1E000000" + "04000000" + "4D454E55"),
            Bytes(w => new HudTicker("MENU", 30).WriteTo(w)));
    }

    [Fact]
    public void UiExecuteScriptHasAOneByteSubOpcodeAndNoThirdHeaderByte()
    {
        // 1a 07 | String8 | u32 count | count x u32 — FUN_1412caa00 reads the sub as a u8, and
        // FUN_1412bfbd0 rejects any trailing byte, so "1a 07 00" would fail the exact-length parse.
        byte[] bytes = Bytes(w => new UiExecuteScript("Console.Show").WriteTo(w));
        Assert.Equal(
            Convert.FromHexString("1A07" + "0C000000" + "436F6E736F6C652E53686F77" + "00000000"),
            bytes);
        Assert.Equal(22, bytes.Length);
    }

    [Fact]
    public void UiExecuteScriptWritesItsIntegerArguments()
    {
        Assert.Equal(
            Convert.FromHexString("1A07" + "02000000" + "6869" + "02000000" + "01000000" + "FFFFFFFF"),
            Bytes(w => new UiExecuteScript("hi", 1u, 0xFFFFFFFFu).WriteTo(w)));
    }

    // ---- c2s: Command.ExecuteCommand (09 42) --------------------------------------------------

    [Fact]
    public void ParsesTheHelpPacketMeasuredOnTheOwners1087Server()
    {
        // R1 part1.md:177 — c2s 19:40:39  09 42 00 | 69 db 1b d5 | 00 00 00 00, the packet an
        // unregistered /name collapses to. The ids are unmoved between 1087 and 1148.
        var request = ExecuteCommandRequest.Parse(Convert.FromHexString("094200" + "69DB1BD5" + "00000000"));

        Assert.Equal(ConsoleOpcodes.CommandBase, request.Base);
        Assert.Equal(ConsoleOpcodes.ExecuteCommandSub, request.Sub);
        Assert.Equal(CommandHash.Help, request.Hash);
        Assert.Equal(0xd51bdb69u, request.Hash);
        Assert.Equal(string.Empty, request.Arguments);
        Assert.False(request.Truncated);
    }

    [Fact]
    public void ParsesANameWithAnArgumentTail()
    {
        // What "/m 3" becomes: the client strips the slash and the name, hashes the name, and sends
        // the rest of the line as the argument string (FUN_141296a90:67,123-140).
        var request = ExecuteCommandRequest.Parse(Convert.FromHexString("094200" + "2AB41F5A" + "01000000" + "33"));

        Assert.Equal(CommandHash.Compute("m"), request.Hash);
        Assert.Equal(0x5a1fb42au, request.Hash);
        Assert.Equal("3", request.Arguments);
        Assert.False(request.Truncated);
    }

    [Fact]
    public void ArgumentsKeepTheirCaseAndTheirUtf8()
    {
        // The name folds to upper before hashing; the tail does not fold at all — the /announce
        // and /raw commands depend on that.
        byte[] payload = Bytes(w =>
        {
            w.WriteByte(0x09);
            w.WriteUInt16(0x0042);
            w.WriteUInt32(CommandHash.Compute("announce"));
            w.WriteString("Ready Up, Ecureuil");
        });

        var request = ExecuteCommandRequest.Parse(payload);
        Assert.Equal("Ready Up, Ecureuil", request.Arguments);
    }

    [Fact]
    public void MatchesAcceptsBothBasesAndBothSubOpcodes()
    {
        // 0x0a AdminBase carries the same two sub-opcodes at 1148, and 0x43 ZoneExecuteCommand has
        // the same two fields (R1 §1.3.1).
        Assert.True(ExecuteCommandRequest.Matches(Convert.FromHexString("094200" + "69DB1BD5" + "00000000")));
        Assert.True(ExecuteCommandRequest.Matches(Convert.FromHexString("0A4200" + "69DB1BD5" + "00000000")));
        Assert.True(ExecuteCommandRequest.Matches(Convert.FromHexString("094300" + "69DB1BD5" + "00000000")));
        Assert.True(ExecuteCommandRequest.Matches(Convert.FromHexString("0A4300" + "69DB1BD5" + "00000000")));
    }

    [Fact]
    public void MatchesRejectsEverythingThatIsNotThisPacket()
    {
        // A short body is not an ExecuteCommand with something missing — it is a different packet,
        // and the caller's default arm must log it as one.
        Assert.False(ExecuteCommandRequest.Matches(Convert.FromHexString("094000" + "04000000" + "68656C70")));
        Assert.False(ExecuteCommandRequest.Matches(Convert.FromHexString("0942")));
        Assert.False(ExecuteCommandRequest.Matches(Convert.FromHexString("094200" + "69DB1BD5" + "000000")));
        Assert.False(ExecuteCommandRequest.Matches(ReadOnlySpan<byte>.Empty));
        Assert.Throws<PacketFormatException>(() => ExecuteCommandRequest.Parse(Convert.FromHexString("0942")));
    }

    [Fact]
    public void AnOverLongDeclaredLengthKeepsTheBytesThatAreThereAndSaysSo()
    {
        // Never drop silently (R1 §5 item 9): the caller logs the whole packet once and answers the
        // command with what arrived.
        var request = ExecuteCommandRequest.Parse(
            Convert.FromHexString("094200" + "2AB41F5A" + "09000000" + "313233"));

        Assert.Equal("123", request.Arguments);
        Assert.True(request.Truncated);
    }

    [Fact]
    public void BytesPastTheDeclaredLengthAreIgnored()
    {
        var request = ExecuteCommandRequest.Parse(
            Convert.FromHexString("094200" + "2AB41F5A" + "01000000" + "33" + "DEADBEEF"));

        Assert.Equal("3", request.Arguments);
        Assert.False(request.Truncated);
    }

    // ---- c2s: the console toggle's Spectate (09 10 05) -----------------------------------------

    [Fact]
    public void SpectateNoticeMatchesTheConsoleTogglesOwnPacket()
    {
        // FUN_141291d50:100-104 sends this on every toggle; Cranberry answers it with one log line.
        byte[] payload = Convert.FromHexString("091005" + "0E000000" + "4F6273657276657243616D657261");

        Assert.True(SpectateNotice.Matches(payload));
        Assert.Equal(SpectateNotice.ConsoleToggleTarget, SpectateNotice.TryReadTarget(payload));
        Assert.True(ConsoleInbound.Matches(payload));
    }

    [Fact]
    public void SpectateNoticeRejectsOtherPacketsAndSurvivesAMalformedString()
    {
        Assert.False(SpectateNotice.Matches(Convert.FromHexString("094200" + "69DB1BD5" + "00000000")));
        Assert.Null(SpectateNotice.TryReadTarget(Convert.FromHexString("094000" + "01000000" + "6D")));
        Assert.Null(SpectateNotice.TryReadTarget(Convert.FromHexString("091005" + "FF000000" + "4F62")));
    }

    [Fact]
    public void ConsoleInboundClaimsBothShapesAndNothingElse()
    {
        Assert.True(ConsoleInbound.Matches(Convert.FromHexString("094200" + "69DB1BD5" + "00000000")));
        Assert.True(ConsoleInbound.Matches(Convert.FromHexString("091005" + "0E000000" + "4F6273657276657243616D657261")));
        Assert.False(ConsoleInbound.Matches(Convert.FromHexString("094000" + "01000000" + "6D")));
        Assert.False(ConsoleInbound.Matches(Convert.FromHexString("0F45" + "0000000000000000")));
    }

    // ---- the banner surface stays where it is --------------------------------------------------

    [Fact]
    public void TheBannerSurfaceIsStillGasAlertsAndIsNotDuplicatedHere()
    {
        // ClientUpdate.TextAlert 11 31 00 | String8 already has one writer (Gas/GasAlerts.cs); the
        // console's alert surface calls it rather than owning a second copy of the same three bytes.
        byte[] bytes = Bytes(w => Cranberry.Zone.Gas.GasAlerts.Write(w, "MENU"));
        Assert.Equal(Convert.FromHexString("113100" + "04000000" + "4D454E55"), bytes);
    }
}
