using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// <c>WeaponDefinitions</c> lists 2-7 and <c>ReferenceData "ProjectileDefinitions"</c> - the
/// 2026-09-02 overhaul lane 2D, which closes docs/58 U2 and U7. Derivation: docs/99.
/// <para>
/// <b>What these tests can and cannot prove.</b> They prove the bytes match the layouts recovered
/// by decompiling <c>FUN_140a422d0</c>, <c>FUN_140a50040</c>, <c>FUN_140a41f00</c>,
/// <c>FUN_140a39ca0</c>, <c>FUN_140a2c1b0</c>, <c>FUN_140a4fbf0</c>, <c>FUN_140a3f970</c>,
/// <c>FUN_140a4cbf0</c>, <c>FUN_140a40e40</c> and <c>FUN_140a40410</c>, and that every value
/// Cranberry writes into a scalar field is the value the client's own record constructors
/// (<c>FUN_1422267e0</c>, <c>FUN_142224d50</c>) preset there. They prove <b>nothing</b> about the
/// client: not one byte of either table has ever been on a wire.
/// </para>
/// </summary>
public sealed class WeaponListsTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>Byte position of a fire-mode record offset inside the 631-byte body.</summary>
    private static int BodyPositionOf(short recordOffset)
    {
        int position = 0;
        foreach (WeaponListField field in WeaponListLayouts.FireModeBody)
        {
            if (field.RecordOffset == recordOffset)
            {
                return position;
            }

            position += field.Size;
        }

        throw new InvalidOperationException($"rec+0x{recordOffset:x} is not written by FUN_140a422d0");
    }

    // ------------------------------------------------------------------ the layout itself

    /// <summary>
    /// <b>The 631 is checked twice, independently.</b> Once by summing the 180 reads
    /// <c>FUN_140a422d0</c> makes, and once by the tamper checksum in <c>FUN_140a39b80</c>: it XORs
    /// <c>0x59</c> whole qwords from <c>rec+0x20</c> and then one more, masked to its high half -
    /// so the qword it half-covers begins at <c>rec+0x20 + 0x59*8 = rec+0x2e8</c>, which is exactly
    /// the offset of the reader's last field. Two decodes that disagreed would disagree here.
    /// </summary>
    [Fact]
    public void TheFireModeBodyIsSixHundredAndThirtyOneBytesAndEndsWhereTheChecksumDoes()
    {
        Assert.Equal(180, WeaponListLayouts.FireModeBody.Count);
        Assert.Equal(
            WeaponListLayouts.FireModeBodyLength,
            WeaponListLayouts.FireModeBody.Sum(field => field.Size));
        Assert.Equal(631, WeaponListLayouts.FireModeBodyLength);

        short last = WeaponListLayouts.FireModeBody.Max(field => field.RecordOffset);
        Assert.Equal(0x2e8, last);

        // FUN_140a39b80: 0x59 whole qwords from rec+0x20, then one more masked to its high half.
        // That last, half-covered qword starts exactly on the reader's last field.
        const int lastChecksumQword = 0x20 + (0x59 * 8);
        Assert.Equal(0x2e8, lastChecksumQword);
        Assert.Equal(lastChecksumQword, (int)last);

        // No offset is written twice - a duplicate would mean the read order was mis-parsed.
        Assert.Equal(
            WeaponListLayouts.FireModeBody.Count,
            WeaponListLayouts.FireModeBody.Select(field => field.RecordOffset).Distinct().Count());
    }

    /// <summary>
    /// Every offset the client's own constructor <c>FUN_1422267e0</c> presets to <c>1.0f</c> is a
    /// four-byte field the wire reader actually writes. A preset the wire could not reach would
    /// mean one of the two decodes is wrong, so this is a cross-check between two functions that
    /// were read independently.
    /// </summary>
    [Fact]
    public void EveryUnitScalarIsAFourByteFieldTheBodyReaderWrites()
    {
        Assert.Equal(34, WeaponListLayouts.FireModeUnitScalars.Count);
        Dictionary<short, WeaponListField> byOffset =
            WeaponListLayouts.FireModeBody.ToDictionary(field => field.RecordOffset);

        foreach (short offset in WeaponListLayouts.FireModeUnitScalars)
        {
            Assert.True(byOffset.ContainsKey(offset), $"rec+0x{offset:x} is preset to 1.0f but never read");
            Assert.Equal(4, byOffset[offset].Size);
        }

        // The two the consumers confirm by name.
        Assert.Contains(WeaponListLayouts.FireModeMovementModifier, WeaponListLayouts.FireModeUnitScalars);
        Assert.Contains(WeaponListLayouts.FireModeTurnModifier, WeaponListLayouts.FireModeUnitScalars);
    }

    // ------------------------------------------------------------------ length theory, per list

    /// <summary>A fire-mode record is fixed-size: <c>4 + 4 + 631 = 639</c>, whatever it carries.</summary>
    [Fact]
    public void AFireModeRecordIsAlwaysSixHundredAndThirtyNineBytes()
    {
        byte[] bare = Bytes(new FireModeRecord(13).WriteTo);
        byte[] filled = Bytes(new FireModeRecord(
            13, DefinitionId: 13, RefireTimeMs: 100, ReloadTimeMs: 2600, PelletsPerShot: 12,
            AmmoSlot: 3, IronSights: true).WriteTo);

        Assert.Equal(FireModeRecord.RecordLength, bare.Length);
        Assert.Equal(639, bare.Length);
        Assert.Equal(bare.Length, filled.Length);
    }

    /// <summary>
    /// Lists 3-7 are <c>12 + 73n</c>, <c>16</c>, <c>96</c>, <c>12 + 16n</c>, <c>12 + 4n</c>. The
    /// list-3 element is 73, not 72: <c>FUN_140a41f00</c> reads one BYTE at <c>elem+0x20</c>
    /// between the first and the second word (the 2026-09-04 20:12 crash, docs/123 §7).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void ListsThreeToSevenFollowTheirLengthTheories(int elements)
    {
        var states = Enumerable.Range(0, elements)
            .Select(i => new ConeOfFireStateRow((uint)i))
            .ToArray();
        Assert.Equal(12 + (73 * elements), Bytes(new ConeOfFireRecord(1, 0, states).WriteTo).Length);
        Assert.Equal(73, WeaponListLayouts.List3ElementLength);

        Assert.Equal(16, Bytes(new FireModeProjectileRecord(1, 1429, 19002).WriteTo).Length);
        Assert.Equal(96, Bytes(new AimAssistRecord(1).WriteTo).Length);

        var sixes = Enumerable.Range(0, elements)
            .Select(i => new WeaponBlobList6Element((uint)i))
            .ToArray();
        Assert.Equal(12 + (16 * elements), Bytes(new WeaponBlobList6Record(1, 0, sixes).WriteTo).Length);

        uint[] sevens = [.. Enumerable.Range(0, elements).Select(i => (uint)i)];
        Assert.Equal(12 + (4 * elements), Bytes(new WeaponBlobList7Record(1, 0, sevens).WriteTo).Length);
    }

    /// <summary>
    /// The blob's own <c>Length</c> has to agree with the bytes it writes for every list, or a
    /// caller sizing a buffer from it would truncate the table.
    /// </summary>
    [Fact]
    public void TheBlobLengthAgreesWithTheBytesForEveryList()
    {
        var blob = new WeaponDefinitionsBlob(
            WeaponDefinitions: [new WeaponDefinitionRecord(6, [6u])],
            FireGroups: [new FireGroupRecord(6, FireGroupRecord.AutomaticFlag, [12u, 13u])],
            FireModes: [new FireModeRecord(12), new FireModeRecord(13)],
            ConeOfFire: [new ConeOfFireRecord(1, 0, [new ConeOfFireStateRow(0)])],
            FireModeProjectiles: [new FireModeProjectileRecord(12, 1429, 19002)],
            AimAssist: [new AimAssistRecord(1)],
            List6: [new WeaponBlobList6Record(1, 0, [new WeaponBlobList6Element(0)])],
            List7: [new WeaponBlobList7Record(1, 0, [7u])]);

        Assert.Equal(blob.Length, blob.ToArray().Length);
    }

    // ------------------------------------------------------------------ hex snapshots

    /// <summary>
    /// <b>Hex snapshot, list 2.</b> The first 24 bytes of the AR-15's first fire mode (id 12, D159
    /// numbering): the key, the trigger charge, the three bit-packed flag bytes and the first
    /// inline reads. If this moves, <c>FUN_140a422d0</c> has to be re-read before it is edited - a
    /// wrong length here does not produce a rejected packet, it silently misaligns lists 3-7.
    /// </summary>
    [Fact]
    public void TheFireModeRecordHexSnapshotIsStable()
    {
        byte[] bytes = Bytes(new FireModeRecord(12, DefinitionId: 12, RefireTimeMs: 100).WriteTo);

        Assert.Equal(
            "0c000000" +      // u32 fireModeId = 12                     -> rec+0x378 (hash key)
            "0c000000" +      // u32 definitionId = 12 (wave 14)          -> rec+0x18
            "000000" +        // u8 x3 the bit-packed flags, IRON_SIGHTS clear -> rec+0x20..0x22
            "00" +            // i8                                       -> rec+0x24
            "00000000" +      // u32                                      -> rec+0x2c
            "00" +            // i8 AMMO_SLOT = 0                         -> rec+0x30  [P]
            "00" +            // i8                                       -> rec+0x34
            "0000" +          // i16                                      -> rec+0x38
            "0000" +          // i16                                      -> rec+0x3c
            "6400",           // i16 RefireTime = 100 ms                  -> rec+0x40  [P]
            Convert.ToHexString(bytes.AsSpan(0, 24)).ToLowerInvariant());

        // Wave 14: the ADS mode. rec+0x20 bit 0x04 = IRON_SIGHTS (FUN_1422935d0:68) and rec+0x30
        // is the AMMO_SLOT index (FUN_14228fcf0:31), an i8 in the middle of the flag run.
        byte[] ads = Bytes(
            new FireModeRecord(13, DefinitionId: 13, AmmoSlot: 2, IronSights: true).WriteTo);
        Assert.Equal(
            "0d000000" +      // u32 fireModeId = 13
            "0d000000" +      // u32 definitionId = 13
            "040000" +        // u8 x3 - bit 0x04 set on the first byte    -> rec+0x20
            "00" +            // i8                                       -> rec+0x24
            "00000000" +      // u32                                      -> rec+0x2c
            "02",             // i8 AMMO_SLOT = 2                         -> rec+0x30
            Convert.ToHexString(ads.AsSpan(0, 17)).ToLowerInvariant());

        // And the two named multipliers really are 1.0f at the offsets the consumers read.
        Assert.Equal(
            1.0f,
            BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(8 + BodyPositionOf(WeaponListLayouts.FireModeMovementModifier))));
        Assert.Equal(
            1.0f,
            BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(8 + BodyPositionOf(WeaponListLayouts.FireModeTurnModifier))));
    }

    /// <summary>Hex snapshots of one minimal record of each of lists 3-7.</summary>
    [Fact]
    public void TheListsThreeToSevenHexSnapshotsAreStable()
    {
        // List 3: id, the +0x00 word, then the element count.
        Assert.Equal(
            "07000000" + "00000000" + "00000000",
            Convert.ToHexString(Bytes(new ConeOfFireRecord(7).WriteTo)).ToLowerInvariant());

        // List 4, the fire-mode to projectile mapping: the hash key (the fire mode's own
        // definition id), rec+0x00 [U], rec+0x04 AmmoSlot.AmmoId, rec+0x08 the projectile id.
        // 12 = the AR-15's mode 0, 1429 = the .223 Round, 19002 = the .223 projectile.
        Assert.Equal(
            "0c000000" + "00000000" + "95050000" + "3a4a0000",
            Convert.ToHexString(
                Bytes(new FireModeProjectileRecord(12, 1429, 19002).WriteTo)).ToLowerInvariant());

        // List 5: id then 23 words.
        byte[] aim = Bytes(new AimAssistRecord(7).WriteTo);
        Assert.Equal("07000000", Convert.ToHexString(aim.AsSpan(0, 4)).ToLowerInvariant());
        Assert.Equal(96, aim.Length);
        Assert.All(aim[4..], b => Assert.Equal(0, b));

        // List 6: id, the +0x18 word, the element count, then one 16-byte element.
        Assert.Equal(
            "07000000" + "00000000" + "01000000" + "09000000" + "00000000" + "00000000" + "00000000",
            Convert.ToHexString(
                Bytes(new WeaponBlobList6Record(7, 0, [new WeaponBlobList6Element(9)]).WriteTo))
                .ToLowerInvariant());

        // List 7: id, the +0x00 word, the count, then bare u32s.
        Assert.Equal(
            "07000000" + "00000000" + "02000000" + "0a000000" + "0b000000",
            Convert.ToHexString(Bytes(new WeaponBlobList7Record(7, 0, [10u, 11u]).WriteTo))
                .ToLowerInvariant());
    }

    // ------------------------------------------------------------------ the shipped table

    /// <summary>
    /// Two fire modes per fire group, ids <c>fireGroupId * 2 + index</c> (D159), and every one of
    /// them named by its group's list-1 record. The AR-15's group 6 owns modes 12 and 13.
    /// </summary>
    [Fact]
    public void EveryFireModeIdAFireGroupNamesResolvesInListTwo()
    {
        WeaponDefinitionsBlob blob = AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true);

        var modes = blob.FireModes!.Select(mode => mode.FireModeId).ToHashSet();
        Assert.Equal(blob.FireModes!.Count, modes.Count);
        Assert.Equal(blob.FireGroups!.Count * AugustWeaponTable.FireModesPerGroup, modes.Count);

        foreach (FireGroupRecord group in blob.FireGroups!)
        {
            IReadOnlyList<uint> ids = group.FireModeIds ?? [];
            Assert.Equal(AugustWeaponTable.FireModesPerGroup, ids.Count);
            foreach (uint id in ids)
            {
                Assert.Contains(id, modes);
            }
        }

        Assert.Equal(12u, AugustWeaponTable.FireModeIdFor(6, 0));
        Assert.Equal(13u, AugustWeaponTable.FireModeIdFor(6, 1));
        Assert.Equal([12u, 13u], blob.FireGroups!.Single(g => g.FireGroupId == 6).FireModeIds);
    }

    /// <summary>
    /// The values in the shipped list 2 are the client's own datasheet cells: mode 0's trigger
    /// charge is <c>CLIP_SIZE</c>, mode 1's is the sentinel, and both carry the group's
    /// <c>REFIRE_TIME_MS</c> / <c>RELOAD_TIME_MS</c>.
    /// </summary>
    [Fact]
    public void TheShippedFireModesCarryTheClientsOwnTimings()
    {
        Dictionary<uint, FireModeRecord> byId =
            AugustWeaponTable.FireModeRecords.ToDictionary(record => record.FireModeId);

        // Fire group 6 is the AR-15's (docs/58 section 8a); item 10 is its lowest item id.
        AugustWeaponFact ar15 = AugustWeaponFacts.All.Where(w => w.FireGroupId == 6)
            .OrderBy(w => w.ItemId).First();

        // Wave 14: rec+0x18 is the mode's OWN id, not the clip size, so it mirrors rec+0x378.
        Assert.Equal(12u, byId[12].DefinitionId);
        Assert.Equal(13u, byId[13].DefinitionId);
        Assert.Equal(ar15.RefireTimeMs, byId[12].RefireTimeMs);
        Assert.Equal(ar15.RefireTimeMs, byId[13].RefireTimeMs);
        Assert.Equal(ar15.ReloadTimeMs, byId[12].ReloadTimeMs);

        // Mode index 1 is the ADS mode and mode 0 is the hip (docs/107 addendum, FUN_1422935d0:68).
        Assert.False(byId[12].IronSights);
        Assert.True(byId[13].IronSights);
        Assert.Equal(0, byId[12].AmmoSlot);
        Assert.Equal(0, byId[13].AmmoSlot);

        // No mode may fail FUN_142291b90's "> 0" gate - a mode that cannot arm is a gun that
        // cannot fire, which is the whole reason rec+0x18 is on the wire at all. And every id has
        // to be distinct, because it is also the key of list 4 and of the override container.
        Assert.All(AugustWeaponTable.FireModeRecords, record => Assert.True(record.DefinitionId > 0));
        Assert.Equal(
            AugustWeaponTable.FireModeRecords.Count,
            AugustWeaponTable.FireModeRecords.Select(record => record.DefinitionId).Distinct().Count());
    }

    /// <summary>
    /// <b>The whole resolution ladder, wave 14: every fire mode a shipped gun can select reaches a
    /// list-4 row, and every list-4 row reaches a projectile record.</b> This is the chain
    /// <c>FUN_14228da90</c> walks before <c>FUN_140c7bb60</c> will spawn anything:
    /// mode -&gt; <c>def+0x18</c> and <c>def+0x30</c> -&gt; the definition's ammo slot -&gt;
    /// <c>(id, ammoId)</c> in list 4 -&gt; a <c>ProjectileDefinitions</c> id.
    /// </summary>
    [Fact]
    public void EveryArmedFireModeReachesAProjectileRecord()
    {
        WeaponDefinitionsBlob blob = AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true);

        var modes = blob.FireModes!.ToDictionary(mode => mode.DefinitionId);
        var projectiles = AugustProjectileTable.Records
            .Select(record => record.ProjectileId).ToHashSet();
        var mapping = blob.FireModeProjectiles!;
        Assert.NotEmpty(mapping);

        // Every ammo slot Cranberry declares carries a round the projectile table can resolve, and
        // the slot index every fire mode names is inside its own weapon definition's array.
        foreach (WeaponDefinitionRecord definition in blob.WeaponDefinitions!)
        {
            IReadOnlyList<WeaponAmmoSlotRow> slots = definition.AmmoSlots ?? [];
            Assert.True(slots.Count <= 1);
            foreach (WeaponAmmoSlotRow slot in slots)
            {
                Assert.NotEqual(0u, slot.AmmoId);
                Assert.Contains(AugustProjectileTable.ProjectileForAmmoItem(slot.AmmoId), projectiles);
            }
        }

        foreach (FireModeProjectileRecord row in mapping)
        {
            Assert.True(
                modes.ContainsKey(row.FireModeDefinitionId),
                $"list-4 row {row.FireModeDefinitionId} names a fire mode list 2 does not carry");
            Assert.Equal(0, modes[row.FireModeDefinitionId].AmmoSlot);
            Assert.Contains(row.ProjectileDefinitionId, projectiles);
        }

        // Fire group 6 is the AR-15's: both of its modes fire the .223 (19002) loaded from 1429.
        foreach (uint modeId in (uint[])[12u, 13u])
        {
            FireModeProjectileRecord row = Assert.Single(
                mapping, record => record.FireModeDefinitionId == modeId);
            Assert.Equal(1429u, row.AmmoItemId);
            Assert.Equal(19002u, row.ProjectileDefinitionId);
            Assert.Equal(0u, row.Unknown00);
        }

        // And the AR-15's own weapon definition (WEAPON_ID 6) declares exactly that round.
        WeaponAmmoSlotRow ar15Slot = Assert.Single(
            blob.WeaponDefinitions!.Single(d => d.WeaponDefinitionId == 6).AmmoSlots ?? []);
        Assert.Equal(1429u, ar15Slot.AmmoId);
        Assert.Equal(30u, ar15Slot.ClipSize);
    }

    /// <summary>
    /// The ammo-slot row is <b>37 bytes</b> with three empty strings, and it starts with
    /// <c>AmmoSlot.AmmoId</c> then <c>AmmoSlot.ClipSize</c> - the two words
    /// <c>FUN_14228de50</c> and <c>FUN_14228df60</c> read.
    /// </summary>
    [Fact]
    public void TheAmmoSlotRowHexSnapshotIsStable()
    {
        byte[] bytes = Bytes(new WeaponAmmoSlotRow(1429, 30).WriteTo);

        Assert.Equal(WeaponAmmoSlotRow.MinimalLength, bytes.Length);
        Assert.Equal(37, bytes.Length);
        Assert.Equal(
            "95050000" +      // u32 AmmoSlot.AmmoId   = 1429 (.223 Round)  -> slot+0x00
            "1e000000" +      // u32 AmmoSlot.ClipSize = 30                 -> slot+0x04
            "00000000" +      // u32 [U]                                    -> slot+0x08
            "00" +            // u8  [U]                                    -> slot+0x0c
            "00000000" + "00000000" + "00000000" +   // u32 x3 [U]          -> slot+0x10..0x18
            "00000000" + "00000000" + "00000000",    // three empty strings -> slot+0x20/0x38/0x50
            Convert.ToHexString(bytes).ToLowerInvariant());

        // A gun's list-0 record grows by exactly one row; everything else keeps its old length.
        var withSlot = new WeaponDefinitionRecord(6, [6u]) { AmmoSlots = [new WeaponAmmoSlotRow(1429, 30)] };
        Assert.Equal(WeaponDefinitionRecord.MinimalLength + 4 + 37, withSlot.Length);
        Assert.Equal(withSlot.Length, Bytes(withSlot.WriteTo).Length);
        Assert.Equal(
            WeaponDefinitionRecord.MinimalLength + 4,
            Bytes(new WeaponDefinitionRecord(6, [6u]).WriteTo).Length);
    }

    /// <summary>
    /// <b>The multiplier guard, extended to list 2</b> (D143). Walks every byte of the shipped
    /// blob with the recovered layouts and fails if any of the 34 scalars the client's own
    /// constructor presets to <c>1.0f</c> ships as <c>0</c> - the shape of the 2026-08-31 wield
    /// freeze. It also proves the cursor lands exactly on the end of the blob, which is the only
    /// check there is that the 639 is right.
    /// </summary>
    [Fact]
    public void NoFireModeShipsAZeroedScalarAndTheCursorLandsExactly()
    {
        WeaponDefinitionsBlob blob = AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true);
        byte[] bytes = blob.ToArray();
        int cursor = SkipListsZeroAndOne(blob, bytes);

        int modes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        Assert.Equal(blob.FireModes!.Count, modes);
        for (int i = 0; i < modes; i++)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor));
            uint definitionId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4));
            Assert.True(definitionId > 0, $"fire mode {key}: rec+0x18 = 0 can never arm (FUN_142291b90)");

            int body = cursor + 8;
            foreach (short offset in WeaponListLayouts.FireModeUnitScalars)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(
                    bytes.AsSpan(body + BodyPositionOf(offset)));
                Assert.True(value > 0f, $"fire mode {key}: rec+0x{offset:x} shipped {value}, not 1.0f");
            }

            cursor += FireModeRecord.RecordLength;
        }

        // List 3 is empty, list 4 carries the mapping, lists 5-7 are empty; the cursor must land
        // exactly on each count and then on the end of the blob.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor)));
        cursor += 4;

        int mapped = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        Assert.Equal(blob.FireModeProjectiles!.Count, mapped);
        cursor += mapped * WeaponListLayouts.List4RecordLength;

        for (int list = 5; list < WeaponDefinitionsBlob.ListCount; list++)
        {
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor)));
            cursor += 4;
        }

        Assert.Equal(bytes.Length, cursor);
    }

    /// <summary>
    /// <c>CRANBERRY_WEAPON_DEFS_LIST2=0</c> is a real revert: it restores exactly the bytes waves
    /// 9-16 shipped - empty mode-id arrays in list 1 and an empty list 2.
    /// </summary>
    [Fact]
    public void TurningListTwoOffRestoresTheWaveNineBytes()
    {
        WeaponDefinitionsBlob off = AugustWeaponTable.CreateBlob(
            populateWeaponDefinitions: false, populateFireModes: false,
            populateFireModeProjectiles: false);
        WeaponDefinitionsBlob on = AugustWeaponTable.CreateBlob(
            populateWeaponDefinitions: false, populateFireModes: true,
            populateFireModeProjectiles: false);

        Assert.Empty(off.FireModes ?? []);
        Assert.All(off.FireGroups!, group => Assert.Empty(group.FireModeIds ?? []));
        Assert.Equal(3212, off.Length);

        // 120 modes at 639 bytes, plus two mode ids on each of the 60 groups.
        Assert.Equal(120, on.FireModes!.Count);
        Assert.Equal(
            off.Length + (120 * FireModeRecord.RecordLength) + (60 * 2 * 4),
            on.Length);
        Assert.Equal(on.Length, on.ToArray().Length);
    }

    private static int SkipListsZeroAndOne(WeaponDefinitionsBlob blob, byte[] bytes)
    {
        int cursor = 0;
        int definitions = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        for (int i = 0; i < definitions; i++)
        {
            // The ammo-slot array's count sits at byte 125 of the record; the fire-group count
            // follows the rows (wave 14 - it used to be at 129 for every record).
            int ammoSlots = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor + 125));
            int afterSlots = cursor + 129 + (ammoSlots * WeaponAmmoSlotRow.MinimalLength);
            int fireGroups = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(afterSlots));
            cursor = afterSlots + 4 + (4 * fireGroups);
        }

        int groups = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        Assert.Equal(blob.FireGroups!.Count, groups);
        for (int i = 0; i < groups; i++)
        {
            int modes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor + 8));
            cursor += FireGroupRecord.MinimalLength + (4 * modes);
        }

        return cursor;
    }

    // -------------------------------------------------- the ADS zoom (docs/107 wave-14 addendum)

    /// <summary>
    /// <b><c>DEFAULT_ZOOM</c> lands at <c>rec+0x128</c></b> - the offset the FireModes datasheet
    /// loader stores the column at (<c>142227ab9</c> / <c>MOVSS [RBX + 0x108]</c>, base
    /// <c>rec+0x20</c>) and the offset <c>FUN_141488490</c> reads under the key whose initialiser
    /// string is <c>&quot;FireMode.DefaultZoom&quot;</c>. <b>1.0f is byte-identical to the
    /// unit-scalar fallback</b>, so the off switch is a real revert, not an equivalent one.
    /// </summary>
    [Fact]
    public void TheAdsZoomLandsWhereTheAimedCameraReadsIt()
    {
        byte[] plain = Bytes(new FireModeRecord(13, DefinitionId: 13, IronSights: true).WriteTo);
        byte[] zoomed = Bytes(
            new FireModeRecord(13, DefinitionId: 13, IronSights: true, DefaultZoom: 1.5f).WriteTo);

        int at = 8 + BodyPositionOf(WeaponListLayouts.FireModeDefaultZoom);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(plain.AsSpan(at)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(zoomed.AsSpan(at)));
        Assert.Equal("0000c03f", Convert.ToHexString(zoomed.AsSpan(at, 4)).ToLowerInvariant());

        // Nothing else moved: same length, and the only differing bytes are inside that word.
        Assert.Equal(plain.Length, zoomed.Length);
        Assert.Equal(FireModeRecord.RecordLength, zoomed.Length);
        int differing = 0;
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] != zoomed[i])
            {
                differing++;
                Assert.InRange(i, at, at + 3);
            }
        }

        Assert.Equal(1, differing);   // 0x3f800000 -> 0x3fc00000 differs in one byte (80 -> c0)

        // ARMS_FOV_SCALAR (rec+0x16c, FUN_141488410) stays at the client's own 1.0f: it scales the
        // held weapon's own FOV, not the view's.
        Assert.Equal(
            1.0f,
            BinaryPrimitives.ReadSingleLittleEndian(
                zoomed.AsSpan(8 + BodyPositionOf(WeaponListLayouts.FireModeArmsFovScalar))));

        // The absolute first-person FOV override (FUN_140e3fff0) is deliberately not written.
        Assert.Equal(0, zoomed[8 + BodyPositionOf(WeaponListLayouts.FireModeFpForceCameraOverrides)]);
        Assert.Equal(
            0f,
            BinaryPrimitives.ReadSingleLittleEndian(
                zoomed.AsSpan(8 + BodyPositionOf(WeaponListLayouts.FireModeFpCameraFov))));
    }

    /// <summary>
    /// <b>Only the iron-sights mode of an ARMED fire group carries the zoom.</b> D205 sets
    /// <c>IRON_SIGHTS</c> on mode index 1 of every group, the fists and every melee row included,
    /// so the zoom is gated on the same ammunition pairing that decides the list-0 ammo slot.
    /// </summary>
    [Fact]
    public void OnlyArmedIronSightsModesCarryTheAdsZoom()
    {
        IReadOnlyList<FireModeRecord> zoomed =
            AugustWeaponTable.FireModeRecordsWithAdsZoom(AugustFireModeFacts.AdsZoom);

        Assert.Equal(AugustWeaponTable.FireModeRecords.Count, zoomed.Count);
        Assert.NotEmpty(AugustWeaponTable.ArmedFireGroupIds);

        int carriers = 0;
        foreach (FireModeRecord record in zoomed)
        {
            Assert.True(AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact));
            bool expected =
                fact.IronSights && AugustWeaponTable.ArmedFireGroupIds.Contains(fact.FireGroupId);
            Assert.Equal(expected ? AugustFireModeFacts.AdsZoom : 1.0f, record.DefaultZoom);
            if (expected)
            {
                carriers++;
            }
        }

        Assert.Equal(AugustWeaponTable.ArmedFireGroupIds.Count, carriers);

        // The AR-15 is fire group 6: mode 12 is the hip mode, mode 13 the iron sights (D159).
        Assert.Contains(6u, AugustWeaponTable.ArmedFireGroupIds);
        Assert.Equal(1.0f, zoomed.Single(r => r.FireModeId == 12).DefaultZoom);
        Assert.Equal(AugustFireModeFacts.AdsZoom, zoomed.Single(r => r.FireModeId == 13).DefaultZoom);
    }

    /// <summary>
    /// The client's own iron-sights band. <c>FUN_140e40090</c> and <c>FUN_140e4a010</c> pick
    /// <c>scopedMouseSensitivity</c> when the zoom is above <c>_DAT_143275150 = 2.0f</c> and
    /// <c>ADSMouseSensitivity</c> when it is above <c>1.0f</c>, so the shipped number has to sit
    /// in <c>(1.0, 2.0]</c> or the client stops treating it as an iron sight.
    /// </summary>
    [Fact]
    public void TheShippedAdsZoomSitsInTheClientsOwnIronSightsBand()
    {
        Assert.InRange(AugustFireModeFacts.AdsZoom, 1.0001f, 2.0f);
        Assert.Equal(AugustFireModeFacts.AdsZoom, WeaponStageOptions.Default.WeaponAdsZoom);
        Assert.True(WeaponStageOptions.Default.WriteAdsZoom);
        Assert.False(WeaponStageOptions.AllOff.WriteAdsZoom);
    }

    /// <summary>
    /// <c>CRANBERRY_WEAPON_ADS_FOV=0</c> is a real revert: the blob is byte-for-byte the one every
    /// wave before the docs/107 wave-14 addendum shipped.
    /// </summary>
    [Fact]
    public void TurningTheAdsZoomOffRestoresTheBytes()
    {
        byte[] off = AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true).ToArray();
        byte[] on = AugustWeaponTable
            .CreateBlob(populateWeaponDefinitions: true, adsZoom: AugustFireModeFacts.AdsZoom)
            .ToArray();

        Assert.Equal(off.Length, on.Length);
        Assert.NotEqual(off, on);
        Assert.Same(
            AugustWeaponTable.FireModeRecords,
            AugustWeaponTable.FireModeRecordsWithAdsZoom(1.0f));

        // Wave 15's two switches move bytes of their own, so the wave-13 control has to clear
        // them here as well or this stops being a byte-for-byte revert. Wave 16's two do the
        // same, on the fire modes of the UNARMED groups.
        var session = new WeaponSession(WeaponStageOptions.Default with
        {
            WriteAdsZoom = false,
            WriteAdsFirstPerson = false,
            WriteIronSightsTimes = false,
            IronSightsArmedOnly = false,
            WriteMeleeAbilityIds = false,
            WriteFireModeTypes = false,
            // docs/121 (D331 / D332) move list-1 flags and list-2 words; cleared for the same reason.
            RetailAutomatic = false,
            ShotgunPellets = 0,
            ShotgunSpreadDegrees = 0.0f,
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
            Throwables = false,
        });
        Assert.Equal(off, session.Blob.ToArray());
    }

    /// <summary>Only the exact string <c>0</c> turns the ADS zoom off; the lever parses a float.</summary>
    [Theory]
    [InlineData(null, null, true, 1.2f)]
    [InlineData("0", null, false, 1.2f)]
    [InlineData("1", "1.25", true, 1.25f)]
    [InlineData(null, "nonsense", true, 1.2f)]
    public void TheAdsZoomSwitchAndLeverReadTheEnvironment(
        string? onOff, string? zoom, bool expectedOn, float expectedZoom)
    {
        WeaponStageOptions options = WeaponStageOptions.FromEnvironment(name =>
            name == WeaponStageOptions.AdsFovVariable ? onOff
            : name == WeaponStageOptions.AdsZoomVariable ? zoom
            : null);

        Assert.Equal(expectedOn, options.WriteAdsZoom);
        Assert.Equal(expectedZoom, options.WeaponAdsZoom);
    }

    // -------------------------------------------------- the fire effect / gunshot sound (report 1)

    /// <summary>
    /// <b>The fire <c>EFFECT_GROUP</c> lands at <c>rec+0x104</c></b> - the group id the client
    /// resolves to the muzzle-flash + gunshot-audio composite effect on a shot. Shipping 0 (waves
    /// 9-16) is why the client logged "missing effect definition for Id #0" and made no fire sound.
    /// The AR-15's fire group 6 carries its own effect group; the binoculars (21) and the AK-47's
    /// group 51 have no <c>FireModeEffectGroups</c> 'fire' row and stay 0. The length never moves.
    /// </summary>
    [Fact]
    public void TheFireEffectLandsWhereTheClientQueuesTheGunshot()
    {
        byte[] plain = Bytes(new FireModeRecord(12, DefinitionId: 12).WriteTo);
        byte[] withEffect = Bytes(new FireModeRecord(12, DefinitionId: 12, EffectGroup: 6).WriteTo);

        int at = 8 + BodyPositionOf(WeaponListLayouts.FireModeEffectGroup);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(withEffect.AsSpan(at)));
        Assert.Equal(plain.Length, withEffect.Length);
        Assert.Equal(FireModeRecord.RecordLength, withEffect.Length);

        Assert.Equal(6u, AugustFireModeFacts.All.First(f => f.FireGroupId == 6).EffectGroup);
        Assert.Equal(0u, AugustFireModeFacts.All.First(f => f.FireGroupId == 21).EffectGroup);
        Assert.Equal(0u, AugustFireModeFacts.All.First(f => f.FireGroupId == 51).EffectGroup);

        // The shipped blob writes the effect; CRANBERRY_WEAPON_FIRE_SOUND=0 zeroes it again with no
        // length change.
        byte[] on = AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true).ToArray();
        byte[] off = AugustWeaponTable
            .CreateBlob(populateWeaponDefinitions: true, writeFireEffect: false).ToArray();
        Assert.Equal(on.Length, off.Length);
        Assert.NotEqual(on, off);
        Assert.True(WeaponStageOptions.Default.WriteFireEffect);
        Assert.False(WeaponStageOptions.AllOff.WriteFireEffect);
    }

    /// <summary>
    /// <b>The binoculars raise to the first-person scope and zoom (report 3).</b> A viewing optic
    /// gets <c>FORCE_FP_SCOPE</c> on its PRIMARY mode - like the wave-15 FP ADS, but independent of
    /// the weapon ADS switch, because a gun aims in third person (report 4) while binoculars are
    /// looked THROUGH. Binoculars (item 1542) are the only optic, fire group 21.
    /// </summary>
    [Fact]
    public void TheBinocularsRaiseToFirstPersonScopeAndZoom()
    {
        Assert.Contains(21u, AugustWeaponTable.OpticFireGroupIds);
        Assert.DoesNotContain(6u, AugustWeaponTable.OpticFireGroupIds);   // the AR-15 is not an optic

        IReadOnlyList<FireModeRecord> records = AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom,
            adsFirstPerson: false,        // guns are third person (report 4)
            fpCameraFovDegrees: 0f,
            writeOpticScope: true,
            opticFovDegrees: 20f);

        // Group 21's primary mode (id 42) carries FORCE_FP_SCOPE + the binocular FOV; mode 1 (43)
        // does not, and a gun's primary mode (the AR-15, id 12) stays third person.
        FireModeRecord primary = records.Single(r => r.FireModeId == 42);
        Assert.True(primary.ForceFpScope);
        Assert.Equal(AugustWeaponTable.DefaultOpticFovDegrees, primary.FpCameraFovDegrees);
        Assert.Equal(4, primary.ReticleId);
        Assert.Equal(4, records.Single(r => r.FireModeId == 43).ReticleId);
        Assert.False(records.Single(r => r.FireModeId == 43).ForceFpScope);
        Assert.False(records.Single(r => r.FireModeId == 12).ForceFpScope);

        Assert.True(WeaponStageOptions.Default.BinocularsOptic);
        Assert.False(WeaponStageOptions.AllOff.BinocularsOptic);
        Assert.Equal(20.0f, WeaponStageOptions.Default.BinocularsOpticFovDegrees);

        byte[] on = AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true).ToArray();
        byte[] off = AugustWeaponTable
            .CreateBlob(populateWeaponDefinitions: true, writeOpticScope: false).ToArray();
        Assert.Equal(on.Length, off.Length);
        Assert.NotEqual(on, off);
    }

    /// <summary>
    /// <b>The ADS first-person switch lands where the client reads it.</b> <c>FORCE_FP_SCOPE</c>
    /// is <c>rec+0x2dc</c> (loader <c>142229746</c>) and <c>FUN_14158ad50:17</c> reads that one
    /// byte off the live fire-mode record; the FP camera FOV gate is <c>rec+0x2cd</c> and its
    /// three stance words are <c>rec+0x2d0</c> / <c>+0x2d4</c> / <c>+0x2d8</c>
    /// (<c>FUN_140e3fff0</c>). Six bytes of one record change and the length does not move.
    /// </summary>
    [Fact]
    public void TheAdsFirstPersonFlagLandsWhereTheSecondaryFireHandlerReadsIt()
    {
        byte[] plain = Bytes(new FireModeRecord(12, DefinitionId: 12).WriteTo);
        byte[] scoped = Bytes(
            new FireModeRecord(
                12,
                DefinitionId: 12,
                ForceFpScope: true,
                FpCameraFovDegrees: AugustFireModeFacts.AdsFpCameraFov).WriteTo);

        int scopeAt = 8 + BodyPositionOf(WeaponListLayouts.FireModeForceFpScope);
        int gateAt = 8 + BodyPositionOf(WeaponListLayouts.FireModeFpForceCameraOverrides);
        int fovAt = 8 + BodyPositionOf(WeaponListLayouts.FireModeFpCameraFov);
        int crouchAt = 8 + BodyPositionOf(WeaponListLayouts.FireModeFpCrouchedCameraFov);
        int proneAt = 8 + BodyPositionOf(WeaponListLayouts.FireModeFpProneCameraFov);

        Assert.Equal(0, plain[scopeAt]);
        Assert.Equal(0, plain[gateAt]);
        Assert.Equal(1, scoped[scopeAt]);
        Assert.Equal(1, scoped[gateAt]);

        foreach (int at in new[] { fovAt, crouchAt, proneAt })
        {
            Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(plain.AsSpan(at)));
            Assert.Equal(
                AugustFireModeFacts.AdsFpCameraFov,
                BinaryPrimitives.ReadSingleLittleEndian(scoped.AsSpan(at)));
        }

        Assert.Equal(plain.Length, scoped.Length);
        Assert.Equal(FireModeRecord.RecordLength, scoped.Length);
        foreach (int i in Enumerable.Range(0, plain.Length))
        {
            if (plain[i] != scoped[i])
            {
                Assert.True(
                    i == scopeAt || i == gateAt
                    || (i >= fovAt && i < fovAt + 4)
                    || (i >= crouchAt && i < crouchAt + 4)
                    || (i >= proneAt && i < proneAt + 4),
                    $"byte {i} moved and it is not one of the five ADS first-person fields");
            }
        }

        // A zero FOV clears the gate, so the override is unreachable and the player's own
        // verticalFOV stands - exactly what every wave before this one shipped.
        byte[] noFov = Bytes(
            new FireModeRecord(12, DefinitionId: 12, ForceFpScope: true).WriteTo);
        Assert.Equal(1, noFov[scopeAt]);
        Assert.Equal(0, noFov[gateAt]);
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(noFov.AsSpan(fovAt)));
    }

    /// <summary>
    /// <b>The flag goes on the PRIMARY mode, not the iron-sights one.</b> <c>FUN_14158ad50</c>
    /// reads it off the <em>live</em> fire-mode record, and the live mode when the right mouse
    /// button goes down is index 0 - on the iron-sights mode it could never be reached. Armed
    /// groups only, on the same predicate as the ammo slot and the zoom.
    /// </summary>
    [Fact]
    public void OnlyArmedPrimaryModesCarryTheAdsFirstPersonFlag()
    {
        // writeOpticScope: false isolates the weapon ADS first-person flag from the binoculars
        // optic (report 3), which also sets ForceFpScope but on the non-armed group 21.
        IReadOnlyList<FireModeRecord> records = AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom,
            adsFirstPerson: true,
            AugustFireModeFacts.AdsFpCameraFov,
            writeOpticScope: false);

        Assert.Equal(AugustWeaponTable.FireModeRecords.Count, records.Count);

        int carriers = 0;
        foreach (FireModeRecord record in records)
        {
            Assert.True(AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact));
            bool expected =
                fact.ModeIndex == 0 && AugustWeaponTable.ArmedFireGroupIds.Contains(fact.FireGroupId);
            Assert.Equal(expected, record.ForceFpScope);
            Assert.Equal(
                expected ? AugustFireModeFacts.AdsFpCameraFov : 0f,
                record.FpCameraFovDegrees);
            if (expected)
            {
                carriers++;
            }

            // The zoom is untouched by the first-person switch - it is simply dormant, because
            // the iron-sights mode is no longer reachable through right-click.
            Assert.Equal(
                fact.IronSights && AugustWeaponTable.ArmedFireGroupIds.Contains(fact.FireGroupId)
                    ? AugustFireModeFacts.AdsZoom
                    : 1.0f,
                record.DefaultZoom);
        }

        Assert.Equal(AugustWeaponTable.ArmedFireGroupIds.Count, carriers);

        // Fire group 6 is the AR-15: mode 12 is the primary and carries the flag, mode 13 is the
        // iron-sights mode and does not.
        Assert.True(records.Single(r => r.FireModeId == 12).ForceFpScope);
        Assert.False(records.Single(r => r.FireModeId == 13).ForceFpScope);

        // A group no ammunition pairs with - a melee row or the fists - never swaps the camera.
        FireModeRecord unarmedPrimary = records.First(r =>
            AugustFireModeFacts.TryGet(r.FireModeId, out AugustFireModeFact f)
            && f.ModeIndex == 0
            && !AugustWeaponTable.ArmedFireGroupIds.Contains(f.FireGroupId));
        Assert.False(unarmedPrimary.ForceFpScope);
        Assert.Equal(0f, unarmedPrimary.FpCameraFovDegrees);
    }

    /// <summary>
    /// <c>CRANBERRY_WEAPON_ADS_FP=0</c> and <c>CRANBERRY_WEAPON_ADS_TIMES=0</c> are real reverts:
    /// with the zoom still on, the blob is byte-for-byte the wave-14 one.
    /// </summary>
    [Fact]
    public void TheDefaultIsThirdPersonAdsAndTheFpSwitchRestoresTheWave15Bytes()
    {
        // Report 4 (D233): the shipped default is now the THIRD-person aim - FP off, the iron-sights
        // fire-mode switch with DEFAULT_ZOOM 1.2 and the 266 ms ramp. CRANBERRY_WEAPON_ADS_FP=1
        // restores the wave-15 first-person camera swap.
        byte[] thirdPerson = AugustWeaponTable
            .CreateBlob(
                populateWeaponDefinitions: true,
                adsZoom: AugustFireModeFacts.AdsZoom,
                ironSightsTimeMs: AugustFireModeFacts.IronSightsTimeMs)
            .ToArray();
        byte[] firstPerson = AugustWeaponTable
            .CreateBlob(
                populateWeaponDefinitions: true,
                adsZoom: AugustFireModeFacts.AdsZoom,
                adsFirstPerson: true,
                adsFpCameraFovDegrees: AugustFireModeFacts.AdsFpCameraFov,
                ironSightsTimeMs: AugustFireModeFacts.IronSightsTimeMs)
            .ToArray();

        Assert.Equal(thirdPerson.Length, firstPerson.Length);
        Assert.NotEqual(thirdPerson, firstPerson);

        // The shipped default is the third-person blob (the wave-16 melee/iron-sights passes are
        // orthogonal and cleared here so this test isolates the FP change).
        var shipped = new WeaponSession(WeaponStageOptions.Default with
        {
            IronSightsArmedOnly = false,
            WriteMeleeAbilityIds = false,
            WriteFireModeTypes = false,
            RetailAutomatic = false,
            ShotgunPellets = 0,
            ShotgunSpreadDegrees = 0.0f,
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
            Throwables = false,
        });
        Assert.Equal(thirdPerson, shipped.Blob.ToArray());

        // Turning CRANBERRY_WEAPON_ADS_FP on restores the wave-15 first-person blob.
        var fp = new WeaponSession(WeaponStageOptions.Default with
        {
            WriteAdsFirstPerson = true,
            RetailAutomatic = false,
            ShotgunPellets = 0,
            ShotgunSpreadDegrees = 0.0f,
            IronSightsArmedOnly = false,
            WriteMeleeAbilityIds = false,
            WriteFireModeTypes = false,
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
            Throwables = false,
        });
        Assert.Equal(firstPerson, fp.Blob.ToArray());
    }

    /// <summary>
    /// The aim-in / aim-out ramp is <c>def+0x38</c> / <c>def+0x3c</c> of the list-0 record, the
    /// two durations <c>FUN_1422935d0:70-76</c> takes, and writing them moves no length.
    /// </summary>
    [Fact]
    public void TheIronSightsRampLandsInTheWeaponDefinitionAndMovesNoLength()
    {
        byte[] instant = Bytes(new WeaponDefinitionRecord(10, [6u]).WriteTo);
        byte[] ramped = Bytes(
            new WeaponDefinitionRecord(
                10,
                [6u],
                ToIronSightsTimeMs: AugustFireModeFacts.IronSightsTimeMs,
                FromIronSightsTimeMs: AugustFireModeFacts.IronSightsTimeMs).WriteTo);

        Assert.Equal(instant.Length, ramped.Length);
        Assert.NotEqual(instant, ramped);

        // Two u32s, adjacent on the wire, immediately after the def+0x50 word.
        int differing = 0;
        foreach (int i in Enumerable.Range(0, instant.Length))
        {
            if (instant[i] != ramped[i])
            {
                differing++;
            }
        }

        Assert.Equal(4, differing);   // 266 = 0x0000010a: two bytes move in each of the two words

        Assert.Equal(266, AugustFireModeFacts.IronSightsTimeMs);
    }

    /// <summary>
    /// The shipped first-person FOV is the client's own default vertical FOV over the D212 zoom,
    /// and both wave-15 switches read the environment the same way every other one does.
    /// </summary>
    [Theory]
    // Report 4 (D233): ADS first person now defaults OFF (right-click is a THIRD-person aim), so the
    // null (default) fp column is False and CRANBERRY_WEAPON_ADS_FP=1 is what turns the FP swap on.
    // The FOV default is 65 / 1.2 = 54.1667 now that AdsZoom is 1.2.
    [InlineData(null, null, null, false, 54.1667f, true)]
    [InlineData("1", null, null, true, 54.1667f, true)]
    [InlineData("0", null, null, false, 54.1667f, true)]
    [InlineData(null, "0", "0", false, 0f, false)]
    [InlineData(null, "52", null, false, 52f, true)]
    [InlineData(null, "nonsense", null, false, 54.1667f, true)]
    public void TheAdsFirstPersonSwitchesReadTheEnvironment(
        string? fp, string? fov, string? times, bool expectedFp, float expectedFov, bool expectedTimes)
    {
        WeaponStageOptions options = WeaponStageOptions.FromEnvironment(name =>
            name == WeaponStageOptions.AdsFirstPersonVariable ? fp
            : name == WeaponStageOptions.AdsFpFovVariable ? fov
            : name == WeaponStageOptions.IronSightsTimesVariable ? times
            : null);

        Assert.Equal(expectedFp, options.WriteAdsFirstPerson);
        Assert.Equal(expectedFov, options.WeaponAdsFpCameraFov);
        Assert.Equal(expectedTimes, options.WriteIronSightsTimes);

        Assert.False(WeaponStageOptions.Default.WriteAdsFirstPerson);
        Assert.False(WeaponStageOptions.AllOff.WriteAdsFirstPerson);
        Assert.False(WeaponStageOptions.AllOff.WriteIronSightsTimes);
        Assert.Equal(65f / AugustFireModeFacts.AdsZoom, AugustFireModeFacts.AdsFpCameraFov, 3);
    }
}

/// <summary>
/// <c>ReferenceData "ProjectileDefinitions"</c> - <c>FUN_140a20370</c> -&gt; <c>FUN_140a40e40</c>
/// -&gt; <c>FUN_140a4fef0</c> -&gt; <c>FUN_140a40410</c>. Shipped ON since wave 14, when list 4
/// made these records reachable from a fire mode (docs/107 addendum).
/// </summary>
public sealed class ProjectileDefinitionsBlobTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// The record is <c>183</c> bytes with three empty strings and an empty <c>rec+0x120</c> array,
    /// and grows by exactly the bytes of each string.
    /// </summary>
    [Fact]
    public void AProjectileRecordIsOneHundredAndEightyFourBytesPlusItsStrings()
    {
        // 184, not 183: wave 13 found the one-byte FLIGHT_TYPE read at rec+0x68 that
        // FUN_140a40410:66-76 makes and the writer was missing (docs/107 s3, D199).
        Assert.Equal(184, Bytes(new ProjectileDefinitionRecord(19002).WriteTo).Length);
        Assert.Equal(
            184 + 5,
            Bytes(new ProjectileDefinitionRecord(19002, ModelFileName: "a.adr").WriteTo).Length);
        Assert.Equal(
            new ProjectileDefinitionRecord(19002, "a.adr", "bb", "ccc").Length,
            Bytes(new ProjectileDefinitionRecord(19002, "a.adr", "bb", "ccc").WriteTo).Length);
    }

    /// <summary>
    /// The four words the client's own constructor <c>FUN_142224d50</c> presets - <c>0.005f</c> at
    /// <c>+0xa0</c>, <c>1.0f</c> at <c>+0xa4</c>, <c>10.0f</c> at <c>+0xa8</c> and <c>1.0f</c> at
    /// <c>+0xec</c> - are what Cranberry writes. D143 for this table.
    /// </summary>
    [Fact]
    public void TheRecordCarriesTheClientsOwnConstructorDefaults()
    {
        byte[] bytes = Bytes(new ProjectileDefinitionRecord(19002).WriteTo);

        // key (4) + body id (4) + flags (2) + 3 empty strings' u32 lengths are interleaved; the
        // +0xa0 run starts after: 4 + 4 + 2 + 4 + 4 + (4 * 4) + 1 + (6 * 4) + 4 = 63. The +1 is
        // the one-byte FLIGHT_TYPE at rec+0x68 (docs/107 s3, D199).
        const int scalarRun = 4 + 4 + 2 + 4 + 4 + (4 * 4) + 1 + (6 * 4) + 4;
        Assert.Equal(0.005f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(scalarRun)), 6);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(scalarRun + 4)));
        Assert.Equal(10.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(scalarRun + 8)));

        // The record's own id is mirrored at rec+0x18, as list 0's def+0x18 is (refute-1).
        Assert.Equal(19002u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(19002u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(
            Bytes((new ProjectileDefinitionRecord(19002) { WriteBodyId = false }).WriteTo).AsSpan(4)));
    }

    /// <summary>
    /// The blob is <c>u32 compressedLength; u32 uncompressedLength;</c> then an LZ4 block that
    /// decodes back to <c>i32 count</c> plus the records - the shape <c>FUN_140a40e40</c> reads and
    /// <c>FUN_14212a7b0</c> decompresses. There is no uncompressed path in that function, which is
    /// why the literal encoder exists at all.
    /// </summary>
    [Fact]
    public void TheBlobIsTwoLengthsAndAnLz4BlockThatRoundTrips()
    {
        ProjectileDefinitionsBlob blob = AugustProjectileTable.CreateBlob();
        byte[] bytes = blob.ToArray();
        byte[] table = blob.Table();

        uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0));
        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        Assert.Equal((uint)(bytes.Length - ProjectileDefinitionsBlob.HeaderLength), compressed);
        Assert.Equal((uint)table.Length, uncompressed);
        Assert.Equal(table, Lz4LiteralBlock.Decode(bytes.AsSpan(ProjectileDefinitionsBlob.HeaderLength)));

        // The table is the count and then the records, and every id is one of the client's own.
        Assert.Equal(
            AugustProjectileTable.Records.Count,
            BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(0)));
        Assert.Equal(4 + (AugustProjectileTable.Records.Count * 184), table.Length);
    }

    /// <summary>
    /// <b>docs/107 §3 - the three fields the client's spawn path actually gates on, at the offsets
    /// the binary puts them.</b> <c>FLIGHT_TYPE</c> is ONE byte at <c>rec+0x68</c>, and getting its
    /// width wrong is what shifted every field past <c>rec+0x88</c> in waves 9-12.
    /// </summary>
    [Fact]
    public void SpeedFlightTypeAndLifespanSitWhereTheSpawnPathReadsThem()
    {
        var record = new ProjectileDefinitionRecord(19002)
        {
            Speed = 375f,
            FlightType = AugustProjectileTable.BallisticFlightType,
            Lifespan = 2.5f,
            ProjectileEffectId = 7u,
        };

        byte[] bytes = Bytes(record.WriteTo);

        // key 4 + bodyId 4 + flags 2 + two empty string lengths 8 = 18, the start of the +0x58 run.
        const int run = 4 + 4 + 2 + 4 + 4;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(run)));         // +0x58
        Assert.Equal(375f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(run + 4)));   // +0x5c SPEED
        Assert.Equal(1, bytes[run + 16]);                                                     // +0x68 FLIGHT_TYPE, u8
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(run + 17)));    // +0x6c effect
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(run + 29)));  // +0x78 LIFESPAN

        // ...and the third string's length still starts exactly where the run ends, which is the
        // whole point of the one-byte fix.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(run + 41)));
    }

    /// <summary>
    /// <b>Every shipped projectile carries the owner's own published muzzle speed for its calibre,
    /// or nothing at all</b> (D53, <c>ZoneRetailBalance.cs:180-211</c>). A projectile with no
    /// published speed gets flight type 0 and lifespan 0 - it resolves and does not fly, which is
    /// the honest shape for a number nobody has. Nothing here is invented per projectile.
    /// </summary>
    [Fact]
    public void EveryProjectileWithAPublishedSpeedFliesAndTheRestDoNot()
    {
        Assert.Equal(14, AugustProjectileTable.Records.Count);

        foreach (ProjectileDefinitionRecord record in AugustProjectileTable.Records)
        {
            AugustProjectileFact fact = AugustProjectileFacts.All
                .First(f => f.ProjectileId == record.ProjectileId);

            bool published = AugustProjectileTable.MuzzleSpeedByPenType
                .TryGetValue(fact.PenTypeName, out float speed);

            Assert.Equal(published ? speed : 0f, record.Speed);
            Assert.Equal(published ? AugustProjectileTable.BallisticFlightType : (byte)0, record.FlightType);
            Assert.Equal(AugustProjectileTable.LifespanFor(record.Speed), record.Lifespan);
            Assert.Equal(published, record.Lifespan > 0f);

            // The client's own constructor presets, unchanged, on every record.
            Assert.Equal(0.005f, record.Drag, 6);
            Assert.Equal(10.0f, record.Gravity);
        }

        // The AR-15's .223 and the pump's 12GA, named because they are the shot the owner clicks.
        ProjectileDefinitionRecord rifle =
            AugustProjectileTable.Records.First(r => r.ProjectileId == 19002u);
        ProjectileDefinitionRecord shotgun =
            AugustProjectileTable.Records.First(r => r.ProjectileId == 70040u);

        Assert.Equal(375f, rifle.Speed);
        Assert.Equal(250f, shotgun.Speed);
        Assert.Equal(AugustProjectileTable.RangeUnits / 375f, rifle.Lifespan);

        // The five arrow rows and the spear: no published speed anywhere, so no flight.
        Assert.Equal(
            6,
            AugustProjectileTable.Records.Count(r => r.FlightType == 0));
        Assert.Equal(
            8,
            AugustProjectileTable.Records.Count(r => r.FlightType == AugustProjectileTable.BallisticFlightType));
    }

    /// <summary>The literal encoder handles the three literal-length encodings the format has.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(269)]
    [InlineData(270)]
    [InlineData(5000)]
    public void TheLiteralBlockRoundTripsAtEveryLengthEncoding(int length)
    {
        byte[] data = [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31))];
        Assert.Equal(data, Lz4LiteralBlock.Decode(Lz4LiteralBlock.Encode(data)));
    }

    /// <summary>
    /// Every projectile id Cranberry ships is one the client's own <c>ProjectileToPenTypes.txt</c>
    /// names, and every one of them resolves in the table - the projectile half of the lane's
    /// "every id referenced resolves" rule.
    /// </summary>
    [Fact]
    public void EveryProjectileIdShippedIsAClientIdAndResolves()
    {
        var shipped = AugustProjectileTable.Records.Select(r => r.ProjectileId).ToHashSet();

        Assert.NotEmpty(shipped);
        Assert.Equal(AugustProjectileFacts.ProjectileIds.Count, shipped.Count);
        foreach (AugustProjectileFact fact in AugustProjectileFacts.All)
        {
            Assert.Contains(fact.ProjectileId, shipped);
        }

        // The .223 (the AR-15's round) is in the client's own sheet and therefore in the table.
        Assert.Contains(19002u, shipped);
    }

    /// <summary>
    /// <b>The table is ON by default from wave 14</b> - list 4 made it reachable - and it still
    /// builds at most once per session. <c>CRANBERRY_PROJECTILE_DEFINITIONS=0</c> is the revert.
    /// </summary>
    [Fact]
    public void TheProjectileTableIsOnByDefaultAndSentAtMostOnce()
    {
        var shipped = new WeaponSession(WeaponStageOptions.Default);
        Assert.True(shipped.TryCreateProjectileDefinitions(out ReferenceData packet));
        Assert.Equal(ProjectileDefinitionsBlob.TypeName, packet.TypeName);
        shipped.MarkProjectileDefinitionsSent();
        Assert.False(shipped.TryCreateProjectileDefinitions(out _));

        var off = new WeaponSession(WeaponStageOptions.Default with { SendProjectileDefinitions = false });
        Assert.False(off.TryCreateProjectileDefinitions(out _));
    }

    /// <summary>
    /// The switch is read from the environment the same way every other stage is: only the exact
    /// string <c>"0"</c> turns a default-on stage off.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void TheProjectileSwitchOnlyRespondsToZero(string? value, bool expected)
    {
        WeaponStageOptions options = WeaponStageOptions.FromEnvironment(
            name => name == WeaponStageOptions.ProjectileDefinitionsVariable ? value : null);

        Assert.Equal(expected, options.SendProjectileDefinitions);
    }

    // ------------------------------------------------------------------------------------------
    // Wave 16 - the owner's 2026-09-03 report: "unable to melee / use binoculars; both are shown
    // as a reloadable object". docs/119.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>D287.</b> The fists (fire group 12) and the binoculars (fire group 21) are not firearms,
    /// so neither of their fire modes may carry <c>IRON_SIGHTS</c>. Before wave 16 every one of
    /// the 60 groups had it on mode index 1, and the client acted on it: at 19:50:35 and 19:51:22
    /// of <c>logs\host-20260903-194804.log</c> it sent <c>82 0c SwitchFireModeRequest ... mode=1
    /// (AIM DOWN SIGHTS)</c> for the binoculars and then for the fists.
    /// </summary>
    [Fact]
    public void TheFistsAndTheBinocularsNoLongerAimDownSights()
    {
        Assert.True(AugustWeaponFacts.TryGet(85u, out AugustWeaponFact fists));
        Assert.True(AugustWeaponFacts.TryGet(1542u, out AugustWeaponFact binoculars));
        Assert.Equal(12u, fists.FireGroupId);
        Assert.Equal(21u, binoculars.FireGroupId);
        Assert.DoesNotContain(fists.FireGroupId, AugustWeaponTable.ArmedFireGroupIds);
        Assert.DoesNotContain(binoculars.FireGroupId, AugustWeaponTable.ArmedFireGroupIds);

        IReadOnlyList<FireModeRecord> before = AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom, adsFirstPerson: false, fpCameraFovDegrees: 0.0f);
        IReadOnlyList<FireModeRecord> after = AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom,
            adsFirstPerson: false,
            fpCameraFovDegrees: 0.0f,
            ironSightsArmedOnly: true);

        Assert.Equal(before.Count, after.Count);

        Assert.True(IronSights(before, fists.FireGroupId, 1));
        Assert.True(IronSights(before, binoculars.FireGroupId, 1));
        Assert.False(IronSights(after, fists.FireGroupId, 1));
        Assert.False(IronSights(after, binoculars.FireGroupId, 1));

        // An ARMED group keeps it: the AK-47's group, which the ADS zoom and FORCE_FP_SCOPE
        // already treated as the only kind of group that may aim.
        Assert.True(AugustWeaponFacts.TryGet(2229u, out AugustWeaponFact ak));
        Assert.Contains(ak.FireGroupId, AugustWeaponTable.ArmedFireGroupIds);
        Assert.True(IronSights(after, ak.FireGroupId, 1));

        // Every mode that lost the flag belongs to an unarmed group, and no other field moved.
        for (int i = 0; i < before.Count; i++)
        {
            Assert.True(AugustFireModeFacts.TryGet(before[i].FireModeId, out AugustFireModeFact fact));
            Assert.Equal(before[i] with { IronSights = after[i].IronSights }, after[i]);
            if (before[i].IronSights != after[i].IronSights)
            {
                Assert.DoesNotContain(fact.FireGroupId, AugustWeaponTable.ArmedFireGroupIds);
            }
        }

        static bool IronSights(IReadOnlyList<FireModeRecord> records, uint group, int index)
        {
            uint id = AugustWeaponTable.FireModeIdFor(group, index);
            return records.Single(r => r.FireModeId == id).IronSights;
        }
    }

    /// <summary>
    /// <b>D288.</b> <c>MELEE_ABILITY_ID</c> (<c>rec+0x194</c>) is the client's own column and it
    /// was 0 on all 120 shipped modes, so no mode Cranberry declares was ever a melee mode. It is
    /// now the group's own item's <c>ACTIVATABLE_ABILITY_ID</c> - and never anything at all on a
    /// group a firearm names.
    /// </summary>
    [Fact]
    public void EveryUnarmedFireGroupCarriesItsOwnMeleeAbility()
    {
        // The client's own column, item by item (ClientItemDefinitions.ACTIVATABLE_ABILITY_ID).
        Assert.Equal(1_111_163u, AugustWeaponTable.MeleeAbilityIdFor(9u));    // hatchet, item 82
        Assert.Equal(1_111_164u, AugustWeaponTable.MeleeAbilityIdFor(10u));   // knife,   item 83
        Assert.Equal(1_111_165u, AugustWeaponTable.MeleeAbilityIdFor(11u));   // machete, item 84
        Assert.Equal(1_111_157u, AugustWeaponTable.MeleeAbilityIdFor(21u));   // binoculars, 1542

        // The one RULING: August gives item 85 a zero, so the fists take the owner's own number.
        Assert.Equal(0u, AugustAbilityFacts.AbilityIdOf(85u));
        Assert.Equal(AbilityPackets.Z1FistsAbilityId, AugustWeaponTable.MeleeAbilityIdFor(12u));

        // A firearm's group never gets one - the AK-47 (51) and the AR-15 (6).
        Assert.Equal(0u, AugustWeaponTable.MeleeAbilityIdFor(51u));
        Assert.Equal(0u, AugustWeaponTable.MeleeAbilityIdFor(6u));
        Assert.Equal(0u, AugustWeaponTable.MeleeAbilityIdFor(0u));

        IReadOnlyList<FireModeRecord> records = AugustWeaponTable.FireModeRecordsFor(
            AugustFireModeFacts.AdsZoom,
            adsFirstPerson: false,
            fpCameraFovDegrees: 0.0f,
            meleeAbilityIds: true);

        foreach (FireModeRecord record in records)
        {
            Assert.True(AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact));
            Assert.Equal(AugustWeaponTable.OpticFireGroupIds.Contains(fact.FireGroupId)
                ? 0u : AugustWeaponTable.MeleeAbilityIdFor(fact.FireGroupId), record.MeleeAbilityId);
            if (AugustWeaponTable.ArmedFireGroupIds.Contains(fact.FireGroupId))
            {
                Assert.Equal(0u, record.MeleeAbilityId);
            }
        }

        // Both modes of the group carry it - FUN_1411ceca0's melee branch asks for index 1.
        Assert.Equal(2, records.Count(r =>
            AugustFireModeFacts.TryGet(r.FireModeId, out AugustFireModeFact f)
            && f.FireGroupId == 12u
            && r.MeleeAbilityId == AbilityPackets.Z1FistsAbilityId));
    }

    /// <summary>
    /// Neither wave-16 switch moves a byte of the blob's LENGTH - both fields were already on the
    /// wire as zeros - and both are real reverts.
    /// </summary>
    [Fact]
    public void TheWave16SwitchesChangeContentButNotLength()
    {
        byte[] off = new WeaponSession(WeaponStageOptions.Default with
        {
            IronSightsArmedOnly = false,
            WriteMeleeAbilityIds = false,
        }).Blob.ToArray();
        byte[] on = new WeaponSession(WeaponStageOptions.Default).Blob.ToArray();

        Assert.Equal(off.Length, on.Length);
        Assert.NotEqual(off, on);

        Assert.True(WeaponStageOptions.Default.IronSightsArmedOnly);
        Assert.True(WeaponStageOptions.Default.WriteMeleeAbilityIds);
        Assert.False(WeaponStageOptions.AllOff.IronSightsArmedOnly);
        Assert.False(WeaponStageOptions.AllOff.WriteMeleeAbilityIds);
    }

    /// <summary>Only the exact string <c>0</c> turns either wave-16 switch off.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void TheWave16SwitchesReadTheEnvironment(string? value, bool expected)
    {
        WeaponStageOptions ironSights = WeaponStageOptions.FromEnvironment(
            name => name == WeaponStageOptions.IronSightsArmedOnlyVariable ? value : null);
        WeaponStageOptions melee = WeaponStageOptions.FromEnvironment(
            name => name == WeaponStageOptions.MeleeAbilityVariable ? value : null);

        Assert.Equal(expected, ironSights.IronSightsArmedOnly);
        Assert.Equal(expected, melee.WriteMeleeAbilityIds);
    }

    /// <summary>
    /// The melee composite effect id and its <c>rec+0x22</c> presence bit come from one cell in
    /// the client's own loader, so the record writes them together or not at all.
    /// </summary>
    [Fact]
    public void TheMeleeCompositeEffectIdCarriesItsOwnPresenceBit()
    {
        byte[] without = Write(new FireModeRecord(4u));
        byte[] with = Write(new FireModeRecord(4u, MeleeCompositeEffectId: 1_706u));

        Assert.Equal(without.Length, with.Length);
        Assert.Equal(FireModeRecord.RecordLength, with.Length);

        // rec+0x20, +0x21, +0x22 are the first three body bytes, after the two u32 keys.
        Assert.Equal(0, without[8 + 2]);
        Assert.Equal(WeaponListLayouts.FireModeMeleeCompositeEffectIdFlag, with[8 + 2]);

        static byte[] Write(FireModeRecord record)
        {
            using var writer = new PacketWriter(FireModeRecord.RecordLength);
            record.WriteTo(writer);
            return writer.Written.ToArray();
        }
    }
}
