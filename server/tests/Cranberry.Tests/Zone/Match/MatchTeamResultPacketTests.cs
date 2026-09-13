using Cranberry.Protocol;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class MatchTeamResultPacketTests
{
    [Fact]
    public void NativeReaderConsumesUtf8NamesBothGuidsAndSixScoreWordsThroughEof()
    {
        using var writer = new PacketWriter();
        new MatchTeamResult([new("Å", 0x1122334455667788, 3, 2, 177_000, true)]).WriteTo(writer);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(0x67, reader.ReadByte());
        Assert.Equal(0x22, reader.ReadByte());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal("Å", reader.ReadString());
        Assert.Equal(0ul, reader.ReadUInt64());
        Assert.Equal(0x1122334455667788ul, reader.ReadUInt64());
        uint[] words = new uint[6];
        for (int index = 0; index < words.Length; index++) words[index] = reader.ReadUInt32();
        Assert.Equal(new uint[] { 3, 2, 2_000, 0, 0, 177_000 }, words);
        Assert.True(reader.ReadBool());
        Assert.True(reader.AtEnd);
        Assert.Equal(53, writer.Written.Length); // six-byte envelope + 45-byte row + two UTF-8 bytes
    }

    [Fact]
    public void TeamPanelPreparationMatchesTheNativeFourByteExactLengthGate()
    {
        using var writer = new PacketWriter();
        PrepareTeamResultScreen.WriteTo(writer);
        Assert.Equal(new byte[] { 0xce, 0x1a, 0, 1 }, writer.Written.ToArray());
    }
}
