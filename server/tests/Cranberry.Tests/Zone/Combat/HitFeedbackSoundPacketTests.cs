using System.Text;
using Cranberry.Protocol;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

public sealed class HitFeedbackSoundPacketTests
{
    // Native 140a5e890: dc03 followed by a u32 byte count and the exact event string.
    // Cue selection independently follows 1412c9f60, with break taking precedence.
    [Theory]
    [InlineData(false, false, false, "UI_HIT_FLESH_BODY")]
    [InlineData(true, false, false, "UI_HIT_FLESH_HEAD")]
    [InlineData(false, true, false, "UI_HIT_ARMOR_BODY")]
    [InlineData(true, true, false, "UI_HIT_ARMOR_HEAD")]
    [InlineData(false, true, true, "UI_BREAK_ARMOR_BODY")]
    [InlineData(true, true, true, "UI_BREAK_ARMOR_HEAD")]
    public void SurfaceSoundUsesTheNativeEventAndStringPacket(
        bool head, bool armour, bool broken, string expectedEvent)
    {
        var marker = new WeaponHitFeedback(0, IsHeadshot: head, HitArmour: armour,
            CrackedArmour: broken);
        var sound = HitFeedbackSound.For(marker);
        using var writer = new PacketWriter();
        sound.WriteTo(writer);

        Assert.Equal(expectedEvent, sound.EventName);
        Assert.Equal(new byte[] { 0xdc, 0x03, (byte)expectedEvent.Length, 0, 0, 0 }
            .Concat(Encoding.UTF8.GetBytes(expectedEvent)).ToArray(), writer.Written.ToArray());
    }

    [Fact]
    public void PunchImpactUsesTheNativeMeleeCueWithoutAReticlePacket()
    {
        var sound = new HitFeedbackSound(IsMelee: true);
        using var writer = new PacketWriter();
        sound.WriteTo(writer);
        // Native 1412d7c80 and shipped SoundbanksInfo event 2134147681.
        const string expectedEvent = "PLAY_MELEE_ENEMY_HUMAN";
        Assert.Equal(new byte[] { 0xdc, 0x03, 22, 0, 0, 0 }
            .Concat(Encoding.ASCII.GetBytes(expectedEvent)).ToArray(), writer.Written.ToArray());
    }

    [Fact]
    public void GenericAudioCanBeSuppressedWithoutChangingHelmetColourOrBreakFrame()
    {
        var marker = new WeaponHitFeedback(0, IsHeadshot: true, HitArmour: true,
            CrackedArmour: true, SuppressAudio: true);
        using var writer = new PacketWriter();
        marker.WriteTo(writer);
        Assert.Equal(new byte[] { 0x1a, 0x10, 0, 0, 0, 0, 0xe5,
            0xff, 0xff, 0xff, 0xff }, writer.Written.ToArray());
    }
}
