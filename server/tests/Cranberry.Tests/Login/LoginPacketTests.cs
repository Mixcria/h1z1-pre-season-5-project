using System.Text;
using Cranberry.Login;
using Cranberry.Protocol;

namespace Cranberry.Tests.Login;

public class LoginPacketTests
{
    [Fact]
    public void DefaultLoginReplyMatchesClientAcceptedCapture()
    {
        using var writer = new PacketWriter();
        new LoginReply().WriteTo(writer);

        Assert.Equal(
            Convert.FromHexString(
                "0201010000000100000000000000000000000000000000000000000000000000" +
                "0000000000000000000000000000000000000000000000000000000000000000" +
                "0000000000"),
            writer.Written.ToArray());
        Assert.Equal(69, writer.Position);
    }

    [Fact]
    public void LoginReplyCarriesTheCountryCodeUsedByRegionSelection()
    {
        using var writer = new PacketWriter();
        new LoginReply { IpCountryCode = "GB" }.WriteTo(writer);

        byte[] packet = writer.Written.ToArray();
        Assert.Equal(71, packet.Length);
        Assert.Equal(2, BitConverter.ToInt32(packet, 28));
        Assert.Equal("GB", Encoding.ASCII.GetString(packet, 32, 2));
    }

    [Fact]
    public void EmptyCharacterListReplyHasMeasuredFieldOrder()
    {
        using var writer = new PacketWriter();
        new CharacterSelectInfoReply(Status: 1, Flag: false, Characters: []).WriteTo(writer);

        Assert.Equal(
            new byte[] { 0x0C, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            writer.Written.ToArray());
    }

    [Fact]
    public void DefaultServerListReplyHasMeasuredFieldOrder()
    {
        using var writer = new PacketWriter();
        var entry = new GameServerEntry();
        new ServerListReply([entry]).WriteTo(writer);

        // Wire order: u64 id, str name, u32, str, u32, u32, str ServerInfo document, str, u8,
        // u8 state, u32 population, str Population document, u8 allowed (derivation log 2026-08-27).
        byte[] population = Encoding.UTF8.GetBytes(entry.Info.ToDocument());
        byte[] expected =
        [
            0x0E,
            0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x09, 0x00, 0x00, 0x00,
            (byte)'C', (byte)'r', (byte)'a', (byte)'n', (byte)'b', (byte)'e', (byte)'r', (byte)'r', (byte)'y',
            0x00, 0x00, 0x00, 0x00, // field 3
            0x00, 0x00, 0x00, 0x00, // text 4, empty
            0x00, 0x00, 0x00, 0x00, // field 5
            0x00, 0x00, 0x00, 0x00, // field 6
            0x00, 0x00, 0x00, 0x00, // ServerInfo document, empty without a region
            0x00, 0x00, 0x00, 0x00, // text 8, empty
            0x00, // flag 9
            0x00, // state: open
            0x00, 0x00, 0x00, 0x00, // population
            (byte)population.Length, 0x00, 0x00, 0x00, .. population,
            0x01, // allowed
        ];

        Assert.Equal(expected, writer.Written.ToArray());
        Assert.StartsWith("<Population ", entry.Info.ToDocument());
        Assert.Contains(" IsLogin=\"1\"", entry.Info.ToDocument());
        Assert.EndsWith("/>", entry.Info.ToDocument());
    }

    [Fact]
    public void CapturedNameValidationTunnelParsesExactly()
    {
        byte[] packet = Convert.FromHexString(
            "1001000000000000000D000000A601030000006D697800000000");

        TunnelAppPacketClientToServer tunnel =
            TunnelAppPacketClientToServer.Parse(packet.AsSpan(1));
        NameValidationRequest request = NameValidationRequest.Parse(tunnel.Payload);

        Assert.Equal(1ul, tunnel.ServerId);
        Assert.Equal("A601030000006D697800000000", Convert.ToHexString(tunnel.Payload));
        Assert.Equal("mix", request.Name);
        Assert.Equal(string.Empty, request.Context);
    }

    [Fact]
    public void SuccessfulNameValidationReplyMatchesAugustParserLayout()
    {
        using var inner = new PacketWriter();
        new NameValidationReply("mix", string.Empty).WriteTo(inner);
        using var outer = new PacketWriter();
        new TunnelAppPacketServerToClient(1, inner.Written.ToArray()).WriteTo(outer);

        Assert.Equal(
            Convert.FromHexString(
                "11010000000000000011000000A602030000006D69780000000001000000"),
            outer.Written.ToArray());
    }

    [Fact]
    public void NameValidationResultsUseTheAugustEnumValues()
    {
        Assert.Equal(1u, NameValidationReply.Success);
        Assert.Equal(2u, NameValidationReply.NameTaken);
        Assert.Equal(3u, NameValidationReply.InvalidName);
        Assert.Equal(4u, NameValidationReply.ProfaneName);
    }

    [Fact]
    public void TunnelAndNameValidationRejectTrailingOrTruncatedData()
    {
        Assert.Throws<PacketFormatException>(() =>
            TunnelAppPacketClientToServer.Parse(
                Convert.FromHexString("010000000000000004000000A601")));

        Assert.Throws<PacketFormatException>(() =>
            NameValidationRequest.Parse(
                Convert.FromHexString("A601000000000000000000")));
    }

    [Fact]
    public void CapturedCharacterCreateRequestParsesExactly()
    {
        byte[] packet = Convert.FromHexString(
            "05010000000000000052000000" +
            "02030000000E01000002000000010000006C980200000200000002000000" +
            "0F00000057696E646F777320313020486F6D65" +
            "03000000362E32" +
            "0E000000302E302E3131382E323038303539" +
            "040000004C697665");

        CharacterCreateRequest request = CharacterCreateRequest.Parse(packet.AsSpan(1));
        CharacterCreatePayload payload = CharacterCreatePayload.Parse(request.Payload);

        Assert.Equal(1ul, request.ServerId);
        Assert.Equal(82, request.Payload.Length);
        Assert.Equal(2, payload.EmpireId);
        Assert.Equal(3u, payload.HeadId);
        Assert.Equal(270u, payload.ProfileId);
        Assert.Equal(2u, payload.Gender);
        Assert.Equal("l", payload.Name);
        Assert.Equal(664u, payload.SkinToneId);
        Assert.Equal(2u, payload.HairId);
        Assert.Equal(2u, payload.RuntimeValue);
        Assert.Equal("Windows 10 Home", payload.OperatingSystem);
        Assert.Equal("6.2", payload.PlatformVersion);
        Assert.Equal("0.0.118.208059", payload.ClientVersion);
        Assert.Equal("Live", payload.Environment);
    }

    [Fact]
    public void CreatedCharacterRosterEntryMatchesAugustParserLayout()
    {
        var create = new CharacterCreatePayload(
            EmpireId: 2,
            HeadId: 3,
            ProfileId: 270,
            Gender: 2,
            Name: "l",
            SkinToneId: 664,
            HairId: 2,
            RuntimeValue: 2,
            OperatingSystem: "Windows 10 Home",
            PlatformVersion: "6.2",
            ClientVersion: "0.0.118.208059",
            Environment: "Live");
        var entry = new CharacterEntry(
            EntityKey: 0x1001,
            ServerId: 1,
            Field3: 0,
            Status: CharacterEntry.StatusAvailable,
            Payload: CharacterSelectionPayload.FromCreate(create).ToArray());

        using var writer = new PacketWriter();
        new CharacterSelectInfoReply(Status: 1, Flag: false, Characters: [entry])
            .WriteTo(writer);

        Assert.Equal(
            Convert.FromHexString(
                "0C010000000001000000" +
                "0110000000000000" +
                "0100000000000000" +
                "0000000000000000" +
                "01000000" +
                "36000000" +
                "010000006C" +
                "02" +
                "01000000" +
                "00000000" +
                "03000000" +
                "0E010000" +
                "02000000" +
                "0E010000" +
                "98020000" +
                "02000000" +
                "00000000" +
                "00000000" +
                "0000000000000000"),
            writer.Written.ToArray());
        Assert.Equal(96, writer.Position);
    }

    [Fact]
    public void CharacterSelectionPayloadRoundTripsTheCreatedAppearanceExactly()
    {
        var expected = new CharacterSelectionPayload(
            Name: "sam",
            EmpireId: 2,
            BattleRank: 1,
            NextBattleRankPercent: 0,
            HeadId: 3,
            ModelId: 270,
            Gender: 2,
            ProfileId: 270,
            Field7: 664,
            Field8: 2,
            Field9: 0);

        byte[] bytes = expected.ToArray();
        CharacterSelectionPayload actual = CharacterSelectionPayload.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(664u, actual.SkinToneId);
        Assert.Equal(2u, actual.HairId);
        Assert.Equal(bytes, actual.ToArray());
        Assert.Throws<PacketFormatException>(() =>
            CharacterSelectionPayload.Parse([.. bytes, 0xFF]));

        byte[] unsupportedFirstCollection = bytes.ToArray();
        // Two nested counts follow the ten fixed values. For "sam", the first is at byte 40.
        unsupportedFirstCollection[40] = 1;
        Assert.Throws<PacketFormatException>(() =>
            CharacterSelectionPayload.Parse(unsupportedFirstCollection));
    }

    [Fact]
    public void GatewayAdmissionCarriesTheRosterAppearanceIntoTheZoneHandoff()
    {
        var tickets = new GatewayTicketRegistry();
        GatewayAdmission issued = tickets.Issue(
            0x1001,
            "sam",
            gender: 2,
            headId: 3,
            hairId: 2,
            skinToneId: 664,
            profileId: 270);

        Assert.True(tickets.TryValidate(issued.Ticket, 0x1001, out GatewayAdmission accepted));
        Assert.Equal(("sam", 2u, 3u, 2u, 664u, 270u),
            (accepted.CharacterName, accepted.Gender, accepted.HeadId, accepted.HairId,
                accepted.SkinToneId, accepted.ProfileId));
    }

    [Fact]
    public void CharacterCreateSuccessReplyMatchesAugustParserLayout()
    {
        using var writer = new PacketWriter();
        new CharacterCreateReply(CharacterCreateReply.StatusSuccess, 0x1001).WriteTo(writer);

        Assert.Equal(
            Convert.FromHexString("06010000000110000000000000"),
            writer.Written.ToArray());
    }

    [Fact]
    public void AcceptedRosterProducesCapturedCharacterLoginRequest()
    {
        byte[] packet = Convert.FromHexString(
            "070110000000000000010000000000000061000000" +
            "05000000656E5F555308000000000000000100000000000000000000000000" +
            "010000003000000000020000000F00000057696E646F777320313020486F6D65" +
            "03000000362E320E000000302E302E3131382E323038303539040000004C69766501");

        CharacterLoginRequest request = CharacterLoginRequest.Parse(packet.AsSpan(1));
        CharacterLoginContext context = CharacterLoginContext.Parse(request.Payload);

        Assert.Equal(0x1001ul, request.EntityKey);
        Assert.Equal(1ul, request.ServerId);
        Assert.Equal(97, request.Payload.Length);
        Assert.Equal("en_US", context.Locale);
        Assert.Equal(8u, context.LocaleId);
        Assert.Equal(0u, context.GatewayId);
        Assert.Equal(1, context.Flag1);
        Assert.Equal(string.Empty, context.Text1);
        Assert.Equal(0u, context.Value1);
        Assert.Equal(0, context.Flag2);
        Assert.Equal(0u, context.Value2);
        Assert.Equal("0", context.Text2);
        Assert.Empty(context.Entries);
        Assert.Equal(2u, context.Runtime.RuntimeValue);
        Assert.Equal("Windows 10 Home", context.Runtime.OperatingSystem);
        Assert.Equal("6.2", context.Runtime.PlatformVersion);
        Assert.Equal("0.0.118.208059", context.Runtime.ClientVersion);
        Assert.Equal("Live", context.Runtime.Environment);
        Assert.Equal(1, context.Flag3);
    }

    [Fact]
    public void CharacterLoginContextEntryMatchesAugustElementWriter()
    {
        using var writer = new PacketWriter();
        writer.WriteString("x");
        writer.WriteUInt32(1);
        writer.WriteUInt32(2);
        writer.WriteByte(3);
        writer.WriteString("a");
        writer.WriteUInt32(4);
        writer.WriteByte(5);
        writer.WriteUInt32(6);
        writer.WriteString("b");
        writer.WriteInt32(1);
        writer.WriteUInt32(7);
        writer.WriteUInt32(8);
        writer.WriteByte(9);
        writer.WriteByte(10);
        writer.WriteUInt32(11);
        writer.WriteString("os");
        writer.WriteString("platform");
        writer.WriteString("client");
        writer.WriteString("environment");
        writer.WriteByte(12);

        CharacterLoginContext context = CharacterLoginContext.Parse(writer.Written);

        Assert.Equal(new CharacterLoginContextEntry(7, 8, 9, 10), Assert.Single(context.Entries));
        Assert.Equal(11u, context.Runtime.RuntimeValue);
        Assert.Equal(12, context.Flag3);
    }

    [Fact]
    public void CharacterLoginContextRejectsImpossibleCountTruncationAndTrailingData()
    {
        using var writer = new PacketWriter();
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteByte(0);
        writer.WriteString(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteByte(0);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteInt32(-1);

        Assert.Throws<PacketFormatException>(() =>
            CharacterLoginContext.Parse(writer.Written.ToArray()));

        byte[] captured = Convert.FromHexString(
            "05000000656E5F555308000000000000000100000000000000000000000000" +
            "010000003000000000020000000F00000057696E646F777320313020486F6D65" +
            "03000000362E320E000000302E302E3131382E323038303539040000004C69766501");
        Assert.Throws<PacketFormatException>(() =>
            CharacterLoginContext.Parse(captured.AsSpan(0, captured.Length - 1)));
        Assert.Throws<PacketFormatException>(() =>
            CharacterLoginContext.Parse([.. captured, 0xFF]));
    }

    [Fact]
    public void CharacterLoginReplyOuterShapeMatchesAugustParser()
    {
        using var writer = new PacketWriter();
        new CharacterLoginReply(
            EntityKey: 0x0102030405060708,
            ServerId: 0x1112131415161718,
            Status: 0x21222324,
            Payload: [0xAA, 0xBB])
            .WriteTo(writer);

        Assert.Equal(
            Convert.FromHexString(
                "08" +
                "0807060504030201" +
                "1817161514131211" +
                "24232221" +
                "02000000AABB"),
            writer.Written.ToArray());
        Assert.Equal(27, writer.Position);
    }

    [Fact]
    public void CharacterLoginSuccessMatchesAugustGatewayConnectVector()
    {
        byte[] key = Convert.FromHexString("17BD086B1B94F02FF0EC53D763589B5F");
        byte[] payload = new GatewayConnectInfo(
            GatewayId: 0,
            Address: "127.0.0.1:20043",
            Ticket: "cranberry-ticket",
            Key: key,
            CipherMode: GatewayConnectInfo.CipherRc4,
            Guid: 0x1001,
            Reserved: 0,
            Text10: string.Empty,
            Text11: string.Empty,
            Text12: string.Empty,
            FeatureBits: 0)
            .ToArray();

        using var writer = new PacketWriter();
        new CharacterLoginReply(
            EntityKey: 0x1001,
            ServerId: 1,
            Status: CharacterLoginReply.StatusSuccess,
            Payload: payload)
            .WriteTo(writer);

        Assert.Equal(
            Convert.FromHexString(
                "08011000000000000001000000000000000100000069000000" +
                "A60D00000000" +
                "0F0000003132372E302E302E313A3230303433" +
                "100000006372616E62657272792D7469636B6574" +
                "1000000017BD086B1B94F02FF0EC53D763589B5F" +
                "03000000" +
                "0110000000000000" +
                "0000000000000000" +
                "000000000000000000000000" +
                "0000000000000000"),
            writer.Written.ToArray());
        Assert.Equal(130, writer.Position);
    }

    [Fact]
    public void RosterStoreValidatesOnlyAdvertisedEntityServerPairs()
    {
        var payload = new CharacterCreatePayload(
            EmpireId: 2,
            HeadId: 1,
            ProfileId: 270,
            Gender: 1,
            Name: "berry",
            SkinToneId: 665,
            HairId: 1,
            RuntimeValue: 2,
            OperatingSystem: "Windows 10 Home",
            PlatformVersion: "6.2",
            ClientVersion: "0.0.118.208059",
            Environment: "Live");
        var store = new CharacterRosterStore();

        CharacterEntry first = store.Create(1, payload);
        CharacterEntry second = store.Create(2, payload with { Name = "cranberry" });

        Assert.Equal(0x1001ul, first.EntityKey);
        Assert.Equal(0x1011ul, second.EntityKey);
        Assert.True(store.ContainsAvailable(first.EntityKey, 1));
        Assert.True(store.ContainsAvailable(second.EntityKey, 2));
        Assert.False(store.ContainsAvailable(first.EntityKey, 2));
        Assert.False(store.ContainsAvailable(0x9999, 1));
        Assert.Equal([first, second], store.Snapshot());
    }
}
