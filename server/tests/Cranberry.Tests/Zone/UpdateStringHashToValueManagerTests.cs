using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class UpdateStringHashToValueManagerTests
{
    [Fact]
    public void SingleSettingUpdateMatchesAugustParser()
    {
        using var writer = new PacketWriter();
        new UpdateStringHashToValueManager("Hood", "123:1").WriteTo(writer);
        // FUN_140a63f70: opcode; length-prefixed name; length-prefixed value; bool.
        Assert.Equal(Convert.FromHexString("FC04000000486F6F64050000003132333A3100"), writer.Written.ToArray());
    }
}
