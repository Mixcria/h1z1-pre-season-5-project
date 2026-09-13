using System.Security.Cryptography;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Weapons;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// docs/120 - what a grenade needs on the wire before the client will throw it: the frag's ruled
/// fire group (D300), the five physics projectile records (D301), a list-4 row per throwable mode
/// keyed on ammo 0 (D303) and <c>FIRE_DURATION_MS</c> on every throwable mode (D304). Every row
/// here is pinned so a retune is a diff, never a surprise.
/// </summary>
public sealed class ThrowableTableTests(ITestOutputHelper output)
{
    private static WeaponStageOptions Control => WeaponStageOptions.Default with { Throwables = false };

    [Fact]
    public void TheFiveThrowablesAreTheClientsOwnRows()
    {
        Assert.Equal(5, AugustThrowables.All.Count);

        uint[] expected = [65u, 2235u, 2236u, 2237u, 14u];
        Assert.Equal(expected, AugustThrowables.All.Select(f => f.ItemDefinitionId).ToArray());

        foreach (ThrowableFact fact in AugustThrowables.All)
        {
            Assert.True(AugustThrowables.IsThrowable(fact.ItemDefinitionId));
            Assert.True(Cranberry.Zone.Inventory.InventoryItemFacts.TryGet(fact.ItemDefinitionId, out var item));
            Assert.Equal(AugustThrowables.ItemClass, item.ItemClass);
            Assert.Equal(AugustThrowables.ActivatableAbilityId, Cranberry.Zone.Combat.AugustAbilityFacts.AbilityIdOf(fact.ItemDefinitionId));
            Assert.EndsWith(".adr", fact.ProjectileModel, StringComparison.Ordinal);
            Assert.True(fact.FuseSeconds > 0f);
            Assert.True(fact.DetonatesOnLifespan);
            Assert.Equal(Rulings.Throwables.ProjectileIdBase + fact.ItemDefinitionId, fact.ProjectileId);
            Assert.DoesNotContain(fact.ProjectileId, AugustProjectileFacts.ProjectileIds);
        }

        // Four groups are the datasheet's own; the frag's is the D300 ruling.
        Assert.Equal(7u, AugustThrowables.FireGroupFor(14u));
        Assert.Equal(42u, AugustThrowables.FireGroupFor(2235u));
        Assert.Equal(43u, AugustThrowables.FireGroupFor(2236u));
        Assert.Equal(44u, AugustThrowables.FireGroupFor(2237u));
        Assert.Equal(Rulings.Throwables.FragFireGroupId, AugustThrowables.FireGroupFor(65u));
        Assert.Equal(0u, AugustThrowables.FireGroupFor(10u));

        // The ruled group collides with nothing the sheet names.
        Assert.DoesNotContain(Rulings.Throwables.FragFireGroupId, AugustWeaponFacts.FireGroupIds);
    }

    [Fact]
    public void TheEffectIdsAreRowsOfTheClientsOwnCatalogue()
    {
        foreach (ThrowableFact fact in AugustThrowables.All)
        {
            if (fact.EffectId != 0)
            {
                AugustEffectDefinition? oneShot = AugustEffectCatalog.ById(fact.EffectId);
                Assert.NotNull(oneShot);
                output.WriteLine($"{fact.Name}: {fact.EffectId} {oneShot.Value.Name}");
            }

            if (fact.CloudEffectId != 0)
            {
                AugustEffectDefinition? loop = AugustEffectCatalog.ById(fact.CloudEffectId);
                Assert.NotNull(loop);
                output.WriteLine($"{fact.Name}: cloud {fact.CloudEffectId} {loop.Value.Name}");
            }

            Assert.True(fact.EffectId != 0 || fact.CloudEffectId != 0);
        }

        // D315: the five ids are the owner's Z1 table's, and every one of them RESOLVES in
        // August's own ActorCompositeEffectDefinitions.xml - which is the check that had to pass
        // before D312 let his numbers ship. The two changed rows are named here on purpose:
        // 5301 replaces 1873 (Default, not the Dirt variant) and 5308 replaces 190 (the molotov's
        // IMPACT, not the fire itself).
        Assert.Equal("PFX_Impact_Explosion_FragGrenade_Default_08m", NameOf(Rulings.Throwables.FragEffectId));
        Assert.Equal("PFX_Impact_Explosion_FlashGrenade_Default", NameOf(Rulings.Throwables.StunEffectId));
        Assert.Equal("PFX_Impact_GrenadeSmoke_loop", NameOf(Rulings.Throwables.SmokeEffectId));
        Assert.Equal("PFX_Impact_GrenadePoison_loop", NameOf(Rulings.Throwables.GasEffectId));
        Assert.Equal("PFX_Fire_Molotov_Impact", NameOf(Rulings.Throwables.MolotovEffectId));
        Assert.Equal("PFX_Fire_Molotov_Persist", NameOf(Rulings.Throwables.MolotovPersistEffectId));

        Assert.Equal<IEnumerable<uint>>(
            [5301u, 4658u, 2333u, 2335u, 5308u],
            new[]
            {
                Rulings.Throwables.FragEffectId,
                Rulings.Throwables.StunEffectId,
                Rulings.Throwables.SmokeEffectId,
                Rulings.Throwables.GasEffectId,
                Rulings.Throwables.MolotovEffectId,
            });
    }

    /// <summary>
    /// D315 (docs/125 §4) - the owner's Z1 grenade table, adopted whole, and the check that let it
    /// ship: every one of his five ACTOR MODEL ids is a row of <b>August's own</b>
    /// <c>Models.txt</c>, and each names byte-for-byte the mesh this table already carried as a
    /// string. An id that did not resolve would have been a 1087 number smuggled into a 1148
    /// server; none of the five is.
    /// </summary>
    [Fact]
    public void TheZ1ModelIdsAreRowsOfAugustsOwnCatalogue()
    {
        Assert.Equal<IEnumerable<uint>>(
            [9443u, 9478u, 9468u, 9480u, 9440u], Rulings.Throwables.ActorModelIds);

        foreach (ThrowableFact fact in AugustThrowables.All)
        {
            Assert.True(
                Cranberry.Zone.Appearance.AugustModelCatalog.TryGet(
                    fact.ActorModelId, out Cranberry.Zone.Appearance.AugustModelRow row),
                $"{fact.Name}: model id {fact.ActorModelId} is not a row of August's Models.txt");

            Assert.Equal(fact.ProjectileModel, row.FileName);
            output.WriteLine($"{fact.Name}: model {fact.ActorModelId} {row.FileName} ships={row.Ships}");
        }
    }

    /// <summary>
    /// D315 - the fuses are the owner's, in milliseconds: frag 3000, flashbang 2000, smoke 1000,
    /// gas 5000, molotov 1500. The molotov's is the one that mattered: at D306's 5.0 s backstop the
    /// 2026-09-04 19:09 throw sat live for five seconds after it had already landed.
    /// </summary>
    [Fact]
    public void TheFusesAreTheOwnersOwn()
    {
        // September 8: shorten activation using contact positions, preserving the native
        // projectile lifespans so the throw still travels correctly.
        Assert.Equal<IEnumerable<float>>(
            [3.0f, 1.0f, 1.0f, 2.0f, 1.5f], Rulings.Throwables.FuseSeconds);

        Assert.True(AugustThrowables.TryGet(14u, out ThrowableFact molotov));
        Assert.Equal(1.5f, molotov.FuseSeconds);
        Assert.True(AugustThrowables.TryGet(2237u, out ThrowableFact gas));
        Assert.Equal(2.0f, gas.FuseSeconds);
        Assert.True(AugustThrowables.TryGet(2236u, out ThrowableFact smoke));
        Assert.Equal(1.0f, smoke.FuseSeconds);
    }

    /// <summary>
    /// The projectile record a grenade spawns: the physics flight model, the client's own mesh, the
    /// fuse as <c>LIFESPAN</c> with the <c>LIFESPAN_DETONATE</c> bit, gravity 1.0, a tumble, and
    /// the layout's own byte positions for each - read back off the wire.
    /// </summary>
    [Fact]
    public void AGrenadeProjectileRecordIsPhysicsWithAFuse()
    {
        Assert.True(AugustThrowables.TryGet(2237u, out ThrowableFact gas));
        ProjectileDefinitionRecord record = AugustThrowables.ProjectileRecordFor(in gas, 18f);

        Assert.Equal(AugustThrowables.PhysicsFlightType, record.FlightType);
        Assert.Equal("Projectile_Grenades_GasGrenade.adr", record.ModelFileName);
        Assert.Equal(gas.ProjectileLifespanSeconds, record.Lifespan);
        Assert.Equal(18f, record.Speed);
        Assert.True(record.DetonatesOnLifespan);
        Assert.False(record.DetonatesOnContact);
        Assert.Equal(ProjectileDefinitionRecord.LifespanDetonateFlag, record.Flags0);
        Assert.Equal(1.2f, record.GravityScale);   // D327: Z1's GRAVITY 1.2 on the grenade rows
        Assert.Equal(0f, record.Drag);             // D327: Z1's DRAG 0
        Assert.Equal(30f, Rulings.Throwables.ThrowSpeed);
        Assert.Equal(-Rulings.Throwables.TumbleRadiansPerSecond, record.AngularVelocityMin);
        Assert.Equal(Rulings.Throwables.TumbleRadiansPerSecond, record.AngularVelocityMax);

        using var writer = new Cranberry.Protocol.PacketWriter();
        record.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();
        Assert.Equal(record.Length, bytes.Length);

        // Wire offsets: key(4) + rec+0x18(4) = 8, then rec+0x20 flags at wire 8.
        Assert.Equal(ProjectileDefinitionRecord.LifespanDetonateFlag, bytes[8]);
        Assert.Equal(0, bytes[9]);
        int modelLength = BitConverter.ToInt32(bytes, 10);
        Assert.Equal(record.ModelFileName.Length, modelLength);
        int afterModel = 14 + modelLength;               // rec+0x40 string starts here
        int afterStrings = afterModel + 4;               // empty AttachmentOverride
        Assert.Equal(18f, BitConverter.ToSingle(bytes, afterStrings + 4));      // +0x5c SPEED
        Assert.Equal(9, bytes[afterStrings + 16]);                               // +0x68 FLIGHT_TYPE
        Assert.Equal(gas.ProjectileLifespanSeconds, BitConverter.ToSingle(bytes, afterStrings + 29)); // +0x78 LIFESPAN

        // The molotov breaks on contact as well.
        Assert.True(AugustThrowables.TryGet(14u, out ThrowableFact molotov));
        ProjectileDefinitionRecord bottle = AugustThrowables.ProjectileRecordFor(in molotov, 18f);
        Assert.Equal(
            ProjectileDefinitionRecord.LifespanDetonateFlag | ProjectileDefinitionRecord.DetonateOnContactFlag,
            bottle.Flags0);
    }

    /// <summary>
    /// docs/120 §3.2 correction: <c>GRAVITY</c> is <c>rec+0xa4</c> (preset 1.0), and the bullet
    /// records are byte-identical to what they were with the word renamed - the +0xa4 word was
    /// always 1.0f and the +0xa8 word is still 10.0f.
    /// </summary>
    [Fact]
    public void TheGravityCorrectionMovesNoBulletByte()
    {
        foreach (ProjectileDefinitionRecord bullet in AugustProjectileTable.Records)
        {
            Assert.Equal(1.0f, bullet.GravityScale);
            Assert.Equal(10.0f, bullet.Gravity);
            Assert.Equal(0, bullet.Flags0);
            Assert.Equal(0f, bullet.AngularVelocityMax);
        }

        byte[] plain = AugustProjectileTable.CreateBlob().ToArray();
        byte[] withoutThrowables = AugustProjectileTable.CreateBlob(true, throwables: false, 18f).ToArray();
        Assert.Equal(plain, withoutThrowables);

        byte[] withThrowables = AugustProjectileTable.CreateBlob(true, throwables: true, 18f).ToArray();
        Assert.NotEqual(plain, withThrowables);
        Assert.Equal(14 + 5, AugustProjectileTable.RecordsFor(true, 18f).Count);
        output.WriteLine($"ProjectileDefinitions: {plain.Length} B without, {withThrowables.Length} B with the throwables");
    }

    /// <summary>
    /// List 4 for the throwables: two rows per grenade (both modes), ammo key 0, pointing at the
    /// grenade's own projectile - and none of them shares a key with a bullet row.
    /// </summary>
    [Fact]
    public void EveryThrowableModeReachesItsProjectileThroughListFour()
    {
        IReadOnlyList<FireModeProjectileRecord> rows = AugustWeaponTable.ThrowableFireModeProjectileRecords;
        Assert.Equal(10, rows.Count);

        foreach (ThrowableFact fact in AugustThrowables.All)
        {
            for (int index = 0; index < AugustWeaponTable.FireModesPerGroup; index++)
            {
                uint modeId = AugustWeaponTable.FireModeIdFor(fact.FireGroupId, index);
                FireModeProjectileRecord row = Assert.Single(rows, r => r.FireModeDefinitionId == modeId);
                Assert.Equal(0u, row.AmmoItemId);
                Assert.Equal(fact.ProjectileId, row.ProjectileDefinitionId);
            }
        }

        HashSet<uint> bulletModes = [.. AugustWeaponTable.FireModeProjectileRecords.Select(r => r.FireModeDefinitionId)];
        Assert.DoesNotContain(rows, r => bulletModes.Contains(r.FireModeDefinitionId));
    }

    /// <summary>
    /// The blob with the throwables on carries: one more fire group, one more weapon definition,
    /// two more fire modes (the frag's, <c>TYPE 12</c>, wind-up set) and ten more list-4 rows;
    /// every datasheet throwable mode now carries the wind-up too; and OFF is the wave-17 blob.
    /// </summary>
    [Fact]
    public void TheThrowablesAddTheFragAndTheWindupAndOffIsWaveSeventeen()
    {
        WeaponDefinitionsBlob on = new WeaponSession(WeaponStageOptions.Default).Blob;
        WeaponDefinitionsBlob off = new WeaponSession(Control).Blob;

        Assert.Equal(off.FireGroups!.Count + 1, on.FireGroups!.Count);
        Assert.Equal(off.WeaponDefinitions!.Count + 1, on.WeaponDefinitions!.Count);
        Assert.Equal(off.FireModes!.Count + 2, on.FireModes!.Count);
        Assert.Equal(off.FireModeProjectiles!.Count + 10, on.FireModeProjectiles!.Count);

        uint frag = Rulings.Throwables.FragFireGroupId;
        FireGroupRecord group = Assert.Single(on.FireGroups, g => g.FireGroupId == frag);
        Assert.Equal([frag * 2, (frag * 2) + 1], group.FireModeIds);
        Assert.Equal(0, group.Flags);

        WeaponDefinitionRecord weapon = Assert.Single(on.WeaponDefinitions, w => w.WeaponDefinitionId == 1404u);
        Assert.Equal([frag], weapon.FireGroupIds);
        Assert.Null(weapon.AmmoSlots);

        foreach (FireModeRecord mode in on.FireModes.Where(m => m.FireModeId / 2 == frag))
        {
            Assert.Equal(WeaponListLayouts.FireModeTypeThrowable, mode.Type);
            Assert.Equal(Rulings.Throwables.ThrowWindupMs, mode.FireDurationMs);
            Assert.Equal(AugustWeaponTable.ThrowableRefireTimeMs, mode.RefireTimeMs);
            Assert.Equal(AugustWeaponTable.ThrowableReloadTimeMs, mode.ReloadTimeMs);
            Assert.False(mode.IronSights);
            Assert.Equal(0u, mode.MeleeAbilityId);
        }

        foreach (FireModeRecord mode in on.FireModes)
        {
            if (!AugustFireModeFacts.TryGet(mode.FireModeId, out AugustFireModeFact fact))
            {
                continue;   // the frag's ruled modes, asserted above
            }

            bool throwable = AugustWeaponTable.ThrowableFireGroupIds.Contains(fact.FireGroupId);
            Assert.Equal(throwable ? Rulings.Throwables.ThrowWindupMs : 0, mode.FireDurationMs);
        }

        foreach (FireModeRecord mode in off.FireModes)
        {
            Assert.Equal(0, mode.FireDurationMs);
        }

        byte[] onBytes = on.ToArray();
        byte[] offBytes = off.ToArray();
        Assert.NotEqual(onBytes.Length, offBytes.Length);
        output.WriteLine($"WeaponDefinitions: {offBytes.Length} B off, {onBytes.Length} B on "
            + $"(sha256 {Convert.ToHexString(SHA256.HashData(onBytes)).ToLowerInvariant()})");
    }

    /// <summary>The shipped default blob, pinned - the docs/120 shape.</summary>
    [Fact]
    public void TheShippedThrowableBlobIsPinned()
    {
        // docs/123 (D313): this pin is the GENERATED table's shape; the captured table is
        // pinned by CapturedWeaponTableTests.
        byte[] shipped = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
            new WeaponSession(WeaponStageOptions.Default with
        {
            WeaponTable = WeaponTableSource.Generated,
        }).Blob);
        string sha = Convert.ToHexString(SHA256.HashData(shipped)).ToLowerInvariant();
        output.WriteLine($"{shipped.Length} {sha}");
        Assert.Equal(ShippedThrowableBlobLength, shipped.Length);
        Assert.Equal(ShippedThrowableBlobSha256, sha);
    }

    /// <summary>
    /// docs/120: the wave-17/docs-121 blob (89801) + one list-1 record + one list-0 record + two
    /// 639-byte list-2 records + ten 16-byte list-4 rows = 91437. Re-pin when a lane moves the
    /// blob; the length is what says a record was added, the sha what says a value moved.
    /// </summary>
    private const int ShippedThrowableBlobLength = 91737;

    private static string NameOf(uint effectId) => AugustEffectCatalog.ById(effectId)?.Name ?? string.Empty;

    private const string ShippedThrowableBlobSha256 =
        // RANGE and launch pitch preserved; systematic shotgun adds 300 bytes in lists 6/7.
        "dbfed14fc0e5a2b2580ffcc1a065cc279680cf17a5a2a500916001a46e4a783b";

    [Fact]
    public void TheFragTailWieldsLikeTheOtherGrenades()
    {
        WeaponItemAddTail? frag = AugustWeaponTable.CreateTail(65u);
        WeaponItemAddTail? gas = AugustWeaponTable.CreateTail(2237u);
        Assert.NotNull(frag);
        Assert.NotNull(gas);
        Assert.Equal(Rulings.Throwables.FragFireGroupId, frag.Groups[0].FireGroupId);
        Assert.Equal(44u, gas.Groups[0].FireGroupId);
        Assert.False(frag.AmmoSlot);
        Assert.False(gas.AmmoSlot);
        Assert.Equal(gas.Length, frag.Length);

        var session = new WeaponSession(Control);
        Assert.Null(session.CreateTail(65u));
        Assert.NotNull(session.CreateTail(2237u));
    }

    [Theory]
    [InlineData(null, null, null, true, 30f, 400)]
    [InlineData("0", null, null, false, 30f, 400)]
    [InlineData("1", "12.5", "250", true, 12.5f, 250)]
    [InlineData(null, "abc", "0", true, 30f, 1)]
    public void TheSwitchesReadTheEnvironment(
        string? throwables, string? speed, string? windup, bool expectOn, float expectSpeed, int expectWindup)
    {
        WeaponStageOptions options = WeaponStageOptions.FromEnvironment(name => name switch
        {
            WeaponStageOptions.ThrowablesVariable => throwables,
            WeaponStageOptions.ThrowableSpeedVariable => speed,
            WeaponStageOptions.ThrowableWindupVariable => windup,
            _ => null,
        });

        Assert.Equal(expectOn, options.Throwables);
        Assert.Equal(expectSpeed, options.ThrowableSpeed);
        Assert.Equal(expectWindup, options.ThrowableWindupMs);
        Assert.Contains(expectOn ? "throwables=ON" : "throwables=off", options.Describe(), StringComparison.Ordinal);
    }
}
