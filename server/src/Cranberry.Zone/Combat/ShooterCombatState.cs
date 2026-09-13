namespace Cranberry.Zone.Combat;

/// <summary>
/// One accepted shot, remembered by the projectile id the client itself chose. A hit report must
/// name one of these or it is refused: <b>the anti-replay rule</b>, and the owner's own.
/// </summary>
/// <param name="ProjectileId">The id from this shot's <c>Fire</c> array.</param>
/// <param name="ItemDefinitionId">What fired it - the damage row is looked up from here, never from
/// whatever the hit report claims.</param>
/// <param name="X">Muzzle point, for the range gate.</param>
/// <param name="Y">Muzzle point.</param>
/// <param name="Z">Muzzle point.</param>
/// <param name="AtMs">Server clock at the shot; the lifetime gate reads this.</param>
/// <param name="DisplayItemDefinitionId">The weapon's displayed skin at fire time; zero uses the base item.</param>
public readonly record struct FireHint(
    uint ProjectileId,
    uint ItemDefinitionId,
    float X,
    float Y,
    float Z,
    long AtMs,
    uint DisplayItemDefinitionId = 0)
{
    public uint KillFeedItemDefinitionId => DisplayItemDefinitionId != 0 ? DisplayItemDefinitionId : ItemDefinitionId;
}

/// <summary>Why a <c>Fire</c> was accepted or refused. Every arm is logged.</summary>
public enum FireVerdict : byte
{
    /// <summary>The shot stands: ammo spent, hints recorded.</summary>
    Accepted,

    /// <summary>The magazine is empty and this weapon has one.</summary>
    EmptyMagazine,

    /// <summary>Too soon after the last shot for this weapon's <c>REFIRE_TIME_MS</c>.</summary>
    RateOfFire,

    /// <summary>A projectile identity was already accepted, or repeated within this shot.</summary>
    DuplicateProjectile,
}

/// <summary>The outcome of one <c>Fire</c>.</summary>
/// <param name="Verdict">Accepted, or why not.</param>
/// <param name="Projectiles">Hints recorded; 0 on a refusal.</param>
/// <param name="AmmoLeft">Rounds left in the magazine after the shot.</param>
/// <param name="ClipSize">The August sheet's magazine size; 0 means the sheet names none.</param>
/// <param name="GateMs">The refire gate that was applied, in milliseconds.</param>
/// <param name="SinceLastShotMs">Milliseconds since the previous accepted shot; -1 for the first.</param>
public readonly record struct FireResult(
    FireVerdict Verdict,
    int Projectiles,
    int AmmoLeft,
    int ClipSize,
    int GateMs,
    long SinceLastShotMs);

/// <summary>
/// Everything one shooter's weapon loop needs.
/// <para>
/// <b>A fixed ring, not a dictionary.</b> The owner keys his fire hints by projectile id in a
/// <c>Dictionary</c> and sweeps it for expiry on every shot and every hit. A twelve-pellet blast is
/// twelve inserts and a sweep per report; at 150 players that is a lot of hashing on the hot path
/// for a window that only ever holds a couple of trigger pulls. This is a 24-entry ring: an insert
/// is a store, a lookup is a bounded linear scan of 24 structs, and expiry is a
/// timestamp comparison at the point of use rather than a sweep. 24 covers two full shotgun blasts.
/// Accepted projectile identities also live in a set until world reset, so expiring or consuming
/// a hint cannot reopen a shot. That set may allocate as it grows.
/// </para>
/// <para>
/// <b>Weapons are a small linear table for the same reason.</b> A player carries a handful of guns,
/// so a compact array keeps the hot path simple. It grows on inventory declarations and
/// never evicts the state of a weapon the player still owns.
/// </para>
/// </summary>
public sealed class ShooterCombatState : Inventory.IWeaponMagazines
{
    /// <summary>Hints held at once. Two full 12-pellet blasts.</summary>
    public const int HintCapacity = 24;

    /// <summary>Initial allocation; the table grows rather than evicting owned weapon state.</summary>
    public const int WeaponCapacity = 8;

    private readonly FireHint[] _hints = new FireHint[HintCapacity];
    private readonly bool[] _live = new bool[HintCapacity];
    private readonly ulong[] _hintWeapons = new ulong[HintCapacity];
    private readonly long[] _hintShots = new long[HintCapacity];
    private readonly bool[] _playbackPending = new bool[HintCapacity];
    private readonly bool[] _triggerStopped = new bool[HintCapacity];
    // Keep accepted identities until world reset, independently of the short damage window.
    // Expiring a hit or consuming it must not let a replayed Fire spend another round.
    private readonly HashSet<uint> _acceptedProjectiles = [];
    private long _shotSequence;
    private WeaponRuntime[] _weapons = new WeaponRuntime[WeaponCapacity];
    private int _weaponCount;
    private int _next;

    /// <summary>Shots this server accepted.</summary>
    public long ShotsFired { get; private set; }

    /// <summary>Shots refused, by any gate.</summary>
    public long ShotsRefused { get; private set; }

    /// <summary>Hit reports that passed every gate and resolved to damage.</summary>
    public long HitsRegistered { get; private set; }

    /// <summary>Health units this shooter has taken off other people.</summary>
    public long DamageDealt { get; set; }

    /// <summary>Records a fire request refused before it reaches a weapon-specific gate.</summary>
    public void CountRefusedShot() => ShotsRefused++;

    /// <summary>Live hints right now - a diagnostic, not a wire value.</summary>
    public int LiveHints
    {
        get
        {
            int n = 0;

            for (int i = 0; i < HintCapacity; i++)
            {
                if (_live[i])
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>
    /// Binds an item instance guid to what it actually is, so the loop never has to trust a
    /// definition id off the wire. Called when a weapon is granted or picked up. Re-declaring the
    /// same guid refills the magazine, which is what a fresh pickup means.
    /// </summary>
    public void DeclareWeapon(ulong itemGuid, uint itemDefinitionId)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            if (_weaponCount == _weapons.Length) Array.Resize(ref _weapons, checked(_weapons.Length * 2));
            index = _weaponCount++;
        }

        _weapons[index] = new WeaponRuntime
        {
            Guid = itemGuid,
            ItemDefinitionId = itemDefinitionId,
            Ammo = RetailBalance.ClipSize(itemDefinitionId),
            Durability = -1,
            HasFired = false,
            LastFireAtMs = 0,
        };
    }

    /// <summary>
    /// The same, with the magazine stated explicitly - <b>what the parity rule needs</b>. A looted
    /// gun is declared with 0 (<c>AmmoOptions.GunsSpawnEmpty</c>, the owner's own
    /// <c>ParityGunsSpawnEmpty</c>, S6 §3.1); a throwable with 1; a debug grant with its clip.
    /// </summary>
    public void DeclareWeapon(ulong itemGuid, uint itemDefinitionId, int ammo)
    {
        DeclareWeapon(itemGuid, itemDefinitionId);
        int index = IndexOf(itemGuid);
        _weapons[index].Ammo = Math.Max(0, ammo);
    }

    /// <summary>
    /// Declare <paramref name="itemGuid"/> with <paramref name="ammo"/> rounds <b>only if this
    /// session has never seen it</b>, and answer whether that happened. The first sight of a weapon
    /// is the pickup as far as combat is concerned, so this is where "looted guns spawn empty"
    /// lands - re-declaring an instance the player has already loaded would empty it again.
    /// </summary>
    public bool EnsureDeclared(ulong itemGuid, uint itemDefinitionId, int ammo)
    {
        if (IndexOf(itemGuid) >= 0)
        {
            return false;
        }

        DeclareWeapon(itemGuid, itemDefinitionId, ammo);
        return true;
    }

    /// <summary>
    /// The magazine a weapon starts life with under <paramref name="options"/>: 0 for a firearm when
    /// guns spawn empty, 1 for a throwable (it always has the one in hand), otherwise the client's
    /// own <c>CLIP_SIZE</c>. <b>One expression shared by the trigger, the reload and the unload</b>,
    /// which is the shape of the owner's own <c>DefaultMagazineFor</c> (S6 §4.5).
    /// </summary>
    public static int DefaultMagazineFor(uint itemDefinitionId, AmmoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int clip = RetailBalance.ClipSize(itemDefinitionId);

        if (clip <= 0)
        {
            // No magazine in the sheet: the fists, a melee weapon, a throwable. A throwable is
            // "chambered" at 1 so the trigger gate below never refuses it.
            return AmmoTypes.AmmoItemFor(itemDefinitionId) == 0 ? 1 : 0;
        }

        return options.GunsSpawnEmpty ? 0 : clip;
    }

    /// <summary>True when this guid is a weapon instance this session declared.</summary>
    public bool Knows(ulong itemGuid) => IndexOf(itemGuid) >= 0;

    /// <summary>The item definition behind a declared guid; 0 when unknown.</summary>
    public uint ItemDefinitionOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? 0u : _weapons[index].ItemDefinitionId;
    }

    /// <summary>
    /// <b>docs/107 §2 - record the fire mode the client just selected.</b> The August client sends
    /// <c>82 0c SwitchFireModeRequest</c> on every ADS press and release, and the mode it names is
    /// what its own shot resolves through, so the server has to hold the same value or its refire
    /// gate, its projectile choice and its rounds-per-shot are answering about the wrong mode.
    /// <para>
    /// Returns false for a guid this session has not declared - the request is then a report about a
    /// weapon combat has never seen, which is exactly the case the active-hand check refuses.
    /// </para>
    /// <para>
    /// <b>The pair mirrors two client fields exactly:</b> <c>comp+0x64</c> (== <c>item+0x10c</c>) is
    /// the group and <c>comp+0x68</c> (== <c>item+0x110</c>) the mode, both written by the client's
    /// own local setter <c>FUN_1422935d0:55-56</c> BEFORE it sends the <c>82 0c</c>
    /// (<c>FUN_14148c1c0:26</c> only sends when that setter returned 1). <c>FUN_14228d970</c>
    /// resolves the live fire-mode record from exactly those two ints, and neither <c>82 03 Fire</c>
    /// nor <c>82 01 FireStateUpdate</c> carries a mode - <b>so this pair is the only thing that says
    /// which mode a shot was fired in</b>, and everything the server decides about the shot (refire
    /// gate, reload time, magazine, rounds per shot, projectile) is a property of that mode.
    /// </para>
    /// <para>
    /// <b>Two s2c packets would silently invalidate it</b>, and this server sends neither today:
    /// <c>82 1c Reset</c> (<c>FUN_142293450:23-24</c> writes <c>comp+0x64 = fireGroupIndex</c> and
    /// <c>comp+0x68 = 0</c>) and <c>82 12 RemoveFireGroup</c> (<c>FUN_1422930c0</c> falls back to
    /// mode 0). Either one yanks an aiming player out of ADS with no packet from the client, so a
    /// future sender of either must call <see cref="SelectFireMode"/> with mode 0 itself.
    /// </para>
    /// </summary>
    public bool SelectFireMode(ulong itemGuid, byte fireGroupIndex, byte fireModeIndex)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return false;
        }

        _weapons[index].FireGroupIndex = fireGroupIndex;
        _weapons[index].FireModeIndex = fireModeIndex;
        _weapons[index].FireModeSwitches++;
        return true;
    }

    /// <summary>
    /// The fire mode this weapon is in - <b>1 is aim-down-sights</b>. -1 when the guid is unknown;
    /// 0 (hip) until the client has sent a <c>82 0c</c> for it.
    /// </summary>
    public int FireModeOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? -1 : _weapons[index].FireModeIndex;
    }

    /// <summary>The fire group this weapon is in; -1 when the guid is unknown.</summary>
    public int FireGroupOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? -1 : _weapons[index].FireGroupIndex;
    }

    /// <summary>How many fire-mode switches this weapon has been asked for; -1 when unknown.</summary>
    public long FireModeSwitchesOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? -1 : _weapons[index].FireModeSwitches;
    }

    /// <summary>Rounds left in a declared weapon's magazine; -1 when the guid is unknown.</summary>
    public int AmmoOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? -1 : _weapons[index].Ammo;
    }

    /// <summary>
    /// Refills a declared weapon's magazine - what completing a reload does. Returns false when the
    /// guid is unknown.
    /// <para>
    /// The reload counter it bumps is what the client's own applier compares against before it will
    /// write the new magazine (<see cref="WeaponReplyPackets.WriteReloadCount"/>), so it must rise
    /// on every completed reload and it must ride the reply.
    /// </para>
    /// </summary>
    public bool Reload(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return false;
        }

        _weapons[index].Ammo = RetailBalance.ClipSize(_weapons[index].ItemDefinitionId);
        _weapons[index].ReloadCount++;
        return true;
    }

    /// <summary>
    /// Put <paramref name="rounds"/> rounds that came <b>out of the bag</b> into a declared
    /// weapon's magazine, never past its clip, and bump the reload counter that rides
    /// <c>82 08</c>. Returns how many actually fitted; -1 when the guid is unknown.
    /// <para>
    /// This is the half of a reload that <see cref="Reload"/> does for free. The two coexist
    /// deliberately: <see cref="Reload"/> is what runs when no <c>IAmmoStore</c> is wired
    /// (<c>CRANBERRY_AMMO_FROM_BAG=0</c>, and every decode-only caller), which is D118's world
    /// exactly.
    /// </para>
    /// </summary>
    public int Load(ulong itemGuid, int rounds)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return -1;
        }

        int clip = RetailBalance.ClipSize(_weapons[index].ItemDefinitionId);
        int room = clip <= 0 ? rounds : Math.Max(0, clip - _weapons[index].Ammo);
        int loaded = Math.Max(0, Math.Min(rounds, room));
        _weapons[index].Ammo += loaded;
        _weapons[index].ReloadCount++;
        return loaded;
    }

    /// <summary>
    /// Empty a declared weapon's magazine and answer what came out - the model half of
    /// <c>UnloadWeapon</c> (S6 §4.5: the rounds are granted back to the bag). -1 when the guid is
    /// unknown.
    /// </summary>
    public int Unload(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return -1;
        }

        int rounds = _weapons[index].Ammo;
        _weapons[index].Ammo = 0;
        return rounds;
    }

    /// <summary>
    /// Force a declared weapon's magazine to <paramref name="ammo"/> - the
    /// <c>82 28 AmmoCountAcknowledge</c> reconcile. The client sends that packet when its own count
    /// disagrees with the one this server put in <c>82 08</c> (S5c §5.3), and the client's count is
    /// what the player can actually see, so it wins. False when the guid is unknown.
    /// </summary>
    public bool SetMagazine(ulong itemGuid, int ammo)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return false;
        }

        int clip = RetailBalance.ClipSize(_weapons[index].ItemDefinitionId);
        _weapons[index].Ammo = clip > 0 ? Math.Clamp(ammo, 0, clip) : Math.Max(0, ammo);
        return true;
    }

    /// <summary>
    /// <b>The magazine resync (D223).</b> Give a declared weapon its full <c>CLIP_SIZE</c>, but only
    /// on the one occasion where the wire says the two copies of the magazine were never the same:
    /// the client has just announced a <b>non-dry</b> fire state for a weapon this session declared
    /// EMPTY and that has never fired and never reloaded.
    /// <para>
    /// <b>Why that is evidence and not a giveaway.</b> The August client writes
    /// <see cref="WeaponBaseDecoder.EmptyFireState"/> (64) in <c>82 01 FireStateUpdate</c> when it
    /// believes the magazine is dry. In the 2026-09-03 17:51 session it wrote 17 and 0 on 112
    /// updates and 64 on none, then sent 53 <c>82 03 Fire</c> packets and 53 <c>82 20</c> hints,
    /// after being handed an <c>ItemAdd</c> tail whose ammo-slot array said <b>0</b>. A client that
    /// had taken our 0 would have reported 64 on the first pull. It did not, so under
    /// <c>GunsSpawnEmpty</c> the server's 0 was a number the client never held, and every shot of
    /// that session was refused against a magazine that existed only here.
    /// </para>
    /// <para>
    /// <b>Once per instance, ever.</b> The guards are the whole safety of it: a weapon that has
    /// fired has a magazine this server itself wrote down, and a weapon that has reloaded has one
    /// the bag paid for. Neither is resynced, so a client cannot spam <c>82 01</c> for rounds, and a
    /// gun that runs genuinely dry mid-magazine reports 64 and reloads through
    /// <see cref="Load"/> exactly as before.
    /// </para>
    /// </summary>
    /// <returns>The rounds the magazine was set to, or -1 when nothing was done.</returns>
    public int AdoptClientMagazine(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return -1;
        }

        int clip = RetailBalance.ClipSize(_weapons[index].ItemDefinitionId);

        if (clip <= 0
            || _weapons[index].Ammo > 0
            || _weapons[index].HasFired
            || _weapons[index].ReloadCount > 0)
        {
            return -1;
        }

        _weapons[index].Ammo = clip;
        return clip;
    }

    /// <summary>
    /// True when a live, unexpired hint exists for <paramref name="projectileId"/> - a
    /// <b>peek</b>, which is what <c>82 20 WeaponFireHint</c> needs. It must never consume: the
    /// hint belongs to the <c>82 06 ProjectileHitReport</c> that may still be coming, and the hint
    /// is the only thing that lets that report pay damage.
    /// </summary>
    public bool HasLiveHint(uint projectileId, long nowMs, long lifetimeMs)
    {
        for (int i = 0; i < HintCapacity; i++)
        {
            if (_live[i]
                && _hints[i].ProjectileId == projectileId
                && nowMs - _hints[i].AtMs <= lifetimeMs)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Claims observer playback once per accepted trigger, independently of hit consumption.
    /// Every hinted pellet must belong to that same shot and weapon. A subset cannot replay
    /// the remaining pellets of an already presented shotgun blast.
    /// </summary>
    public bool TryPresentFire(in WeaponFireHint hint, long nowMs, long lifetimeMs, out bool stopped)
    {
        stopped = false;
        long shot = 0;
        foreach (var entry in hint.Hints)
        {
            int found = -1;
            for (int i = 0; i < HintCapacity; i++)
            {
                long age = nowMs - _hints[i].AtMs;
                if (_playbackPending[i] && _hintWeapons[i] == hint.WeaponGuid
                    && _hints[i].ProjectileId == entry.ProjectileId && age >= 0 && age <= lifetimeMs)
                {
                    found = i;
                    break;
                }
            }
            if (found < 0 || shot != 0 && shot != _hintShots[found]) return false;
            shot = _hintShots[found];
            stopped |= _triggerStopped[found];
        }
        if (shot == 0) return false;
        for (int i = 0; i < HintCapacity; i++)
            if (_hintShots[i] == shot) _playbackPending[i] = false;
        return true;
    }

    public void NoteTriggerStop(ulong guid)
    {
        for (int i = 0; i < HintCapacity; i++)
            if (_hintWeapons[i] == guid) _triggerStopped[i] = true;
    }

    /// <summary>Changing hands retires pending presentation, while already fired bullets can still hit.</summary>
    public void EndWeaponPresentation(ulong guid)
    {
        for (int i = 0; i < HintCapacity; i++)
            if (_hintWeapons[i] == guid) _playbackPending[i] = false;
    }

    /// <summary>Suppress repeated copies of the native chamber notification.</summary>
    public bool NoteChamber(ulong guid, uint gameTime, bool interrupt)
    {
        int index = IndexOf(guid);
        if (index < 0) return false;
        ref var weapon = ref _weapons[index];
        if (weapon.HasChamberEvent && weapon.ChamberTime == gameTime && weapon.ChamberInterrupt == interrupt)
            return false;
        weapon.HasChamberEvent = true;
        weapon.ChamberTime = gameTime;
        weapon.ChamberInterrupt = interrupt;
        return true;
    }

    /// <summary>
    /// Take <paramref name="loss"/> off a declared weapon's durability, floor 0, and answer what is
    /// left; -1 when the guid is unknown. The bar starts at <paramref name="max"/> the first time it
    /// is asked for, because the August client ships no durability column anywhere.
    /// <para>
    /// The owner's rule, verbatim: <c>CurrentDurability −5</c> per shot, and <b>a gun at 0 is not
    /// removed</b> (S6 §4.5) - it keeps firing, which is why nothing here refuses a shot.
    /// </para>
    /// </summary>
    public int SpendDurability(ulong itemGuid, int loss, int max)
    {
        int index = IndexOf(itemGuid);

        if (index < 0)
        {
            return -1;
        }

        if (_weapons[index].Durability < 0)
        {
            _weapons[index].Durability = max;
        }

        _weapons[index].Durability = Math.Max(0, _weapons[index].Durability - Math.Max(0, loss));
        return _weapons[index].Durability;
    }

    /// <summary>Durability left on a declared weapon; -1 when unknown or never spent.</summary>
    public int DurabilityOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? -1 : _weapons[index].Durability;
    }

    /// <inheritdoc />
    int Inventory.IWeaponMagazines.MagazineOf(ulong itemGuid) => AmmoOf(itemGuid);

    /// <inheritdoc />
    int Inventory.IWeaponMagazines.UnloadMagazine(ulong itemGuid) => Unload(itemGuid);

    /// <summary>Completed reloads on a declared weapon; 0 when the guid is unknown.</summary>
    public ulong ReloadCountOf(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        return index < 0 ? 0ul : _weapons[index].ReloadCount;
    }

    /// <summary>
    /// The fire arbitration, in the owner's own order: refuse an empty magazine, refuse a shot
    /// inside the weapon's refire time, then stamp the clock, spend the round and record <b>one hint
    /// per projectile</b>, keyed by the id the client itself sent.
    /// <para>
    /// The two things that changed on the way across: the refire gate is
    /// <c>max(floor, REFIRE_TIME_MS)</c> from the August client's own datasheet rather than a branch
    /// chain on definition id, and the hint window is this ring rather than a swept dictionary.
    /// </para>
    /// </summary>
    public FireResult Fire(in WeaponFire fire, uint itemDefinitionId, long nowMs, CombatOptions options,
        uint displayItemDefinitionId = 0, uint clientGameTime = 0)
    {
        ArgumentNullException.ThrowIfNull(options);

        int index = IndexOf(fire.WeaponGuid);

        if (index < 0)
        {
            DeclareWeapon(fire.WeaponGuid, itemDefinitionId);
            index = IndexOf(fire.WeaponGuid);
        }

        ref WeaponRuntime weapon = ref _weapons[index];
        int clip = RetailBalance.ClipSize(weapon.ItemDefinitionId);
        int gate = RetailBalance.RefireGateMs(
            weapon.ItemDefinitionId, options.RefireFloorMs, weapon.FireModeIndex, options.ShippedRefireGate, options.Z1LiveGunplay);
        long since = weapon.HasFired ? nowMs - weapon.LastFireAtMs : -1;

        for (int n = 0; n < fire.ProjectileIds.Length; n++)
        {
            uint id = fire.ProjectileIds[n];
            if (_acceptedProjectiles.Contains(id) || Array.IndexOf(fire.ProjectileIds, id, 0, n) >= 0)
            {
                ShotsRefused++;
                return new FireResult(FireVerdict.DuplicateProjectile, 0, weapon.Ammo, clip, gate, since);
            }
        }

        if (clip > 0 && weapon.Ammo <= 0)
        {
            ShotsRefused++;
            return new FireResult(FireVerdict.EmptyMagazine, 0, weapon.Ammo, clip, gate, since);
        }

        long due = weapon.PacedFireAtMs + gate;
        int tolerance = Math.Clamp(options.RefireJitterMs, 0, Math.Min(16, gate / 4));
        if (weapon.HasFired && nowMs < due - tolerance
            && !(clip > 0 && CanRecoverArrival(in fire, in weapon, nowMs, gate, tolerance, clientGameTime)))
        {
            ShotsRefused++;
            return new FireResult(FireVerdict.RateOfFire, 0, weapon.Ammo, clip, gate, since);
        }

        weapon.PacedFireAtMs = weapon.HasFired ? Math.Max(nowMs, due) : nowMs;
        weapon.LastFireAtMs = nowMs;
        weapon.HasFired = true;
        weapon.ClientFireTime = clientGameTime;

        if (clip > 0)
        {
            weapon.Ammo--;
        }

        long shot = ++_shotSequence;
        foreach (uint id in fire.ProjectileIds)
        {
            _acceptedProjectiles.Add(id);
            Remember(new FireHint(id, weapon.ItemDefinitionId, fire.X, fire.Y, fire.Z, nowMs,
                displayItemDefinitionId), fire.WeaponGuid, shot);
        }

        ShotsFired++;
        return new FireResult(
            FireVerdict.Accepted, fire.ProjectileIds.Length, weapon.Ammo, clip, gate, since);
    }

    private bool CanRecoverArrival(in WeaponFire fire, in WeaponRuntime weapon, long nowMs,
        int gate, int tolerance, uint clientGameTime)
    {
        // One compressed arrival interval, never banked idle credit. Keeping the normal paced
        // deadline above advances it by a FULL gate: another recovery must wait for that debt.
        // Client time only corroborates spacing; it never supplies a server time or rewinds it.
        if (weapon.PacedFireAtMs > nowMs || weapon.ClientFireTime == 0
            || clientGameTime == 0) return false;
        int elapsed = unchecked((int)(clientGameTime - weapon.ClientFireTime));
        if (elapsed < gate - tolerance || elapsed > gate + tolerance) return false;

        // Do not introduce an immediate projectile replay through the recovery exception.
        // Consumed hints retain their identity. Lifetime-wide replay protection is separate.
        for (int n = 0; n < fire.ProjectileIds.Length; n++)
        {
            uint id = fire.ProjectileIds[n];
            if (Array.IndexOf(fire.ProjectileIds, id, 0, n) >= 0) return false;
            foreach (var hint in _hints)
                if (hint.ItemDefinitionId != 0 && hint.ProjectileId == id) return false;
        }
        return fire.ProjectileIds.Length > 0;
    }

    /// <summary>
    /// Finds and <b>consumes</b> the hint for a projectile id. One pellet is one hit: consuming it
    /// is what stops a duplicated report being paid twice. False when there is no live hint, which
    /// is a hit that did not follow a shot this server accepted.
    /// </summary>
    public bool TryConsumeHint(uint projectileId, long nowMs, long lifetimeMs, out FireHint hint)
    {
        for (int i = 0; i < HintCapacity; i++)
        {
            if (!_live[i] || _hints[i].ProjectileId != projectileId)
            {
                continue;
            }

            if (nowMs - _hints[i].AtMs > lifetimeMs)
            {
                _live[i] = false;
                continue;
            }

            hint = _hints[i];
            _live[i] = false;
            return true;
        }

        hint = default;
        return false;
    }

    /// <summary>Counts one accepted hit and the health it took off.</summary>
    public void CountHit(int damageUnits)
    {
        HitsRegistered++;
        DamageDealt += damageUnits;
    }

    /// <summary>Drops every hint and every weapon - a respawn, or a new match.</summary>
    public void Reset()
    {
        Array.Clear(_live);
        Array.Clear(_hints);
        Array.Clear(_playbackPending);
        Array.Clear(_hintWeapons);
        _acceptedProjectiles.Clear();
        Array.Clear(_weapons);
        _weaponCount = 0;
        _next = 0;
    }

    private void Remember(in FireHint hint, ulong weaponGuid, long shot)
    {
        _hints[_next] = hint;
        _live[_next] = true;
        _hintWeapons[_next] = weaponGuid;
        _hintShots[_next] = shot;
        _playbackPending[_next] = true;
        _triggerStopped[_next] = false;
        _next = (_next + 1) % HintCapacity;
    }

    private int IndexOf(ulong guid)
    {
        for (int i = 0; i < _weaponCount; i++)
        {
            if (_weapons[i].Guid == guid)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Forget an inventory instance that was removed. Already-fired projectiles keep their hints.</summary>
    public bool ForgetWeapon(ulong itemGuid)
    {
        int index = IndexOf(itemGuid);
        if (index < 0) return false;
        _weaponCount--;
        _weapons[index] = _weapons[_weaponCount];
        _weapons[_weaponCount] = default;
        return true;
    }

    /// <summary>Inventory instances whose magazines and firing state are currently tracked.</summary>
    public int WeaponCount => _weaponCount;

    private struct WeaponRuntime
    {
        public ulong Guid;
        public uint ItemDefinitionId;
        public int Ammo;

        /// <summary>Durability left, or -1 while it has never been spent. See
        /// <see cref="SpendDurability"/> for why it starts unset.</summary>
        public int Durability;

        /// <summary>False until the first accepted shot, so a match-clock zero is not "long ago".</summary>
        public bool HasFired;

        public long LastFireAtMs;
        public long PacedFireAtMs;
        public uint ClientFireTime;
        public bool HasChamberEvent;
        public uint ChamberTime;
        public bool ChamberInterrupt;

        /// <summary>Completed reloads, rising. Rides <c>82 08</c>; see
        /// <see cref="WeaponReplyPackets.WriteReloadCount"/> for why it must never fall.</summary>
        public ulong ReloadCount;

        /// <summary>The fire group the client last selected (<c>82 0c</c>); 0 until it says.</summary>
        public byte FireGroupIndex;

        /// <summary>
        /// The fire mode the client last selected (<c>82 0c</c>). <b>1 = aim down sights.</b> 0
        /// until the client says otherwise, which is hip fire and is what a weapon is drawn in.
        /// </summary>
        public byte FireModeIndex;

        /// <summary>Fire-mode switches this weapon has been asked for - a diagnostic.</summary>
        public long FireModeSwitches;
    }
}
