using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Progression;

namespace Cranberry.Tests.Zone.Progression;

public sealed class ExperiencePacketTests
{
    [Fact]
    public void DefaultExperienceRetainsThe55ByteHistoricalLayout()
    {
        byte[] bytes = Bytes(new SetExperience().WriteTo);
        Assert.Equal(SetExperience.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString(
            "8701" + "03000000" + "02000000" + "00000000" + "0B020000" +
            "01000000" + "00000000" + "01000000" + "01020000" + "00000000" +
            "0B000000" + "0000803F" + "0D000000" + "0E000000" + "01"), bytes);
    }

    [Fact]
    public void KillerAwardMatchesTheNativeFieldOrderAndPreservesFollowingFields()
    {
        var packet = new SetExperience
        {
            Flags = 0,
            RecordId = 0,
            Experience = 100,
            Word2 = 0,
            Rank = 1,
            Word4 = 20,
            Word6 = 0,
            Word5 = 1,
            Awards = [new ExperienceAward(100, 1, 1, 0, 0x0102030405060708, "Dummy")],
            Word30 = 0,
            Word38 = 0,
            Word3c = 0,
            Word40 = false
        };
        byte[] bytes = Bytes(packet.WriteTo);
        Assert.Equal(Convert.FromHexString(
            "8701" + "00000000" + "00000000" + "64000000" + "00000000" +
            "01000000" + "14000000" + "00000000" + "01000000" + "01000000" +
            "64000000" + "01000000" + "01000000" + "00000000" + "0807060504030201" +
            "05000000" + "44756D6D79" +
            "00000000" + "0000803F" + "00000000" + "00000000" + "00"), bytes);

        var reader = new PacketReader(bytes);
        reader.Skip(2 + 8 * 4);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(100u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0x0102030405060708ul, reader.ReadUInt64());
        Assert.Equal("Dummy", reader.ReadString());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(1f, reader.ReadSingle());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.False(reader.ReadBool());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void MultipleAwardsRetainTheirOwnGuidAndUtf8Name()
    {
        byte[] bytes = Bytes(new SetExperience
        {
            Awards =
            [
                new ExperienceAward(100, 1, 1, 0, 17, "Dümmy"),
                new ExperienceAward(25, 2, 3, 4, 23, "Other")
            ]
        }.WriteTo);
        var reader = new PacketReader(bytes);
        reader.Skip(2 + 8 * 4);
        Assert.Equal(2, reader.ReadInt32());
        reader.Skip(4 * 4);
        Assert.Equal(17ul, reader.ReadUInt64());
        Assert.Equal("Dümmy", reader.ReadString());
        Assert.Equal(25u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(4u, reader.ReadUInt32());
        Assert.Equal(23ul, reader.ReadUInt64());
        Assert.Equal("Other", reader.ReadString());
        Assert.Equal(17, reader.Remaining);
    }

    [Fact]
    public void RankTableHasFourCompleteDescriptorsForEveryThreshold()
    {
        uint[] thresholds = [0, 500, 1500];
        byte[] bytes = Bytes(new SetExperienceRanks(thresholds).WriteTo);
        string emptyDescriptors = new('0', 4 * 16 * 2);
        Assert.Equal(Convert.FromHexString(
            "8702" + "01000000" + "00000000" + "03000000" +
            "00000000" + emptyDescriptors +
            "F4010000" + emptyDescriptors +
            "DC050000" + emptyDescriptors), bytes);

        var reader = new PacketReader(bytes);
        Assert.Equal(0x87, reader.ReadByte());
        Assert.Equal(0x02, reader.ReadByte());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(thresholds.Length, reader.ReadInt32());
        foreach (uint threshold in thresholds)
        {
            Assert.Equal(threshold, reader.ReadUInt32());
            for (int descriptor = 0; descriptor < 4; descriptor++)
            {
                Assert.Equal(0u, reader.ReadUInt32());
                Assert.Equal(0u, reader.ReadUInt32());
                Assert.Equal(0u, reader.ReadUInt32());
                Assert.Equal(0, reader.ReadInt32());
            }
        }
        Assert.True(reader.AtEnd);
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }
}
