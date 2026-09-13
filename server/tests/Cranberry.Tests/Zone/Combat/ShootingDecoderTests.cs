using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

// docs/81 §2. The 0x82 family header and the three c2s subs docs/20 §6 marked BLOCKED. What is
// pinned here is the DECODER's contract - the field order it reads and, more importantly, every
// gate that makes it refuse. A packet that fails a gate must produce no damage and full hex; these
// tests are what stop that promise rotting.
public sealed class ShootingDecoderTests
{
    [Fact]
    public void TheFamilyHeaderIsOpcodeGameTimeSub()
    {
        byte[] packet = ShootingPacketBuilder.FireStateUpdate(0x1234, fireState: 1, gameTime: 0xDEADBEEF);

        Assert.True(WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header));
        Assert.Equal(WeaponBaseDecoder.SubFireStateUpdate, header.Sub);

        // docs/20 open question 2: this u32 was "read and never used" on the receive side. The
        // owner's recovered 1087 sender writes the game time there.
        Assert.Equal(0xDEADBEEFu, header.GameTime);
        Assert.Equal(6, WeaponBaseDecoder.HeaderLength);
    }

    [Fact]
    public void ANonWeaponPacketIsNotAHeader()
    {
        Assert.False(WeaponBaseDecoder.TryReadHeader([0x11, 0, 0, 0, 0, 1], out _));
        Assert.False(WeaponBaseDecoder.TryReadHeader([ZoneOpcodes.WeaponBase, 0, 0], out _));
    }

    [Fact]
    public void FireStateSixtyFourMeansTheMagazineIsDry()
    {
        byte[] packet = ShootingPacketBuilder.FireStateUpdate(0x99, WeaponBaseDecoder.EmptyFireState);

        Assert.True(WeaponBaseDecoder.TryReadFireStateUpdate(packet, out FireStateUpdate update));
        Assert.Equal(0x99ul, update.WeaponGuid);
        Assert.Equal(64, update.FireState);
    }

    [Fact]
    public void FireReadsEveryProjectileInTheArrayNotJustTheFirst()
    {
        // The whole point of §2c: reading `count | id | unknown` as three scalars is byte-identical
        // for one projectile and silently truncates a shotgun, whose array carries one entry PER
        // PELLET.
        uint[] pellets = [10, 11, 12, 13, 14, 15, 16, 17];
        byte[] packet = ShootingPacketBuilder.Fire(0x4242, 1f, 2f, 3f, pellets);

        Assert.True(WeaponBaseDecoder.TryReadFire(packet, out WeaponFire fire));
        Assert.Equal(0x4242ul, fire.WeaponGuid);
        Assert.Equal(1f, fire.X);
        Assert.Equal(2f, fire.Y);
        Assert.Equal(3f, fire.Z);
        Assert.Equal(pellets, fire.ProjectileIds);
    }

    [Fact]
    public void FireRefusesAnArrayThatDoesNotConsumeThePayloadExactly()
    {
        byte[] packet = ShootingPacketBuilder.Fire(1, 0, 0, 0, [7]);

        Assert.True(WeaponBaseDecoder.TryReadFire(packet, out _));
        Assert.False(WeaponBaseDecoder.TryReadFire([.. packet, 0x00], out _));
        Assert.False(WeaponBaseDecoder.TryReadFire(packet.AsSpan(0, packet.Length - 1), out _));
    }

    [Fact]
    public void FireRefusesAnImplausibleProjectileCount()
    {
        byte[] packet = ShootingPacketBuilder.Fire(1, 0, 0, 0, [7]);
        byte[] tampered = [.. packet];

        // Overwrite the array header with 65 - one past the owner's own MaxProjectilesPerShot.
        BitConverter.GetBytes(WeaponBaseDecoder.MaxProjectilesPerShot + 1)
            .CopyTo(tampered, WeaponBaseDecoder.HeaderLength + 20);

        Assert.False(WeaponBaseDecoder.TryReadFire(tampered, out _));
    }

    [Fact]
    public void FireRefusesAMuzzlePointThatIsNotAPlace()
    {
        Assert.False(WeaponBaseDecoder.TryReadFire(
            ShootingPacketBuilder.Fire(1, float.NaN, 0, 0, [7]), out _));
        Assert.False(WeaponBaseDecoder.TryReadFire(
            ShootingPacketBuilder.Fire(1, float.PositiveInfinity, 0, 0, [7]), out _));
        Assert.False(WeaponBaseDecoder.TryReadFire(
            ShootingPacketBuilder.Fire(1, WeaponBaseDecoder.CoordinateLimit * 2, 0, 0, [7]), out _));
    }

    [Fact]
    public void FireRefusesAZeroWeaponGuid()
    {
        Assert.False(WeaponBaseDecoder.TryReadFire(ShootingPacketBuilder.Fire(0, 0, 0, 0, [7]), out _));
    }

    [Fact]
    public void TheHitReportReadsTheLiteralStringForm()
    {
        byte[] packet = ShootingPacketBuilder.HitReport(77, 0xABCD, "HEAD", 5f, 6f, 7f, totalShots: 3);

        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(packet, out ProjectileHitReport report));
        Assert.Equal(77u, report.ProjectileId);
        Assert.Equal(0xABCDul, report.CharacterId);
        Assert.Equal("HEAD", report.HitLocation);
        Assert.False(report.HitLocationIsDictionaryId);
        Assert.Equal(3, report.TotalShotCount);
        Assert.Equal(0x80, report.Flags);
        Assert.Equal(5f, report.X);
    }

    [Fact]
    public void TheHitReportReadsTheStringTableFormWhereNoTextFollows()
    {
        // The case a "low byte is the length" reader cannot see at all: 0x8000 set means the low 15
        // bits ARE a string-table id and there is no text on the wire.
        byte[] packet = ShootingPacketBuilder.HitReportByStringId(5, 0xBEEF, stringId: 300);

        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(packet, out ProjectileHitReport report));
        Assert.True(report.HitLocationIsDictionaryId);
        Assert.Equal(300, report.HitLocationId);
        Assert.Equal(string.Empty, report.HitLocation);
    }

    [Fact]
    public void TheHitReportCountsTheTwentyByteEntryArray()
    {
        byte[] packet = ShootingPacketBuilder.HitReport(1, 2, "SPINE", hitEntries: 3);

        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(packet, out ProjectileHitReport report));
        Assert.Equal(3, report.HitEntryCount);
        Assert.Equal("SPINE", report.HitLocation);

        // ...and a declared count that does not consume the payload is refused rather than guessed at.
        byte[] short_ = [.. packet.AsSpan(0, packet.Length - WeaponBaseDecoder.HitEntryBytes)];
        Assert.False(WeaponBaseDecoder.TryReadProjectileHitReport(short_, out _));
    }

    [Fact]
    public void AHitLocationThatIsNotPrintableAsciiIsRefused()
    {
        byte[] packet = ShootingPacketBuilder.HitReport(1, 2, "HEAD");
        byte[] tampered = [.. packet];

        // First text byte, immediately after the 26-byte fixed head.
        tampered[WeaponBaseDecoder.HeaderLength + 26] = 0x01;

        Assert.False(WeaponBaseDecoder.TryReadProjectileHitReport(tampered, out _));
    }

    [Fact]
    public void MultiWeaponYieldsCompletePacketsWithoutReframingThem()
    {
        // 274 of 428 c2s weapon packets in the owner's own session arrived inside this wrapper, and
        // his code carries the scar of prepending a second base to each body.
        byte[] fire = ShootingPacketBuilder.Fire(0x11, 0, 0, 0, [1]);
        byte[] state = ShootingPacketBuilder.FireStateUpdate(0x11, 0);
        byte[] wrapped = ShootingPacketBuilder.MultiWeapon(fire, state);

        var members = new List<Range>();
        Assert.True(WeaponBaseDecoder.TryReadMultiWeapon(wrapped, members));
        Assert.Equal(2, members.Count);
        Assert.Equal(fire, wrapped[members[0]].ToArray());
        Assert.Equal(state, wrapped[members[1]].ToArray());
    }

    [Fact]
    public void MultiWeaponRefusesAMemberThatIsNotAWeaponPacket()
    {
        byte[] wrapped = ShootingPacketBuilder.MultiWeapon([0x11, 1, 2, 3, 4, 5, 6]);
        Assert.False(WeaponBaseDecoder.TryReadMultiWeapon(wrapped, []));
    }

    [Fact]
    public void MultiWeaponRefusesASizeThatOverrunsTheBody()
    {
        byte[] fire = ShootingPacketBuilder.Fire(0x11, 0, 0, 0, [1]);
        byte[] wrapped = ShootingPacketBuilder.MultiWeapon(fire);

        // Inflate the member's declared size past what is actually there.
        BitConverter.GetBytes((uint)(fire.Length + 40))
            .CopyTo(wrapped, WeaponBaseDecoder.HeaderLength + 4);

        Assert.False(WeaponBaseDecoder.TryReadMultiWeapon(wrapped, []));
    }

    [Fact]
    public void TheHitMarkerIsOneAOneCPlusOneFlagByte()
    {
        // registrations-1148.json: UiPacket::cUiPacketConfirmHit at [26, 28] = 0x1a 0x1c.
        Assert.Equal(0x1a, ConfirmHit.Opcode);
        Assert.Equal(0x1c, ConfirmHit.SubOpcode);
        Assert.Equal(3, ConfirmHit.Length);

        Assert.Equal(0x00, new ConfirmHit().Flags);
        Assert.Equal(0x02, new ConfirmHit(IsHeadshot: true).Flags);
        Assert.Equal(0x0c, new ConfirmHit(DamagedArmour: true, CrackedArmour: true).Flags);

        using var writer = new Cranberry.Protocol.PacketWriter();
        new ConfirmHit(IsHeadshot: true).WriteTo(writer);
        Assert.Equal(new byte[] { 0x1a, 0x1c, 0x02 }, writer.Written.ToArray());
    }
}
