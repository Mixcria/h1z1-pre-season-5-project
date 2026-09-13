using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Weapons;

/// <summary>
/// Everything a connection needs to ship docs/58's mechanism, in one object: which stages are on,
/// the <c>ReferenceData "WeaponDefinitions"</c> packet to send once, the Weapon <c>ItemAdd</c> tail
/// for a given item, and the <see cref="IActiveHandClearance"/> that decides whether that item may
/// ever reach body slot 7.
/// <para>
/// <b>Intended use, in this order</b> (docs/60 §3 spells out the call sites):
/// </para>
/// <code>
/// var weapons = new WeaponSession(WeaponStageOptions.FromEnvironment());
/// // once per session, in the bootstrap burst, BEFORE any weapon ItemAdd:
/// if (weapons.TryCreateWeaponDefinitions(out ReferenceData table)) { send(table); weapons.MarkWeaponDefinitionsSent(); }
/// // per granted item, instead of new ItemAdd(guid, record):
/// SendTunnel(connection, weapons.CreateItemAdd(selfGuid, record));
/// // wherever a slot-7 row might be wanted:
/// new SetCharacterEquipmentWithSlots(..., Clearance: weapons.Clearance)
/// </code>
/// <para>
/// <b>Ordering is load-bearing in one place</b> (docs/58 §7): the table must arrive before the
/// <c>ItemAdd</c>, because <c>CreateItem</c>'s self-init runs once, at construction, and no
/// re-initialisation path for a definition that arrives later is known (docs/58 U8). The ledger
/// enforces the consequence rather than the ordering: an item whose tail went out before the table
/// was declared is simply never cleared.
/// </para>
/// </summary>
public sealed class WeaponSession
{
    private readonly WeaponFireGroupLedger _ledger = new();
    private readonly bool _crateOpeningWeapon;

    /// <param name="options">
    /// The stage switches. <see cref="WeaponStageOptions.Effective"/> is applied here, so a caller
    /// cannot accidentally enable stage 3 without stage 2.
    /// </param>
    public WeaponSession(WeaponStageOptions? options = null, bool crateOpeningWeapon = false)
    {
        Options = (options ?? WeaponStageOptions.Default).Effective;
        _crateOpeningWeapon = crateOpeningWeapon;
    }

    /// <summary>The stages actually in force, after the stage-3-needs-stage-2 rule.</summary>
    public WeaponStageOptions Options { get; }

    /// <summary>
    /// <b>docs/107 §1 - where the magazine in the <c>ItemAdd</c> tail comes from.</b> Given the item
    /// instance guid and its definition id, answers the rounds now in that weapon. Null (the default
    /// for every decode-only and test caller) means "nobody is counting", and the tail then carries
    /// <see cref="WeaponItemAddTail.Magazine"/> = 0 - which is exactly what a looted gun holds under
    /// <c>AmmoOptions.GunsSpawnEmpty</c>, so the fallback is truthful rather than merely safe.
    /// <para>
    /// A delegate rather than a reference to <c>ShooterCombatState</c> because the tail writer must
    /// not depend on the combat loop, and because the live wiring has to fall back to
    /// <c>ShooterCombatState.DefaultMagazineFor</c> for a guid combat has not met yet - a decision
    /// that needs <c>AmmoOptions</c>, which lives on the other side of the session.
    /// <c>ZoneService.NewSessionState</c> is the one place that binds it.
    /// </para>
    /// </summary>
    public Func<ulong, uint, int>? MagazineSource { get; set; }

    /// <summary>
    /// The clearance to hand <c>SetCharacterEquipmentWithSlots</c>. <b>Null unless stage 3 is on</b>,
    /// so with the shipped defaults <c>ActiveHandRowGuard</c> gets no clearance object at all and
    /// behaves exactly as it did in wave 5.
    /// </summary>
    public IActiveHandClearance? Clearance => Options.AllowWielding ? _ledger : null;

    /// <summary>The ledger itself, for logging and tests.</summary>
    public WeaponFireGroupLedger Ledger => _ledger;

    private Dictionary<uint, (uint Equip, uint Unequip)>? _equipmentTimings;

    public (uint Equip, uint Unequip) EquipmentTiming(uint weaponDefinitionId)
    {
        _equipmentTimings ??= ReadEquipmentTimings(Blob);
        return _equipmentTimings.GetValueOrDefault(weaponDefinitionId);
    }

    private static Dictionary<uint, (uint Equip, uint Unequip)> ReadEquipmentTimings(WeaponDefinitionsBlob blob) =>
        (blob.WeaponDefinitions ?? []).ToDictionary(
            row => row.WeaponDefinitionId, row => (row.EquipTimeMs, row.UnequipTimeMs));

    /// <summary>
    /// The blob this session would send, whether or not stage 1 is on.
    /// <para>
    /// <b>D313</b>: with <see cref="WeaponStageOptions.WeaponTable"/> at
    /// <see cref="WeaponTableSource.Captured"/> the generated blob is handed to
    /// <see cref="CapturedWeaponTable.Apply"/>, which crosses the friend's captured numbers into
    /// list 2 and fills lists 3 and 5. List 0 also receives verified August weapon audio hashes;
    /// its IDs and arrays, and lists 1 and 4, remain generated, so the
    /// August ids the capture has never heard of are "filled from the generated table" by
    /// construction (docs/123 §3).
    /// </para>
    /// </summary>
    public bool SupportsCrateOpeningWeapon => _crateOpeningWeapon && CrateOpeningWeapon.IsSupported(Options);

    public WeaponDefinitionsBlob Blob
    {
        get
        {
            var blob = _crateOpeningWeapon ? CrateOpeningWeapon.Apply(OrdinaryBlob, Options) : OrdinaryBlob;
            if (Options.ShotgunPellets > 0) blob = AugustShotgunPattern.Apply(blob);
            if (Options.FastLongGunDraw && Options.WeaponTable == WeaponTableSource.Captured)
                blob = ResponsiveWeaponHandling.Apply(blob);
            if (Options.Z1LiveGunplay && Options.WeaponTable == WeaponTableSource.Captured)
                blob = Z1LiveGunplay.Apply(blob, Options.FastLongGunDraw);
            return blob;
        }
    }

    private WeaponDefinitionsBlob OrdinaryBlob => Options.WeaponTable == WeaponTableSource.Captured
            ? CapturedWeaponTable.Apply(GeneratedBlob)
            : GeneratedBlob;

    /// <summary>The table Cranberry generates from August's own sheets, before any crossing.</summary>
    public WeaponDefinitionsBlob GeneratedBlob =>
        AugustWeaponTable.CreateBlob(
            Options.PopulateWeaponDefinitions,
            Options.PopulateFireGroups,
            Options.MarkFireGroupsAutomatic,
            Options.WeaponMovementModifier,
            Options.WriteWeaponDefinitionBodyId,
            Options.PopulateFireModes,
            Options.PopulateAmmoSlots,
            Options.PopulateFireModeProjectiles,
            Options.WriteAdsZoom ? Options.WeaponAdsZoom : 1.0f,
            Options.WriteAdsFirstPerson,
            Options.WriteAdsFirstPerson ? Options.WeaponAdsFpCameraFov : 0.0f,
            Options.WriteIronSightsTimes ? AugustFireModeFacts.IronSightsTimeMs : 0,
            Options.IronSightsArmedOnly,
            Options.WriteMeleeAbilityIds,
            Options.WriteFireEffect,
            Options.BinocularsOptic,
            Options.BinocularsOptic ? Options.BinocularsOpticFovDegrees : 0.0f,
            Options.WriteFireModeTypes,
            Options.BinocularsTriggerAbility,
            Options.RetailAutomatic,
            Options.ShotgunPellets,
            Options.ShotgunSpreadDegrees,
            Options.Throwables,
            Options.ThrowableWindupMs);

    /// <summary>
    /// The <c>ReferenceData "ProjectileDefinitions"</c> packet, or false when
    /// <see cref="WeaponStageOptions.SendProjectileDefinitions"/> is off (the default) or the table
    /// has already gone out for this session.
    /// <para>
    /// docs/99 section 4: it is sent immediately BEFORE <c>WeaponDefinitions</c>, because that is
    /// the order the reference burst uses and because a fire mode that names a projectile id can
    /// only resolve it against a table the client already holds.
    /// </para>
    /// </summary>
    public bool TryCreateProjectileDefinitions(out ReferenceData packet)
    {
        if (!Options.SendProjectileDefinitions || ProjectileDefinitionsSent)
        {
            packet = null!;
            return false;
        }

        // docs/120: the five grenade records ride the same table as the bullets, behind the same
        // switch that gives the grenades their fire modes; off is the wave-17 table exactly.
        // D314 (docs/123 §6): "z1" swaps the 14 constructor-default bullet records for Z1's own
        // 131 rows through the same writer and the same envelope.
        packet = new ReferenceData(
            ProjectileDefinitionsBlob.TypeName,
            (Options.ProjectileTable == ProjectileTableSource.Z1
                ? Z1ProjectileTable.CreateBlob(true, Options.Throwables, Options.ThrowableSpeed)
                : AugustProjectileTable.CreateBlob(true, Options.Throwables, Options.ThrowableSpeed))
                .ToArray());
        return true;
    }

    /// <summary>The projectile table has gone out for this session.</summary>
    public bool ProjectileDefinitionsSent { get; private set; }

    /// <summary>Records that the projectile table went out; call only after the bytes are on the wire.</summary>
    public void MarkProjectileDefinitionsSent() => ProjectileDefinitionsSent = true;

    /// <summary>
    /// The <c>ReferenceData "WeaponDefinitions"</c> packet, or false when stage 1 is off.
    /// <b>Call <see cref="MarkWeaponDefinitionsSent"/> only after the bytes are on the wire.</b>
    /// </summary>
    public bool TryCreateWeaponDefinitions(out ReferenceData packet)
    {
        if (!Options.SendWeaponDefinitions || WeaponDefinitionsSent)
        {
            packet = null!;
            return false;
        }

        var blob = Blob;
        // The complete table is already being built for login. Index its draw clocks here,
        // so 150 players drawing together do not rebuild that table during the movement burst.
        _equipmentTimings ??= ReadEquipmentTimings(blob);
        packet = new ReferenceData(WeaponDefinitionsBlob.TypeName, blob.ToArray());
        return true;
    }

    /// <summary>
    /// The table has already gone out for this session, so <see cref="TryCreateWeaponDefinitions"/>
    /// will refuse to build a second one.
    /// <para>
    /// docs/89 §4 A4 (wave 9): this became load-bearing when the send moved off the login burst,
    /// which ran once by construction, and onto <c>EnsureInventory</c>, which does not. It is
    /// separate from <c>Ledger.WeaponDefinitionsSent</c>, which records what the blob
    /// <em>declared</em> and is deliberately false when list 1 was empty.
    /// </para>
    /// </summary>
    public bool WeaponDefinitionsSent { get; private set; }

    /// <summary>
    /// Records that the table went out. Until this is called nothing can be cleared for the active
    /// hand, however many weapon tails have been written.
    /// <para>
    /// It declares <b>the ids the blob actually carried</b>, not the static table: with
    /// <see cref="WeaponStageOptions.PopulateFireGroups"/> off the packet is the 32-byte empty
    /// envelope, and declaring the 60 ids there would tell the ledger a list the client never
    /// received is resolvable - which is exactly the null-deref guard 5 exists to prevent.
    /// </para>
    /// </summary>
    public void MarkWeaponDefinitionsSent()
    {
        WeaponDefinitionsSent = true;
        _ledger.DeclareFireGroupsAvailable((Blob.FireGroups ?? []).Select(g => g.FireGroupId));
    }

    /// <summary>
    /// The Weapon-class <c>ItemAdd</c> tail for an item definition, or null when the item should
    /// keep Cranberry's live-proven 1-byte Generic tail. Null is returned when stage 2 is off, when
    /// the item is not a <c>CODE_FACTORY_NAME = Weapon</c> row, or when the client's own datasheet
    /// names no fire group for it (<c>FIRE_GROUP_ID = 0</c>, or no row at all).
    /// <para>
    /// This is also where <see cref="WeaponStageOptions.TailIdleState"/> is applied, so the
    /// static table keeps knowing only about the client's own datasheet and every wire-bound tail
    /// passes through one switch (S6 §7.2).
    /// </para>
    /// </summary>
    public WeaponItemAddTail? CreateTail(uint itemDefinitionId) => CreateTail(itemDefinitionId, 0);

    /// <summary>
    /// The same tail for a known instance, carrying <paramref name="magazine"/> rounds in the
    /// ammo-slot array (docs/107 §1). <see cref="WeaponStageOptions.WriteWeaponTailMagazine"/> is
    /// applied here, so every wire-bound tail passes through one switch exactly as
    /// <see cref="WeaponStageOptions.TailIdleState"/> does.
    /// </summary>
    public WeaponItemAddTail? CreateTail(uint itemDefinitionId, int magazine)
    {
        if (!Options.WriteWeaponItemAddTail
            || (itemDefinitionId == CrateOpeningWeapon.ItemId && !SupportsCrateOpeningWeapon))
        {
            return null;
        }

        WeaponItemAddTail? tail =
            AugustWeaponTable.CreateTail(itemDefinitionId, Options.PlainWieldNoMagazine, Options.Throwables);

        if (tail is null)
        {
            return null;
        }

        // AugustWeaponTable.CreateTail has already decided whether this item HAS an ammo slot (Z1's
        // own gate at ZoneInventory.cs:3140, applied where the item's calibre and clip are known).
        // All this switch does is take the slot away again - the revert, never a grant.
        bool hasAmmoSlot = tail.AmmoSlot && Options.WriteWeaponTailMagazine;

        return tail with
        {
            IdleState = Options.TailIdleState,
            AmmoSlot = hasAmmoSlot,
            Magazine = hasAmmoSlot ? Math.Max(magazine, 0) : 0,
        };
    }

    /// <summary>
    /// The <c>ItemAdd</c> to send for a granted item: a <see cref="WeaponItemAdd"/> with the real
    /// Weapon tail when stage 2 applies to it, otherwise the ordinary <see cref="ItemAdd"/> with the
    /// 1-byte Generic tail - byte for byte what wave 5 sent.
    /// <para>
    /// The returned object also updates the ledger, so the caller has exactly one thing to remember:
    /// write what this returns.
    /// </para>
    /// </summary>
    public Action<PacketWriter> CreateItemAdd(ulong targetCharacterGuid, InventoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DefinitionId == CrateOpeningWeapon.ItemId
            && (!SupportsCrateOpeningWeapon || !WeaponDefinitionsSent))
            throw new InvalidOperationException("The crate gallery requires its complete weapon definitions to be sent before the weapon item.");

        // docs/107 §1: the tail's ammo-slot array is the client's ONLY input for "rounds in this
        // gun", so it is filled from whatever is counting them. A re-announce (11 04 + 11 02, which
        // is how this server changes a containerSlotId) therefore carries the CURRENT magazine
        // rather than resetting the hotbar to the pickup value.
        int magazine = MagazineSource?.Invoke(item.ItemGuid, item.DefinitionId) ?? 0;
        WeaponItemAddTail? tail = CreateTail(item.DefinitionId, magazine);
        if (tail is null)
        {
            var generic = new ItemAdd(targetCharacterGuid, item);
            return generic.WriteTo;
        }

        _ledger.RecordFireGroupsDelivered(item.ItemGuid, tail);
        var weapon = new WeaponItemAdd(targetCharacterGuid, item, tail);
        return weapon.WriteTo;
    }

    /// <summary>A one-line description of what this session will do, for the boot / session log.</summary>
    public string Describe()
    {
        WeaponDefinitionsBlob blob = Blob;
        int projectiles = Options.SendProjectileDefinitions
            ? AugustProjectileTable.Records.Count
            : 0;
        return $"{Options.Describe()}; table = {blob.Length} blob bytes "
            + $"({(blob.WeaponDefinitions ?? []).Count} weapon definitions, "
            + $"{(blob.FireGroups ?? []).Count} fire groups, "
            + $"{(blob.FireModes ?? []).Count} fire modes, "
            + $"lists 3-7 empty); projectile table = {projectiles} records";
    }
}

/// <summary>
/// The bridge from the August client's own datasheets to the wire records, and the one place in this
/// lane where a <b>designed</b> value is allowed to appear. Every such value is called out.
/// </summary>
public static class AugustWeaponTable
{
    /// <summary>
    /// <b>DESIGN.</b> The fire groups Cranberry marks automatic (<c>rec+0x38</c> bit 6, which
    /// selects <c>FUN_1411ceca0</c>'s automatic branch). docs/58 §8a works exactly one value - fire
    /// group <b>6</b>, the AR-15's - and nothing in the client's data separates automatic from
    /// semi-automatic weapons, so every other group ships with the flags byte clear (the
    /// single-shot / melee branch). Getting this wrong makes a gun fire in the wrong mode; it is not
    /// a crash. docs/58 U4 is the work that would replace this list with an extracted one.
    /// </summary>
    public static IReadOnlyCollection<uint> AutomaticFireGroupIds { get; } = new HashSet<uint> { 6 };

    /// <summary>
    /// <b>The RETAIL automatic set (D331, docs/121 §3)</b> - the fire groups of every weapon the
    /// client's own text calls automatic. The AK-47's description (locale 11959, item 2229 and its
    /// skins) reads <i>"a reliable and accurate automatic weapon made for those with itchy trigger
    /// fingers"</i>; the AR-15's (609) says <i>"pinpoint accurate assault rifle"</i> and the three
    /// pistols (608 / 11020 / 11007) each say <i>"semi-automatic pistol"</i>. So the AK-47 family -
    /// every <c>WEAPON_ID</c> <c>AmmoTypes.ByWeaponDefinitionId</c> pairs with the 7.62x39 round,
    /// item 2325 - is automatic and nothing else is. Groups 51 (AK-47), 77 (Modified AK-47) and 82
    /// (the sheet's third AK row) in this build.
    /// <para>
    /// <see cref="AutomaticFireGroupIds"/> (group 6, the AR-15) was the wave-9 diagnostic
    /// (docs/95, D186), and it had the two guns the wrong way round: the AR-15 held the automatic
    /// branch and the AK-47 the single-shot one. <see cref="WeaponStageOptions.RetailAutomatic"/>
    /// selects this set; off restores the diagnostic byte for byte.
    /// </para>
    /// </summary>
    public static IReadOnlySet<uint> RetailAutomaticFireGroupIds { get; } =
        FireGroupsFedBy(AmmoTypes.Ak47RoundItemDefinitionId);

    /// <summary>
    /// <b>The shotgun fire groups</b> - every group whose weapon eats the 12 Gauge Buckshot Shell
    /// (item 1511): 16 (both 12GA rows) in this build. The pellet count and the pellet spread
    /// (<see cref="WeaponStageOptions.ShotgunPellets"/>, <see cref="WeaponStageOptions.ShotgunSpreadDegrees"/>)
    /// go on exactly these groups' modes.
    /// </summary>
    public static IReadOnlySet<uint> ShotgunFireGroupIds { get; } =
        FireGroupsFedBy(AmmoTypes.ShotgunShellItemDefinitionId);

    private static HashSet<uint> FireGroupsFedBy(uint ammoItemDefinitionId)
    {
        var groups = new HashSet<uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId != 0
                && AmmoTypes.ByWeaponDefinitionId.TryGetValue(weapon.WeaponId, out uint ammoItemId)
                && ammoItemId == ammoItemDefinitionId)
            {
                groups.Add(weapon.FireGroupId);
            }
        }

        return groups;
    }

    /// <summary>
    /// <b>DESIGN.</b> Two fire modes per group, because <c>FUN_1411ceca0</c>'s melee / single-shot
    /// branch calls <c>FUN_142291b90(weapon, item+0x10c, <b>1</b>)</c> with the mode index hard-coded
    /// to 1, and its automatic branch asks for modes 0 <em>and</em> 1 - so a one-mode group can never
    /// attack (docs/56 §1.7, docs/58 §8a).
    /// </summary>
    public const int FireModesPerGroup = 2;

    /// <summary>
    /// <b>DESIGN.</b> The smallest value that passes <c>FUN_142291b90</c>'s <c>0 &lt; mode-&gt;+0x18</c>
    /// test. Used for mode 1 always, and for mode 0 when the client's <c>CLIP_SIZE</c> is 0 (melee
    /// rows and the fists). Nothing in the August client says 1.
    /// </summary>
    public const int TriggerChargeSentinel = 1;

    /// <summary>
    /// The current fire-group index every tail carries. Only 0 is safe, because it is the index the
    /// crash site dereferences and Cranberry sends exactly one group.
    /// </summary>
    public const sbyte CurrentFireGroupIndex = 0;

    /// <summary>
    /// <b>RULING D235.</b> The aimed first-person field of view for a viewing optic (the binoculars),
    /// in degrees - a strong zoom (the client's own default vertical FOV is 65). The
    /// <see cref="WeaponStageOptions.BinocularsOpticFovDegrees"/> lever overrides it live; this is the
    /// value the low-level <see cref="CreateBlob"/> / <see cref="FireModeRecordsFor"/> default to so a
    /// test blob matches the shipped one.
    /// </summary>
    public const float DefaultOpticFovDegrees = 20.0f;

    /// <summary>
    /// The list-1 fire-group ids Cranberry ships: <b>every distinct non-zero <c>FIRE_GROUP_ID</c> in
    /// the client's own <c>ClientItemDatasheetData.txt</c></b> (<see cref="AugustWeaponFacts"/>).
    /// Shipping all 60 rather than one per play-test costs about 3 KB and means no weapon the loot
    /// roster can produce is left unresolvable.
    /// </summary>
    public static IReadOnlyList<uint> FireGroupIds => AugustWeaponFacts.FireGroupIds;

    /// <summary>The list-1 records, one per <see cref="FireGroupIds"/> entry.</summary>
    public static IReadOnlyList<FireGroupRecord> FireGroupRecords { get; } = BuildFireGroupRecords();

    /// <summary>
    /// The list-0 records: one weapon definition per distinct <c>WEAPON_ID</c>, carrying that
    /// weapon's own fire-group id. Built but <b>not sent by default</b> - see
    /// <see cref="WeaponStageOptions.PopulateWeaponDefinitions"/> for why.
    /// </summary>
    public static IReadOnlyList<WeaponDefinitionRecord> WeaponDefinitionRecords { get; } =
        BuildWeaponDefinitionRecords();

    /// <summary>
    /// The blob for a session, with or without list 0 and list 1. With both off it is the 32-byte
    /// empty envelope - eight <c>i32 0</c>s, a well-formed table with no record layout on the wire.
    /// </summary>
    /// <param name="markAutomatic">
    /// <b>The wave-10 follow-up discriminator (docs/95, D186).</b> When false, NO fire group carries
    /// <see cref="FireGroupRecord.AutomaticFlag"/> — every weapon ships as single-shot.
    /// <para>
    /// This exists because the flag is the one bit the client's per-frame active-hand block
    /// branches on: <c>FUN_1411ceca0</c> reads <c>FUN_14228d840(item+0xa8)-&gt;+0x38 &amp; 0x40</c>
    /// and takes a completely different path either way. Group 6 is the AR-15's, it is the only
    /// group Cranberry marks automatic, and the AR-15 is the gun that froze the owner's input on
    /// 2026-08-31. Turning the flag off does not fix anything by itself — it SPLITS the search: if
    /// the freeze survives, the automatic branch is innocent and the cause is the binding; if it
    /// stops, the cause is in that branch and <c>FUN_1411ceca0</c> names the four calls to look at.
    /// </para>
    /// </param>
    /// <param name="populateAmmoSlots">
    /// Fill each list-0 record's <c>def+0xd0</c> ammo-slot array
    /// (<see cref="WeaponStageOptions.PopulateAmmoSlots"/>). False restores the wave-9..13 empty
    /// array and every list-0 record's old length.
    /// </param>
    /// <param name="populateFireModeProjectiles">
    /// Fill list 4, the fire-mode to projectile mapping
    /// (<see cref="WeaponStageOptions.PopulateFireModeProjectiles"/>).
    /// </param>
    /// <param name="adsZoom">
    /// The <c>DEFAULT_ZOOM</c> multiplier (<c>rec+0x128</c>) the iron-sights mode of an ARMED fire
    /// group carries - <see cref="WeaponStageOptions.WeaponAdsZoom"/>. <c>1.0f</c>, the client's
    /// own constructor preset, is "no zoom" and reproduces every wave before the docs/107
    /// wave-14 addendum byte for byte.
    /// </param>
    /// <param name="adsFirstPerson">
    /// <c>FORCE_FP_SCOPE</c> (<c>rec+0x2dc</c>) on the PRIMARY mode of every armed fire group -
    /// <see cref="WeaponStageOptions.WriteAdsFirstPerson"/>. False is byte-identical to every
    /// wave before this one.
    /// </param>
    /// <param name="adsFpCameraFovDegrees">
    /// The absolute aimed first-person field of view in degrees for <c>rec+0x2d0</c> /
    /// <c>+0x2d4</c> / <c>+0x2d8</c> behind the <c>rec+0x2cd</c> gate. <c>0</c> clears the gate
    /// and leaves the player's own <c>[Rendering] VerticalFOV</c> standing.
    /// </param>
    /// <param name="ironSightsTimeMs">
    /// <c>Weapon.ToIronSightsTime</c> / <c>Weapon.FromIronSightsTime</c> (<c>def+0x38</c> /
    /// <c>def+0x3c</c>), the aim-in and aim-out ramp <c>FUN_1422935d0:70-76</c> takes on a
    /// non-silent fire-mode switch. <c>0</c> is the client's own "instant".
    /// </param>
    /// <param name="retailAutomatic">
    /// <b>D331 (docs/121 §3).</b> Mark <see cref="RetailAutomaticFireGroupIds"/> (the AK-47 family)
    /// automatic instead of <see cref="AutomaticFireGroupIds"/> (the AR-15), and give those groups'
    /// modes the <c>AUTOMATIC</c> bit and an <c>AUTO_FIRE_TIME_MS</c> equal to their
    /// <c>REFIRE_TIME_MS</c>. Ignored when <paramref name="markAutomatic"/> is false. False is the
    /// wave-9 diagnostic byte for byte.
    /// </param>
    /// <param name="shotgunPellets">
    /// <b>D332.</b> <c>PELLETS_PER_SHOT</c> on the modes of <see cref="ShotgunFireGroupIds"/>;
    /// 0 keeps the client's own default, which the spawn reads as ONE pellet.
    /// </param>
    /// <param name="shotgunSpreadDegrees">
    /// <b>D332.</b> <c>PELLET_SPREAD</c> in degrees on the same modes; 0 is the client's preset.
    /// </param>
    public static WeaponDefinitionsBlob CreateBlob(
        bool populateWeaponDefinitions,
        bool populateFireGroups = true,
        bool markAutomatic = true,
        float movementModifier = 1.0f,
        bool writeBodyId = true,
        bool populateFireModes = true,
        bool populateAmmoSlots = true,
        bool populateFireModeProjectiles = true,
        float adsZoom = 1.0f,
        bool adsFirstPerson = false,
        float adsFpCameraFovDegrees = 0.0f,
        int ironSightsTimeMs = 0,
        bool ironSightsArmedOnly = false,
        bool meleeAbilityIds = false,
        bool writeFireEffect = true,
        bool writeOpticScope = true,
        float opticFovDegrees = DefaultOpticFovDegrees,
        bool writeFireModeTypes = false,
        bool binocularsTriggerAbility = false,
        bool retailAutomatic = false,
        int shotgunPellets = 0,
        float shotgunSpreadDegrees = 0.0f,
        bool throwables = false,
        int throwableWindupMs = 0) =>
        new(
            WeaponDefinitions: !populateWeaponDefinitions
                ? []
                : movementModifier == 1.0f && writeBodyId && populateAmmoSlots
                  && ironSightsTimeMs == 0
                    ? WeaponDefinitionRecordsFor(throwables)
                    // The 2026-09-02 bisect levers (OVERHAUL-PLAN §4.3): every record gets the same
                    // Weapon.MovementModifier and the same body-id choice; nothing else changes.
                    // Wave 15 adds the aim-in / aim-out ramp, which is two u32s already on the wire
                    // (def+0x38 / def+0x3c) and moves no length.
                    : [.. WeaponDefinitionRecordsFor(throwables).Select(record => record with
                    {
                        MovementModifier = movementModifier,
                        WriteBodyId = writeBodyId,
                        AmmoSlots = populateAmmoSlots ? record.AmmoSlots : null,
                        ToIronSightsTimeMs = ironSightsTimeMs,
                        FromIronSightsTimeMs = ironSightsTimeMs,
                    })],
            // docs/120 D300: the frag's ruled group rides list 1 beside the datasheet's groups.
            FireGroups: populateFireGroups
                ? throwables
                    ? [.. FireGroupsFor(markAutomatic, populateFireModes, retailAutomatic), FragFireGroupRecord(populateFireModes)]
                    : FireGroupsFor(markAutomatic, populateFireModes, retailAutomatic)
                : [],
            // List 2. The mode ids in list 1 and the records here are one decision: a group that
            // names ids the client cannot resolve is worse than a group that names none, because
            // FUN_142290f60 then blank-constructs a mode the server believes it authored.
            FireModes: populateFireModes && populateFireGroups
                ? FireModeRecordsFor(
                    adsZoom,
                    adsFirstPerson,
                    adsFpCameraFovDegrees,
                    ironSightsArmedOnly,
                    meleeAbilityIds,
                    writeFireEffect,
                    writeOpticScope,
                    opticFovDegrees,
                    writeFireModeTypes,
                    binocularsTriggerAbility,
                    markAutomatic && retailAutomatic,
                    shotgunPellets,
                    shotgunSpreadDegrees,
                    throwables,
                    throwableWindupMs)
                : [],
            // List 4. A mapping row is only reachable through a list-2 record and a list-0
            // ammo slot, so it ships only when both of those do. docs/120 D303: a throwable's rows
            // carry the ammo key 0 - the value FUN_14228de50 returns for a weapon with no slot -
            // so they need no ammo slot to be reached; they ride the same switches for symmetry.
            FireModeProjectiles:
                populateFireModeProjectiles && populateFireModes && populateFireGroups
                && populateWeaponDefinitions && populateAmmoSlots
                    ? throwables
                        ? [.. FireModeProjectileRecords, .. ThrowableFireModeProjectileRecords]
                        : FireModeProjectileRecords
                    : []);

    /// <summary>
    /// <b>CRANBERRY RULING (D159).</b> The fire-mode id for one slot of one fire group:
    /// <c>fireGroupId * 2 + index</c>.
    /// <para>
    /// The August client ships <b>no</b> fire-mode table and nothing in its datasheets names a
    /// fire-mode id - <c>ClientItemDatasheetData</c> stops at <c>FIRE_GROUP_ID</c>. The ids are
    /// therefore the server's to choose, exactly as the fire-group-to-mode arity already is
    /// (<see cref="FireModesPerGroup"/>). Doubling the group id is collision-free by construction
    /// and keeps the id readable in a hex dump: group 6 (the AR-15) owns modes 12 and 13.
    /// </para>
    /// </summary>
    public static uint FireModeIdFor(uint fireGroupId, int index) =>
        (fireGroupId * 2) + (uint)index;

    /// <summary>
    /// True when the item is a <c>CODE_FACTORY_NAME = Weapon</c> row whose datasheet names a
    /// non-zero fire group - the only items a real weapon tail may be written for.
    /// </summary>
    public static bool HasFireGroup(uint itemDefinitionId, out uint fireGroupId) =>
        HasFireGroup(itemDefinitionId, out fireGroupId, throwables: true);

    /// <summary>
    /// <see cref="HasFireGroup(uint, out uint)"/> with the docs/120 overlay switchable: when
    /// <paramref name="throwables"/> is on, a throwable whose datasheet row names no group (the
    /// M67 Frag, item 65, <c>FIRE_GROUP_ID 0</c>) resolves to <see cref="AugustThrowables"/>'
    /// ruled group (D300) - the group the blob then also carries, so the tail and the table agree.
    /// </summary>
    public static bool HasFireGroup(uint itemDefinitionId, out uint fireGroupId, bool throwables)
    {
        fireGroupId = 0;
        if (!InventoryItemFacts.TryGet(itemDefinitionId, out InventoryItemFact item)
            || item.CodeFactory != ItemCodeFactory.Weapon)
        {
            return false;
        }

        if (!WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact weapon))
        {
            return false;
        }

        if (weapon.FireGroupId == 0)
        {
            uint ruled = throwables ? AugustThrowables.FireGroupFor(itemDefinitionId) : 0u;
            if (ruled == 0)
            {
                return false;
            }

            fireGroupId = ruled;
            return true;
        }

        fireGroupId = weapon.FireGroupId;
        return true;
    }

    /// <summary>
    /// The tail for one item definition, or null when the item must keep the 1-byte Generic tail.
    /// <para>
    /// <b>Why null rather than a zero-group tail for a weapon with no fire group.</b> A tail with
    /// <c>fireGroupCount = 0</c> parses cleanly, but it changes bytes on a path that has worked in
    /// every live session for no gain, and it would leave the item in the same unwieldable state
    /// anyway. The 1-byte tail is what the owner has picked guns up with dozens of times.
    /// </para>
    /// </summary>
    public static WeaponItemAddTail? CreateTail(uint itemDefinitionId) =>
        CreateTail(itemDefinitionId, plainWieldNoMagazine: true);

    /// <summary>
    /// The tail for one item definition. <paramref name="plainWieldNoMagazine"/> (report 3): when
    /// true, a fire group whose weapon has no magazine (<c>CLIP_SIZE == 0</c> - the fists, the
    /// binoculars and every melee row) ships mode 0's trigger charge as <b>0</b> instead of the
    /// <see cref="TriggerChargeSentinel"/>, so the client stops rendering a 1-round magazine /
    /// reloadable indicator for them. See <see cref="CreateGroup"/>.
    /// </summary>
    public static WeaponItemAddTail? CreateTail(uint itemDefinitionId, bool plainWieldNoMagazine) =>
        CreateTail(itemDefinitionId, plainWieldNoMagazine, throwables: true);

    /// <summary>
    /// <see cref="CreateTail(uint, bool)"/> with the docs/120 frag overlay switchable
    /// (<see cref="HasFireGroup(uint, out uint, bool)"/>).
    /// </summary>
    public static WeaponItemAddTail? CreateTail(uint itemDefinitionId, bool plainWieldNoMagazine, bool throwables)
    {
        if (!HasFireGroup(itemDefinitionId, out uint fireGroupId, throwables))
        {
            return null;
        }

        WeaponItemProfiles.TryGet(itemDefinitionId, out AugustWeaponFact weapon);

        // docs/107 §1: the ammo-slot array is declared only for a weapon that HAS one - a calibre
        // and a magazine. This is Z1's gate (`hasAmmoSlot = profile.AmmoItemId != 0 &&
        // profile.ClipSize > 0`, ZoneInventory.cs:3140) at the one place that knows both, and it
        // matches the client's own self-init, which sizes the same vector from the weapon
        // definition's def+0xd0 array (FUN_142291180). The fists and every melee row get the empty
        // array they have always had, so their tail length does not move.
        bool hasAmmoSlot =
            AmmoTypes.AmmoItemFor(itemDefinitionId) != 0 && weapon.ClipSize > 0;

        return new WeaponItemAddTail(
            // Native mode lookup (14228d970 / 142291b90) checks the resolved definition's
            // ID at +0x18, not this runtime charge. An optic therefore needs no fake round.
            // HUD visibility is a separate string-coercion bug in CurrentLoadoutRow; see
            // tools/client/build-binocular-hud.py.
            [CreateGroup(fireGroupId, weapon.ClipSize, plainWieldNoMagazine)],
            CurrentFireGroupIndex,
            AmmoSlot: hasAmmoSlot);
    }

    /// <summary>
    /// One fire group for the <c>ItemAdd</c> tail. Mode 0's charge is the client's own
    /// <c>CLIP_SIZE</c> (EXTRACTED); mode 1's is always the <see cref="TriggerChargeSentinel"/>,
    /// because it is the mode the trigger/wield gate <c>FUN_142291b90</c> actually tests (index 1,
    /// hard-coded) and no extracted value exists for it. <c>flags</c> and <c>effectId</c> are left
    /// 0 rather than guessed - docs/56 open question 2 has not established what they mean.
    /// <para>
    /// <b>Report 3 - mode 0's charge for a magazine-less weapon.</b> The client renders mode 0's
    /// charge (<c>mode+0x18</c>) as the hotbar's magazine CAPACITY (docs/107 §1, wire mapping
    /// <c>FireGroupPackets.cs</c>), so the <see cref="TriggerChargeSentinel"/> Cranberry used to
    /// force in for a <c>CLIP_SIZE == 0</c> item (the fists, the binoculars, the melee rows) made
    /// each of them show a 1-round magazine / reloadable indicator - the owner's "fists AND
    /// binoculars are shown as a reloadable object". With <paramref name="plainWieldNoMagazine"/>
    /// (the default), mode 0's charge is <b>0</b> for those items, exactly as the owner's own Z1
    /// writes <c>a=b=0</c> for every fire-mode entry (<c>ZoneInventory.cs:3166-3172</c>). The wield
    /// and attack gate is unaffected because it reads mode <b>1</b>, whose sentinel is untouched.
    /// </para>
    /// </summary>
    public static FireGroupDefinition CreateGroup(
        uint fireGroupId, int clipSize, bool plainWieldNoMagazine = true) =>
        new(
            fireGroupId,
            [
                new FireModeDefinition(
                    Flags: 0,
                    EffectId: 0,
                    Charge: clipSize > 0
                        ? clipSize
                        : plainWieldNoMagazine ? 0 : TriggerChargeSentinel),
                new FireModeDefinition(Flags: 0, EffectId: 0, Charge: TriggerChargeSentinel),
            ]);

    /// <summary>
    /// The same list with every automatic flag cleared — the control arm of the wave-10 follow-up
    /// experiment (docs/95, D186).
    /// </summary>
    public static IReadOnlyList<FireGroupRecord> SingleShotFireGroupRecords { get; } =
        [.. AugustWeaponFacts.FireGroupIds.Select(id => new FireGroupRecord(id, 0))];

    /// <summary>
    /// The same two lists again, each fire group now naming its <see cref="FireModesPerGroup"/>
    /// mode ids (<see cref="FireModeIdFor"/>) in the <c>rec+0x28</c>/<c>+0x30</c> array.
    /// </summary>
    public static IReadOnlyList<FireGroupRecord> FireGroupRecordsWithModes { get; } =
        WithModeIds(FireGroupRecords);

    /// <summary>The single-shot control arm, with mode ids.</summary>
    public static IReadOnlyList<FireGroupRecord> SingleShotFireGroupRecordsWithModes { get; } =
        WithModeIds(SingleShotFireGroupRecords);

    /// <summary>
    /// The list-2 records: <see cref="FireModesPerGroup"/> per shipped fire group, so every id
    /// <see cref="FireGroupRecordsWithModes"/> names resolves.
    /// <para>
    /// <b>Values.</b> Every field is the client's own record-constructor default
    /// (<c>FUN_1422267e0</c>) except three. <c>rec+0x18</c> carries the trigger charge, which
    /// <c>FUN_142291b90</c> requires to be <c>&gt; 0</c> and which mirrors what the
    /// <c>ItemAdd</c> tail already delivers (<see cref="CreateGroup"/>): mode 0 gets the client's
    /// own <c>CLIP_SIZE</c>, mode 1 the <see cref="TriggerChargeSentinel"/>. <c>rec+0x40</c>
    /// (<c>FireMode.RefireTime</c> [P]) and <c>rec+0x60</c> (<c>FireMode.ReloadTime</c> [I]) carry
    /// the client's own <c>REFIRE_TIME_MS</c> / <c>RELOAD_TIME_MS</c> for the group's first weapon.
    /// </para>
    /// <para>
    /// <b>Why the group's FIRST weapon.</b> A fire group is shared by every item that names it, and
    /// the client's sheet carries the timing per <em>item</em>, not per group; there is no
    /// per-group row anywhere in the August data. The lowest item id that names the group is a
    /// deterministic, documented choice, and <c>AugustWeaponFactsTests</c> already pins that the
    /// sheet is stable. Where the group's items disagree the mode carries the first one's timing -
    /// recorded as a known imprecision in docs/99 section 2.4, not hidden.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FireModeRecord> FireModeRecords { get; } = BuildFireModeRecords();

    private static IReadOnlyList<FireGroupRecord> WithModeIds(IReadOnlyList<FireGroupRecord> groups) =>
    [
        .. groups.Select(group => group with
        {
            FireModeIds = Enumerable.Range(0, FireModesPerGroup)
                .Select(index => FireModeIdFor(group.FireGroupId, index))
                .ToArray(),
        }),
    ];

    private static IReadOnlyList<FireGroupRecord> FireGroupsFor(
        bool markAutomatic, bool withModeIds, bool retailAutomatic = false) =>
        (markAutomatic, withModeIds, retailAutomatic) switch
        {
            (true, true, true) => RetailFireGroupRecordsWithModes,
            (true, false, true) => RetailFireGroupRecords,
            (true, true, false) => FireGroupRecordsWithModes,
            (true, false, false) => FireGroupRecords,
            (false, true, _) => SingleShotFireGroupRecordsWithModes,
            (false, false, _) => SingleShotFireGroupRecords,
        };

    /// <summary>
    /// The list-1 records with <see cref="RetailAutomaticFireGroupIds"/> automatic and every other
    /// group - the AR-15's included - single-shot (D331).
    /// </summary>
    public static IReadOnlyList<FireGroupRecord> RetailFireGroupRecords { get; } =
        [.. AugustWeaponFacts.FireGroupIds.Select(id => new FireGroupRecord(
            id, RetailAutomaticFireGroupIds.Contains(id) ? FireGroupRecord.AutomaticFlag : (byte)0))];

    /// <summary>The retail-automatic list, with mode ids.</summary>
    public static IReadOnlyList<FireGroupRecord> RetailFireGroupRecordsWithModes { get; } =
        WithModeIds(RetailFireGroupRecords);

    private static FireModeRecord[] BuildFireModeRecords() =>
    [
        .. AugustFireModeFacts.All.Select(fact => new FireModeRecord(
            fact.FireModeId,
            DefinitionId: fact.DefinitionId,
            RefireTimeMs: fact.RefireTimeMs,
            ReloadTimeMs: fact.ReloadTimeMs,
            AmmoSlot: fact.AmmoSlot,
            IronSights: fact.IronSights,
            EffectGroup: fact.EffectGroup)),
    ];

    /// <summary>
    /// <b>The fire groups an ARMED weapon names</b> - every <c>FIRE_GROUP_ID</c> whose
    /// <c>WEAPON_ID</c> <c>AmmoTypes.ByWeaponDefinitionId</c> pairs with a round. The same
    /// predicate decides the list-0 ammo slot (<see cref="AmmoSlotsFor"/>) and the list-4 mapping,
    /// and it is what keeps the ADS zoom off the fists, the melee rows and the throwables: D205
    /// sets <c>IRON_SIGHTS</c> on mode index 1 of every group because that is the index the
    /// client's right-click sends, but a hatchet's alternate swing is not aim-down-sights.
    /// </summary>
    public static IReadOnlySet<uint> ArmedFireGroupIds { get; } = BuildArmedFireGroupIds();

    private static HashSet<uint> BuildArmedFireGroupIds()
    {
        var groups = new HashSet<uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId != 0
                && AmmoTypes.ByWeaponDefinitionId.TryGetValue(weapon.WeaponId, out uint ammoItemId)
                && ammoItemId != 0)
            {
                groups.Add(weapon.FireGroupId);
            }
        }

        return groups;
    }

    /// <summary>
    /// The item definitions that are <b>viewing optics</b> - the binoculars (item 1542). Their fire
    /// group's primary mode carries <c>FORCE_FP_SCOPE</c> so use / right-click raises the
    /// first-person camera and zooms (report 3). A viewing optic is inherently first person, which
    /// is why it is treated separately from the weapon ADS switch (guns aim in third person).
    /// </summary>
    private static readonly uint[] OpticItemDefinitionIds = [1542, 1695];

    /// <summary>
    /// <b>The optic fire groups</b> - the fire groups <see cref="OpticItemDefinitionIds"/> resolve
    /// to (binoculars = fire group 21, the only optic in this build). Their primary mode gets the
    /// first-person scope treatment (<see cref="FireModeRecordsFor"/>).
    /// </summary>
    public static IReadOnlySet<uint> OpticFireGroupIds { get; } = BuildOpticFireGroupIds();

    private static HashSet<uint> BuildOpticFireGroupIds()
    {
        var groups = new HashSet<uint>();
        foreach (uint itemDefinitionId in OpticItemDefinitionIds)
        {
            if (AugustWeaponFacts.TryGet(itemDefinitionId, out AugustWeaponFact fact)
                && fact.FireGroupId != 0)
            {
                groups.Add(fact.FireGroupId);
            }
        }

        return groups;
    }

    /// <summary>
    /// <see cref="FireModeRecords"/> with <c>rec+0x128</c> (<c>DEFAULT_ZOOM</c> /
    /// <c>FireMode.DefaultZoom</c>, the field <c>FUN_141488490</c> reads and the aimed camera zooms
    /// by) set to <paramref name="adsZoom"/> on the <c>IRON_SIGHTS</c> mode of every
    /// <see cref="ArmedFireGroupIds"/> group. <c>1.0f</c> returns the shared records untouched, so
    /// the off switch is byte-identical rather than merely equivalent. <b>D212.</b>
    /// </summary>
    /// <summary>
    /// <c>ITEM_CLASS 25078</c> - the class the August client gives every throwable it ships
    /// (<c>ClientItemDefinitions</c> rows 14 Molotov, 65/66 M67 Frag, 2235 Stun, 2236 Smoke,
    /// 2237 Gas; all with <c>ACTIVATABLE_ABILITY_ID 1111507</c>). [P]
    /// </summary>
    public const uint ThrowableItemClass = 25078;

    /// <summary>
    /// The fire groups named by a throwable item (<see cref="ThrowableItemClass"/>). A group is
    /// classified by the items that name it, the same rule <see cref="ArmedFireGroupIds"/> uses.
    /// </summary>
    public static IReadOnlySet<uint> ThrowableFireGroupIds { get; } = BuildThrowableFireGroupIds();

    private static HashSet<uint> BuildThrowableFireGroupIds()
    {
        var groups = new HashSet<uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId != 0
                && Inventory.InventoryItemFacts.TryGet(weapon.ItemId, out Inventory.InventoryItemFact item)
                && item.ItemClass == ThrowableItemClass)
            {
                groups.Add(weapon.FireGroupId);
            }
        }

        // docs/120 D300: the frag's datasheet row names no group, so its ruled group is added by
        // name - it is a throwable's group by the same ITEM_CLASS 25078 rule, just not one the
        // sheet could produce.
        foreach (ThrowableFact fact in AugustThrowables.All)
        {
            groups.Add(fact.FireGroupId);
        }

        return groups;
    }

    /// <summary>
    /// <c>ACTIVATABLE_ABILITY_ID 1111157</c> - the 9-stage <c>StageMaintainOnGuid</c> row
    /// (<c>AbilityEx.txt</c>) the August client hands the things you HOLD UP rather than swing or
    /// shoot: both binoculars rows (1542, 1695), the military flashlights (1380, 1530) and the
    /// bows (113, 1716, 1720, 1986 - armed, so they never reach this classification). [P]
    /// </summary>
    public const uint MaintainAbilityId = 1_111_157;

    /// <summary>
    /// The <c>ITEM_CLASS</c> values the August client gives its hand-held strikers: 25006 (the
    /// fists, 85), 4098 (crowbar 82, machete 83/2494, knife 84, hatchets 3/49/1708, torches
    /// 5/1367/1389, claw hammer 1536, wrench 1538, branch 1725, pipes 1727/1903, flashlight 1741,
    /// road flare 4) and 25037 (wood axe 58, bats 1442/1721/1724, pipe-with-bolts 1448, guitar
    /// 1733, crafted hatchet 1707, spear 1382). [P] - the class column of
    /// <c>ClientItemDefinitions.txt</c>; the grouping into "melee" is the RULING that a striker's
    /// class is what makes it one.
    /// </summary>
    public static readonly IReadOnlySet<uint> MeleeItemClasses = new HashSet<uint> { 25006, 4098, 25037 };

    /// <summary>
    /// The fire groups named by an unarmed item whose <c>ACTIVATABLE_ABILITY_ID</c> is
    /// <see cref="MaintainAbilityId"/>: the binoculars (21 and 22) and the flashlights (17). A
    /// trigger pull on these RUNS the item's ability (<c>TYPE 8</c>) rather than swinging or
    /// firing. Bows carry the same ability but are armed, so they keep the projectile type.
    /// </summary>
    public static IReadOnlySet<uint> TriggerAbilityFireGroupIds { get; } = BuildTriggerAbilityFireGroupIds();

    private static HashSet<uint> BuildTriggerAbilityFireGroupIds()
    {
        var groups = new HashSet<uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId != 0
                && !ArmedFireGroupIds.Contains(weapon.FireGroupId)
                && !ThrowableFireGroupIds.Contains(weapon.FireGroupId)
                && AugustAbilityFacts.AbilityIdOf(weapon.ItemId) == MaintainAbilityId)
            {
                groups.Add(weapon.FireGroupId);
            }
        }

        return groups;
    }

    /// <summary>
    /// The fire groups of the things that SWING: every group an item of a
    /// <see cref="MeleeItemClasses"/> class names, that no armed weapon, no throwable and no
    /// maintain-ability item also names - the fists (12), the crowbar (9), the machete (10), the
    /// knife (11), the wood axe (8), the bats and pipes, the torches. This is the set D288 already
    /// gives a <c>MELEE_ABILITY_ID</c>; D291 gives it the <c>TYPE</c> that makes the client act
    /// on it. An unarmed GUN row (the unpaired M16 and pistol variants, the crossbow 200) is not a
    /// striker and keeps the projectile type it always had.
    /// </summary>
    public static IReadOnlySet<uint> MeleeFireGroupIds { get; } = BuildMeleeFireGroupIds();

    private static HashSet<uint> BuildMeleeFireGroupIds()
    {
        var groups = new HashSet<uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId != 0
                && !ArmedFireGroupIds.Contains(weapon.FireGroupId)
                && !ThrowableFireGroupIds.Contains(weapon.FireGroupId)
                && !TriggerAbilityFireGroupIds.Contains(weapon.FireGroupId)
                && Inventory.InventoryItemFacts.TryGet(weapon.ItemId, out Inventory.InventoryItemFact item)
                && MeleeItemClasses.Contains(item.ItemClass))
            {
                groups.Add(weapon.FireGroupId);
            }
        }

        return groups;
    }

    /// <summary>
    /// <b>D291-D293.</b> The <c>TYPE</c> (<c>rec+0x24</c>) a fire group's modes carry:
    /// <see cref="WeaponListLayouts.FireModeTypeThrowable"/> for a
    /// <see cref="ThrowableFireGroupIds"/> group,
    /// <see cref="WeaponListLayouts.FireModeTypeTriggerItemAbility"/> for a
    /// <see cref="TriggerAbilityFireGroupIds"/> group when <paramref name="binocularsTriggerAbility"/>
    /// is on, <see cref="WeaponListLayouts.FireModeTypeMelee"/> for a
    /// <see cref="MeleeFireGroupIds"/> group, and the client constructor's 0 (a projectile shot)
    /// for everything else - every armed gun and bow included, which is what they carried before.
    /// </summary>
    public static int FireModeTypeFor(uint fireGroupId, bool binocularsTriggerAbility = true)
    {
        if (fireGroupId == 0)
        {
            return 0;
        }

        if (ThrowableFireGroupIds.Contains(fireGroupId))
        {
            return WeaponListLayouts.FireModeTypeThrowable;
        }

        if (TriggerAbilityFireGroupIds.Contains(fireGroupId))
        {
            return binocularsTriggerAbility ? WeaponListLayouts.FireModeTypeTriggerItemAbility : 0;
        }

        return MeleeFireGroupIds.Contains(fireGroupId) ? WeaponListLayouts.FireModeTypeMelee : 0;
    }

    public static IReadOnlyList<FireModeRecord> FireModeRecordsWithAdsZoom(float adsZoom) =>
        adsZoom == 1.0f
            ? FireModeRecords
            : [.. FireModeRecords.Select(record =>
                AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact)
                && fact.IronSights
                && ArmedFireGroupIds.Contains(fact.FireGroupId)
                    ? record with { DefaultZoom = adsZoom }
                    : record)];

    /// <summary>
    /// <see cref="FireModeRecordsWithAdsZoom"/> plus the two ADS FIRST-PERSON fields, which go on
    /// the <b>PRIMARY</b> (index 0) mode of every <see cref="ArmedFireGroupIds"/> group rather
    /// than on the iron-sights one.
    /// <para>
    /// <c>FORCE_FP_SCOPE</c> (<c>rec+0x2dc</c>) is read off the <b>live</b> fire-mode record
    /// (<c>FUN_14158ad50:17</c>), and the live mode when the right mouse button goes down is mode
    /// 0 - so the flag has to sit there or it is never reached. Setting it makes
    /// <c>FUN_14158b700</c> take its camera branch (<c>FUN_14158ab50</c>: camera <c>0x1e</c> held,
    /// <c>0x24</c> released) instead of preparing the third-person aim camera through virtual
    /// slot <c>+0x370</c>. Those CAMERA branches are exclusive at
    /// <c>14158bb2d JZ 14158bc1f</c>; they do not prove that the ADS fire mode is unreachable.
    /// The caller <c>14158ef20</c> separately passes its aim-input bit to <c>1411ceca0</c>,
    /// which can select mode 1 and send through <c>1411b3500</c>. Keep camera selection,
    /// fire-mode selection and the optional absolute FOV override as separate inputs.
    /// </para>
    /// </summary>
    /// <param name="adsZoom">See <see cref="FireModeRecordsWithAdsZoom"/>.</param>
    /// <param name="adsFirstPerson">See the summary above.</param>
    /// <param name="fpCameraFovDegrees">The aimed first-person field of view, in degrees.</param>
    /// <param name="ironSightsArmedOnly">
    /// <b>D287, wave 16.</b> Clear <c>IRON_SIGHTS</c> (<c>rec+0x20</c> bit <c>0x04</c>) on the fire
    /// modes of every group NO armed weapon names - the fists, the melee rows, the throwables and
    /// the binoculars. See <see cref="MeleeAbilityIdFor"/> for why they are a class.
    /// </param>
    /// <param name="meleeAbilityIds">
    /// <b>D288, wave 16.</b> Write <see cref="MeleeAbilityIdFor"/> into <c>MELEE_ABILITY_ID</c>
    /// (<c>rec+0x194</c>) on both modes of every unarmed group.
    /// </param>
    public static IReadOnlyList<FireModeRecord> FireModeRecordsFor(
        float adsZoom,
        bool adsFirstPerson,
        float fpCameraFovDegrees,
        bool ironSightsArmedOnly = false,
        bool meleeAbilityIds = false,
        bool writeFireEffect = true,
        bool writeOpticScope = true,
        float opticFovDegrees = DefaultOpticFovDegrees,
        bool writeFireModeTypes = false,
        bool binocularsTriggerAbility = false,
        bool retailAutomatic = false,
        int shotgunPellets = 0,
        float shotgunSpreadDegrees = 0.0f,
        bool throwables = false,
        int throwableWindupMs = 0)
    {
        IReadOnlyList<FireModeRecord> records = FireModeRecordsWithAdsZoom(adsZoom);

        // D331 (docs/121 §3). An automatic group's modes carry the AUTOMATIC bit and an
        // AUTO_FIRE_TIME_MS equal to their REFIRE_TIME_MS. FUN_14228d2d0 reads REFIRE_TIME_MS for the
        // first two shots of a hold and AUTO_FIRE_TIME_MS (rec+0x48) for every shot after, clamped to
        // >= 1 ms - so an automatic group whose rec+0x48 is 0 cycles at 1 ms from the third round
        // on, and every one of those shots is refused by this server's rate-of-fire gate. The two
        // words come from the same client sheet and are written together.
        if (retailAutomatic)
        {
            records =
            [
                .. records.Select(record =>
                    AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact)
                    && RetailAutomaticFireGroupIds.Contains(fact.FireGroupId)
                        ? record with { Automatic = true, AutoFireTimeMs = record.RefireTimeMs }
                        : record),
            ];
        }

        // D332. The 12GA's pellet count and spread. The local spawn FUN_140c7bb60:500-513 reads
        // PELLETS_PER_SHOT through FUN_14228fbc0 and clamps it to >= 1, reads PELLET_SPREAD only when
        // that count is >= 2, and loops the pellet count spawning one projectile each - so the
        // client's own 0 is ONE pellet, and the retail shell is eight.
        if (shotgunPellets > 0 || shotgunSpreadDegrees > 0.0f)
        {
            int pellets = Math.Max(0, shotgunPellets);
            float spread = Math.Max(0.0f, shotgunSpreadDegrees);
            records =
            [
                .. records.Select(record =>
                    AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact)
                    && ShotgunFireGroupIds.Contains(fact.FireGroupId)
                        ? record with { PelletsPerShot = pellets, PelletSpread = spread }
                        : record),
            ];
        }

        if (adsFirstPerson)
        {
            float fov = fpCameraFovDegrees > 0.0f ? fpCameraFovDegrees : 0.0f;
            records =
            [
                .. records.Select(record =>
                    AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact)
                    && fact.ModeIndex == 0
                    && ArmedFireGroupIds.Contains(fact.FireGroupId)
                        ? record with { ForceFpScope = true, FpCameraFovDegrees = fov }
                        : record),
            ];
        }

        // Wave 16. The two passes above touch ARMED groups only; this one touches only the
        // groups they skip, so the two never see the same record and the order is immaterial.
        if (ironSightsArmedOnly || meleeAbilityIds)
        {
            records = [.. records.Select(record => Unarmed(record, ironSightsArmedOnly, meleeAbilityIds))];
        }

        // Report 1 (fire sound). The base records carry the client's own EFFECT_GROUP (rec+0x104);
        // turning the switch off zeroes it again, which is the wave-9..16 silence, byte for byte.
        if (!writeFireEffect)
        {
            records = [.. records.Select(record =>
                record.EffectGroup == 0 ? record : record with { EffectGroup = 0 })];
        }

        // D291-D293 (wave 17). TYPE (rec+0x24) is what the client's fire executor dispatches on:
        // 3 swings (and hides the hotbar's ammo readout), 12 throws, 8 runs the item's own
        // ability. One i8 per mode, already on the wire as 0, so no length moves. Every armed
        // gun keeps the constructor's 0 - a projectile shot - which is what it always carried.
        if (writeFireModeTypes)
        {
            records = [.. records.Select(record =>
            {
                if (!AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact))
                {
                    return record;
                }

                int type = FireModeTypeFor(fact.FireGroupId, binocularsTriggerAbility);
                return type == record.Type ? record : record with { Type = type };
            })];
        }

        // docs/120 (D300, D304). FIRE_DURATION_MS (rec+0x38) on every throwable mode - the value
        // FUN_142293af0 reads on a TYPE-12 trigger press and refuses to enter the throw state
        // (0x10) without - and the frag's two ruled modes, which no datasheet row produces.
        if (throwables)
        {
            int windup = Math.Max(1, throwableWindupMs);
            records =
            [
                .. records.Select(record =>
                {
                    if (!AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact)
                        || !ThrowableFireGroupIds.Contains(fact.FireGroupId))
                        return record;

                    record = record with { FireDurationMs = windup };
                    return AugustThrowables.TryGetByFireGroup(fact.FireGroupId, out _)
                        ? record with
                        {
                            Range = AugustThrowables.AimRange,
                            LaunchPitchAdditiveDegrees = AugustThrowables.LaunchPitchAdditiveDegrees,
                        }
                        : record;
                }),
                .. FragFireModeRecords(writeFireModeTypes, windup),
            ];
        }

        // The client tests FORCE_FP_SCOPE on the live PRIMARY mode before switching ADS
        // (14158ad50 -> 14158ab50). That branch stays on mode 0, so its reticle must also
        // be BinocularReticleViewMovie (4). Ordinary third-person ADS zooms into the helmet.
        if (writeOpticScope)
            records = [.. records.Select(record => OpticFireGroupIds.Contains(record.FireModeId / 2)
                ? record with { Type = 0, MeleeAbilityId = 0,
                    // Local CanFire (1414862b0 -> 14228c590 -> 142290a90) checks
                    // AMMO_PER_SHOT only with CHECK_ENTER_FIRE_STATE. Optics have no
                    // ammo slot, so requiring a round prevents a primary-click fire
                    // state without adding a magazine or affecting scope selection.
                    CheckEnterFireState = true, AmmoPerShot = 1,
                    ForceFpScope = record.FireModeId % 2 == 0,
                    FpCameraFovDegrees = record.FireModeId % 2 == 0 ? opticFovDegrees : 0f,
                    ReticleId = 4 }
                : record)];

        return records;
    }

    // ---------------------------------------------------------------------------------------------
    //  docs/120 - the throwables overlay (D300, D301, D303, D304)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The list-1 record for the frag's ruled fire group (<see cref="AugustThrowables"/>, D300):
    /// no automatic flag, and the two D159 mode ids when list 2 ships.
    /// </summary>
    public static FireGroupRecord FragFireGroupRecord(bool withModeIds)
    {
        uint group = Generated.Rulings.Throwables.FragFireGroupId;
        var record = new FireGroupRecord(group, 0);
        return withModeIds
            ? record with
            {
                FireModeIds = Enumerable.Range(0, FireModesPerGroup)
                    .Select(index => FireModeIdFor(group, index))
                    .ToArray(),
            }
            : record;
    }

    /// <summary>
    /// The list-0 record for the frag's <c>WEAPON_ID</c> 1404, naming its ruled fire group. No
    /// ammo slot - a grenade has no calibre (<see cref="AmmoSlotsFor"/>), exactly like the other
    /// four throwables' weapon records.
    /// </summary>
    public static WeaponDefinitionRecord FragWeaponDefinitionRecord { get; } =
        new(1404u, [Generated.Rulings.Throwables.FragFireGroupId]);

    /// <summary>
    /// The list-0 records with or without the frag's (docs/120 D300). The datasheet leaves
    /// <c>WEAPON_ID 1404</c> out of <see cref="WeaponDefinitionRecords"/> because its
    /// <c>FIRE_GROUP_ID</c> is 0; the overlay appends it, so the wave-17 records are untouched
    /// and off is byte-identical.
    /// </summary>
    public static IReadOnlyList<WeaponDefinitionRecord> WeaponDefinitionRecordsFor(bool throwables) =>
        throwables ? [.. WeaponDefinitionRecords, FragWeaponDefinitionRecord] : WeaponDefinitionRecords;

    /// <summary>
    /// The frag's two list-2 records. Its datasheet row is <c>REFIRE 0 / RELOAD 0 / CLIP 1</c>, so
    /// the timings are the other three grenades' (150 / 950) - the D300 ruling - with the throwable
    /// <c>TYPE</c> when types ship, no iron sights, no melee ability, no fire effect group (no
    /// <c>FireModeEffectGroups</c> row exists for a ruled group) and the throw wind-up.
    /// </summary>
    public static IReadOnlyList<FireModeRecord> FragFireModeRecords(bool writeFireModeTypes, int windupMs)
    {
        uint group = Generated.Rulings.Throwables.FragFireGroupId;
        var records = new FireModeRecord[FireModesPerGroup];
        for (int index = 0; index < records.Length; index++)
        {
            uint id = FireModeIdFor(group, index);
            records[index] = new FireModeRecord(
                id,
                DefinitionId: id,
                RefireTimeMs: ThrowableRefireTimeMs,
                ReloadTimeMs: ThrowableReloadTimeMs,
                AmmoSlot: 0,
                IronSights: false,
                EffectGroup: 0,
                Type: writeFireModeTypes ? WeaponListLayouts.FireModeTypeThrowable : 0,
                FireDurationMs: Math.Max(1, windupMs))
            {
                Range = AugustThrowables.AimRange,
                LaunchPitchAdditiveDegrees = AugustThrowables.LaunchPitchAdditiveDegrees,
            };
        }

        return records;
    }

    /// <summary><c>REFIRE_TIME_MS</c> of items 14/2235/2236/2237 (<c>ClientItemDatasheetData</c>). [P]</summary>
    public const int ThrowableRefireTimeMs = 150;

    /// <summary><c>RELOAD_TIME_MS</c> of items 14/2235/2236/2237 (<c>ClientItemDatasheetData</c>). [P]</summary>
    public const int ThrowableReloadTimeMs = 950;

    /// <summary>
    /// <b>List 4 for the throwables (docs/120 D303)</b>: one row per throwable fire mode, both
    /// indices, keyed <c>(fireModeDefinitionId, ammoItemId 0)</c> and pointing at the grenade's
    /// own projectile. The ammo key is 0 because that is what <c>FUN_14228de50</c> hands
    /// <c>FUN_14228fcf0</c> for a weapon definition with no ammo slot, and <c>FUN_1411542f0</c>
    /// walks list 4 for <c>rec+0x04 == 0</c> like any other value - a throwable needs no calibre to
    /// reach its projectile. Ascending by group so the wire is deterministic.
    /// </summary>
    public static IReadOnlyList<FireModeProjectileRecord> ThrowableFireModeProjectileRecords { get; } =
        BuildThrowableFireModeProjectileRecords();

    private static FireModeProjectileRecord[] BuildThrowableFireModeProjectileRecords()
    {
        var records = new List<FireModeProjectileRecord>();
        foreach (ThrowableFact fact in AugustThrowables.All.OrderBy(fact => fact.FireGroupId))
        {
            for (int index = 0; index < FireModesPerGroup; index++)
            {
                records.Add(new FireModeProjectileRecord(
                    FireModeIdFor(fact.FireGroupId, index), AmmoItemId: 0, fact.ProjectileId));
            }
        }

        return [.. records];
    }

    private static FireModeRecord Unarmed(
        FireModeRecord record,
        bool ironSightsArmedOnly,
        bool meleeAbilityIds)
    {
        if (!AugustFireModeFacts.TryGet(record.FireModeId, out AugustFireModeFact fact)
            || ArmedFireGroupIds.Contains(fact.FireGroupId))
        {
            return record;
        }

        FireModeRecord updated = record;

        if (ironSightsArmedOnly && updated.IronSights)
        {
            updated = updated with { IronSights = false };
        }

        if (meleeAbilityIds)
        {
            uint ability = MeleeAbilityIdFor(fact.FireGroupId);
            if (ability != 0)
            {
                updated = updated with { MeleeAbilityId = ability };
            }
        }

        return updated;
    }

    /// <summary>
    /// <b>D288 - the <c>MELEE_ABILITY_ID</c> (<c>rec+0x194</c>) one fire group carries</b>, or 0
    /// for a group an armed weapon names.
    /// <para>
    /// <b>The offset is [P]</b> (wave 16): the client's own <c>FireModes</c> row loader stores the
    /// schema column <c>MELEE_ABILITY_ID</c> at <c>LEA RDX,[RBX+0x174]</c> over the <c>rec+0x20</c>
    /// base (<c>142228759</c>, <c>out\w14-adsfov\disasm-firemodes-loader.txt</c>), and
    /// <c>MELEE_COMPOSITE_EFFECT_ID</c> at <c>+0x170</c> = <c>rec+0x190</c> beside it. Cranberry
    /// shipped 0 in both from wave 9 to wave 15, so <b>not one of the 120 shipped fire modes was
    /// ever a melee mode</b> - which is exactly what the owner reports: a fists trigger-pull
    /// produces <c>82 01</c> down, <c>82 01</c> up and nothing in between
    /// (<c>logs\host-20260903-194804.log</c> 19:51:12, 19:52:04, 19:52:17).
    /// </para>
    /// <para>
    /// <b>The value is the client's own column, per group.</b> A fire group is named by items, and
    /// August's <c>ClientItemDefinitions.ACTIVATABLE_ABILITY_ID</c> gives every melee item its own
    /// ability - hatchet 82 → 1111163, knife 83 → 1111164, machete 84 → 1111165, item 58 →
    /// 1111162, and each of those items owns its fire group outright (9, 10, 11, 8). The value
    /// here is therefore the ability of the LOWEST item id that names the group - the same
    /// "group's first item" rule the timings already use - resolved through
    /// <c>AbilityPackets.AbilityIdOf</c> so the existing fists lever still applies.
    /// </para>
    /// <para>
    /// <b>The one RULING is the fists.</b> August gives item 85 <c>ACTIVATABLE_ABILITY_ID = 0</c>
    /// (docs/89 §3d), so its group would carry nothing. It falls back to
    /// <c>AbilityPackets.Z1FistsAbilityId</c> (1111157) - the owner's own Z1 number, adopted under
    /// D53, and a real 9-stage <c>StageMaintainOnGuid</c> row in August's own
    /// <c>AbilityEx.txt</c>. <c>CRANBERRY_WEAPON_MELEE_ABILITY=0</c> reverts the whole field.
    /// </para>
    /// </summary>
    public static uint MeleeAbilityIdFor(uint fireGroupId)
    {
        if (fireGroupId == 0 || ArmedFireGroupIds.Contains(fireGroupId))
        {
            return 0;
        }

        uint itemDefinitionId = 0;
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId == fireGroupId
                && (itemDefinitionId == 0 || weapon.ItemId < itemDefinitionId))
            {
                itemDefinitionId = weapon.ItemId;
            }
        }

        if (itemDefinitionId == 0)
        {
            return 0;
        }

        uint ability = AbilityPackets.AbilityIdOf(itemDefinitionId);
        return ability != 0 ? ability
            : itemDefinitionId == AbilityPackets.FistsItemDefinitionId
                ? AbilityPackets.Z1FistsAbilityId
                : 0;
    }

    private static FireGroupRecord[] BuildFireGroupRecords()
    {
        var records = new FireGroupRecord[AugustWeaponFacts.FireGroupIds.Count];
        for (int i = 0; i < records.Length; i++)
        {
            uint fireGroupId = AugustWeaponFacts.FireGroupIds[i];
            byte flags = AutomaticFireGroupIds.Contains(fireGroupId) ? FireGroupRecord.AutomaticFlag : (byte)0;
            records[i] = new FireGroupRecord(fireGroupId, flags);
        }

        return records;
    }

    private static WeaponDefinitionRecord[] BuildWeaponDefinitionRecords()
    {
        var byWeaponId = new SortedDictionary<uint, uint>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.WeaponId != 0 && weapon.FireGroupId != 0)
            {
                byWeaponId.TryAdd(weapon.WeaponId, weapon.FireGroupId);
            }
        }

        var records = new WeaponDefinitionRecord[byWeaponId.Count];
        int i = 0;
        foreach ((uint weaponId, uint fireGroupId) in byWeaponId)
        {
            records[i++] = new WeaponDefinitionRecord(weaponId, [fireGroupId])
            {
                AmmoSlots = AmmoSlotsFor(weaponId),
            };
        }

        return records;
    }

    /// <summary>
    /// The <c>def+0xd0</c> ammo-slot array for one <c>WEAPON_ID</c> - <b>one slot for a gun this
    /// build carries a round for, and none for anything else</b>.
    /// <para>
    /// The pair is the client's own: <c>AmmoTypes.ByWeaponDefinitionId</c> reads it out of the
    /// firearm's own locale description (<c>"[Ammo Type: X]"</c>), and <c>CLIP_SIZE</c> is the
    /// datasheet cell of the lowest-numbered item that uses this weapon id. A weapon with no
    /// round - every melee row, every throwable, the fists - keeps the empty array it has had
    /// since wave 9, and therefore keeps its record length.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WeaponAmmoSlotRow>? AmmoSlotsFor(uint weaponId)
    {
        if (!AmmoTypes.ByWeaponDefinitionId.TryGetValue(weaponId, out uint ammoItemId)
            || ammoItemId == 0)
        {
            return null;
        }

        int clip = 0;
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.WeaponId == weaponId && weapon.ClipSize > clip)
            {
                clip = weapon.ClipSize;
            }
        }

        return [new WeaponAmmoSlotRow(ammoItemId, (uint)Math.Max(clip, 0))];
    }

    /// <summary>
    /// <b>List 4 - the fire mode to projectile mapping</b>
    /// (<see cref="FireModeProjectileRecord"/>), one row per
    /// <c>(fireModeDefinitionId, ammoItemId)</c> pair the shipped tables can actually produce.
    /// <para>
    /// <b>Why a pair and not one row per mode.</b> A fire group is shared by every item that names
    /// it, and two items sharing a group may take different rounds. The client's own lookup is
    /// keyed on both halves precisely so that one mode can fire different projectiles for different
    /// ammunition, so Cranberry emits every distinct pair a shipped weapon definition can present:
    /// the ammunition ids come from the ammo slots of every <c>WEAPON_ID</c> that names the mode's
    /// fire group, and the projectile comes from that round's own calibre
    /// (<c>AugustProjectileTable.ProjectileForAmmoItem</c>).
    /// </para>
    /// <para>
    /// A mode whose group no armed weapon names, and any round this build has no
    /// <c>PROJECTILE_ID</c> for, produce no row - which correctly leaves the fists, melee and
    /// throwables unable to reach a projectile, exactly as they were.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FireModeProjectileRecord> FireModeProjectileRecords { get; } =
        BuildFireModeProjectileRecords();

    private static FireModeProjectileRecord[] BuildFireModeProjectileRecords()
    {
        // fire group -> the distinct ammunition items every weapon that names it can be loaded
        // with, in ascending order so the wire is deterministic.
        var ammoByGroup = new SortedDictionary<uint, SortedSet<uint>>();
        foreach (AugustWeaponFact weapon in AugustWeaponFacts.All)
        {
            if (weapon.FireGroupId == 0
                || !AmmoTypes.ByWeaponDefinitionId.TryGetValue(weapon.WeaponId, out uint ammoItemId)
                || ammoItemId == 0)
            {
                continue;
            }

            if (!ammoByGroup.TryGetValue(weapon.FireGroupId, out SortedSet<uint>? ammo))
            {
                ammo = [];
                ammoByGroup[weapon.FireGroupId] = ammo;
            }

            ammo.Add(ammoItemId);
        }

        var records = new List<FireModeProjectileRecord>();
        foreach (AugustFireModeFact mode in AugustFireModeFacts.All)
        {
            if (!ammoByGroup.TryGetValue(mode.FireGroupId, out SortedSet<uint>? ammo))
            {
                continue;
            }

            foreach (uint ammoItemId in ammo)
            {
                uint projectileId = AugustProjectileTable.ProjectileForAmmoItem(ammoItemId);
                if (projectileId != 0)
                {
                    records.Add(new FireModeProjectileRecord(mode.DefinitionId, ammoItemId, projectileId));
                }
            }
        }

        return [.. records];
    }
}
