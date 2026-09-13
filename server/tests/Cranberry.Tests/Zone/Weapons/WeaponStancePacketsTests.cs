using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// <c>0f 20 Character.WeaponStance</c> - S6 §7.3.
/// <para>
/// The id is the client's own: <c>out/registrations-1148.json</c> carries
/// <c>{"id": 536874752, "idHex": "0x20000f00", "levels": [15, 32], "family":
/// "cPacketIdCharacterBase", "member": "cCharacterPacketIdWeaponStance"}</c>, and the bridge
/// (<c>out/wave9-bridge/opcode-map-1087-to-1148.json</c>) marks that member <c>identical</c> with
/// <c>base_delta 0, sub_delta 0</c> - so the owner's 1087 14-byte body ports to 1148 unchanged.
/// </para>
/// </summary>
public sealed class WeaponStancePacketsTests
{
    private const ulong SelfGuid = 0x3100_0000_0000_0001;

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void TheStanceIsFourteenBytesOfBaseSubGuidStance()
    {
        byte[] wire = Bytes(new WeaponStance(SelfGuid, WeaponStance.Initial).WriteTo);

        Assert.Equal(WeaponStance.Length, wire.Length);
        Assert.Equal(14, wire.Length);
        Assert.Equal(
        [
            0x0F, 0x20,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31,   // u64 characterGuid, little endian
            0x01, 0x00, 0x00, 0x00,                           // u32 stance = 1
        ], wire);

        Assert.Equal(ZoneOpcodes.CharacterBase, wire[0]);
        Assert.Equal(0x20, wire[1]);
        Assert.Equal(SelfGuid, BinaryPrimitives.ReadUInt64LittleEndian(wire.AsSpan(2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(10)));
        Assert.Equal(wire, new WeaponStance(SelfGuid, WeaponStance.Initial).ToArray());
    }

    /// <summary>The client's own report uses the identical layout, so one reader serves both.</summary>
    [Fact]
    public void TheClientsOwnStanceParsesBackToTheSameFields()
    {
        byte[] wire = Bytes(new WeaponStance(SelfGuid, 3).WriteTo);

        Assert.True(WeaponStance.TryParse(wire, out WeaponStance? parsed));
        Assert.Equal(new WeaponStance(SelfGuid, 3), parsed);
    }

    /// <summary>
    /// Strict on length and on both id bytes: a short or wrong-sub payload under base <c>0x0f</c> is
    /// something else (<c>0f 45 FullCharacterDataRequest</c> shares the family), and must not be
    /// mistaken for the proof that the stance machine started.
    /// </summary>
    [Fact]
    public void ARejectedPayloadIsNeverReadAsAStance()
    {
        byte[] wire = Bytes(new WeaponStance(SelfGuid, 1).WriteTo);

        Assert.False(WeaponStance.TryParse(wire.AsSpan(0, 13), out _));            // short
        Assert.False(WeaponStance.TryParse([.. wire, 0x00], out _));               // long
        byte[] wrongSub = [.. wire];
        wrongSub[1] = 0x45;
        Assert.False(WeaponStance.TryParse(wrongSub, out _));
        byte[] wrongBase = [.. wire];
        wrongBase[0] = 0x0E;
        Assert.False(WeaponStance.TryParse(wrongBase, out _));
    }

    /// <summary>Both wave-12 switches are visible on the boot banner, or a play-test has to guess.</summary>
    [Fact]
    public void TheBootBannerNamesBothWaveTwelveSwitches()
    {
        string on = WeaponStageOptions.Default.Describe();
        Assert.Contains("tailIdleState=ON", on, StringComparison.Ordinal);
        Assert.Contains("weaponStance=ON", on, StringComparison.Ordinal);

        string off = (WeaponStageOptions.Default with
        {
            TailIdleState = false,
            SendWeaponStance = false,
        }).Describe();
        Assert.Contains("tailIdleState=off", off, StringComparison.Ordinal);
        Assert.Contains("weaponStance=off", off, StringComparison.Ordinal);
    }

    /// <summary>Only the exact string "0" reverts, exactly as every other stage switch behaves.</summary>
    [Fact]
    public void TheEnvironmentSwitchesDefaultOnAndOnlyZeroReverts()
    {
        Assert.True(WeaponStageOptions.FromEnvironment(_ => null).TailIdleState);
        Assert.True(WeaponStageOptions.FromEnvironment(_ => null).SendWeaponStance);

        WeaponStageOptions off = WeaponStageOptions.FromEnvironment(name => name switch
        {
            WeaponStageOptions.TailIdleStateVariable => "0",
            WeaponStageOptions.WeaponStanceVariable => "0",
            _ => null,
        });
        Assert.False(off.TailIdleState);
        Assert.False(off.SendWeaponStance);

        WeaponStageOptions typo = WeaponStageOptions.FromEnvironment(name => name switch
        {
            WeaponStageOptions.TailIdleStateVariable => "false",
            WeaponStageOptions.WeaponStanceVariable => "no",
            _ => null,
        });
        Assert.True(typo.TailIdleState);
        Assert.True(typo.SendWeaponStance);

        // AllOff is the wave-5 control: neither of these may change a byte in it.
        Assert.False(WeaponStageOptions.AllOff.TailIdleState);
        Assert.False(WeaponStageOptions.AllOff.SendWeaponStance);
    }
}
