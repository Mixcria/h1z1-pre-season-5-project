using Cranberry.Zone.World;

namespace Cranberry.Zone.Match;

/// <summary>
/// Lane 3C's switches: everything that lets <b>two sessions on one host see each other</b>.
///
/// <para>
/// Every arm is separately revertible and each one names the packet it puts on the wire, so a
/// two-client run that goes wrong has a one-word revert rather than a bisect. The defaults follow
/// the lane rule: <b>on where the packet is [P]</b> (docs/100 — <c>d5</c>, <c>94 01</c>,
/// <c>82 15</c>, <c>0x78</c> and <c>0f 01</c> are all proven writers) and off nowhere, because
/// <c>d9 LightweightToFullPc</c> — the one underived packet in the family — is refused by
/// <see cref="PeerSpawnWriter.LightweightToFullPc"/> itself and needs no switch.
/// </para>
///
/// <para>
/// <b>Provably a no-op with one player.</b> The registry sweep skips
/// <c>ReferenceEquals(subject, viewer)</c> and the relay never relays to the reporter, exactly as
/// <c>VehiclePoseBroadcast</c> does — so with a single session on the host not one byte of this
/// lane reaches the wire, which is what makes shipping it on by default safe.
/// </para>
/// <para>
/// <b>Why it lives beside <see cref="MatchEndOptions"/> and not under <c>World/</c>.</b> It is a
/// match-level configuration record, not a world system, and <c>WorldSeamTests.
/// NoTypeUnderWorldReadsAWallClock</c> forbids <c>Environment.</c> anywhere in that folder — a rule
/// worth keeping exactly as it is, because the seam it protects is what lets the world systems be
/// ticked from a supplied clock in a test.
/// </para>
/// </summary>
public sealed record PeerOptions
{
    /// <summary>
    /// Environment switch for <see cref="SelfTransientId"/> — docs/100 §7.
    /// <c>CRANBERRY_SELF_TRANSIENT_ID=0</c> puts the self record's <c>+0xe0</c> varint back to 0.
    /// </summary>
    public const string SelfTransientIdVariable = "CRANBERRY_SELF_TRANSIENT_ID";

    /// <summary>
    /// Environment switch for <see cref="Spawn"/>. <c>CRANBERRY_PEER_SPAWN=0</c> is the one-word
    /// revert for the whole enter/leave burst: no <c>d5</c>, no remote <c>94 01</c>, no
    /// <c>82 15</c>, no <c>0f 01</c>.
    /// </summary>
    public const string SpawnVariable = "CRANBERRY_PEER_SPAWN";

    /// <summary>
    /// Environment switch for <see cref="Relay"/>. <c>CRANBERRY_PEER_RELAY=0</c> leaves peers
    /// spawned but frozen at the pose they entered on — which is also the useful bisect when a
    /// two-client run shows a peer in the wrong place.
    /// </summary>
    public const string RelayVariable = "CRANBERRY_PEER_RELAY";

    /// <summary>Diagnostic A/B only: restore the September 9 merged movement relay.</summary>
    public const string CoalesceMovementVariable = "CRANBERRY_PEER_COALESCE_MOVEMENT";

    /// <summary>
    /// Environment switch for <see cref="SharedGasSeed"/>. <c>CRANBERRY_SHARED_GAS_SEED=0</c>
    /// restores the pre-lane behaviour: every session draws its own gas plan and the two players
    /// see two different circles.
    /// </summary>
    public const string SharedGasSeedVariable = "CRANBERRY_SHARED_GAS_SEED";

    /// <summary>Environment switch for <see cref="FireRelay"/> (docs/121 §7, D335). Default ON.</summary>
    public const string FireRelayVariable = "CRANBERRY_PEER_FIRE_RELAY";

    /// <summary>
    /// Environment switch for <see cref="ProjectileLaunch"/> (docs/125 §6, D315). Default ON;
    /// <c>CRANBERRY_PROJECTILE_LAUNCH_RELAY=0</c> is the revert, and puts docs/121 row 52 back
    /// ("never sent").
    /// </summary>
    public const string ProjectileLaunchVariable = "CRANBERRY_PROJECTILE_LAUNCH_RELAY";

    /// <summary>
    /// Environment switch for <see cref="Redress"/>. <c>CRANBERRY_PEER_REDRESS=0</c> puts the
    /// relay back to enter-only: a viewer keeps the dress and the gun a peer had when it came into
    /// view, however many times that peer picks up, draws, stows or drops afterwards.
    /// </summary>
    public const string RedressVariable = "CRANBERRY_PEER_REDRESS";

    /// <summary>
    /// docs/100 §7: write <c>TransientIdTable.LocalPlayer</c> (1) into the self record's
    /// <c>+0xe0</c> varint instead of 0. The client copies that value to <c>world+0x324f8</c> and
    /// uses it as "is this packet about me?" for the rest of the session
    /// (<c>FUN_140af3950</c> L545), so a self id of 0 makes any record with a zero id look like
    /// self-traffic. <b>Zero bytes of length change</b> — both 0 and 1 are one-byte client varints
    /// (<c>00</c> and <c>04</c>) — but the content moves, which is why it is a switch.
    /// </summary>
    public bool SelfTransientId { get; init; } = true;

    /// <summary>
    /// Send the enter/leave burst at all: <c>d5 AddLightweightPc</c>, the peer's
    /// <c>94 01 SetCharacterEquipment</c>, <c>82 15 01 Reset</c> (+ <c>82 15 02 AddWeapon</c> for a
    /// held gun) on enter, and <c>0f 01 RemovePlayer</c> on leave.
    /// </summary>
    public bool Spawn { get; init; } = true;

    /// <summary>
    /// Re-frame each decoded channel-2 record as <c>78 | varint transientId | record</c> for every
    /// viewer that has the mover spawned, and forward the mover's own <c>0f 20 WeaponStance</c>
    /// the same way (docs/100 §5).
    /// </summary>
    public bool Relay { get; init; } = true;

    /// <summary>
    /// Disabled by default. The native client extrapolates its current record then merges only
    /// present fields (FUN_140b14d30 -> FUN_142335730). Expanding a look-only delta with an older
    /// position but the new timestamp changes that operation. Preserve original sparse records
    /// until a coalescer can preserve each field's time semantics, not just its last bytes.
    /// </summary>
    public bool CoalesceMovement { get; init; }

    /// <summary>
    /// Seed a second session's <see cref="Gas.GasController"/> from the first session's
    /// <c>(seed, matchClockMs)</c> pair so both players see the same circle (S1 §5.4 / D67 minimum
    /// fix). <see cref="Gas.GasController"/> is a pure function of exactly those two values, so
    /// sharing them makes the two schedules byte-identical without any new gas API.
    /// </summary>
    public bool SharedGasSeed { get; init; } = true;

    /// <summary>
    /// <b>D322 (docs/106 §13).</b> When a character's own <c>94 01</c> dress changes - a pickup, a
    /// hotbar draw or stow, a drop, a shred - re-send that dress under its guid to every viewer
    /// that already has it spawned, and when the gun in its hand changed, the <c>82 15 01</c> /
    /// <c>82 15 02</c> pair the enter burst sends, so the viewer's remote-weapon state follows the
    /// hand. Before this the enter burst was the only carrier of a peer's dress: a bystander saw
    /// the outfit and the gun a player had at the moment he came into view and nothing after -
    /// which is why a skinned rifle drawn in front of a friend stayed, on the friend's screen,
    /// whatever was in the hand 310 m ago.
    /// </summary>
    public bool Redress { get; init; } = true;
    /// <b>DEFAULT ON (D335, docs/121 §7)</b> - <c>CRANBERRY_PEER_FIRE_RELAY=0</c> is the revert.
    /// Relay a shooter's trigger to everyone who has him spawned as
    /// <c>82 15 / 04 / 01 RemoteWeaponUpdate.FireState</c> (docs/20 §3c, [P]): a <b>start</b> with
    /// the aim point of every corroborated <c>82 20 WeaponFireHint</c>, a <b>stop</b> on the
    /// <c>82 01</c> trigger-up. That packet is what drives a proxied weapon's own firing loop
    /// (<c>FUN_1414986c0</c> -> <c>FUN_1411b3350</c>), i.e. the muzzle flash and the gunshot a
    /// bystander sees and hears. Requires <see cref="Relay"/>. Provably a no-op with one player.
    /// </summary>
    public bool FireRelay { get; init; } = true;

    /// <summary>
    /// <b>D315 (docs/125 §6), DEFAULT ON</b> - <c>CRANBERRY_PROJECTILE_LAUNCH_RELAY=0</c> reverts.
    /// One <c>82 15 / 04 / 0b RemoteWeaponUpdate.ProjectileLaunch</c> with a payload of <b>12 zero
    /// bytes</b> to everyone who has the shooter spawned, on every accepted <c>82 03 Fire</c> -
    /// shot and throw alike.
    /// <para>
    /// The layout is [P] (docs/20 §3d: <c>u32 projectileId; u64</c>, handed to the remote spawn
    /// <c>FUN_140c7bb60</c>). docs/121 row 52 had it as "never sent, its u64 param block is
    /// [BLOCKED]" and D310 sent it on a throw only, with the client's real projectile id. D312
    /// settles both: the owner's own server sends <c>new byte[12]</c> on every shot and every
    /// throw (<c>ZoneCombat.cs:1069</c>, <c>:1119</c>), so the zero is not a guess about a
    /// [BLOCKED] field - it is the value a working server ships, and shipping the id instead was
    /// the guess.
    /// </para>
    /// <para>
    /// Requires <see cref="Relay"/> and the shooter's registered right-hand instance (the viewers'
    /// <c>82 15 02 AddWeapon</c> row). <b>Provably a no-op with one player</b>: with no viewers
    /// there is nobody to send it to.
    /// </para>
    /// </summary>
    public bool ProjectileLaunch { get; init; } = true;

    /// <summary>Forward every available pose by default under the September 6 responsiveness
    /// request. The byte budget and reliable batching still bound the fan-out.</summary>
    public int RelayStride { get; init; } = 1;

    /// <summary>
    /// Bytes of pose one relay pass may write, mirroring <c>MatchSettings.RelayBudgetBytes</c>.
    /// One frame is <c>1 + varint + record</c>. 16 KiB accommodates all 149 other players
    /// at measured record sizes. The round-robin cursor still bounds exceptionally large records;
    /// transport batching groups the small messages within the existing 512-byte MTU.
    /// </summary>
    public int RelayBudgetBytes { get; init; } = 16 * 1024;

    /// <summary>The shipped defaults.</summary>
    public static PeerOptions Default { get; } = new();

    /// <summary>
    /// Reads the switches from the environment. Only the exact strings <c>"1"</c> and <c>"0"</c>
    /// move a switch, so a typo leaves the default rather than silently changing what two clients
    /// send each other.
    /// </summary>
    public static PeerOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return new PeerOptions
        {
            SelfTransientId = Switch(read, SelfTransientIdVariable, @default: true),
            Spawn = Switch(read, SpawnVariable, @default: true),
            Relay = Switch(read, RelayVariable, @default: true),
            CoalesceMovement = Switch(read, CoalesceMovementVariable, @default: false),
            SharedGasSeed = Switch(read, SharedGasSeedVariable, @default: true),
            Redress = Switch(read, RedressVariable, @default: true),
            FireRelay = Switch(read, FireRelayVariable, @default: true),
            ProjectileLaunch = Switch(read, ProjectileLaunchVariable, @default: true),
        };
    }

    /// <summary>One boot-log line, so a two-client run can never guess which arms were live.</summary>
    public string Describe() =>
        $"peers: selfTransientId={(SelfTransientId ? $"{TransientIdTable.LocalPlayer}" : "0")} "
        + $"spawn d5/94-01/82-15={On(Spawn)} relay 0x78={On(Relay)} "
        + $"stride={RelayStride} budget={RelayBudgetBytes}B mergedPoseExperiment={On(CoalesceMovement)} "
        + $"redress 94-01/82-15={On(Redress)} "
        + $"fireRelay 82 15 04 01={On(Relay && FireRelay)} "
        + $"projectileLaunch 82 15 04 0b={On(Relay && ProjectileLaunch)} "
        + $"sharedGasSeed={On(SharedGasSeed)}; "
        + $"interest {ObserverView.PlayerEnterMetres:F0} m enter / "
        + $"{ObserverView.PlayerLeaveMetres:F0} m leave (D156); "
        + "d9 LightweightToFullPc is REFUSED (docs/100 §3)";

    private static string On(bool value) => value ? "ON" : "off";

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
