using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Destructibles;

namespace Cranberry.Tests.Zone.Destructibles;

public sealed class DestructibleTests
{
    private static readonly DestructibleProp Fence = new(42, "Farm_Props_Fences_Fence01.adr", new(0, 0, 3), 5296, 5000);
    private static readonly DestructibleCatalog Catalog = new([Fence]);
    private static readonly CombatOptions Options = CombatOptions.Default;

    private static void Fire(ShooterCombatState shooter, uint item, long time, params uint[] ids)
    {
        Assert.Equal(FireVerdict.Accepted,
            shooter.Fire(new WeaponFire(item, 0, 0, 0, ids), item, time, Options).Verdict);
    }

    [Theory]
    [InlineData(2425u)] // AR-15
    [InlineData(2229u)] // AK-47
    [InlineData(2u)] // M1911
    [InlineData(4072u)] // AR-15 skin, PARAM1 fallback
    [InlineData(4033u)] // AK-47 skin, PARAM1 fallback
    [InlineData(1373u)] // .308
    [InlineData(1991u)] // R380 item (weapon definition 1400)
    [InlineData(1997u)] // M9 item (weapon definition 1401)
    [InlineData(1718u)] // .44 item (weapon definition 1388)
    public void NonShotgunGunsNeedExactlyFiveHits(uint item)
    {
        var world = new DestructibleWorld();
        var shooter = new ShooterCombatState();
        Assert.NotNull(RetailBalance.RowForItem(item));
        for (uint bullet = 1; bullet <= 5; bullet++)
        {
            long now = bullet * 2000;
            Fire(shooter, item, now, bullet);
            var hit = world.Hit(Catalog, new(42, Fence.Model, bullet, 0), shooter, Options, Vector3.Zero, now);
            Assert.Equal(5000 - (int)bullet * 1000, hit!.Value.RemainingHealth);
            Assert.Equal(bullet == 5, hit.Value.Destroyed);
        }
        Assert.Single(world.Destroyed);
    }

    [Theory]
    [InlineData(1374u)]
    [InlineData(2663u)]
    public void CloseShotgunBreaksWoodWithEightOfItsSeventeenPellets(uint item)
    {
        var shotgunWorld = new DestructibleWorld();
        var shotgun = new ShooterCombatState();
        Fire(shotgun, item, 0, Enumerable.Range(1, 17).Select(id => (uint)id).ToArray());
        for (uint pellet = 1; pellet <= 17; pellet++)
        {
            var hit = shotgunWorld.Hit(Catalog, new(42, Fence.Model, pellet, 0), shotgun, Options, Vector3.Zero, 0);
            if (pellet <= 8)
            {
                Assert.Equal(706, hit!.Value.Damage);
                Assert.Equal(pellet == 8, hit.Value.Destroyed);
            }
            else Assert.Null(hit);
        }
        Assert.Single(shotgunWorld.Destroyed);
        Assert.Equal(1, shotgun.ShotsFired);
    }

    [Fact]
    public void GlassBreaksAndDamageDoesNotLeakIntoAnotherMatch()
    {
        var glass = Fence with { Model = "Glass.adr", Health = 2000 };
        var catalog = new DestructibleCatalog([glass]);
        var world = new DestructibleWorld();
        var shooter = new ShooterCombatState();
        Fire(shooter, 2425, 0, 1);
        Assert.True(world.Hit(catalog, new(42, glass.Model, 1, 0), shooter, Options, Vector3.Zero, 0)!.Value.Destroyed);
        Assert.Empty(new DestructibleWorld().Destroyed);
        Assert.Equal(2000, catalog.Props[42].Health);
        Assert.Equal(0, shooter.HitsRegistered); // props are not player combat statistics
    }

    [Fact]
    public void DuplicateExpiredUnfiredAndWrongObjectReportsCannotPayDamage()
    {
        var world = new DestructibleWorld();
        var shooter = new ShooterCombatState();
        var hit = new DestructibleHit(42, Fence.Model, 1, 0);
        Assert.Null(world.Hit(Catalog, hit, shooter, Options, Vector3.Zero, 0));
        Fire(shooter, 2425, 0, 1);
        Assert.Null(world.Hit(Catalog, hit with { ObjectId = 43 }, shooter, Options, Vector3.Zero, 0));
        Assert.Null(world.Hit(Catalog, hit with { Model = "Other.adr" }, shooter, Options, Vector3.Zero, 0));
        Assert.Null(world.Hit(Catalog, hit, shooter, Options, new(float.NaN, 0, 0), 0));
        Assert.Null(world.Hit(Catalog, hit, shooter, Options, new(1000, 0, 0), 0));
        Assert.Null(world.Hit(Catalog, hit, shooter, Options with { EnableCombatDamage = false }, Vector3.Zero, 0));
        Assert.Equal(4000, world.Hit(Catalog, hit, shooter, Options, Vector3.Zero, 0)!.Value.RemainingHealth);
        Assert.Null(world.Hit(Catalog, hit, shooter, Options, Vector3.Zero, 0));
        Fire(shooter, 2425, 1000, 2);
        Assert.Null(world.Hit(Catalog, hit with { ProjectileId = 2 }, shooter, Options, Vector3.Zero,
            1001 + Options.FireHintLifetimeMs));
        Assert.Empty(world.Destroyed);
    }

    [Fact]
    public void ShotgunPelletsDoNotHaveAnExtraRangePenaltyAgainstWood()
    {
        var far = Fence with { Position = new(0, 0, 30) };
        var world = new DestructibleWorld();
        var shooter = new ShooterCombatState();
        Fire(shooter, 1374, 0, 1, 2, 3, 4, 5, 6, 7, 8);
        var catalog = new DestructibleCatalog([far]);
        for (uint pellet = 1; pellet <= 8; pellet++)
        {
            var hit = world.Hit(catalog, new(42, far.Model, pellet, 0), shooter, Options, Vector3.Zero, 0);
            Assert.Equal(706, hit!.Value.Damage);
            Assert.Equal(pellet == 8, hit.Value.Destroyed);
        }
        Assert.Single(world.Destroyed);
    }

    [Fact]
    public void NativeBulletDtoPacketIsExactlyConsumedAndMalformedLengthsAreRejected()
    {
        // FUN_1413b27e0: ba/u16 1/u32 object/string/u32 projectile/u64 source.
        byte[] bytes = Convert.FromHexString("BA01002A0000000900000046656E63652E616472070000000000000000000000");
        Assert.True(DestructiblePackets.TryReadHit(bytes, out var hit));
        Assert.Equal(new DestructibleHit(42, "Fence.adr", 7, 0), hit);
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(DestructiblePackets.TryReadHit(bytes.AsSpan(0, length), out _));
        Assert.False(DestructiblePackets.TryReadHit([.. bytes, 0], out _));
        bytes[7] = 255;
        Assert.False(DestructiblePackets.TryReadHit(bytes, out _));
    }

    [Fact]
    public void DestructionUsesAugustOpcodeEffectAndPersistentNoncollidingOverride()
    {
        using var writer = new PacketWriter();
        DestructiblePackets.WriteDestroyed(writer, Fence);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(0xba, reader.ReadByte());
        Assert.Equal(2, reader.ReadUInt16());
        Assert.Equal(42u, reader.ReadUInt32());
        Assert.Equal("Weapon_Empty.adr", reader.ReadString());
        Assert.Equal(5296u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadSingle());
        Assert.False(reader.ReadBool());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(new byte[] { 1, 1, 0, 1 }, reader.ReadRest().ToArray());
    }

    [Fact]
    public void InitialDataEnablesNamesAndRestoresBrokenCollisionWithoutReplayingEffects()
    {
        using var writer = new PacketWriter();
        DestructiblePackets.WriteInitial(writer, Catalog, [Fence]);
        var reader = new PacketReader(writer.Written);
        Assert.Equal(0xba, reader.ReadByte());
        Assert.Equal(3, reader.ReadUInt16());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(42u, reader.ReadUInt32());
        Assert.Equal(DestructiblePackets.EmptyModel, reader.ReadString());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(new byte[] { 1, 1, 0 }, reader.ReadBytes(3).ToArray());
        uint count = reader.ReadUInt32();
        var hashes = new List<uint>();
        for (int n = 0; n < count; n++) { hashes.Add(reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32()); }
        Assert.Contains(StringHashValue.HashName(Fence.Model), hashes);
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void WallUsesWeaponDamageAndDistinctProjectileReportsInAnyOrder()
    {
        var wall = DestructibleCatalog.Default.Props.Values.First(prop => prop.IsWallHoleBlocker)
            with { Position = new(0, 0, 3) };
        var catalog = new DestructibleCatalog([wall]);
        var world = new DestructibleWorld();
        var shooter = new ShooterCombatState();
        Fire(shooter, 1374, 1000, 1, 2, 3, 4, 5, 6, 7, 8);
        uint[] order = [5, 2, 6, 1, 4, 3];
        for (int i = 0; i < order.Length; i++)
        {
            var report = new DestructibleHit(wall.ObjectId, wall.Model, order[i], 0);
            var hit = world.Hit(catalog, report, shooter, Options, Vector3.Zero, 1000);
            Assert.Equal(706, hit!.Value.Damage);
            Assert.Equal(Math.Max(0, 4000 - 706 * (i + 1)), hit.Value.RemainingHealth);
            Assert.Equal(i == 5, hit.Value.Destroyed);
            Assert.Null(world.Hit(catalog, report, shooter, Options, Vector3.Zero, 1000));
        }
        Assert.Null(world.Hit(catalog, new(wall.ObjectId, wall.Model, 7, 0), shooter, Options, Vector3.Zero, 1000));
        Assert.Single(world.Destroyed);
        Assert.Empty(new DestructibleWorld().Destroyed);
        Assert.Equal(0, shooter.HitsRegistered);

        // A rifle retains its ordinary 2500 body damage rather than the fence's 1000 override.
        world = new();
        shooter = new();
        Fire(shooter, 2425, 1000, 1);
        Assert.Equal(1500, world.Hit(catalog, new(wall.ObjectId, wall.Model, 1, 0),
            shooter, Options, Vector3.Zero, 1000)!.Value.RemainingHealth);
        Fire(shooter, 2425, 2000, 2);
        Assert.True(world.Hit(catalog, new(wall.ObjectId, wall.Model, 2, 0),
            shooter, Options, Vector3.Zero, 2000)!.Value.Destroyed);
    }

    [Fact]
    public void ShippedMapHasExactUnsignedIdsAndClientEffectFamilies()
    {
        var catalog = DestructibleCatalog.Default;
        Assert.Equal(45932, catalog.Props.Count);
        Assert.Equal(16, catalog.Models.Count);
        Assert.Equal(2621, catalog.Props.Values.Count(prop => prop.IsExplosive));
        Assert.Equal(451, catalog.Props.Values.Count(prop => prop.IsWallHoleBlocker));
        Assert.DoesNotContain("City_Structures_Buildings_Int_WallHole01.adr", catalog.Models);
        Assert.All(catalog.Props.Values.Where(prop => prop.IsWallHoleBlocker), prop =>
        {
            Assert.Equal(5298u, prop.EffectId);
            Assert.Equal(4000, prop.Health); // Declared wall tuning, independent of fence policy.
            Assert.False(prop.IsWoodenFence);
        });
        Assert.Contains(catalog.Props.Values, prop => prop.ObjectId > int.MaxValue);
        Assert.Equal(15407, catalog.Props.Values.Count(prop => prop.Health == 2000));
        Assert.All(catalog.Props.Values, prop => Assert.Equal(prop.Health == 5000, prop.IsWoodenFence));
        Assert.All(catalog.Props.Values, prop => Assert.True(prop.EffectId > 0));
    }
}
