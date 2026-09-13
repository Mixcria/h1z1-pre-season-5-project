using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchEndgame;

// The match-end banners. The wire is GasAlerts' (11 31 00 + one u32-counted UTF-8 string, proven
// twice at 1148); what is new is the sentences, so what these tests actually pin is that the
// #count([*slot0*]) expansion produces the client's own words and that the length rule still holds
// once a name of arbitrary width is substituted into one.
public sealed class MatchAlertTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void RemainingIsLocale11113WithTheCountExpanded()
    {
        // 11113 BR.RemainingPlayers, hash 1256554906: "Only #count([*slot0*]) remain."
        Assert.Equal("Only 12 remain.", MatchAlerts.Remaining(12));
        Assert.Equal("Only 1 remain.", MatchAlerts.Remaining(1));
        Assert.Equal(11113u, MatchAlerts.RemainingId);
    }

    [Fact]
    public void WinnerAnnouncedIsLocale11124WithTheNameSubstituted()
    {
        // 11124 BR.AnnounceWinner, hash 4204263506: "[*slot0*] has won the match!"
        Assert.Equal("Cranberry has won the match!", MatchAlerts.WinnerAnnounced("Cranberry"));
        Assert.Equal(11124u, MatchAlerts.WinnerAnnouncedId);
        Assert.Throws<ArgumentNullException>(() => MatchAlerts.WinnerAnnounced(null!));
    }

    [Fact]
    public void CelebrationEndingIsLocale11106()
    {
        // 11106, hash 2034493096: "Ending winner celebration in #count([*slot0*])."
        Assert.Equal("Ending winner celebration in 30 seconds.", MatchAlerts.CelebrationEnding(30));
        Assert.Equal(11106u, MatchAlerts.CelebrationEndingId);
    }

    [Fact]
    public void ConnectedIsLocale11123()
    {
        // 11123, hash 3204975855: "#count([*slot0*]) connected."
        Assert.Equal("5 connected.", MatchAlerts.Connected(5));
        Assert.Equal(11123u, MatchAlerts.ConnectedId);
    }

    [Fact]
    public void NegativeCountsNeverReachTheBanner()
    {
        Assert.Equal("Only 0 remain.", MatchAlerts.Remaining(-3));
        Assert.Equal("0 connected.", MatchAlerts.Connected(-1));
    }

    [Fact]
    public void TheWireIsTheGasAlertWriterUnchanged()
    {
        // One TextAlert writer for the whole server: 11 31 00, u32 byte count, UTF-8 bytes.
        string message = MatchAlerts.Remaining(2);
        byte[] bytes = Bytes(w => MatchAlerts.Write(w, message));

        Assert.Equal(Bytes(w => GasAlerts.Write(w, message)), bytes);
        Assert.Equal(MatchAlerts.HeaderLength + Encoding.UTF8.GetByteCount(message), bytes.Length);
        Assert.Equal(MatchAlerts.LengthOf(message), bytes.Length);
        Assert.Equal(Convert.FromHexString("113100"), bytes[..3]);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(message), BitConverter.ToUInt32(bytes, 3));
        Assert.Equal(message, Encoding.UTF8.GetString(bytes, 7, bytes.Length - 7));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("a name with spaces")]
    [InlineData("éé")]
    public void WinnerBannerLengthFollowsTheNameInUtf8Bytes(string name)
    {
        string message = MatchAlerts.WinnerAnnounced(name);
        Assert.Equal(
            MatchAlerts.HeaderLength + Encoding.UTF8.GetByteCount(message),
            Bytes(w => MatchAlerts.Write(w, message)).Length);
    }

    [Fact]
    public void SecondsRoundUpSoAnAlmostWholeSecondIsNotLost()
    {
        Assert.Equal(30u, MatchAlerts.SecondsOf(29_001));
        Assert.Equal(30u, MatchAlerts.SecondsOf(30_000));
        Assert.Equal(0u, MatchAlerts.SecondsOf(0));
    }

    [Fact]
    public void TheAlertFamilyIsTheUnmovedClientUpdateOne()
    {
        Assert.Equal(0x11, MatchAlerts.Family);
        Assert.Equal(0x0031, MatchAlerts.SubOpcode);
        Assert.Equal(7, MatchAlerts.HeaderLength);
    }
}
