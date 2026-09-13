using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// docs/120 §6 - the throw, the bounce, the detonation and the fallback, without a socket. The two
/// c2s layouts are built here exactly as the client's own serializers write them
/// (<c>FUN_140eec1f0</c> for <c>82 19</c>, <c>FUN_140ef6680</c> for <c>82 26</c>).
/// </summary>
public sealed class ThrowableArmTests
{
    private const ulong GasStack = 0x3100_0000_0000_0020;
    private const uint Gas = 2237;
    private const uint Frag = 65;

    private static byte[] Packed(uint value)
    {
        using var writer = new Cranberry.Protocol.PacketWriter();
        ClientVarInt.Write(writer, value);
        return writer.Written.ToArray();
    }

    /// <summary><c>82 19</c>: <c>packed; packed; u32 projectileId; packed; f32 x,y,z; f32 dx,dy,dz; u8</c>.</summary>
    internal static byte[] GuidedExplode(
        uint projectileId, float x, float y, float z, uint owner = 5, uint second = 5, uint target = 0, byte trailer = 1)
    {
        var bytes = new List<byte>(ShootingPacketBuilder.Header(WeaponBaseDecoder.SubGuidedExplode, 77));
        bytes.AddRange(Packed(owner));
        bytes.AddRange(Packed(second));
        bytes.AddRange(BitConverter.GetBytes(projectileId));
        bytes.AddRange(Packed(target));
        bytes.AddRange(BitConverter.GetBytes(x));
        bytes.AddRange(BitConverter.GetBytes(y));
        bytes.AddRange(BitConverter.GetBytes(z));
        bytes.AddRange(BitConverter.GetBytes(0f));
        bytes.AddRange(BitConverter.GetBytes(-1f));
        bytes.AddRange(BitConverter.GetBytes(0f));
        bytes.Add(trailer);
        return [.. bytes];
    }

    /// <summary><c>82 26</c>: <c>u32 projectileId; u32 effectId; u64 characterGuid</c>.</summary>
    internal static byte[] Bounce(uint projectileId, uint effectId = 4321, ulong characterGuid = 0x1234)
    {
        var bytes = new List<byte>(ShootingPacketBuilder.Header(WeaponBaseDecoder.SubGrenadeBounceReport, 78));
        bytes.AddRange(BitConverter.GetBytes(projectileId));
        bytes.AddRange(BitConverter.GetBytes(effectId));
        bytes.AddRange(BitConverter.GetBytes(characterGuid));
        return [.. bytes];
    }

    private static List<WeaponArmResult> Handle(SessionCombat session, byte[] packet, uint held, CombatOptions? options = null, long nowMs = 1_000)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, packet, options ?? CombatOptions.Default, held, Vector3.Zero, nowMs, results);
        return results;
    }

    [Fact]
    public void TheTwoLayoutsRoundTrip()
    {
        Assert.True(WeaponBaseDecoder.TryReadGuidedExplode(GuidedExplode(9, 10.5f, 4.25f, -3f, owner: 300, second: 70000, target: 0), out GuidedExplode explode));
        Assert.Equal(9u, explode.ProjectileId);
        Assert.Equal(300u, explode.OwnerNetworkId);
        Assert.Equal(70000u, explode.SecondNetworkId);
        Assert.Equal(0u, explode.TargetNetworkId);
        Assert.Equal(10.5f, explode.X);
        Assert.Equal(4.25f, explode.Y);
        Assert.Equal(-3f, explode.Z);
        Assert.Equal(-1f, explode.DirectionY);
        Assert.Equal(1, explode.Trailer);

        // A body one byte short or long is refused; a nonsense position is refused.
        byte[] good = GuidedExplode(9, 1, 1, 1);
        Assert.False(WeaponBaseDecoder.TryReadGuidedExplode(good[..^1], out _));
        Assert.False(WeaponBaseDecoder.TryReadGuidedExplode([.. good, 0], out _));
        Assert.False(WeaponBaseDecoder.TryReadGuidedExplode(GuidedExplode(9, float.NaN, 1, 1), out _));

        Assert.True(WeaponBaseDecoder.TryReadGrenadeBounceReport(Bounce(9, 4321, 0xABCD), out GrenadeBounceReport bounce));
        Assert.Equal(9u, bounce.ProjectileId);
        Assert.Equal(4321u, bounce.EffectId);
        Assert.Equal(0xABCDul, bounce.CharacterGuid);
        Assert.False(WeaponBaseDecoder.TryReadGrenadeBounceReport(Bounce(9)[..^1], out _));
    }

    [Fact]
    public void ThePackedIntReadsWhatClientVarIntWrites()
    {
        foreach (uint value in new uint[] { 0, 1, 63, 64, 16_383, 16_384, 4_194_303, 4_194_304, 1_000_000_000 })
        {
            byte[] bytes = Packed(value);
            int offset = 0;
            Assert.True(WeaponBaseDecoder.TryReadPackedUInt32(bytes, ref offset, out uint read));
            Assert.Equal(value, read);
            Assert.Equal(bytes.Length, offset);
        }

        int end = 0;
        Assert.False(WeaponBaseDecoder.TryReadPackedUInt32([0x03], ref end, out _));
    }

    /// <summary>D305: the throw is accepted with no magazine and no refire gate - three grenades in a row, none refused.</summary>
    [Fact]
    public void AThrowableFireIsAThrowAndTheSecondOneIsNeverRefused()
    {
        var session = new SessionCombat();

        for (uint shot = 1; shot <= 3; shot++)
        {
            List<WeaponArmResult> results = Handle(
                session, ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [shot]), Gas, nowMs: 1_000 + shot);
            WeaponArmResult result = Assert.Single(results);
            Assert.Contains("THROWN", result.Line, StringComparison.Ordinal);
            Assert.Null(result.Reply);
            Assert.NotNull(result.Thrown);
            Assert.Equal(shot, result.Thrown.Value.Grenade.ProjectileId);
            Assert.Equal(Gas, result.Thrown.Value.ItemDefinitionId);
        }

        Assert.Equal(3, session.Grenades.Live.Count);
        Assert.Equal(3, session.Grenades.Thrown);
        Assert.Equal(0, session.Shooter.ShotsRefused);

        LiveGrenade first = session.Grenades.Live[0];
        Assert.Equal(new Vector3(1, 2, 3), first.ThrowPoint);
        Assert.Equal(1_001 + (long)Math.Round(Rulings.Throwables.FuseSeconds[3] * 1000.0), first.FuseDueAtMs);
    }

    [Theory]
    [InlineData(7u, 8u)]
    [InlineData(7u, 7u)]
    public void OneThrowCannotCreateMultipleGrenades(uint first, uint second)
    {
        var session = new SessionCombat();
        WeaponArmResult result = Assert.Single(Handle(
            session, ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [first, second]), Gas));

        Assert.Contains("must name one projectile", result.Line, StringComparison.Ordinal);
        Assert.Null(result.Thrown);
        Assert.False(result.LaunchRelay);
        Assert.Empty(session.Grenades.Live);
        Assert.Equal(0, session.Grenades.Thrown);
        Assert.Equal(1, session.Shooter.ShotsRefused);
    }

    [Fact]
    public void RepeatedLiveThrowDoesNotConsumeOrDetonateAnotherGrenade()
    {
        var session = new SessionCombat();
        byte[] fire = ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [7]);
        LiveGrenade original = Assert.Single(Handle(session, fire, Gas)).Thrown!.Value.Grenade;
        WeaponArmResult duplicate = Assert.Single(Handle(session, fire, Gas, nowMs: 1_100));

        Assert.Null(duplicate.Thrown);
        Assert.False(duplicate.LaunchRelay);
        Assert.Same(original, Assert.Single(session.Grenades.Live));
        Assert.Equal(1, session.Grenades.Thrown);
        Assert.NotNull(Assert.Single(Handle(session, GuidedExplode(7, 11, 2, 3), Gas)).Detonation);
        Assert.Empty(ThrowableArm.Overdue(session, long.MaxValue));

        // Native numbering may restart; a new Fire timestamp distinguishes it from an exact replay.
        byte[] nextThrow = ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [7], gameTime: 5000);
        Assert.NotNull(Assert.Single(Handle(session, nextThrow, Gas, nowMs: 5_000)).Thrown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSettledSmokeThrowCannotSpendAnotherStackUnitOrStartAnotherCloud(bool fallback)
    {
        const uint smoke = 2236;
        var session = new SessionCombat();
        byte[] fire = ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [7]);
        Assert.NotNull(Assert.Single(Handle(session, fire, smoke)).Thrown);
        if (fallback) Assert.Single(ThrowableArm.Overdue(session, 10_000));
        else Assert.NotNull(Assert.Single(Handle(session, GuidedExplode(7, 11, 2, 3), smoke, nowMs: 2000)).Detonation);
        var duplicate = Assert.Single(Handle(session, fire, smoke, nowMs: 10_001));
        Assert.Null(duplicate.Thrown); // OnGrenadeThrown is the sole stack-consumption callback.
        Assert.False(duplicate.LaunchRelay);
        Assert.Null(Assert.Single(Handle(session, GuidedExplode(7, 12, 2, 3), smoke, nowMs: 10_002)).Detonation);
        Assert.Empty(ThrowableArm.Overdue(session, 20_000));
        Assert.Equal(1, session.Grenades.Thrown);

        session.Grenades.Clear(); // A new world may start the client's projectile counter again.
        Assert.NotNull(Assert.Single(Handle(session, fire, smoke, nowMs: 30_000)).Thrown);
    }

    /// <summary>With the arm off a grenade's 82 03 goes down the gun path - the wave-17 behaviour, refused on the 1-round clip from the second throw.</summary>
    [Fact]
    public void TurningTheArmOffRestoresTheGunPath()
    {
        var session = new SessionCombat();
        CombatOptions off = CombatOptions.Default with { Throwables = false };

        WeaponArmResult first = Assert.Single(Handle(session, ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [1]), Gas, off));
        Assert.DoesNotContain("THROWN", first.Line, StringComparison.Ordinal);
        Assert.Null(first.Thrown);
        Assert.Empty(session.Grenades.Live);

        WeaponArmResult second = Assert.Single(Handle(session, ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [2]), Gas, off, nowMs: 5_000));
        Assert.Contains("REFUSED", second.Line, StringComparison.Ordinal);

        WeaponArmResult explode = Assert.Single(Handle(session, GuidedExplode(1, 1, 2, 3), Gas, off));
        Assert.Contains("seen, not acted on", explode.Line, StringComparison.Ordinal);
        Assert.Null(explode.Detonation);
    }

    /// <summary>D306/D309: the bounces are counted and the client's 82 19 places the detonation and settles the grenade.</summary>
    [Fact]
    public void TheClientsExplodePlacesTheBlast()
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [7]), Gas);

        Assert.Contains("bounce 1", Assert.Single(Handle(session, Bounce(7), Gas)).Line, StringComparison.Ordinal);
        Assert.Contains("bounce 2", Assert.Single(Handle(session, Bounce(7), Gas)).Line, StringComparison.Ordinal);

        WeaponArmResult result = Assert.Single(Handle(session, GuidedExplode(7, 11, 2, 3), Gas, nowMs: 3_500));
        Assert.Contains("DETONATES", result.Line, StringComparison.Ordinal);
        Assert.NotNull(result.Detonation);
        Detonation detonation = result.Detonation;
        Assert.Equal(new Vector3(11, 2, 3), detonation.Position);
        Assert.True(detonation.FromClient);
        Assert.False(detonation.SpareThrower);
        Assert.Equal(ThrowableKind.Gas, detonation.Fact.Kind);
        Assert.Equal(7u, detonation.ProjectileId);

        Assert.Empty(session.Grenades.Live);
        Assert.Equal(1, session.Grenades.ClientDetonations);

        // A second report for the same projectile, or one for a projectile never thrown, detonates nothing.
        Assert.Null(Assert.Single(Handle(session, GuidedExplode(7, 11, 2, 3), Gas)).Detonation);
        WeaponArmResult stranger = Assert.Single(Handle(session, GuidedExplode(99, 11, 2, 3), Gas));
        Assert.Null(stranger.Detonation);
        Assert.Contains("no live grenade", stranger.Line, StringComparison.Ordinal);
    }

    /// <summary>D306: no 82 19 within the fuse plus the grace - the server detonates at the throw point and spares the thrower.</summary>
    [Fact]
    public void TheFallbackAlwaysGoesOffAndSparesTheThrower()
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(GasStack, 1, 2, 3, [7]), Gas, nowMs: 1_000);
        // Without a flight/contact position, keep the native projectile alive until its
        // lifespan expires; the shorter activation fuse must not detonate in the hand.
        long fuse = (long)Math.Round(Assert.Single(session.Grenades.Live).Fact.ProjectileLifespanSeconds * 1000.0);

        Assert.Empty(ThrowableArm.Overdue(session, 1_000 + fuse + Rulings.Throwables.FallbackGraceMs - 1));

        List<Detonation> due = ThrowableArm.Overdue(session, 1_000 + fuse + Rulings.Throwables.FallbackGraceMs);
        Detonation detonation = Assert.Single(due);
        Assert.Equal(new Vector3(1, 2, 3), detonation.Position);
        Assert.False(detonation.FromClient);
        Assert.True(detonation.SpareThrower);
        Assert.Empty(session.Grenades.Live);
        Assert.Equal(1, session.Grenades.FallbackDetonations);
        Assert.Empty(ThrowableArm.Overdue(session, long.MaxValue));
    }

    /// <summary>D308: the frag falls off linearly to the 8 m edge; a cloud is flat per tick; the stun and smoke hurt nobody.</summary>
    [Fact]
    public void TheDamageFallsOffTheWayTheRulingSays()
    {
        // D315: the owner's own inverse-distance fall-off, not D308's linear ramp. Full damage
        // inside the one-unit pivot, base / distance beyond it, and the radius still cuts it dead -
        // so unlike the ramp it does NOT reach zero at the rim (20 hp at 5 u on a 100 hp bar) and a
        // step past the rim takes it to nothing.
        Assert.True(AugustThrowables.TryGet(Frag, out ThrowableFact frag));
        Assert.Equal(5f, frag.Radius);
        Assert.Equal(Rulings.Throwables.FragDamageHp, ThrowableArm.DamageHpAt(in frag, 0));
        Assert.Equal(Rulings.Throwables.FragDamageHp, ThrowableArm.DamageHpAt(in frag, 1));
        Assert.Equal(50, ThrowableArm.DamageHpAt(in frag, 2));
        Assert.Equal(25, ThrowableArm.DamageHpAt(in frag, 4));
        Assert.Equal(20, ThrowableArm.DamageHpAt(in frag, frag.Radius));
        Assert.Equal(0, ThrowableArm.DamageHpAt(in frag, frag.Radius + 0.01));
        Assert.Equal(DamageCause.Explosion, ThrowableArm.CauseOf(in frag));

        Assert.True(AugustThrowables.TryGet(Gas, out ThrowableFact gas));
        Assert.Equal(Rulings.Throwables.GasCloudDamagePerSecondHp, ThrowableArm.DamageHpAt(in gas, 0));
        Assert.Equal(Rulings.Throwables.GasCloudDamagePerSecondHp, ThrowableArm.DamageHpAt(in gas, gas.Radius));
        Assert.Equal(0, ThrowableArm.DamageHpAt(in gas, gas.Radius + 0.5));
        Assert.Equal(DamageCause.ToxicGas, ThrowableArm.CauseOf(in gas));

        // A cloud's amount is PER TICK and flat inside its radius - the owner's gas is 500 a
        // second anywhere in its 7 units, and Cranberry's kept burning patch is the same shape.
        Assert.Equal(7f, gas.Radius);
        Assert.True(AugustThrowables.TryGet(14, out ThrowableFact molotov));
        Assert.Equal(DamageCause.Fire, ThrowableArm.CauseOf(in molotov));
        Assert.True(molotov.Lingers);
        Assert.Equal(5f, molotov.Radius);
        Assert.Equal(
            Rulings.Throwables.MolotovDamagePerSecondHp, ThrowableArm.DamageHpAt(in molotov, 0));
        Assert.Equal(
            Rulings.Throwables.MolotovDamagePerSecondHp,
            ThrowableArm.DamageHpAt(in molotov, molotov.Radius));
        Assert.Equal(0, ThrowableArm.DamageHpAt(in molotov, molotov.Radius + 0.5));

        Assert.True(AugustThrowables.TryGet(2235, out ThrowableFact stun));
        Assert.True(AugustThrowables.TryGet(2236, out ThrowableFact smoke));
        Assert.False(stun.Hurts);
        Assert.False(smoke.Hurts);
        Assert.Equal(0, ThrowableArm.DamageHpAt(in stun, 0));
        Assert.Equal(0, ThrowableArm.DamageHpAt(in smoke, 0));
    }

    [Fact]
    public void TheWorldEffectPacketIsThirtyFourBytes()
    {
        byte[] bytes = new PlayWorldCompositeEffect(0x0510_0000_0000_0000, 1873, new Vector3(1, 2, 3)).ToArray();
        Assert.Equal(PlayWorldCompositeEffect.Length, bytes.Length);
        Assert.Equal(ZoneOpcodes.CharacterBase, bytes[0]);
        Assert.Equal(0x43, bytes[1]);
        Assert.Equal(0x0510_0000_0000_0000ul, BitConverter.ToUInt64(bytes, 2));
        Assert.Equal(1873u, BitConverter.ToUInt32(bytes, 10));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 14));
        Assert.Equal(2f, BitConverter.ToSingle(bytes, 18));
        Assert.Equal(3f, BitConverter.ToSingle(bytes, 22));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 26));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 30));
    }

    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData("0", null, false, true)]
    [InlineData("1", "0", true, false)]
    public void TheSwitchesReadTheEnvironment(string? arm, string? damage, bool expectArm, bool expectDamage)
    {
        CombatOptions options = CombatOptions.FromEnvironment(name => name switch
        {
            CombatOptions.ThrowablesVariable => arm,
            CombatOptions.ThrowableDamageVariable => damage,
            _ => null,
        });

        Assert.Equal(expectArm, options.Throwables);
        Assert.Equal(expectDamage, options.ThrowableDamage);
        Assert.Contains(expectArm ? "throwables=ON" : "throwables=off", options.Describe(), StringComparison.Ordinal);
    }
}
