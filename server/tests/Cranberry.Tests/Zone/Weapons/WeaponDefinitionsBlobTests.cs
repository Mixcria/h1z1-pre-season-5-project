using System.Buffers.Binary;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// <c>ReferenceData "WeaponDefinitions"</c> (0x17) - docs/58 §4, docs/60 stage 1.
/// <para>
/// <b>What these tests can and cannot prove.</b> They prove the bytes match the layout recovered by
/// decompiling <c>FUN_140a20570</c>, <c>FUN_140a4ef30</c>, <c>FUN_140a398c0</c>, <c>FUN_140a51070</c>,
/// <c>FUN_140a46ef0</c> and the four sub-readers dumped this wave. They prove <b>nothing</b> about
/// the client: not one byte of this table has ever been on a wire, so the label is DERIVED, and the
/// only thing that can promote it is the absence of
/// <c>Received ReferenceData type=WeaponDefinitions, but no handler!</c> from the client's own
/// <c>ClientBadData.log</c> (docs/60 §5, run 1).
/// </para>
/// </summary>
public sealed class WeaponDefinitionsBlobTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ------------------------------------------------------------------ the empty table

    /// <summary>
    /// docs/58 §4b: every one of the eight list readers begins with an <c>i32 count</c>, so 32 zero
    /// bytes is a well-formed, completely empty table. This is the property that makes stage 1 safe
    /// to try at all, so it is pinned before anything is put in the lists.
    /// </summary>
    [Fact]
    public void AnEmptyTableIsThirtyTwoZeroBytes()
    {
        byte[] blob = new WeaponDefinitionsBlob().ToArray();

        Assert.Equal(WeaponDefinitionsBlob.EmptyLength, blob.Length);
        Assert.Equal(32, blob.Length);
        Assert.All(blob, b => Assert.Equal(0, b));
    }

    // ------------------------------------------------------------------ docs/58 §8a, byte for byte

    /// <summary>
    /// <b>The docs/58 §8a worked packet for the AR-15, byte for byte</b>: 114 bytes total, an
    /// 85-byte blob, list 0 empty, list 1 carrying fire group 6 with <c>rec+0x38 = 0x40</c>
    /// (the automatic branch of <c>FUN_1411ceca0</c>), lists 2-7 empty.
    /// <para>
    /// If this test is ever edited, re-read <c>FUN_140a398c0</c> first. A wrong record length here
    /// does not produce a rejected packet - it silently misaligns every record after it.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWorkedAr15PacketIsExactlyDocs58Section8a()
    {
        var blob = new WeaponDefinitionsBlob(
            WeaponDefinitions: [],
            FireGroups: [new FireGroupRecord(6, FireGroupRecord.AutomaticFlag)]);

        byte[] wire = Bytes(new ReferenceData(WeaponDefinitionsBlob.TypeName, blob.ToArray()).WriteTo);

        var expected = new List<byte>
        {
            0x17,                       // opcode
            0x11, 0x20,                 // u16 0x2011 = 0x2000 | 17
        };
        expected.AddRange(System.Text.Encoding.ASCII.GetBytes("WeaponDefinitions"));
        expected.Add(0x00);                                     // NUL the 13-bit length excludes
        expected.AddRange([0x55, 0x00, 0x00, 0x00]);            // uncompressedLength = 85
        expected.AddRange([0x55, 0x00, 0x00, 0x00]);            // blobLength         = 85
        expected.AddRange([0x00, 0x00, 0x00, 0x00]);            // list 0 WeaponDefinitions count 0
        expected.AddRange([0x01, 0x00, 0x00, 0x00]);            // list 1 FireGroups        count 1
        expected.AddRange([0x06, 0x00, 0x00, 0x00]);            //   fireGroupId = 6   -> rec+0x78
        expected.AddRange([0x00, 0x00, 0x00, 0x00]);            //   rec+0x18 = 0
        expected.AddRange([0x00, 0x00, 0x00, 0x00]);            //   fire-mode id count m = 0
        expected.Add(0x40);                                     //   rec+0x38 bit 6 = AUTOMATIC
        for (int i = 0; i < FireGroupRecord.TrailingWordCount; i++)   // rec+0x3c..+0x60
        {
            // 2026-09-02: words 6 and 7 (rec+0x54 / +0x58) are the spin-up MULTIPLIERS = 1.0f;
            // docs/58 §8a wrote them as 0 before DIAGNOSIS-wield-freeze named them.
            expected.AddRange(i is FireGroupRecord.SpinUpMovementModifierIndex or FireGroupRecord.SpinUpTurnRateModifierIndex
                ? [0x00, 0x00, 0x80, 0x3f]
                : [0x00, 0x00, 0x00, 0x00]);
        }

        expected.AddRange(new byte[4 * 6]);                     // lists 2-7 count 0

        Assert.Equal(expected, wire);
        Assert.Equal(114, wire.Length);
        Assert.Equal(85, blob.Length);
    }

    /// <summary>
    /// docs/58 §4c: <b>53 + 4m</b> bytes per fire-group record - <c>u32 id; u32; i32 m + u32[m];
    /// u8 flags; u32 x10</c>. The <c>+0x38</c> flags byte must sit at offset 12 of a record with no
    /// fire-mode ids, because that is the byte the crash instruction reads.
    /// </summary>
    [Fact]
    public void AFireGroupRecordIsFiftyThreeBytesWithTheFlagsByteAtTwelve()
    {
        byte[] record = Bytes(new FireGroupRecord(0x11223344, 0x40).WriteTo);

        Assert.Equal(FireGroupRecord.MinimalLength, record.Length);
        Assert.Equal(53, record.Length);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(record));  // rec+0x78
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4))); // rec+0x18
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8)));   // m
        Assert.Equal(0x40, record[12]);                                              // rec+0x38

        // The ten trailing words: indices 6 and 7 (rec+0x54 / rec+0x58) are the spin-up movement
        // and turn-rate MULTIPLIERS and carry the client's own default 1.0f; the rest stay 0.
        for (int i = 0; i < FireGroupRecord.TrailingWordCount; i++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(13 + (4 * i)));
            uint expected = i is FireGroupRecord.SpinUpMovementModifierIndex or FireGroupRecord.SpinUpTurnRateModifierIndex
                ? 0x3f800000u
                : 0u;
            Assert.Equal(expected, word);
        }
    }

    /// <summary>The fire-mode id array is inline and grows the record by exactly 4 bytes each.</summary>
    [Fact]
    public void FireModeIdsGrowTheRecordByFourBytesEach()
    {
        byte[] record = Bytes(new FireGroupRecord(6, 0, [7u, 8u]).WriteTo);

        Assert.Equal(FireGroupRecord.MinimalLength + 8, record.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)));
        Assert.Equal(0, record[20]);    // the flags byte moved by 8, as the layout requires
    }

    // ------------------------------------------------------------------ list 0 (docs/58 U1, closed)

    /// <summary>
    /// The <c>WeaponDefinitions</c> record recovered this wave: <b>133 bytes</b> plus 4 per
    /// fire-group id, with the id first and the fire-group id list last.
    /// <para>
    /// The count of <c>133</c> is the whole point of the test - it is the sum of the exact field
    /// sequence <c>FUN_140a46ef0</c> reads, including the two <c>u32</c>s of <c>FUN_140a51490</c>,
    /// the empty <c>SoeUtil::StringFixed&lt;32&gt;</c> of <c>FUN_140b78f60</c>, the empty
    /// <c>FUN_140a515d0</c> array and the <c>FUN_140a52a50</c> count.
    /// </para>
    /// </summary>
    [Fact]
    public void AWeaponDefinitionRecordIsOneHundredAndThirtyThreeBytesPlusItsFireGroupIds()
    {
        byte[] record = Bytes(new WeaponDefinitionRecord(6, [6u]).WriteTo);

        Assert.Equal(WeaponDefinitionRecord.MinimalLength + 4, record.Length);
        Assert.Equal(137, record.Length);
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(record));               // -> rec+0x110
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(129)));     // -> def+0xf8
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(133)));   // -> def+0xf0

        // Between the id and the fire-group list, three fields carry the client's own defaults and
        // everything else is zero (2026-09-02, DIAGNOSIS-wield-freeze §6): the body "ID" at def+0x18
        // (bytes 4..7) repeats the list key, and the two MULTIPLIERS def+0x58 / def+0x5c
        // (bytes 57..60 / 61..64) are 1.0f. Shipping 0 there was the 08-31 wield freeze.
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4)));            // -> def+0x18
        Assert.All(record.Skip(8).Take(49), b => Assert.Equal(0, b));                              // def+0x20 .. def+0x4c
        Assert.Equal(0x3f800000u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(57)));   // -> def+0x58 TurnModifier
        Assert.Equal(0x3f800000u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(61)));   // -> def+0x5c MovementModifier
        Assert.All(record.Skip(65).Take(64), b => Assert.Equal(0, b));                             // def+0x70 .. def+0xd0
    }

    /// <summary>
    /// The bisect levers for the owner's click (OVERHAUL-PLAN §4.3): a movement modifier of 0.5
    /// changes exactly bytes 61..64, and <c>WriteBodyId = false</c> changes exactly bytes 4..7.
    /// </summary>
    [Fact]
    public void TheMovementModifierAndBodyIdLeversChangeExactlyTheirOwnBytes()
    {
        byte[] baseline = Bytes(new WeaponDefinitionRecord(6, [6u]).WriteTo);
        byte[] halved = Bytes(new WeaponDefinitionRecord(6, [6u], MovementModifier: 0.5f).WriteTo);
        byte[] noBodyId = Bytes(new WeaponDefinitionRecord(6, [6u]) { WriteBodyId = false }.WriteTo);

        Assert.Equal(baseline.Length, halved.Length);
        Assert.Equal(baseline.Length, noBodyId.Length);
        // 1.0f = 00 00 80 3f and 0.5f = 00 00 00 3f share three of four bytes, so compare the words.
        int[] halvedDiff = [.. Enumerable.Range(0, baseline.Length).Where(i => baseline[i] != halved[i])];
        Assert.NotEmpty(halvedDiff);
        Assert.All(halvedDiff, i => Assert.InRange(i, 61, 64));
        Assert.Equal(0x3f000000u, BinaryPrimitives.ReadUInt32LittleEndian(halved.AsSpan(61)));   // 0.5f

        int[] noBodyIdDiff = [.. Enumerable.Range(0, baseline.Length).Where(i => baseline[i] != noBodyId[i])];
        Assert.NotEmpty(noBodyIdDiff);
        Assert.All(noBodyIdDiff, i => Assert.InRange(i, 4, 7));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(noBodyId.AsSpan(4)));
    }

    /// <summary>
    /// <b>The guard docs/49 §5.3 asked for on 2026-08-30</b>, one day before the zero shipped: no
    /// record the server actually sends may carry a zero multiplier or a body ID that differs from
    /// its list key. Parses every list-0 and list-1 record of the shipped blob with the
    /// <c>FUN_140a46ef0</c> / <c>FUN_140a398c0</c> layouts. This test would have failed from wave 9
    /// until 2026-09-02.
    /// </summary>
    [Fact]
    public void TheShippedTableNeverZeroesAMultiplierOrItsOwnId()
    {
        // populateFireModes: false keeps this test on the two lists it was written for. The list-2
        // half of the same guard - every one of the 34 scalars FUN_1422267e0 presets to 1.0f - is
        // WeaponListsTests.NoFireModeShipsAZeroedScalarAndTheCursorLandsExactly (lane 2D).
        WeaponDefinitionsBlob blob = AugustWeaponTable.CreateBlob(
            populateWeaponDefinitions: true, populateFireModes: false);
        byte[] bytes = blob.ToArray();
        int cursor = 0;

        int definitions = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        Assert.Equal(blob.WeaponDefinitions!.Count, definitions);
        for (int i = 0; i < definitions; i++)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor));            // -> def+0x110
            uint bodyId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4));     // -> def+0x18
            float turn = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(cursor + 57));     // -> def+0x58
            float movement = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(cursor + 61)); // -> def+0x5c
            Assert.Equal(key, bodyId);
            Assert.True(turn > 0f, $"weapon {key}: TurnModifier {turn} would pin the player's yaw");
            Assert.True(movement > 0f, $"weapon {key}: MovementModifier {movement} would freeze the player");

            // Wave 14: the ammo-slot array's count is at byte 125 and its rows come before the
            // fire-group count, so neither offset is fixed any more.
            int ammoSlots = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor + 125)); // -> def+0xe0
            int afterSlots = cursor + 129 + (ammoSlots * WeaponAmmoSlotRow.MinimalLength);
            int fireGroups = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(afterSlots));  // -> def+0xf8
            cursor = afterSlots + 4 + (4 * fireGroups);
        }

        int groups = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        Assert.Equal(blob.FireGroups!.Count, groups);
        for (int i = 0; i < groups; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor));
            int modes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor + 8));
            int trailing = cursor + 13 + (4 * modes);
            float spinMove = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(trailing + (4 * FireGroupRecord.SpinUpMovementModifierIndex)));
            float spinTurn = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(trailing + (4 * FireGroupRecord.SpinUpTurnRateModifierIndex)));
            Assert.True(spinMove > 0f, $"fire group {id}: SpinUpMovementModifier {spinMove}");
            Assert.True(spinTurn > 0f, $"fire group {id}: SpinUpTurnRateModifier {spinTurn}");
            cursor += FireGroupRecord.MinimalLength + (4 * modes);
        }

        // Lists 2-7 follow and are empty; the cursor must land exactly on them.
        Assert.Equal(bytes.Length - (4 * 6), cursor);
    }

    /// <summary>An empty fire-group id list still costs its <c>i32 0</c>: 133 bytes exactly.</summary>
    [Fact]
    public void AWeaponDefinitionWithNoFireGroupsIsTheMinimalLength() =>
        Assert.Equal(
            WeaponDefinitionRecord.MinimalLength,
            Bytes(new WeaponDefinitionRecord(1400, []).WriteTo).Length);

    // ------------------------------------------------------------------ the shipped table

    /// <summary>
    /// The table Cranberry actually ships at stage 1: <b>every distinct non-zero
    /// <c>FIRE_GROUP_ID</c> in the client's own datasheet</b>, list 0 empty. 60 records, so the blob
    /// is <c>32 + 60 * 53 = 3,212</c> bytes - small enough that shipping all of them beats shipping
    /// one per experiment.
    /// </summary>
    [Fact]
    public void TheShippedTableCarriesEveryFireGroupTheClientsOwnDataNames()
    {
        // The 3,212 is the list-0-and-1-only figure; lane 2D's list 2 is measured in
        // WeaponListsTests.TurningListTwoOffRestoresTheWaveNineBytes.
        WeaponDefinitionsBlob blob = AugustWeaponTable.CreateBlob(
            populateWeaponDefinitions: false, populateFireModes: false);

        Assert.Empty(blob.WeaponDefinitions!);
        Assert.Equal(60, blob.FireGroups!.Count);
        Assert.Equal(32 + (60 * FireGroupRecord.MinimalLength), blob.Length);
        Assert.Equal(3212, blob.Length);
        Assert.Equal(blob.Length, blob.ToArray().Length);

        // The three weapons docs/58 works: AR-15 (6), fists (12), R380 (38).
        uint[] ids = [.. blob.FireGroups.Select(group => group.FireGroupId)];
        Assert.Contains(6u, ids);
        Assert.Contains(12u, ids);
        Assert.Contains(38u, ids);
        Assert.DoesNotContain(0u, ids);         // FIRE_GROUP_ID 0 means "no fire group", never a key
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal([.. ids.OrderBy(id => id)], ids);
    }

    /// <summary>
    /// The one DESIGN value in the table: fire group 6 (the AR-15's) is the only group Cranberry
    /// marks automatic, because docs/58 §8a is the only worked value and nothing in the client's own
    /// data separates automatic from semi-automatic. Fists (12) must stay on the melee branch.
    /// </summary>
    [Fact]
    public void OnlyTheAr15sFireGroupIsMarkedAutomatic()
    {
        Dictionary<uint, FireGroupRecord> byId =
            AugustWeaponTable.FireGroupRecords.ToDictionary(record => record.FireGroupId);

        Assert.True(byId[6].IsAutomatic);
        Assert.False(byId[12].IsAutomatic);     // fists - bit 6 clear = single-shot / melee
        Assert.False(byId[38].IsAutomatic);     // R380
        Assert.Single(AugustWeaponTable.FireGroupRecords, record => record.IsAutomatic);
    }

    /// <summary>
    /// With list 0 populated the blob grows by one record per distinct <c>WEAPON_ID</c> and stays
    /// self-consistent. This is the opt-in shape (<c>CRANBERRY_WEAPON_DEFS_LIST0=1</c>).
    /// </summary>
    [Fact]
    public void PopulatingListZeroAddsOneRecordPerWeaponId()
    {
        WeaponDefinitionsBlob populated = AugustWeaponTable.CreateBlob(
            populateWeaponDefinitions: true, populateFireModes: false);
        WeaponDefinitionsBlob empty = AugustWeaponTable.CreateBlob(
            populateWeaponDefinitions: false, populateFireModes: false);

        int definitions = populated.WeaponDefinitions!.Count;
        Assert.True(definitions > 0);

        // Wave 14: a gun's record also carries one 37-byte ammo-slot row (WeaponAmmoSlotRow);
        // melee, throwables and the fists keep the empty array and their old length.
        int ammoSlots = populated.WeaponDefinitions.Sum(record => record.AmmoSlots?.Count ?? 0);
        Assert.True(ammoSlots > 0);
        Assert.Equal(
            empty.Length
                + (definitions * (WeaponDefinitionRecord.MinimalLength + 4))
                + (ammoSlots * WeaponAmmoSlotRow.MinimalLength),
            populated.Length);
        Assert.Equal(populated.Length, populated.ToArray().Length);

        // Every definition names exactly one fire group, and that group is in list 1 - otherwise the
        // client's self-init would build an array whose id cannot resolve.
        var groups = AugustWeaponTable.FireGroupIds.ToHashSet();
        foreach (WeaponDefinitionRecord definition in populated.WeaponDefinitions)
        {
            uint fireGroupId = Assert.Single(definition.FireGroupIds);
            Assert.Contains(fireGroupId, groups);
        }

        // The AR-15's PARAM1 is 6 and its fire group is 6 (docs/58 §8a); the R380's are 1400 / 38.
        Dictionary<uint, WeaponDefinitionRecord> byId =
            populated.WeaponDefinitions.ToDictionary(record => record.WeaponDefinitionId);
        Assert.Equal([6u], byId[6].FireGroupIds);
        Assert.Equal([38u], byId[1400].FireGroupIds);
        Assert.Equal([12u], byId[12].FireGroupIds);     // fists
    }
}
