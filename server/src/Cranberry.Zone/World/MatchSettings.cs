using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.World;

/// <summary>
/// Everything one match is configured with. Values that already exist elsewhere in the tree are
/// mirrored, not restated: <see cref="Gas"/> is the existing <see cref="GasSettings"/> record whose
/// numbers come from D23 and validate themselves, and <see cref="StagingSpawn"/> defaults to the
/// live-proven <c>ZoneOptions.StagingSpawn</c>.
/// </summary>
public sealed record MatchSettings
{
    /// <summary>The August battle-royale map.</summary>
    public string ZoneName { get; init; } = "Z2";

    public int MaxPlayers { get; init; } = 150;

    public int EntityCapacity { get; init; } = 4096;

    /// <summary>Live-proven Z2 staging spawn (<c>ZoneOptions.cs:86</c>).</summary>
    public Vector4 StagingSpawn { get; init; } = new(-233.83f, 506.36f, -4892.03f, 1f);

    public float DropAltitude { get; init; } = 1500f;

    /// <summary>Players needed before the lobby countdown starts.</summary>
    public int MinPlayersToStart { get; init; } = 1;

    public int LobbyCountdownMs { get; init; } = 20_000;

    /// <summary>How long <see cref="MatchPhase.Dropping"/> lasts before the match goes live.</summary>
    public int DropDurationMs { get; init; } = 60_000;

    /// <summary>How long <see cref="MatchPhase.Ending"/> holds before the match is finished.</summary>
    public int EndingDurationMs { get; init; } = 10_000;

    public ulong Seed { get; init; } = 0x5A32_4741_5320_0001UL;

    /// <summary>D23's gas numbers; already validated by <see cref="GasSettings.Validate"/>.</summary>
    public GasSettings Gas { get; init; } = new();

    /// <summary>Rollback gate for migration step 5.</summary>
    public bool GasEnabled { get; init; }

    /// <summary>Rollback gate for migration step 7: nothing is relayed to peers until this is on.</summary>
    public bool RelayEnabled { get; init; }

    /// <summary>Relay at 10 Hz.</summary>
    public int RelayStride { get; init; } = 2;

    /// <summary>Interest at 5 Hz.</summary>
    public int InterestStride { get; init; } = 4;

    /// <summary>Bytes of pose relay one viewer may receive per relay pass.</summary>
    public int RelayBudgetBytes { get; init; } = 16 * 1024;

    public int InboundBudgetPerTick { get; init; } = 512;

    public int CommandCapacity { get; init; } = 1024;

    public int CommandArenaBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Server-side sanity ceiling for an accepted pose, in metres per second. Cranberry's own
    /// number, not a client fact: it only has to be loose enough never to reject honest play
    /// (a 1,500 m drop is far faster than any ground movement) and tight enough that a teleport
    /// is visible in the violation counter.
    /// </summary>
    public float MaxSpeedMetresPerSecond { get; init; } = 150f;

    /// <summary>
    /// Starting health, and the maximum <c>11 01 Hitpoints</c> reports — the client divides by it
    /// (<c>(current*100)/max</c>, docs/16 §4d), so it has to be the bar the player actually has.
    /// docs/22 §6.3's pseudo-code sends <c>Gas.MaxHitpoints</c> there instead; the two are the same
    /// 10,000 by default and must be kept equal, because <see cref="GasSettings.DamageForPhase"/>
    /// expresses the damage curve as a fraction of <see cref="GasSettings.MaxHitpoints"/> — lowering
    /// only this field silently multiplies the gas rate the player sees.
    /// </summary>
    public int StartingHealth { get; init; } = 10_000;

    /// <summary>
    /// docs/81: the switches the fire/hit/damage loop runs under. Defaults are the behaviour the
    /// owner asked for - decode the <c>0x82</c> family, gate every field, apply the retail damage
    /// table and send the hit marker.
    /// </summary>
    public CombatOptions Combat { get; init; } = CombatOptions.Default;

    public static MatchSettings Default { get; } = new();
}
