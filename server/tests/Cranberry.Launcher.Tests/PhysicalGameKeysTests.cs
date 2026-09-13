using System.Buffers.Binary;
using Cranberry.Launcher.Core.Voice;

namespace Cranberry.Launcher.Tests;

public sealed class PhysicalGameKeysTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void MiddleMousePressReleaseAndWheelAreDistinct(int header)
    {
        var keys = new PhysicalGameKeys(); byte[] packet = new byte[header + 24];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(header + 4), 0x10);
        keys.ReadRawPacket(packet, header);
        Assert.True(VoiceBindings.Pressed([VoiceBindings.Parse("Mouse_2")], keys.Down));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(header + 4), 0x400);
        keys.ReadRawPacket(packet, header); Assert.True(keys.Down(4));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(header + 4), 0x20);
        keys.ReadRawPacket(packet, header); Assert.False(keys.Down(4));
    }

    [Fact]
    public void ShiftTabAndLeftRightModifiersTrackReleasesAndFocusReset()
    {
        var keys = new PhysicalGameKeys(); byte[] packet = new byte[40]; packet[0] = 1;
        void Key(ushort vk, ushort scan, ushort flags)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(24), scan);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(26), flags);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(30), vk);
            keys.ReadRawPacket(packet, 24);
        }
        Key(16, 0x2a, 0); Key(16, 0x36, 0); Key(9, 15, 0);
        Assert.True(VoiceBindings.Pressed([VoiceBindings.Parse("Shift+Tab")], keys.Down));
        Key(16, 0x2a, 1); Assert.True(keys.Down(16)); Assert.False(keys.Down(0xa0));
        Key(9, 15, 1); Assert.False(keys.Down(9));
        Key(18, 0x38, 2); Assert.True(keys.Down(0xa5)); Assert.True(keys.Down(18));
        keys.Clear(); Assert.False(keys.Down(16)); Assert.False(keys.Down(18));
        Key(0x25, 0x4b, 0); Assert.True(keys.Down(0x64)); Assert.False(keys.Down(0x25));
        Key(0x25, 0x4b, 1); Key(0x25, 0x4b, 2); Assert.False(keys.Down(0x64)); Assert.True(keys.Down(0x25));
        keys.ReadRawPacket([0, 0], 24); Assert.False(keys.Down(4));
    }
}
