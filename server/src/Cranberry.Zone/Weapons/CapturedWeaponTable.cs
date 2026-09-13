using System.Collections.Frozen;

namespace Cranberry.Zone.Weapons;

/// <summary>Which table <c>CRANBERRY_WEAPON_TABLE</c> selects.</summary>
public enum WeaponTableSource
{
    /// <summary>Cranberry's own generated table - every wave before docs/123, byte for byte.</summary>
    Generated = 0,

    /// <summary>
    /// The generated table with the friend's captured 1087 numbers crossed into it (D313).
    /// </summary>
    Captured = 1,
}

/// <summary>
/// <b>D313 - the friend's captured weapon table, crossed into Cranberry's own writers (docs/123).</b>
///
/// <para>
/// <b>Why "crossed into" and not "shipped verbatim".</b> docs/122 lane A step 1 set one test: walk
/// the captured body with Cranberry's [P] 1148 layouts and see whether it is consumed exactly. It is
/// not. <c>tools/weapons/decode-friend-table.py</c> diverges in <b>list 0, record index 0, at body
/// offset 168</b>, and the 1087 schema that DOES consume the body to its last byte
/// (<c>C:\Z1\Server\Data\weaponDefinitionSchema.json</c>, 245,944 of 245,944 bytes) says why:
/// </para>
/// <list type="number">
/// <item><b>The 1087 packet has six lists; the 1148 reader calls eight.</b>
/// <c>FUN_140a20570</c> runs list 6 (<c>FUN_140a4fa40</c>) and list 7 (<c>FUN_140a4fd40</c>) after
/// the aim-assist list, and the 1087 schema has nothing for them - so even a body that walked
/// cleanly would end eight bytes early and trip <c>FUN_140b055c0</c>'s <c>_DAT_00000000 = 1</c>.</item>
/// <item><b>A list-0 record is one word longer in 1148.</b> The 1087 record ends
/// <c>VEHICLE_FIRST_PERSON_CAMERA_ID, VEHICLE_THIRD_PERSON_CAMERA_ID, OVERHEAT_EFFECT_ID, MIN_PITCH,
/// MAX_PITCH, AUDIO_GAME_OBJECT</c> - six words - where <c>FUN_140a46ef0</c> reads seven
/// (<c>def+0xa8 +0xac +0xb8 +0xbc +0xc0 +0xc4 +0xc8</c>). The 1148 walk therefore starts the
/// ammo-slot array four bytes late and reads the clip model name's length across a field boundary.</item>
/// <item><b>List 3 remains 73 bytes per state in both builds.</b> August
/// <c>FUN_140a41f00</c> reads <c>GROUP_ID, ID, FLAGS(u8), 16 numbers</c>. The initial
/// 72-byte interpretation was disproved and corrected in docs/123 section 7; preserve the byte.</item>
/// </list>
///
/// <para>
/// So docs/122 lane A step 3 is what ships: the captured values are crossed into Cranberry's own
/// [P] 1148 writers, keyed by the ids August has. <b>List 4 and list-1 identities stay generated</b> - they
/// carry August's own weapon ids, fire groups, ammo item ids and projectile mappings, and the
/// capture's ammo convention is not August's (see <see cref="CapturedAmmoSlotSentinel"/>). What the
/// capture supplies the following shooting parameters. List 0 also receives verified August
/// audio-object hashes and captured animation timings; armed list-1 groups receive captured input
/// flags (docs/weapon-audio-20260904.md and docs/handling-skins-20260904.md):
/// </para>
/// <list type="bullet">
/// <item><b>list 2</b>: the <c>COF_*</c>, <c>CYLOF_*</c> and <c>RECOIL_*</c> families, pellet
/// count/spread, fire/reload clocks, sway, the two named flag bytes and the third byte's
/// native recoil gates. Armed modes also receive their camera, arms and reticle configuration,
/// joined weapon id to weapon id;</item>
/// <item><b>list 3</b>: all 59 cone-of-fire (player-state) groups, which Cranberry has shipped
/// EMPTY since wave 9 - the missing recoil recovery and the missing bloom;</item>
/// <item><b>list 5</b>: all 16 aim-assist configs, also shipped empty until now, and an exact
/// shape match (23 words either way).</item>
/// </list>
///
/// <para>
/// <b>The four accuracy deltas of docs/122 §1 A2 fall out of the crossing rather than being patched
/// on top</b> - they are the difference between the reference table and the capture, and the
/// capture is now what ships. <see cref="AccuracyDeltas"/> pins all four so a silent regression in
/// the join cannot pass.
/// </para>
/// </summary>
public static class CapturedWeaponTable
{
    /// <summary>
    /// Every captured <c>AMMO_SLOTS[0].AMMO_ID</c> is this value - <b>1</b>, on all 102 slots the
    /// capture carries, and every one of its 234 list-4 rows keys on the same 1. It is a 1087 slot
    /// index, not a <c>ClientItemDefinitions</c> row, so it CANNOT be adopted: Cranberry's list 0
    /// and list 4 are keyed on August's own round ids (<c>AmmoTypes.ByWeaponDefinitionId</c>:
    /// 1429 for the AR-15, 2325 for the AK-47, 1511 for the 12GA, 1719 for the .44), and the
    /// capture's own <em>fire modes</em> name those very ids in <c>AMMO_ITEM_ID</c>. docs/123 §5.
    /// </summary>
    public const uint CapturedAmmoSlotSentinel = 1;

    /// <summary>
    /// August fire group id -&gt; the captured fire-mode ids of the group the SAME weapon definition
    /// id names in the capture, in the capture's own order (index 0 is the primary mode).
    /// <para>
    /// The join is <b>weapon id to weapon id</b>, not group id to group id, so a group id that
    /// moved between the two builds cannot silently mis-key a record.
    /// </para>
    /// </summary>
    public static FrozenDictionary<uint, uint[]> CapturedModesByAugustFireGroup { get; } = Build();

    /// <summary>Every weapon definition id the capture carries.</summary>
    public static FrozenSet<uint> CapturedWeaponIds { get; } =
        CapturedWeaponFacts.Weapons.Select(row => row.WeaponDefinitionId).ToFrozenSet();

    /// <summary>The August weapon definition ids the capture also defines.</summary>
    public static FrozenSet<uint> SharedWeaponDefinitionIds { get; } =
        AugustWeaponFacts.All
            .Select(fact => fact.WeaponId)
            .Where(id => id != 0 && CapturedWeaponIds.Contains(id))
            .ToFrozenSet();

    /// <summary>
    /// The August weapon definition ids the capture does NOT define. Their list-0 record, fire
    /// group and fire modes are Cranberry's generated ones, unchanged - the "+N August fills" of
    /// the banner.
    /// </summary>
    public static FrozenSet<uint> AugustOnlyWeaponDefinitionIds { get; } =
        AugustWeaponFacts.All
            .Select(fact => fact.WeaponId)
            .Where(id => id != 0 && !CapturedWeaponIds.Contains(id))
            .ToFrozenSet();

    private static readonly FrozenDictionary<uint, CapturedFireModeRow> ModesById =
        CapturedWeaponFacts.FireModes.ToFrozenDictionary(row => row.FireModeId);

    /// <summary>
    /// <b>The four accuracy deltas of docs/122 §1 A2, as assertions.</b> Each row names the August
    /// weapon definition id, the mode index within its fire group, the client's own column name and
    /// the value the capture carries. <c>CapturedWeaponTableTests</c> reads them back out of the
    /// shipped blob, so "the join stopped working" cannot be a quiet loss of feel.
    /// </summary>
    /// <param name="WeaponDefinitionId">The weapon whose group carries the mode.</param>
    /// <param name="ModeIndex">0 = the hip-fire mode, 1 = the aimed mode.</param>
    /// <param name="Column">The client's own <c>FireModes</c> column name.</param>
    /// <param name="Value">What the capture holds there, as a float (an integer column is exact).</param>
    /// <param name="Ruling">The decision row this delta is recorded under.</param>
    public readonly record struct AccuracyDelta(
        uint WeaponDefinitionId, int ModeIndex, string Column, float Value, string Ruling);

    /// <inheritdoc cref="AccuracyDelta"/>
    public static IReadOnlyList<AccuracyDelta> AccuracyDeltas { get; } =
    [
        // D317 - the AR-15's hip-fire crosshair neither blooms per shot nor triples while walking.
        new(6, 0, "COF_RECOIL", 0.0f, "D317"),
        new(6, 0, "COF_SCALAR_MOVING", 1.0f, "D317"),
        // D318 - the AK-47's bloom is 0.28 a shot, not 0.60, and moving costs nothing.
        new(1405, 0, "COF_RECOIL", 0.28f, "D318"),
        new(1405, 0, "COF_SCALAR_MOVING", 1.0f, "D318"),
        // D319 - the 12GA throws 8 pellets, the retail PS3 number, not 12.
        new(1374, 0, "PELLETS_PER_SHOT", 8.0f, "D319"),
        // D326 - the .44's aimed mode refires in 120 ms behind a 450 ms cooldown, not 450 flat.
        new(1388, 1, "REFIRE_TIME_MS", 120.0f, "D326"),
        new(1388, 1, "FIRE_COOLDOWN_DURATION_MS", 450.0f, "D326"),
    ];

    /// <summary>
    /// The captured words for one generated fire mode, or null when the capture has nothing for it.
    /// The mode is identified the way D159 defines it: <c>fireModeId = fireGroupId * 2 + index</c>.
    /// </summary>
    public static IReadOnlyDictionary<short, uint>? OverridesFor(uint fireModeId)
    {
        uint fireGroupId = fireModeId / 2;
        int index = (int)(fireModeId % 2);

        if (!CapturedModesByAugustFireGroup.TryGetValue(fireGroupId, out uint[]? modeIds)
            || index >= modeIds.Length
            || !ModesById.TryGetValue(modeIds[index], out CapturedFireModeRow row))
        {
            return null;
        }

        var map = new Dictionary<short, uint>(CapturedWeaponFacts.CrossedOffsets.Length + 2);
        for (int i = 0; i < CapturedWeaponFacts.CrossedOffsets.Length; i++)
        {
            map[CapturedWeaponFacts.CrossedOffsets[i]] = row.Words[i];
        }

        // Camera distance, stance offsets, first-person arms and scope selection are part of
        // the reference weapon's handling. Previously only DEFAULT_ZOOM crossed, so the
        // August camera never received the reference's hip-to-aim distance change. Optics and
        // other non-firearms retain their separately verified August setup.
        if (!AugustWeaponTable.ArmedFireGroupIds.Contains(fireGroupId))
        {
            foreach (short offset in CapturedWeaponFacts.GunPresentationOffsets)
                map.Remove(offset);
            // The August throwable windup is already supplied by its separate implementation.
            map.Remove(WeaponListLayouts.FireModeFireDurationMs);
        }

        // The captured AR experiment zeros first-shot response/kick and writes 50,000,000 for
        // animation recoil. Restore the separate Z1 weaponDefinitions.json mode-8/9 values.
        // These are presentation/first-shot corrections, not a change to the cone-of-fire rules.
        if (fireGroupId == 6)
        {
            map[WeaponListLayouts.FireModeRecoilFirstShotModifier] = BitConverter.SingleToUInt32Bits(0.7f);
            map[WeaponListLayouts.FireModeAnimKickMagnitude] = BitConverter.SingleToUInt32Bits(1f);
            map[WeaponListLayouts.FireModeAnimRecoilMagnitude] = BitConverter.SingleToUInt32Bits(50f);
        }

        // The third byte's 0x40 and 0x20 have no datasheet names, but are explicitly consumed
        // by August FUN_141485bf0 as the master and horizontal recoil gates. Leaving them at
        // zero disabled recoil entirely, regardless of the magnitude/recovery words above.
        map[CapturedWeaponFacts.Flags0Offset] = row.Flags0;
        map[CapturedWeaponFacts.Flags1Offset] = row.Flags1;
        if (AugustWeaponTable.ArmedFireGroupIds.Contains(fireGroupId))
            map[CapturedWeaponFacts.Flags2Offset] = (uint)(row.Flags2 & 0x60);

        // A PLAYER_STATE_GROUP_ID that names no shipped list-3 record is zeroed: the client's lookup
        // (FUN_14147f420) returns null on a miss, and a 0 is the constructor default it already
        // copes with. The capture has seven such modes (groups 29 and 37, weapons 1403/1418/1419/1423).
        if (map.TryGetValue(WeaponListLayouts.FireModePlayerStateGroupId, out uint stateGroup)
            && stateGroup != 0
            && !ConeOfFireIds.Contains(stateGroup))
        {
            map[WeaponListLayouts.FireModePlayerStateGroupId] = 0;
        }

        // D339: AMMO_ITEM_ID (rec+0x2c) - the SECOND word the client's reload key needs. Its
        // predicate (FUN_1411c7330 -> comp vt+0xb0 FUN_14228c870) is AMMO_PER_SHOT > 0, rounds <
        // CLIP_SIZE, and reserve(FUN_141487990(comp, rec+0x2c)) > 0 - the bag count of THIS item
        // id. With 0 here the count is 0 and R is silent, whatever the bag holds (the 21:05
        // session: 120 x 1429 in the bag, no 82 07). The value is the August ammo item of the
        // weapon that names this fire group, the same id the capture's own column carries
        // (docs/123 §5). Not crossed from the capture only because D196 kept the whole ammo
        // family August's; the id IS August's.
        if (AmmoItemByFireGroup.TryGetValue(fireGroupId, out uint ammoItemId) && ammoItemId != 0)
        {
            map[WeaponListLayouts.FireModeAmmoItemId] = ammoItemId;
        }

        return map;
    }

    /// <summary>Fire group id -> the August ammo item its weapon fires (D339).</summary>
    public static IReadOnlyDictionary<uint, uint> AmmoItemByFireGroup { get; } = BuildAmmoItemByFireGroup();

    private static Dictionary<uint, uint> BuildAmmoItemByFireGroup()
    {
        var map = new Dictionary<uint, uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId != 0
                && Combat.AmmoTypes.ByWeaponDefinitionId.TryGetValue(weapon.WeaponId, out uint ammoItemId)
                && ammoItemId != 0)
            {
                map.TryAdd(weapon.FireGroupId, ammoItemId);
            }
        }

        return map;
    }

    /// <summary>Every cone-of-fire group id list 3 ships - what a fire mode's link may name.</summary>
    public static IReadOnlySet<uint> ConeOfFireIds { get; } =
        CapturedWeaponFacts.ConeOfFire.Select(group => group.ConeOfFireId).ToHashSet();

    /// <summary>
    /// The captured cone-of-fire groups as 1148 list-3 records - <b>the recoil recovery and the
    /// crosshair bloom Cranberry has never sent</b>. A record is the group id, the [U] word at
    /// <c>rec+0x00</c>, and one 73-byte element per player state - the key, the first word, the
    /// one-byte <c>FLAGS</c> the client reads at <c>elem+0x20</c>, then sixteen words.
    /// </summary>
    public static IReadOnlyList<ConeOfFireRecord> ConeOfFireRecords { get; } =
    [
        .. CapturedWeaponFacts.ConeOfFire.Select(group => new ConeOfFireRecord(
            group.ConeOfFireId,
            States: [.. group.States.Select(state =>
                new ConeOfFireStateRow(state.StateId, state.Words, state.Flags))])),
    ];

    /// <summary>The captured aim-assist configs as 1148 list-5 records: 23 words, an exact match.</summary>
    public static IReadOnlyList<AimAssistRecord> AimAssistRecords { get; } =
    [
        .. CapturedWeaponFacts.AimAssist.Select(row => new AimAssistRecord(row.AimAssistId, row.Words)),
    ];

    /// <summary>
    /// The blob Cranberry would send with <see cref="WeaponTableSource.Captured"/> selected: the
    /// generated one, with every fire mode the capture knows carrying the capture's words and with
    /// lists 3 and 5 filled.
    /// <para>
    /// Lists 1 and 4 come through untouched; list 0 changes only the audio-object hash for armed
    /// weapons whose captured hash resolves in August. Every weapon id, fire group, ammo slot and
    /// projectile mapping is exactly what <see cref="WeaponTableSource.Generated"/> ships - the merge
    /// rule of docs/122 lane A step 2 ("August ids absent from the capture are filled from the
    /// generated table") is satisfied by construction, for all of them at once.
    /// </para>
    /// </summary>
    public static WeaponDefinitionsBlob Apply(WeaponDefinitionsBlob generated)
    {
        ArgumentNullException.ThrowIfNull(generated);

        // Lists 3 and 5 ride list 2: a fire mode is the only thing that names a cone-of-fire group
        // or an aim-assist config, so with list 2 empty (CRANBERRY_WEAPON_DEFS_LIST2=0, or the
        // 32-byte empty envelope every stage-1 first run ships) the capture changes nothing at all
        // and the blob stays byte-identical to the generated one.
        if (generated.FireModes is not { Count: > 0 })
        {
            return generated;
        }

        var handled = (generated.WeaponDefinitions ?? [])
            .Where(record => record.AmmoSlots is { Count: > 0 }
                && CapturedWeaponHandlingFacts.ByWeaponId.ContainsKey(record.WeaponDefinitionId))
            .ToDictionary(record => record.WeaponDefinitionId,
                record => CapturedWeaponHandlingFacts.ByWeaponId[record.WeaponDefinitionId]);
        var groupFlags = new Dictionary<uint, byte>();
        foreach (var record in generated.WeaponDefinitions ?? [])
            if (handled.TryGetValue(record.WeaponDefinitionId, out var handling))
                foreach (uint group in record.FireGroupIds)
                    groupFlags.TryAdd(group, handling.FireGroupFlags);

        // The source grenade modes consume a one-round magazine. Our supported throws
        // consume an inventory stack on 82 03 and intentionally have no local ammo slot.
        // Even without CHECK_ENTER_FIRE_STATE, August 1422934f0 checks AMMO_PER_SHOT at
        // release: importing 1 lets the windup play, then aborts against that missing slot.
        var magazinelessGroups = (generated.WeaponDefinitions ?? [])
            .Where(record => record.AmmoSlots is not { Count: > 0 })
            .SelectMany(record => record.FireGroupIds).ToHashSet();
        var stackThrowableModes = (generated.FireModeProjectiles ?? [])
            .Where(mapping => AugustThrowables.TryGetByProjectile(mapping.ProjectileDefinitionId, out var fact)
                && magazinelessGroups.Contains(fact.FireGroupId))
            .Select(mapping => mapping.FireModeDefinitionId).ToHashSet();

        return generated with
        {
            WeaponDefinitions = generated.WeaponDefinitions is null ? null :
                [.. generated.WeaponDefinitions.Select(record =>
                {
                    if (handled.TryGetValue(record.WeaponDefinitionId, out var handling))
                        record = record with
                        {
                            WeaponGroupId = handling.WeaponGroupId,
                            EquipTimeMs = handling.EquipMs,
                            UnequipTimeMs = handling.UnequipMs,
                            ToIronSightsTimeMs = checked((int)handling.AimInMs),
                            FromIronSightsTimeMs = checked((int)handling.AimOutMs),
                            AimInAnimationTimeMs = handling.AimInAnimMs,
                            AimOutAnimationTimeMs = handling.AimOutAnimMs,
                            SprintRecoveryTimeMs = handling.SprintRecoveryMs,
                            AnimationSetName = handling.AnimationSetName,
                        };
                    return (record.AmmoSlots is { Count: > 0 } || record.WeaponDefinitionId == 12)
                        && CapturedWeaponAudioFacts.ByWeaponDefinitionId.TryGetValue(
                            record.WeaponDefinitionId, out var audio)
                        ? record with { AudioGameObject = audio.Hash }
                        : record;
                })],
            // Group bit 0x40 selects the separate left/right-trigger input branch in
            // FUN_1411ceca0. It is not the mode's AUTOMATIC bit. Z1's firearm groups use 0;
            // keep the AK mode's AUTOMATIC flag so tapping stops and holding repeats.
            FireGroups = generated.FireGroups is null ? null :
                [.. generated.FireGroups.Select(group => groupFlags.TryGetValue(group.FireGroupId, out byte flags)
                    ? group with { Flags = flags } : group)],
            FireModes = [.. (generated.FireModes ?? []).Select(mode =>
            {
                if (mode.FireModeId is 24 or 25) return mode with { EffectGroup = 0 };
                if (OverridesFor(mode.FireModeId) is not { } overrides) return mode;
                if (mode.CheckEnterFireState)
                {
                    // The optical no-fire guard is August behavior, independent of the
                    // captured tuning. Do not replace its prerequisite with the old zero.
                    overrides = new Dictionary<short, uint>(overrides)
                    {
                        [WeaponListLayouts.FireModeFlags1] =
                            overrides.GetValueOrDefault(WeaponListLayouts.FireModeFlags1)
                            | WeaponListLayouts.FireModeCheckEnterFireStateFlag,
                        [WeaponListLayouts.FireModeAmmoPerShot] = (uint)mode.AmmoPerShot,
                    };
                }
                if (mode.Type == WeaponListLayouts.FireModeTypeThrowable
                    && stackThrowableModes.Contains(mode.FireModeId))
                {
                    overrides = new Dictionary<short, uint>(overrides)
                    {
                        [WeaponListLayouts.FireModeAmmoPerShot] = 0,
                    };
                }
                return mode with { Overrides = overrides };
            })],
            ConeOfFire = ConeOfFireRecords,
            AimAssist = AimAssistRecords,
        };
    }

    /// <summary>How many of the shipped fire modes the capture supplies words for.</summary>
    public static int CrossedFireModeCount(WeaponDefinitionsBlob generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        return (generated.FireModes ?? []).Count(mode => OverridesFor(mode.FireModeId) is not null);
    }

    private static FrozenDictionary<uint, uint[]> Build()
    {
        Dictionary<uint, uint[]> capturedByWeapon = [];
        foreach (CapturedWeaponRow row in CapturedWeaponFacts.Weapons)
        {
            // A capture row exists per (weapon, fire group) pair; the first group is the one the
            // client's own CreateItem walks first, and it is the only one August's sheet names.
            if (row.FireModeIds.Length > 0)
            {
                _ = capturedByWeapon.TryAdd(row.WeaponDefinitionId, row.FireModeIds);
            }
        }

        Dictionary<uint, uint[]> byAugustGroup = [];
        foreach (AugustWeaponFact fact in AugustWeaponFacts.All)
        {
            if (fact.FireGroupId == 0
                || !capturedByWeapon.TryGetValue(fact.WeaponId, out uint[]? modeIds))
            {
                continue;
            }

            _ = byAugustGroup.TryAdd(fact.FireGroupId, modeIds);
        }

        return byAugustGroup.ToFrozenDictionary();
    }
}
