using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// The inbound <c>0x82</c> trace - docs/60 §6, the capture half of this wave.
/// <para>
/// The c2s <c>Fire</c> and <c>ProjectileHitReport</c> layouts are <b>not guessed anywhere</b>
/// (docs/16 §5, docs/20). The plan is to record them the first time the client sends one, so the
/// only thing that has to be right today is that the recording is complete and greppable.
/// </para>
/// </summary>
public sealed class WeaponBasePacketTraceTests
{
    /// <summary>
    /// The whole payload is printed, however long. <c>ZoneService</c>'s catch-all branch truncates
    /// at 48 bytes, which would make a long <c>ProjectileHitReport</c> undecodable after the fact -
    /// that truncation is the reason this formatter exists.
    /// </summary>
    [Fact]
    public void TheHexIsNeverTruncated()
    {
        byte[] payload = new byte[200];
        payload[0] = 0x82;
        payload[5] = 0x06;
        payload[199] = 0xAB;

        string line = WeaponBasePacketTrace.Format(payload);

        Assert.EndsWith("AB", line, StringComparison.Ordinal);
        Assert.Contains("len=200", line, StringComparison.Ordinal);
        Assert.Contains("body=194", line, StringComparison.Ordinal);
        Assert.Contains("sub=0x06", line, StringComparison.Ordinal);
        Assert.Contains("ProjectileHitReport?", line, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(payload), line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>0x82</c> family header is <c>u8 opcode; u32 (read, never used); u8 sub</c> - docs/20
    /// §2 - so the sub-id is at offset 5, not 1. Reading it from the wrong offset would mislabel
    /// the first combat packet ever captured.
    /// </summary>
    [Fact]
    public void TheSubIdComesFromOffsetFive()
    {
        byte[] fire = [0x82, 0x00, 0x00, 0x00, 0x00, 0x03, 0xDE, 0xAD];

        Assert.Equal(5, WeaponBasePacketTrace.SubOpcodeOffset);
        Assert.Equal(6, WeaponBasePacketTrace.FamilyHeaderLength);
        Assert.True(WeaponBasePacketTrace.IsWeaponBase(fire));
        Assert.Contains("sub=0x03 (Fire?)", WeaponBasePacketTrace.Format(fire), StringComparison.Ordinal);
        Assert.Contains("hex=8200000000" + "03DEAD", WeaponBasePacketTrace.Format(fire), StringComparison.Ordinal);
    }

    /// <summary>Every traced line carries the grep tag docs/60 §6 tells the owner to search for.</summary>
    [Fact]
    public void EveryLineCarriesTheGrepTag()
    {
        Assert.Equal("WEAPONFIRE", WeaponBasePacketTrace.Tag);
        Assert.StartsWith(WeaponBasePacketTrace.Tag, WeaponBasePacketTrace.Format([0x82, 0, 0, 0, 0, 0x03]), StringComparison.Ordinal);
        Assert.StartsWith(WeaponBasePacketTrace.Tag, WeaponBasePacketTrace.Format([0x82]), StringComparison.Ordinal);
    }

    /// <summary>A runt <c>0x82</c> is still recorded rather than dropped - it would itself be news.</summary>
    [Fact]
    public void ARuntPacketIsStillRecorded()
    {
        string line = WeaponBasePacketTrace.Format([0x82, 0x01]);

        Assert.Contains("sub=? (runt)", line, StringComparison.Ordinal);
        Assert.Contains("hex=8201", line, StringComparison.Ordinal);
    }

    /// <summary>Only a 0x82 is traced; the zone dispatch must not divert anything else.</summary>
    [Fact]
    public void NothingButZeroX82IsTraced()
    {
        Assert.False(WeaponBasePacketTrace.IsWeaponBase([]));
        Assert.False(WeaponBasePacketTrace.IsWeaponBase([0x94, 0x01]));
        Assert.True(WeaponBasePacketTrace.IsWeaponBase([0x82]));
    }
}
