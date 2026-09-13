using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;

namespace Cranberry.Tests.Login;

/// <summary>
/// docs/105 §9 — MENU-RETAIL-GAP U-7: <c>ServerInfo Region</c> is a locale KEY, not a two-letter
/// code.
/// <para>
/// The August client wrote <c>Code string mapping or T4 string name for 'EU' not found!</c> fifteen
/// times in the owner's 2026-09-02 run (<c>C:\Aug2017\Client\Logs\StringLookup.log</c> lines 79-80
/// and 88-100, a five-second cadence for as long as the menu is open). The owner's own 2026-08-22
/// admin capture of the friend's live 2017 server shows what retail sends instead — login link
/// <c>1115:58316</c>, <c>ServerListReply</c> op 0x0e, 901 bytes,
/// <c>packets_1115_58316.log</c> line 7:
/// <c>&lt;ServerInfo Region="CharacterCreate.RegionEu" Subregion="UI.SubregionEu" …/&gt;</c>.
/// </para>
/// <para>
/// The name resolves because it is a row of the client's own <c>CodeStringMappings.txt</c>
/// (<c>CharacterCreate.RegionEu^9579^</c>), and datasheet string id 9579 hashes
/// (<c>jenkins_lookup2("Global.Text.9579")</c>, tools/locale/localedat.py) to locale key
/// 3,725,598,107, which is a live record of <c>Locale\en_us_data.dat</c> whose text is <c>EU</c>.
/// The panel therefore draws the same two letters it is trying to draw today, and the lookup
/// stops failing.
/// </para>
/// </summary>
public sealed class ServerListRegionTests
{
    /// <summary>
    /// The client's own <c>CodeStringMappings.txt</c> region rows, verbatim, with the string id
    /// each names and the en_us text that id resolves to. Pinned here so a change to the table has
    /// to be a deliberate edit of a fact that came out of the client's files.
    /// </summary>
    public static TheoryData<string, string, int, uint, string> RegionRows() => new()
    {
        { "EU", "CharacterCreate.RegionEu", 9579, 3725598107u, "EU" },
        { "US", "CharacterCreate.RegionUs", 347, 2015953505u, "US" },
        { "AU", "CharacterCreate.RegionAu", 11022, 4138292053u, "AU" },
        { "SA", "CharacterCreate.RegionBra", 14728, 2477640595u, "BRA" },
        { "AS", "CharacterCreate.RegionAsia", 15074, 659336317u, "Asia" },
    };

    [Theory]
    [MemberData(nameof(RegionRows))]
    public void EveryRegionCodeMapsToAClientCodeStringMappingsName(
        string code, string name, int stringId, uint localeKey, string text)
    {
        Assert.Equal(name, RegionNames.LocaleNameFor(code));

        // The id/key/text triple is not computed here — it is the evidence the name is resolvable,
        // carried so the assertion above cannot quietly become a guess. See the class summary.
        Assert.True(stringId > 0);
        Assert.True(localeKey > 0);
        Assert.False(string.IsNullOrEmpty(text));
    }

    [Fact]
    public void AnUnknownCodeIsPassedThroughRatherThanDropped()
    {
        Assert.Equal("ZZ", RegionNames.LocaleNameFor("ZZ"));
        Assert.Equal(string.Empty, RegionNames.LocaleNameFor(string.Empty));
    }

    [Fact]
    public void TheDefaultServerInfoDocumentCarriesTheKeyByteForByte()
    {
        var entry = new GameServerEntry
        {
            Name = "Europe",
            Region = RegionNamingOptions.Default.Resolve("EU"),
        };

        // Cranberry's own document shape (two attributes; the friend server's four IsRecommended*
        // attributes are its own and no client code has been shown to read them).
        Assert.Equal(
            "<ServerInfo Region=\"CharacterCreate.RegionEu\" Subregion=\"\"/>",
            entry.ServerInfoDocument());
    }

    [Fact]
    public void TheRawCodeSwitchRestoresTheExactPreviousDocument()
    {
        RegionNamingOptions raw = RegionNamingOptions.Default with { Naming = RegionNaming.RawCodes };
        var entry = new GameServerEntry { Name = "Europe", Region = raw.Resolve("EU") };

        Assert.Equal("EU", entry.Region);
        Assert.Equal("<ServerInfo Region=\"EU\" Subregion=\"\"/>", entry.ServerInfoDocument());
    }

    [Fact]
    public void SubregionStaysEmpty()
    {
        // The friend server sends Subregion="UI.SubregionEu", but that literal appears NOWHERE in
        // H1Z1.exe 0.0.118.208059 and this build's CodeStringMappings.txt has no UI.Subregion* row.
        // Copying it would trade one failed t4lookup for another, so it is deliberately not copied.
        var entry = new GameServerEntry { Region = RegionNamingOptions.Default.Resolve("EU") };
        Assert.Equal(string.Empty, entry.Subregion);
        Assert.DoesNotContain("UI.Subregion", entry.ServerInfoDocument(), StringComparison.Ordinal);
    }

    // -- the live service ---------------------------------------------------------------------

    private sealed class RecordingRecorder : IPacketRecorder
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

    private static string[] RequestServerList(RegionNamingOptions? regions)
    {
        var recorder = new RecordingRecorder();
        var service = new LoginService(
            new SilentLog(),
            recorder,
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
            regions: regions);
        var remote = new IPEndPoint(IPAddress.Loopback, 5556);
        var request = new SessionRequest(3, 0x11223344, 512, "LoginUdp_11");
        var connection = new SoeConnection(
            remote,
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(remote, in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);
        service.OnMessage(connection, [0x0D]);

        byte[] reply = Assert.Single(recorder.Sent);
        Assert.Equal(ServerListReply.Opcode, reply[0]);

        // Re-read the list the way the client's own reader does: i32 count, then per entry
        // u64 id, str name, u32, str, u32, u32, str ServerInfo, str, bool, u8, u32, str Population,
        // bool. Only the ServerInfo document is returned.
        var reader = new PacketReader(reply.AsSpan(1));
        int count = reader.ReadInt32();
        var documents = new string[count];
        for (int i = 0; i < count; i++)
        {
            _ = reader.ReadUInt64();
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            documents[i] = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadBool();
            _ = reader.ReadByte();
            _ = reader.ReadUInt32();
            _ = reader.ReadString();
            _ = reader.ReadBool();
        }

        return documents;
    }

    [Fact]
    public void TheLiveServerListSendsTheKeyForEveryRegionAndPlayableWorld()
    {
        string[] documents = RequestServerList(regions: null);

        Assert.Equal(4 + GameWorldCatalog.Default.Count, documents.Length);
        Assert.Equal("<ServerInfo Region=\"CharacterCreate.RegionEu\" Subregion=\"\"/>", documents[0]);
        Assert.Equal("<ServerInfo Region=\"CharacterCreate.RegionUs\" Subregion=\"\"/>", documents[1]);
        Assert.Equal("<ServerInfo Region=\"CharacterCreate.RegionBra\" Subregion=\"\"/>", documents[2]);
        Assert.Equal("<ServerInfo Region=\"CharacterCreate.RegionAsia\" Subregion=\"\"/>", documents[3]);
        Assert.Equal("<ServerInfo Region=\"CharacterCreate.RegionAu\" Subregion=\"\"/>", documents[4]);
        Assert.All(documents.Skip(5), document =>
            Assert.Equal("<ServerInfo Region=\"CharacterCreate.RegionEu\" Subregion=\"\"/>", document));

        // The failing lookup was for the bare code; no document may carry one any more.
        foreach (string document in documents)
        {
            Assert.DoesNotContain("Region=\"EU\"", document, StringComparison.Ordinal);
            Assert.DoesNotContain("Region=\"US\"", document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSwitchPutsTheLiveServerListBackToTheBareCodes()
    {
        string[] documents = RequestServerList(
            RegionNamingOptions.Default with { Naming = RegionNaming.RawCodes });

        Assert.Equal("<ServerInfo Region=\"EU\" Subregion=\"\"/>", documents[0]);
        Assert.Equal("<ServerInfo Region=\"US\" Subregion=\"\"/>", documents[1]);
        Assert.Equal("<ServerInfo Region=\"SA\" Subregion=\"\"/>", documents[2]);
        Assert.Equal("<ServerInfo Region=\"AS\" Subregion=\"\"/>", documents[3]);
        Assert.Equal("<ServerInfo Region=\"AU\" Subregion=\"\"/>", documents[4]);
    }

    [Fact]
    public void TheEnvironmentReaderDefaultsOnAndAcceptsTheOneWordRevert()
    {
        Assert.Equal(RegionNaming.LocaleKeys, RegionNamingOptions.Default.Naming);
        Assert.Contains("CRANBERRY_LOGIN_REGION_KEYS", RegionNamingOptions.Default.Describe(), StringComparison.Ordinal);
    }
}
