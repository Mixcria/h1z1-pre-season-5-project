namespace Cranberry.Zone.Combat;

/// <summary>
/// How much of the <c>0x82 WeaponBase</c> family this server is willing to act on.
/// </summary>
public enum WeaponFireLayout : byte
{
    /// <summary>
    /// Decode and log, never damage. The one-word revert for "shooting went wrong": the trace stays
    /// as rich as it is under <see cref="Z1Candidate"/>, so the evidence keeps arriving, but no
    /// point of health can move on a layout that has never been proven at 1148.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Read <c>82 01</c>, <c>82 03</c> and <c>82 06</c> with the owner's recovered
    /// <c>ClientProtocol_1087</c> sender-side layouts (docs/81 §2c), behind the plausibility gate of
    /// <see cref="WeaponBaseDecoder"/>. The sub numbering is byte-identical between the two builds
    /// for every sub this loop touches, which is what makes these candidates rather than guesses.
    /// </summary>
    Z1Candidate = 1,
}

/// <summary>
/// D52's hole, closed behind switches. Everything that changes how a match plays lives here so the
/// owner can revert one behaviour at a time from the environment, without a rebuild.
/// <para>
/// The defaults are the behaviour the owner asked for: decode the family, gate every field, apply
/// the retail damage table, and send the hit marker. Nothing here can put a byte on the wire that
/// the client did not ask for by shooting first.
/// </para>
/// </summary>
public sealed record CombatOptions
{
    /// <summary>Environment switch for <see cref="Enabled"/>.</summary>
    public const string EnabledVariable = "CRANBERRY_COMBAT";

    /// <summary>Environment switch for <see cref="EnableCombatDamage"/>.</summary>
    public const string DamageVariable = "CRANBERRY_COMBAT_DAMAGE";

    /// <summary>Environment switch for <see cref="SendHitMarker"/>.</summary>
    public const string HitMarkerVariable = "CRANBERRY_HIT_MARKER";

    /// <summary>Environment switch selecting <see cref="Layout"/> (<c>0</c> = Unknown).</summary>
    public const string LayoutVariable = "CRANBERRY_WEAPON_FIRE_LAYOUT";

    /// <summary>Environment switch for <see cref="PracticeTarget"/>.</summary>
    public const string PracticeTargetVariable = "CRANBERRY_PRACTICE_TARGET";

    /// <summary>Environment switch for <see cref="MeleeDamage"/>.</summary>
    public const string MeleeDamageVariable = "CRANBERRY_MELEE_DAMAGE";

    /// <summary>Environment switch for <see cref="MeleeOnTrigger"/> (docs/89 addendum). Default ON.</summary>
    public const string MeleeOnTriggerVariable = "CRANBERRY_MELEE_ON_TRIGGER";

    /// <summary>Environment switch for <see cref="SendAbilityManager"/>.</summary>
    public const string AbilityManagerVariable = "CRANBERRY_ABILITY_MANAGER";

    /// <summary>Environment switch for <see cref="SwitchFireModeReply"/> (docs/107 §2). Default ON.</summary>
    public const string SwitchFireModeReplyVariable = "CRANBERRY_WEAPON_FIREMODE_REPLY";

    /// <summary>Environment switch for <see cref="MagazineResync"/> (docs/107 §10). Default ON.</summary>
    public const string MagazineResyncVariable = "CRANBERRY_MAGAZINE_RESYNC";

    /// <summary>Environment switch for <see cref="ActOnWeaponFireHint"/> (docs/107 §10). Default ON.</summary>
    public const string WeaponFireHintVariable = "CRANBERRY_WEAPON_FIRE_HINT";

    /// <summary>Environment switch for <see cref="MagazineResyncFromBag"/> (docs/121 §5). Default ON.</summary>
    public const string MagazineResyncFromBagVariable = "CRANBERRY_MAGAZINE_RESYNC_FROM_BAG";

    /// <summary>Environment switch for <see cref="TimedReload"/> (docs/121 §6). Default ON.</summary>
    public const string TimedReloadVariable = "CRANBERRY_RELOAD_TIMED";

    /// <summary>Environment switch for <see cref="Throwables"/> (docs/120, D305-D310). Default ON.</summary>
    public const string ThrowablesVariable = "CRANBERRY_THROWABLES_ARM";

    /// <summary>Environment switch for <see cref="ThrowableDamage"/> (docs/120, D308). Default ON.</summary>
    public const string ThrowableDamageVariable = "CRANBERRY_THROWABLE_DAMAGE";

    /// <summary>
    /// Answer <c>0x82</c> at all. Off restores D52's behaviour exactly - one <c>WEAPONFIRE</c> line
    /// with untruncated hex and no reply - which is the control arm for any run where the client
    /// misbehaves the moment it holds a gun.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Let an accepted hit move health. Off decodes, gates and logs the whole loop but pays no
    /// damage: the diagnostic arm, and the reason <see cref="SendHitMarker"/> is independent of it.
    /// </summary>
    public bool EnableCombatDamage { get; init; } = true;

    /// <summary>
    /// Send <c>Ui.WeaponHitFeedback 1a 10</c> to the shooter on every accepted hit. The August
    /// parser and reticle handler carry the colour and animation the modern HUD requires
    /// (docs/hit-feedback-20260906.md). The owner's own rule is that it
    /// goes out whether or not <see cref="EnableCombatDamage"/> is on, because it is UI and seeing
    /// the X confirms the whole path even in a diagnostic run.
    /// </summary>
    public bool SendHitMarker { get; init; } = true;

    /// <summary>Which c2s layouts the decoder will act on. See <see cref="WeaponFireLayout"/>.</summary>
    public WeaponFireLayout Layout { get; init; } = WeaponFireLayout.Z1Candidate;

    /// <summary>
    /// Lane 1F's switches: where a magazine's rounds come from, whether a looted gun arrives loaded,
    /// and whether <c>11 03 ItemUpdate</c> reaches the client. See <see cref="AmmoOptions"/>.
    /// </summary>
    public AmmoOptions Ammo { get; init; } = AmmoOptions.Default;

    /// <summary>
    /// Let a resolved melee swing (<c>a0 02 UpdateAbility</c>) move health. Off parses the swing,
    /// logs it in full and hurts nobody - the mirror of the owner's own
    /// <c>ZoneAbilities.MeleeDamage</c> off-switch, and the right first run if a swing ever turns
    /// out to be resolving against the wrong thing. On by default: docs/89 §0.5.
    /// </summary>
    public bool MeleeDamage { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_MELEE_ON_TRIGGER=0</c> is the revert). Arbitrate a melee swing
    /// off the WEAPON fire path - the <c>82 01 FireStateUpdate</c> trigger-down of a fists / melee
    /// row - instead of off the abilities family (<c>0xa0</c>).
    /// <para>
    /// <b>docs/89's <c>0xa0</c> premise is falsified by the August wire.</b> When the owner wielded
    /// the fists (item 85) and clicked, the client sent only <c>82 01 FireStateUpdate state=17</c>
    /// (trigger DOWN) then <c>state=0</c> - never an <c>82 03 Fire</c> and never an
    /// <c>a0 01/a0 02</c> ability packet (zero <c>0xa0</c> inbound across the whole 2026-09-03 19:48
    /// session; every <c>a0 05</c> there is this server's own send). The August client resolves a
    /// melee hit client-side and reports nothing but the trigger state, so
    /// <see cref="MeleeArm"/>'s <c>0xa0</c> entry point was dead code and no server data could ever
    /// wake it. A swing is therefore resolved here, on the trigger-down of a melee-class item, using
    /// the same damage table, range gate and hit marker <see cref="MeleeArm"/> already carried.
    /// </para>
    /// <para>
    /// Guns also send <c>82 01 state=17</c>, but they are not melee items and they follow it with an
    /// <c>82 03 Fire</c> that does the damage; the melee arbitration is gated on
    /// <see cref="MeleeArm.IsMeleeItem"/>, so a gun's trigger-down never resolves a swing and a
    /// melee item never spends a round.
    /// </para>
    /// </summary>
    public bool MeleeOnTrigger { get; init; } = true;

    /// <summary>
    /// Send <c>a0 05 SetActivatableAbilityManager</c> beside every <c>86 04 SetLoadoutSlots</c>
    /// addressed to the owner's own character, as the client's own <c>updateLoadout</c> does.
    /// <para>
    /// <b>This is the one genuinely NEW s2c packet family in the wave-9 weapons lane</b>, and it has
    /// its own revert for that reason: the id is registered by the August client
    /// (<c>0x500a000</c> at <c>FUN_1413bef90</c>) and the body is the owner's, but no <c>0xa0</c>
    /// has ever been seen on an August wire in either direction. Off, melee is decoded and logged
    /// but the client is never told a swing is available, so nothing swings.
    /// </para>
    /// </summary>
    public bool SendAbilityManager { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_WEAPON_FIREMODE_REPLY=0</c> is the revert). Act on
    /// <c>82 0c SwitchFireModeRequest</c> - the August client's ADS packet - instead of naming it in
    /// the log and dropping it (docs/107 §2).
    /// <para>
    /// <b>The name says "reply" and the arm sends nothing, deliberately.</b> The question this
    /// switch was cut for was "does the local weapon's mode switch wait on the server?", and the
    /// answer from the binary is <b>no</b>: <c>FUN_140b07010</c>'s s2c <c>0x82</c> switch is exactly
    /// <c>{08 0b 0f 11 12 13 14 15 19 1b 1c 1e 24 25 26}</c> and <b><c>0x0c</c> has no receive
    /// case</b> (S5c §5.3), so this build has no packet the local weapon could consume as an
    /// acknowledgement. The local chain in <c>FUN_1411ceca0</c> picks the mode itself (S5c §5.4).
    /// What the arm does instead is the half the shot actually needs: it records which mode the
    /// weapon is in, because everything the server decides about a shot - the refire gate, the
    /// rounds per shot, which projectile - is a property of the fire MODE, and the client changes
    /// it 48 times a session without ever being asked.
    /// </para>
    /// <para>
    /// It is a switch rather than unconditional because acting on a packet that has never been acted
    /// on is a behaviour change, and because with it off the arm reverts to the exact pre-wave-13
    /// "seen, not acted on" line - the control for any session where ADS misbehaves.
    /// </para>
    /// </summary>
    public bool SwitchFireModeReply { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_MAGAZINE_RESYNC=0</c> is the revert). Believe the client's
    /// own <c>82 01 FireStateUpdate</c> when it says a weapon this session declared EMPTY is not
    /// dry, and give that instance its clip once - <see cref="ShooterCombatState.AdoptClientMagazine"/>.
    /// <para>
    /// <b>This is what unblocked the shot.</b> The 2026-09-03 17:51 session refused all 53 of its
    /// trigger pulls with "magazine empty" while the client was drawing tracers, spawning
    /// projectiles and reporting hits, because <c>GunsSpawnEmpty</c> put a 0 in the server's
    /// magazine that the client never took (docs/107 §10). Off, that session's behaviour returns
    /// exactly: the refusal, the <c>82 1e FireRejected</c>, and no damage.
    /// </para>
    /// </summary>
    public bool MagazineResync { get; init; } = true;

    /// <summary>
    /// D337: the rate-of-fire gate reads the SHIPPED fire mode's <c>REFIRE_TIME_MS</c> (the
    /// captured table's, via <c>CapturedWeaponTable.OverridesFor</c>) instead of the August sheet's.
    /// Set by the host beside D336 whenever the captured weapon table is on; off, the gate reads
    /// the sheet as before.
    /// </summary>
    public bool ShippedRefireGate { get; init; }

    /// <summary>
    /// Use the captured mode's reload clock when the host ships the captured weapon table.
    /// Otherwise the server completes reloads against the generated table's August sheet clock.
    /// </summary>
    public bool ShippedReloadTime { get; init; }

    /// <summary>Derived from the session's weapon profile by the host.</summary>
    public bool Z1LiveGunplay { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON</b> (<c>CRANBERRY_WEAPON_FIRE_HINT=0</c> is the revert). Decode
    /// <c>82 20 WeaponFireHint</c> into its fields and cross-check it against the shot this server
    /// accepted, instead of naming it and printing its hex.
    /// <para>
    /// It never spends a round. The hint is the DIRECTION half of a trigger pull whose ammunition
    /// the paired <c>82 03 Fire</c> already paid for; treating it as a second shot would double the
    /// magazine cost of every pull in the game.
    /// </para>
    /// </summary>
    public bool ActOnWeaponFireHint { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D333, docs/121 §5)</b> - <c>CRANBERRY_MAGAZINE_RESYNC_FROM_BAG=0</c> is the
    /// revert. The magazine <see cref="MagazineResync"/> adopts is <b>paid for out of the bag</b>:
    /// the rounds come off the shooter's own stack of the weapon's calibre (<c>11 03</c> /
    /// <c>11 04</c> go out for the stack), and when the bag holds none the adoption is withheld and
    /// the shot is refused exactly as before D223.
    /// <para>
    /// <b>The evidence.</b> Both sessions that ever needed the resync had picked ammunition up
    /// first: 17:51 on 2026-09-03 (60 x 7.62), and 21:35 the same night, where the owner took two
    /// boxes of .223 (21:35:41, 21:35:43), drew the AR-15, and the first trigger pull at 21:35:46
    /// was answered <i>"this server had 0 and the client is not dry, so AR-15 is now 30/30"</i>
    /// while the bag still held all 60 - ninety rounds from sixty picked up, and no
    /// <c>82 07 ReloadRequest</c> anywhere. Whichever side loads the first magazine, retail
    /// ammunition is conserved; this makes the server's copy of it so.
    /// </para>
    /// <para>
    /// A throwable (no ammunition item) still chambers its one free round: it is its own round.
    /// </para>
    /// </summary>
    public bool MagazineResyncFromBag { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D334, docs/121 §6)</b> - <c>CRANBERRY_RELOAD_TIMED=0</c> is the revert. The
    /// <c>82 08 Reload</c> that fills the magazine - and the stack's own <c>11 03</c> /
    /// <c>11 04</c> - go out <b>after</b> the weapon's own <c>RELOAD_TIME_MS</c> (the client's sheet,
    /// [P]) instead of inside the request, so the hotbar counter fills when the reload animation
    /// completes rather than the instant R is pressed; a pump shotgun's shells land one per
    /// <c>RELOAD_TIME_MS</c> (800 ms). The mechanism is the owner's own Z1 (<c>ReloadDueAtMs =
    /// now + profile.ReloadMs</c>, <c>ZoneCombat.cs:1407</c>, click-proven on 1087; D53). The
    /// bag and server magazine change only when each step completes. Interrupting the reload
    /// cancels future steps without spending their rounds (docs/reload-completion-20260904.md).
    /// </summary>
    public bool TimedReload { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (docs/120, D305-D310)</b> - <c>CRANBERRY_THROWABLES_ARM=0</c> is the revert.
    /// An <c>82 03 Fire</c> on a throwable is a THROW, not a shot: no magazine, no refire gate,
    /// one grenade leaves the stack, the projectile is remembered until the client's own
    /// <c>82 19 GuidedExplode</c> says where it went off - or the fuse plus
    /// <see cref="Generated.Rulings.Throwables.FallbackGraceMs"/> passes and the server detonates
    /// it at the throw point (D306). <c>82 26 GrenadeBounceReport</c> is decoded and counted. Off
    /// restores wave 17: a grenade's <c>82 03</c> goes down the gun path (and is refused on its
    /// 1-round magazine from the second throw on), <c>82 19</c> / <c>82 26</c> are seen and not
    /// acted on.
    /// </summary>
    public bool Throwables { get; init; } = true;

    /// <summary>
    /// <b>DEFAULT ON (D308)</b> - <c>CRANBERRY_THROWABLE_DAMAGE=0</c> is the revert. A detonation
    /// hurts what is inside its radius: practice targets, other players, and the thrower himself
    /// unless the detonation was the server's own fallback. Off keeps the effects and the clouds
    /// and hurts nobody.
    /// </summary>
    public bool ThrowableDamage { get; init; } = true;

    /// <summary>
    /// The floor under the rate-of-fire gate, in milliseconds. A Cranberry design value: one 25 Hz
    /// frame, chosen to sit below every real <c>REFIRE_TIME_MS</c> in the August sheet (120 ms on
    /// the AR-15, the fastest gun) so it can never gate a legitimate weapon, and above zero so a
    /// sheet row of 0 cannot open a firehose. It replaces the 50 ms the owner's server transcribed
    /// from elsewhere.
    /// </summary>
    public int RefireFloorMs { get; init; } = 40;

    /// <summary>Bounded receive-clock tolerance; early shots repay the time on subsequent shots.</summary>
    public int RefireJitterMs { get; init; }

    /// <summary>
    /// How long a fire hint stays live. A hit naming a projectile older than this is refused: the
    /// anti-replay window. The owner runs 10 s and does not derive it from muzzle velocity, and the
    /// reason transfers - neither server has ever seen a real hit report, so a tighter gate would
    /// only be a new way to refuse a legitimate hit.
    /// </summary>
    public long FireHintLifetimeMs { get; init; } = 10_000;

    /// <summary>
    /// The registration range gate, in world units. The victim's position is the one rewound out of
    /// <c>PoseHistory</c> at the shot's tick, not the live one.
    /// </summary>
    public double MaxHitDistance { get; init; } = 350;

    /// <summary>
    /// Body damage, in health units, for a weapon with no row in <see cref="RetailBalance"/>. 2,000
    /// = 20 HP = a five-shot kill, deliberately between the pistol floor (18 HP) and the rifle band
    /// (25-30 HP): an untuned weapon can be neither a one-shot nor a pea-shooter. A Cranberry
    /// design value - the alternative the owner's server uses is a third-party balance table, which
    /// may not cross.
    /// </summary>
    public int UnmappedWeaponBodyUnits { get; init; } = 2_000;

    /// <summary>
    /// Spawn a practice dummy in front of the player at world entry, so shooting can be verified in
    /// a single-player session. See <see cref="PracticeTargetPack"/> for its NPC representation.
    /// <para>
    /// Opt in with <c>CRANBERRY_PRACTICE_TARGET=1</c> for shooting diagnostics. This static,
    /// undressed actor otherwise appears on every landing at the last parachute height and looks
    /// like a floating duplicate player. Ordinary matches must not create a diagnostic target.
    /// </para>
    /// </summary>
    public bool PracticeTarget { get; init; }

    /// <summary>How many dummies. Fanned out along the spawner's facing.</summary>
    public int PracticeTargetCount { get; init; } = 1;

    public bool PracticeTargetFullKit { get; init; }

    /// <summary>Distance in front of the spawner, in world units. The owner's own number.</summary>
    public float PracticeTargetDistance { get; init; } = 4f;

    /// <summary>Health of one dummy, on the 10,000-unit bar - the same bar a player has.</summary>
    public int PracticeTargetHealth { get; init; } = 10_000;

    /// <summary>How long the body lies there before it is removed. The owner's own 10 s.</summary>
    public long PracticeTargetDespawnAfterDeathMs { get; init; } = 10_000;

    /// <summary>The shipped defaults.</summary>
    public static CombatOptions Default { get; } = new();

    /// <summary>
    /// Reads the switches from the environment. Only the exact string <c>"1"</c> or <c>"0"</c>
    /// moves a switch, so a typo leaves the default rather than silently changing how a match plays.
    /// </summary>
    public static CombatOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return new CombatOptions
        {
            Enabled = Switch(read, EnabledVariable, @default: true),
            EnableCombatDamage = Switch(read, DamageVariable, @default: true),
            SendHitMarker = Switch(read, HitMarkerVariable, @default: true),
            Layout = Switch(read, LayoutVariable, @default: true)
                ? WeaponFireLayout.Z1Candidate
                : WeaponFireLayout.Unknown,
            PracticeTarget = Switch(read, PracticeTargetVariable, @default: false),
            MeleeDamage = Switch(read, MeleeDamageVariable, @default: true),
            MeleeOnTrigger = Switch(read, MeleeOnTriggerVariable, @default: true),
            SendAbilityManager = Switch(read, AbilityManagerVariable, @default: true),
            SwitchFireModeReply = Switch(read, SwitchFireModeReplyVariable, @default: true),
            MagazineResync = Switch(read, MagazineResyncVariable, @default: true),
            ActOnWeaponFireHint = Switch(read, WeaponFireHintVariable, @default: true),
            MagazineResyncFromBag = Switch(read, MagazineResyncFromBagVariable, @default: true),
            TimedReload = Switch(read, TimedReloadVariable, @default: true),
            Throwables = Switch(read, ThrowablesVariable, @default: true),
            ThrowableDamage = Switch(read, ThrowableDamageVariable, @default: true),
            Ammo = AmmoOptions.FromEnvironment(read),
        };
    }

    /// <summary>A one-line boot-log description, so a play-test can never guess which arms ran.</summary>
    public string Describe() =>
        $"combat: 0x82={On(Enabled)} layout={Layout} damage={On(EnableCombatDamage)} "
        + $"hitMarker={On(SendHitMarker)} melee 0xa0={On(MeleeDamage)} "
        + $"meleeOnTrigger={On(MeleeOnTrigger)} "
        + $"abilityManager={On(SendAbilityManager)} "
        + $"fireModeReply={On(SwitchFireModeReply)} "
        + $"magazineResync={On(MagazineResync)}{(MagazineResync ? $"(fromBag={On(MagazineResyncFromBag)})" : string.Empty)} "
        + $"fireHint={On(ActOnWeaponFireHint)} timedReload={On(TimedReload)} "
        + $"practiceTarget={On(PracticeTarget)}"
        + (PracticeTarget ? $" x{PracticeTargetCount} @ {PracticeTargetDistance:0.#}u" : string.Empty)
        + $" throwables={On(Throwables)} throwableDamage={On(Throwables && ThrowableDamage)}";

    private static string On(bool value) => value ? "ON" : "off";

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
