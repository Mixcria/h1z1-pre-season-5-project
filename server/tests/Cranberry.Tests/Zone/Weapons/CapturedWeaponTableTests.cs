using System.Security.Cryptography;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Weapons;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// <b>docs/123 - the friend's captured 1087 table crossed into the 1148 writers (D313), and Z1's
/// 131 projectile definitions (D314).</b>
/// <para>
/// The lane's whole premise is that a record whose LENGTH moves misaligns every record after it and
/// kills the client, so every test here is either a length theory or a value read straight back out
/// of the bytes that would go on the wire.
/// </para>
/// </summary>
public sealed class CapturedWeaponTableTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ the decode's own shape

    /// <summary>
    /// <b>The decoder's length theory.</b> Every row <c>tools/weapons/decode-friend-table.py</c>
    /// produced has exactly the width the 1148 writer will consume: one word per crossed offset, 17
    /// per cone-of-fire element, 23 per aim-assist record, 43 per projectile row. A generator that
    /// emitted one value too few or too many could not pass this and could not reach a wire.
    /// </summary>
    [Fact]
    public void EveryGeneratedFactRowHasTheWidthItsWriterConsumes()
    {
        Assert.Equal(CapturedWeaponFacts.CrossedOffsets.Length, CapturedWeaponFacts.CrossedNames.Length);
        Assert.Equal(CapturedWeaponFacts.FireModeRowCount, CapturedWeaponFacts.FireModes.Length);
        Assert.Equal(CapturedWeaponFacts.ConeOfFireRowCount, CapturedWeaponFacts.ConeOfFire.Length);
        Assert.Equal(CapturedWeaponFacts.AimAssistRowCount, CapturedWeaponFacts.AimAssist.Length);

        foreach (CapturedFireModeRow row in CapturedWeaponFacts.FireModes)
        {
            Assert.Equal(CapturedWeaponFacts.CrossedOffsets.Length, row.Words.Length);
        }

        foreach (CapturedConeOfFireRow group in CapturedWeaponFacts.ConeOfFire)
        {
            foreach (CapturedConeOfFireState state in group.States)
            {
                Assert.Equal(ConeOfFireStateRow.WordCount, state.Words.Length);

                // The 1087 element carried a one-byte FLAGS equal to its own GROUP_ID; that
                // redundancy is exactly what the 1148 element (72 B, not 73) dropped.
                Assert.Equal(state.StateId, state.Flags);
            }
        }

        foreach (CapturedAimAssistRow row in CapturedWeaponFacts.AimAssist)
        {
            Assert.Equal(AimAssistRecord.WordCount, row.Words.Length);
        }

        Assert.Equal(Z1ProjectileFacts.RowCount, Z1ProjectileFacts.Rows.Length);
        foreach (Z1ProjectileRow row in Z1ProjectileFacts.Rows)
        {
            Assert.Equal(ProjectileDefinitionRecord.RawWordCount, row.Words.Length);
        }

        // Every crossed offset is a field the 1148 body actually writes, exactly once.
        foreach (short offset in CapturedWeaponFacts.CrossedOffsets)
        {
            Assert.Single(WeaponListLayouts.FireModeBody, field => field.RecordOffset == offset);
        }
    }

    // ------------------------------------------------------------------------ the weapon table

    /// <summary>
    /// The captured table adds lists 3 and 5 and changes list-2 values, and <b>changes no record's
    /// length</b>: the blob grows by exactly the two new lists' own bytes and by nothing else.
    /// </summary>
    [Fact]
    public void TheCapturedBlobGrowsByAddedListsAndAnimationStrings()
    {
        var session = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false });
        WeaponDefinitionsBlob generated = session.GeneratedBlob;
        WeaponDefinitionsBlob captured = session.Blob;

        Assert.Empty(generated.ConeOfFire ?? []);
        Assert.Empty(generated.AimAssist ?? []);
        Assert.Equal(CapturedWeaponFacts.ConeOfFireRowCount, (captured.ConeOfFire ?? []).Count);
        Assert.Equal(CapturedWeaponFacts.AimAssistRowCount, (captured.AimAssist ?? []).Count);

        int listThree = (captured.ConeOfFire ?? []).Sum(record => record.Length);
        int listFive = (captured.AimAssist ?? []).Sum(record => record.Length);
        int pelletPatterns = (captured.List6 ?? []).Sum(record => record.Length)
            + (captured.List7 ?? []).Sum(record => record.Length);

        int animationStrings = captured.WeaponDefinitions!.Sum(record =>
            System.Text.Encoding.UTF8.GetByteCount(record.AnimationSetName));
        Assert.Equal(generated.Length + listThree + listFive + animationStrings + pelletPatterns, captured.Length);
        Assert.Equal(captured.Length, captured.ToArray().Length);

        // Every list-2 record is still 639 bytes; the crossing writes at each field's own width.
        foreach (FireModeRecord mode in captured.FireModes ?? [])
        {
            Assert.Equal(FireModeRecord.RecordLength, mode.Length);
        }

        // A list-5 record is 96 bytes and a list-3 element 72 - both exact, both pinned.
        foreach (AimAssistRecord assist in captured.AimAssist ?? [])
        {
            Assert.Equal(WeaponListLayouts.List5RecordLength, assist.Length);
        }

        foreach (ConeOfFireRecord cone in captured.ConeOfFire ?? [])
        {
            Assert.Equal(
                ConeOfFireRecord.MinimalLength
                    + (WeaponListLayouts.List3ElementLength * (cone.States ?? []).Count),
                cone.Length);
        }

        output.WriteLine(
            $"generated {generated.Length} B -> captured {captured.Length} B "
                + $"(+{listThree} list 3, +{listFive} list 5), "
                + $"{CapturedWeaponTable.CrossedFireModeCount(generated)} of "
                + $"{(generated.FireModes ?? []).Count} fire modes crossed; sha256 "
                + Convert.ToHexString(SHA256.HashData(captured.ToArray())).ToLowerInvariant());
    }

    /// <summary>
    /// <c>CRANBERRY_WEAPON_TABLE=generated</c> is a real revert: byte for byte the blob the wave
    /// before docs/123 shipped, and it is the shape every pinned-sha test still asserts.
    /// </summary>
    [Fact]
    public void TheGeneratedSwitchIsAByteForByteRevert()
    {
        var captured = new WeaponSession(WeaponStageOptions.Default with
        {
            WeaponTable = WeaponTableSource.Captured,
            Z1LiveGunplay = false,
        });
        var generated = new WeaponSession(WeaponStageOptions.Default with
        {
            WeaponTable = WeaponTableSource.Generated,
        });

        Assert.Equal(AugustShotgunPattern.Apply(generated.GeneratedBlob).ToArray(), generated.Blob.ToArray());
        Assert.NotEqual(generated.Blob.ToArray(), captured.Blob.ToArray());
        // Captured is the default again: the 20:12 crash was the 72-byte list-3 element, fixed
        // (73 with the FLAGS byte at elem+0x20 - docs/123 §7).
        Assert.Equal(WeaponTableSource.Captured, WeaponStageOptions.Default.WeaponTable);
        Assert.Equal(WeaponTableSource.Generated, WeaponStageOptions.AllOff.WeaponTable);
    }

    /// <summary>
    /// With list 2 empty - the 32-byte empty envelope, or <c>CRANBERRY_WEAPON_DEFS_LIST2=0</c> -
    /// the capture changes nothing: nothing can name a cone-of-fire group or an aim-assist config,
    /// so shipping them would be records no fire mode reaches.
    /// </summary>
    [Fact]
    public void TheCaptureIsInertWhenListTwoIsEmpty()
    {
        var envelope = new WeaponSession(new WeaponStageOptions
        {
            SendWeaponDefinitions = true,
            PopulateFireGroups = false,
            PopulateWeaponDefinitions = false,
        });
        Assert.Equal(WeaponDefinitionsBlob.EmptyLength, envelope.Blob.Length);
        Assert.Equal(new byte[WeaponDefinitionsBlob.EmptyLength], envelope.Blob.ToArray());

        var noListTwo = new WeaponSession(WeaponStageOptions.Default with
        {
            PopulateFireModes = false,
            FastLongGunDraw = false,
        });
        Assert.Equal(noListTwo.GeneratedBlob.ToArray(), noListTwo.Blob.ToArray());
    }

    /// <summary>
    /// <b>The four accuracy deltas of docs/122 §1 A2 (D317-D326), read back out of the bytes.</b>
    /// They are not patched on top of the capture - they ARE the capture, and this test proves the
    /// weapon-id join actually delivers them to the fire modes August's own sheet names.
    /// </summary>
    [Fact]
    public void TheFourAccuracyDeltasReachTheShippedBytes()
    {
        WeaponDefinitionsBlob blob = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }).Blob;

        foreach (CapturedWeaponTable.AccuracyDelta delta in CapturedWeaponTable.AccuracyDeltas)
        {
            uint fireGroupId = FireGroupOf(delta.WeaponDefinitionId);
            uint fireModeId = AugustWeaponTable.FireModeIdFor(fireGroupId, delta.ModeIndex);
            FireModeRecord mode = Assert.Single(
                blob.FireModes ?? [], record => record.FireModeId == fireModeId);

            float value = ReadField(mode, delta.Column);

            output.WriteLine(
                $"{delta.Ruling} weapon {delta.WeaponDefinitionId} mode {delta.ModeIndex} "
                    + $"({fireModeId}) {delta.Column} = {value}");
            // D319's earlier captured eight-pellet count is superseded after crossing.
            float expected = delta.WeaponDefinitionId == AugustShotgunPattern.WeaponId
                && delta.Column == "PELLETS_PER_SHOT" ? AugustShotgunPattern.PelletCount : delta.Value;
            Assert.Equal(expected, value, 3);
        }
    }

    /// <summary>
    /// The capture supplies the <c>AUTOMATIC</c> bit itself, and it disagrees with the generated
    /// D331 set in the direction docs/121 already ruled: the AK-47 is automatic, the AR-15 is not.
    /// </summary>
    [Fact]
    public void TheCapturedFlagByteCarriesTheAutomaticBit()
    {
        WeaponDefinitionsBlob blob = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }).Blob;

        Assert.True(IsAutomatic(blob, 1405));    // AK-47
        Assert.False(IsAutomatic(blob, 6));      // AR-15
        Assert.False(IsAutomatic(blob, 1374));   // 12GA pump
        Assert.False(IsAutomatic(blob, 1388));   // .44 Magnum

        static bool IsAutomatic(WeaponDefinitionsBlob blob, uint weaponId)
        {
            uint fireModeId = AugustWeaponTable.FireModeIdFor(FireGroupOf(weaponId), 0);
            FireModeRecord mode = Assert.Single(
                blob.FireModes ?? [], record => record.FireModeId == fireModeId);
            return (RawField(mode, WeaponListLayouts.FireModeFlags0)
                & WeaponListLayouts.FireModeAutomaticFlag) != 0;
        }
    }

    /// <summary>
    /// <b>The AMMO_ID audit (docs/123 §5).</b> Every captured ammo slot names <c>1</c> - a 1087
    /// slot index, not a <c>ClientItemDefinitions</c> row - so the slot ids are NOT adopted; but the
    /// capture's own fire modes name August's real round ids, which is the independent confirmation
    /// that <c>AmmoTypes.ByWeaponDefinitionId</c> is right.
    /// </summary>
    [Fact]
    public void TheCapturedAmmoSlotIdsAreASentinelAndTheFireModeAmmoIdsAreAugustsOwn()
    {
        foreach (CapturedWeaponRow row in CapturedWeaponFacts.Weapons)
        {
            Assert.True(row.AmmoSlotAmmoId is 0 or CapturedWeaponTable.CapturedAmmoSlotSentinel);
        }

        // ...and Cranberry keeps writing the August round id, which no captured slot carries.
        foreach ((uint weaponId, uint ammoId) in AmmoTypes.ByWeaponDefinitionId)
        {
            Assert.NotEqual(CapturedWeaponTable.CapturedAmmoSlotSentinel, ammoId);
            output.WriteLine($"weapon {weaponId}: August ammo {ammoId}, captured slot id "
                + $"{CapturedWeaponFacts.Weapons.FirstOrDefault(r => r.WeaponDefinitionId == weaponId).AmmoSlotAmmoId}");
        }
    }

    /// <summary>The merge arithmetic the banner reports.</summary>
    [Fact]
    public void TheMergeCountsAreConsistent()
    {
        int august = CapturedWeaponTable.SharedWeaponDefinitionIds.Count
            + CapturedWeaponTable.AugustOnlyWeaponDefinitionIds.Count;
        Assert.Equal(
            AugustWeaponFacts.All.Select(f => f.WeaponId).Where(id => id != 0).Distinct().Count(),
            august);
        Assert.Empty(CapturedWeaponTable.SharedWeaponDefinitionIds
            .Intersect(CapturedWeaponTable.AugustOnlyWeaponDefinitionIds));

        output.WriteLine(
            $"August weapon definition ids {august}: "
                + $"{CapturedWeaponTable.SharedWeaponDefinitionIds.Count} also in the capture, "
                + $"{CapturedWeaponTable.AugustOnlyWeaponDefinitionIds.Count} August-only "
                + $"(filled from the generated table): "
                + string.Join(", ", CapturedWeaponTable.AugustOnlyWeaponDefinitionIds.Order()));
    }

    /// <summary>The banner says which table shipped, and which rulings went with it.</summary>
    [Fact]
    public void TheBannerNamesTheTableThatShipped()
    {
        string captured = (WeaponStageOptions.Default with
        {
            WeaponTable = WeaponTableSource.Captured,
            Z1LiveGunplay = false,
        }).Describe();
        Assert.Contains("table=captured", captured, StringComparison.Ordinal);
        Assert.Contains("projectileTable=z1", captured, StringComparison.Ordinal);
        Assert.Contains("D331 automatic and D332 pellets NOT applied", captured, StringComparison.Ordinal);
        output.WriteLine(captured);

        string generated = (WeaponStageOptions.Default with
        {
            WeaponTable = WeaponTableSource.Generated,
            ProjectileTable = ProjectileTableSource.August,
        }).Describe();
        Assert.Contains("table=generated", generated, StringComparison.Ordinal);
        Assert.Contains("projectileTable=august", generated, StringComparison.Ordinal);
        output.WriteLine(generated);
    }

    /// <summary>Only the exact strings flip the two new levers; a typo leaves the default.</summary>
    [Theory]
    [InlineData(null, null, WeaponTableSource.Captured, ProjectileTableSource.Z1)]
    [InlineData("generated", "august", WeaponTableSource.Generated, ProjectileTableSource.August)]
    [InlineData("captured", "z1", WeaponTableSource.Captured, ProjectileTableSource.Z1)]
    [InlineData("Generated", "AUGUST", WeaponTableSource.Captured, ProjectileTableSource.Z1)]
    public void TheTwoLeversReadTheEnvironment(
        string? table, string? projectiles,
        WeaponTableSource expectedTable, ProjectileTableSource expectedProjectiles)
    {
        WeaponStageOptions options = WeaponStageOptions.FromEnvironment(name => name switch
        {
            WeaponStageOptions.WeaponTableVariable => table,
            WeaponStageOptions.ProjectileTableVariable => projectiles,
            _ => null,
        });

        Assert.Equal(expectedTable, options.WeaponTable);
        Assert.Equal(expectedProjectiles, options.ProjectileTable);
    }

    // -------------------------------------------------------------------- the projectile table

    /// <summary>
    /// Z1's 131 rows go out through Cranberry's own writer and envelope, and every record is the
    /// [P] 1148 length: <c>184</c> plus its three strings and its radius array. The blob is decoded
    /// back out of the LZ4 block it ships in, so this is a round trip and not a length theory.
    /// </summary>
    [Fact]
    public void TheZ1ProjectileTableRoundTripsAtTheDeclaredLengths()
    {
        ProjectileDefinitionsBlob blob = Z1ProjectileTable.CreateBlob(
            populate: true, throwables: true, throwSpeed: Rulings.Throwables.ThrowSpeed);

        byte[] wire = blob.ToArray();
        byte[] table = Lz4LiteralBlock.Decode(
            wire.AsSpan(ProjectileDefinitionsBlob.HeaderLength));

        int expected = 4 + blob.Projectiles!.Sum(record => record.Length);
        Assert.Equal(expected, table.Length);
        Assert.Equal(
            Z1ProjectileFacts.RowCount + AugustThrowables.All.Count,
            blob.Projectiles!.Count);
        Assert.Equal(
            blob.Projectiles!.Count,
            BitConverter.ToInt32(table.AsSpan(0, 4)));

        foreach (ProjectileDefinitionRecord record in Z1ProjectileTable.Records)
        {
            Assert.Equal(
                ProjectileDefinitionRecord.MinimalLength
                    + System.Text.Encoding.UTF8.GetByteCount(record.ModelFileName)
                    + System.Text.Encoding.UTF8.GetByteCount(record.AttachmentOverride)
                    + System.Text.Encoding.UTF8.GetByteCount(record.FirstPersonModelFileName)
                    + (4 * (record.BulletRadii?.Count ?? 0)),
                record.Length);
        }

        output.WriteLine($"Z1 projectile table: {table.Length} body bytes, {wire.Length} on the wire");
    }

    /// <summary>
    /// <c>CRANBERRY_PROJECTILE_TABLE=august</c> is a real revert - the 14 constructor-default
    /// records, byte for byte.
    /// </summary>
    [Fact]
    public void TheAugustProjectileSwitchIsAByteForByteRevert()
    {
        var august = new WeaponSession(WeaponStageOptions.Default with
        {
            ProjectileTable = ProjectileTableSource.August,
        });
        Assert.True(august.TryCreateProjectileDefinitions(out ReferenceData augustPacket));

        var z1 = new WeaponSession(WeaponStageOptions.Default);
        Assert.True(z1.TryCreateProjectileDefinitions(out ReferenceData z1Packet));

        Assert.Equal(
            AugustProjectileTable
                .CreateBlob(true, WeaponStageOptions.Default.Throwables,
                    WeaponStageOptions.Default.ThrowableSpeed)
                .ToArray(),
            augustPacket.Payload);
        Assert.NotEqual(augustPacket.Payload, z1Packet.Payload);
        Assert.Equal(ProjectileTableSource.Z1, WeaponStageOptions.Default.ProjectileTable);
        Assert.Equal(ProjectileTableSource.August, WeaponStageOptions.AllOff.ProjectileTable);
    }

    /// <summary>
    /// <b>The effect audit (docs/123 §6).</b> No effect id survives into the shipped projectile
    /// records that <see cref="AugustEffectCatalog"/> - the build's own 1,047 definitions - cannot
    /// resolve. The ones that were zeroed are reported, because they are what this build lacks.
    /// </summary>
    [Fact]
    public void NoShippedProjectileNamesAnEffectThisBuildCannotResolve()
    {
        foreach (Z1ProjectileRow row in Z1ProjectileFacts.Rows)
        {
            foreach (int column in Z1ProjectileFacts.EffectColumnIndices)
            {
                uint id = row.Words[column];
                if (id != 0 && !AugustEffectCatalog.Contains(id))
                {
                    output.WriteLine(
                        $"projectile {row.ProjectileId} {Z1ProjectileFacts.Columns[column]} "
                            + $"= {id} is not in this build - zeroed");
                }
            }
        }

        foreach (ProjectileDefinitionRecord record in Z1ProjectileTable.Records)
        {
            foreach (int column in Z1ProjectileFacts.EffectColumnIndices)
            {
                uint id = record.RawWords![column];
                Assert.True(id == 0 || AugustEffectCatalog.Contains(id),
                    $"projectile {record.ProjectileId} names effect {id}");
            }
        }

        output.WriteLine(
            $"zeroed {Z1ProjectileTable.ZeroedEffectCellCount} cell(s) over "
                + $"{Z1ProjectileTable.ZeroedEffectIds.Count} distinct id(s): "
                + string.Join(", ", Z1ProjectileTable.ZeroedEffectIds));
    }

    /// <summary>
    /// <b>Every list-4 row resolves.</b> A fire mode that reaches a projectile id the shipped
    /// projectile table has no record for spawns nothing, which is the dead-trigger feel docs/122
    /// opens with - so the two tables are checked against each other, both ways round.
    /// </summary>
    [Fact]
    public void EveryShippedFireModeProjectileIdResolvesAgainstTheShippedProjectileTable()
    {
        var session = new WeaponSession(WeaponStageOptions.Default);
        WeaponDefinitionsBlob weapons = session.Blob;
        Assert.True(session.TryCreateProjectileDefinitions(out _));

        HashSet<uint> shipped = [..
            Z1ProjectileTable
                .RecordsFor(WeaponStageOptions.Default.Throwables,
                    WeaponStageOptions.Default.ThrowableSpeed)
                .Select(record => record.ProjectileId)];

        List<uint> unresolved = [..
            (weapons.FireModeProjectiles ?? [])
                .Select(row => row.ProjectileDefinitionId)
                .Where(id => !shipped.Contains(id))
                .Distinct()
                .Order()];

        output.WriteLine(
            $"{(weapons.FireModeProjectiles ?? []).Count} list-4 rows over "
                + $"{shipped.Count} shipped projectiles; unresolved: "
                + (unresolved.Count == 0 ? "none" : string.Join(", ", unresolved)));
        Assert.Empty(unresolved);
    }

    // ------------------------------------------------------------------------------- helpers

    [Theory]
    [InlineData(6u, 0.3185f, 1.2f)]
    [InlineData(1405u, 0.35f, 1.2f)]
    public void AdsUsesTheWorkingZ1MovementAndZoom(uint weaponId, float movement, float zoom)
    {
        var blob = new WeaponSession(WeaponStageOptions.Default with { Z1LiveGunplay = false }).Blob;
        uint group = FireGroupOf(weaponId);
        var hip = Assert.Single(blob.FireModes!, row => row.FireModeId == group * 2);
        var ads = Assert.Single(blob.FireModes!, row => row.FireModeId == group * 2 + 1);
        Assert.Equal(1f, ReadField(hip, "MOVEMENT_MODIFIER"), 4);
        Assert.Equal(movement, ReadField(ads, "MOVEMENT_MODIFIER"), 4);
        Assert.Equal(zoom, ReadField(ads, "DEFAULT_ZOOM"), 4);
        Assert.Equal(0, Assert.Single(blob.FireGroups!, row => row.FireGroupId == group).Flags);
        var definition = Assert.Single(blob.WeaponDefinitions!, row => row.WeaponDefinitionId == weaponId);
        Assert.Equal(ResponsiveWeaponHandling.EquipTimeMs, definition.EquipTimeMs);
        Assert.Equal(150u, definition.UnequipTimeMs);
        Assert.Equal(266u, definition.AimInAnimationTimeMs);
        Assert.Equal(266u, definition.AimOutAnimationTimeMs);
        Assert.Equal(300u, definition.SprintRecoveryTimeMs);
        Assert.Equal("Pistol", definition.AnimationSetName);
    }

    private static uint FireGroupOf(uint weaponDefinitionId) =>
        AugustWeaponFacts.All.First(fact => fact.WeaponId == weaponDefinitionId).FireGroupId;

    private static short OffsetOf(string column) =>
        WeaponListLayouts.FireModeColumns.First(c => c.Name == column).RecordOffset;

    /// <summary>The raw little-endian word this record writes at <paramref name="offset"/>.</summary>
    private static uint RawField(FireModeRecord record, short offset)
    {
        using var writer = new PacketWriter(FireModeRecord.RecordLength);
        record.WriteTo(writer);
        byte[] bytes = writer.Written.ToArray();

        int cursor = 8;   // the id and rec+0x18, ahead of the body
        foreach (WeaponListField field in WeaponListLayouts.FireModeBody)
        {
            if (field.RecordOffset == offset)
            {
                return field.Size switch
                {
                    1 => bytes[cursor],
                    2 => BitConverter.ToUInt16(bytes, cursor),
                    _ => BitConverter.ToUInt32(bytes, cursor),
                };
            }

            cursor += field.Size;
        }

        throw new InvalidOperationException($"no field at rec+0x{offset:x3}");
    }

    /// <summary>
    /// The same word, read as the number the client's own row LOADER says it is
    /// (<see cref="FireModeColumnKind"/>: <c>MOVSS</c> = float, the string-to-integer helper =
    /// int). The body reader's <see cref="WeaponListFieldKind"/> only fixes the WIDTH - it takes
    /// <c>COF_RECOIL</c>, a float, with its four-byte integer reader - so it is not the authority
    /// on what a value means.
    /// </summary>
    private static float ReadField(FireModeRecord record, string column)
    {
        short offset = OffsetOf(column);
        uint raw = RawField(record, offset);
        FireModeColumn declared = WeaponListLayouts.FireModeColumns.First(c => c.Name == column);
        WeaponListField field = WeaponListLayouts.FireModeBody.First(f => f.RecordOffset == offset);

        return declared.Kind == FireModeColumnKind.Float
            ? BitConverter.UInt32BitsToSingle(raw)
            : field.Kind == WeaponListFieldKind.Int16
                ? (short)raw
                : raw;
    }

    /// <summary>
    /// docs/123 §7 - the 20:12 session on 2026-09-04 died the moment the Z1 projectile table
    /// landed, and 16 of its 131 rows named a model the August packs do not carry (RPG rockets,
    /// snowballs, tank shells, vehicle debris - Planetside leftovers in the 2016 sheet). Every
    /// model a shipped record names must be a SHIPPING row of <see cref="AugustModelCatalog"/>;
    /// the ones that are not ship blank, which the local spawn turns into
    /// <c>InvisibleTriangle.adr</c>. Sixteen names, and not one of them is a bullet or a grenade
    /// the August game uses.
    /// </summary>
    [Fact]
    public void EveryZ1ProjectileModelTheAugustClientCannotLoadShipsBlank()
    {
        HashSet<string> shipping = new(
            AugustModelCatalog.Rows.Where(m => m.Ships).Select(m => m.FileName),
            StringComparer.OrdinalIgnoreCase);

        foreach (ProjectileDefinitionRecord record in Z1ProjectileTable.Records)
        {
            Assert.True(
                record.ModelFileName.Length == 0 || shipping.Contains(record.ModelFileName),
                $"projectile {record.ProjectileId} names {record.ModelFileName}, which August cannot load");
        }

        Assert.Equal(16, Z1ProjectileTable.BlankedModelNames.Count);
        Assert.Contains("Projectile_Rockets_RPG7.adr", Z1ProjectileTable.BlankedModelNames);
        Assert.Contains("Projectile_Snowball.adr", Z1ProjectileTable.BlankedModelNames);
        Assert.DoesNotContain("Projectile_Grenades_GasGrenade.adr", Z1ProjectileTable.BlankedModelNames);

        // The August game's own projectiles keep their models.
        Assert.Contains(
            Z1ProjectileTable.Records,
            r => r.ModelFileName.Length > 0 && r.ModelFileName.Contains("Grenade", StringComparison.Ordinal));
    }

    /// <summary>
    /// D339 - the reload key's second word. The client's reload predicate counts the bag by the
    /// fire mode's own AMMO_ITEM_ID (rec+0x2c); a 0 there made R silent with 120 rounds in the
    /// bag (2026-09-04 21:05). Every armed mode now carries its weapon's August ammo item.
    /// </summary>
    [Fact]
    public void EveryArmedModeCarriesItsAmmoItemIdForTheReloadKey()
    {
        // AR-15 def 6 -> group 6 -> modes 12/13 -> .223 (1429); pump 1374 -> group 16 -> modes 32/33 -> 1511
        Assert.Equal(1429u, CapturedWeaponTable.OverridesFor(12)![WeaponListLayouts.FireModeAmmoItemId]);
        Assert.Equal(1429u, CapturedWeaponTable.OverridesFor(13)![WeaponListLayouts.FireModeAmmoItemId]);
        Assert.Equal(1511u, CapturedWeaponTable.OverridesFor(32)![WeaponListLayouts.FireModeAmmoItemId]);

        var session = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = WeaponTableSource.Captured });
        int armedWithAmmo = 0;
        foreach (FireModeRecord mode in session.Blob.FireModes!)
        {
            if (AugustWeaponTable.ArmedFireGroupIds.Contains(mode.FireModeId / 2)
                && mode.Overrides is { } o
                && o.TryGetValue(WeaponListLayouts.FireModeAmmoItemId, out uint ammo)
                && ammo != 0)
            {
                armedWithAmmo++;
            }
        }

        Assert.True(armedWithAmmo >= 20, $"only {armedWithAmmo} armed modes carry an AMMO_ITEM_ID");
    }

    /// <summary>
    /// Set <c>CRANBERRY_DUMP_WEAPON_BLOB=&lt;path&gt;</c> to write the captured-table blob body (the
    /// bytes after the ReferenceData header) to that file, so the client's own list frames
    /// (<c>out\ghidra-aug\w19-verify\walk_client.py</c>) can walk it without a client. The test
    /// itself only asserts the arithmetic: list 3 is <c>12 + 73n</c> per record.
    /// </summary>
    [Fact]
    public void TheCapturedBlobCanBeDumpedForTheClientFrameWalker()
    {
        var session = new WeaponSession(WeaponStageOptions.Default with
        {
            WeaponTable = WeaponTableSource.Captured,
            Z1LiveGunplay = false,
        });
        byte[] body = session.Blob.ToArray();

        int list3 = CapturedWeaponTable.ConeOfFireRecords.Sum(
            r => 12 + (WeaponListLayouts.List3ElementLength * (r.States ?? []).Count));
        Assert.Equal(12 + (73 * 6), 12 + (WeaponListLayouts.List3ElementLength * 6));
        Assert.True(list3 > 0);

        string? path = Environment.GetEnvironmentVariable("CRANBERRY_DUMP_WEAPON_BLOB");
        if (!string.IsNullOrEmpty(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, body);
            output.WriteLine($"wrote {body.Length} bytes to {path}");
        }
    }
}
