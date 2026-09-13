using Cranberry.Protocol;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

// Frozen vectors of the two stat packets. Byte expectations come from the August readers cited in
// docs/40 §7: the shared 13-byte entry (FUN_140a30280), Character.UpdateStat 0f 40
// (FUN_140c260c0, entity dispatcher case 0x3f) and ClientUpdate.UpdateStat 11 05 (FUN_140a61520),
// whose sub id is u16 LE while 0x0f's is u8.
public sealed class CharacterStatPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void StatEntryIsThirteenBytesInTheReaderOrder()
    {
        // u32 statId; u8 valueType; u32 base; u32 modifier — 5.5f = 0x40B00000.
        byte[] bytes = Bytes(w => CharacterStat.Float(CharacterStatId.MaxMovementSpeed, 5.5f).WriteTo(w));
        Assert.Equal(CharacterStat.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString("02000000" + "01" + "0000B040" + "00000000"),
            bytes);
    }

    [Fact]
    public void IntegerStatUsesValueTypeZero()
    {
        byte[] bytes = Bytes(w => CharacterStat.Integer(1, 10_000).WriteTo(w));
        Assert.Equal(
            Convert.FromHexString("01000000" + "00" + "10270000" + "00000000"),
            bytes);
    }

    [Fact]
    public void EffectiveValueIsBasePlusModifierInTheDeclaredType()
    {
        // GetStat (FUN_140c4beb0) returns base + modifier, as two floats when valueType == 1 and
        // as two int32 otherwise (docs/40 §1.3).
        Assert.Equal(1.25f, CharacterStat.Float(4, 1.5f, -0.25f).EffectiveValue, 5);
        Assert.Equal(7f, CharacterStat.Integer(4, 10, -3).EffectiveValue, 5);
    }

    [Fact]
    public void AnOutOfRangeValueTypeIsRefused()
    {
        // The reader stops after the type byte when valueType > 1, so such an entry would
        // desynchronise the rest of the list — it must never reach the wire.
        var stat = new CharacterStat(2, (CharacterStatValueType)2, 0, 0);
        Assert.Throws<InvalidOperationException>(() => Bytes(stat.WriteTo));
    }

    [Fact]
    public void UpdateStatCarriesTheGuidThenACountedList()
    {
        var packet = new CharacterStatPackets.UpdateStat(
            0x1122334455667788UL,
            [CharacterStat.Float(CharacterStatId.SprintSpeedModifier, 1.45f)]);

        byte[] bytes = Bytes(packet.WriteTo);
        Assert.Equal(CharacterStatPackets.UpdateStatHeaderLength + CharacterStat.Length, bytes.Length);
        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "0F40"
                + "8877665544332211"
                + "01000000"
                + "05000000" + "01" + "9A99B93F" + "00000000"),
            bytes);
    }

    [Fact]
    public void TheProfileBurstIsEighteenEntriesAndTwoHundredFortyEightBytes()
    {
        // docs/40 §8.1: one 0f 40 with 18 entries, 14 + 18×13 = 248 bytes.
        var packet = CharacterStatPackets.UpdateStat.ForProfile(0xABCDEF01UL, MovementProfile.Default);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(MovementProfile.StatCount, packet.Stats.Count);
        Assert.Equal(248, bytes.Length);
        Assert.Equal(0x0f, bytes[0]);
        Assert.Equal(0x40, bytes[1]);
        Assert.Equal(
            (uint)MovementProfile.StatCount,
            BitConverter.ToUInt32(bytes, 10));
    }

    [Fact]
    public void ClientUpdateStatUsesTheSixteenBitSubOpcodeAndNoGuid()
    {
        // 11 05 00: u8; u16 LE; u32 count; entries — 7 + 13 = 20 bytes for the one entry the
        // handler does anything numeric with (docs/40 §7.3, §8.1 step 2).
        var packet = CharacterStatPackets.ClientUpdateStat.BaseSpeed(MovementProfile.Default);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(20, bytes.Length);
        Assert.Equal(packet.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "11" + "0500"
                + "01000000"
                + "02000000" + "01" + "33338340" + "00000000"),   // 4.10f — docs/76 §7.1
            bytes);
    }

    [Fact]
    public void StatIdOrdinalsComeFromTheClientsOwnTable()
    {
        // docs/40 §1.1 / §5: the six modes' ordinals, plus the eight blend times.
        Assert.Equal(2u, CharacterStatId.MaxMovementSpeed);
        Assert.Equal(3u, CharacterStatId.BackpedalSpeedModifier);
        Assert.Equal(4u, CharacterStatId.CrouchSpeedModifier);
        Assert.Equal(5u, CharacterStatId.SprintSpeedModifier);
        Assert.Equal(6u, CharacterStatId.SwimSpeedModifier);
        Assert.Equal(7u, CharacterStatId.StrafeSpeedModifier);
        Assert.Equal(67u, CharacterStatId.ProneSpeedModifier);
        Assert.Equal(84u, CharacterStatId.ProneRollSpeedModifier);
        Assert.Equal(85u, CharacterStatId.WaterSpeedModifier);
        Assert.Equal(86u, CharacterStatId.WalkSpeedModifier);
    }

    [Fact]
    public void TheEightRemoteAnimationTimesAreTheSendToRemoteClientRows()
    {
        // CharacterStatDefinitions.txt flags rows 21, 22, 24-29 (plus 37 DeployInfoId, not ours);
        // FUN_140c60380 re-reads exactly these after every 0f 40 (docs/40 §1.1, §3).
        Assert.Equal(
            new uint[] { 21, 22, 24, 25, 26, 27, 28, 29 },
            CharacterStatId.RemoteAnimationTimes.Order().ToArray());
    }

    [Fact]
    public void AnUnknownStatNameIsRefusedRatherThanSilentlyZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterStatId.Of("StatId.NotAStat"));
        Assert.False(CharacterStatId.TryGet("StatId.NotAStat", out _));
        Assert.True(CharacterStatId.TryGet("StatId.WalkSpeedModifier", out uint walk));
        Assert.Equal(86u, walk);
    }
}
