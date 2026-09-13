using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class CrateScenePacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void PlayAnimationMatchesCapturedBodyVerifiedAgainstAugustReader()
    {
        // Owner's 2026-08-22 capture, frame35613. Its shared body was verified against
        // August 140c21610; both packed name lengths exclude their following NUL.
        Assert.Equal(Convert.FromHexString(
            "0F0492FCD6ABE981A4D407004C6566744A61620096050000009605000000000000C0B244"),
            Bytes(w => new CrateSceneAnimation(0xd4a481e9abd6fc92, "LeftJab",
                DurationMs: 1430, ParameterValue: 1430, ClientTime: 1430).WriteTo(w)));
    }

    [Fact]
    public void OpenUsesRequestNameAndMillisecondDurationWithoutInventedSceneFields()
    {
        Assert.Equal(Convert.FromHexString(
            "0F04080706050403020104004F70656E000000000000E803000000000000000000"),
            Bytes(w => new CrateSceneAnimation(0x0102030405060708, "Open").WriteTo(w)));
    }

    [Theory]
    [InlineData("Open\0Close", "")]
    [InlineData("Open", "State\0Opened")]
    public void EmbeddedNulCannotDesynchronizeFollowingFields(string name, string parameter)
    {
        Assert.Throws<ArgumentException>(() => Bytes(w =>
            new CrateSceneAnimation(1, name, ParameterName: parameter).WriteTo(w)));
    }

    [Theory]
    [InlineData(8, "F508")]
    [InlineData(9, "F509")]
    [InlineData(11, "F50B")]
    [InlineData(12, "F50C")]
    public void NativeSealAndShootingControlsAreHeaderOnly(byte sub, string expected)
    {
        Assert.Equal(Convert.FromHexString(expected), Bytes(w => new CrateOpeningControl(sub).WriteTo(w)));
    }

    [Fact]
    public void TargetUsesGuidAndCountedSocketWithoutRevealingAnItem()
    {
        Assert.Equal(Convert.FromHexString(
            "F50A080706050403020109000000576F726C64526F6F74"),
            Bytes(w => new CrateOpeningTarget(0x0102030405060708).WriteTo(w)));
    }
}
