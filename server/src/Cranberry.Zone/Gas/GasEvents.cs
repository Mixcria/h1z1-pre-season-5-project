using System.Numerics;

namespace Cranberry.Zone.Gas;

/// <summary>What one <see cref="GasController.Tick"/> is telling the caller to send.</summary>
[Flags]
public enum GasTickEvents
{
    None = 0,

    /// <summary>A new circle became visible: send <c>ce 01</c> (ring) and <c>ce 02</c> (safe zone).</summary>
    RevealSafeZone = 1 << 0,

    /// <summary>The active circle moved: re-send <c>ce 01</c> with it (rate-limited).</summary>
    SafeZoneUpdate = 1 << 1,

    /// <summary>At least one player is outside the active circle; see <see cref="GasTickResult.Damage"/>.</summary>
    DamageTick = 1 << 2,

    /// <summary>The phase number changed; <see cref="GasTickResult.PhaseIndex"/> carries the new one.</summary>
    PhaseChanged = 1 << 3,

    /// <summary>The last phase has just closed. Raised once; the final circle stays lethal.</summary>
    Finished = 1 << 4,

    /// <summary>
    /// This phase's ring has just begun to travel. Raised once on the false-to-true edge of "the
    /// circle is between two radii" and cleared by the next <see cref="PhaseChanged"/>. It drives
    /// the HUD label 14152 "Gas is spreading!", and under <see cref="GasPreMoveRing.None"/> it is
    /// the first tick on which a <c>ce 01</c> may be drawn at all (docs/77 sections 5 and 6).
    /// </summary>
    ShrinkStarted = 1 << 5,
}

/// <summary>One player as the controller sees them for a tick.</summary>
/// <param name="PlayerIndex">The caller's own index into its player list; echoed back on damage.</param>
/// <param name="Position">Latest authoritative world position (X/Z are compared, Y ignored).</param>
/// <param name="Alive">False for a dead or not-yet-landed player: no damage is produced for them.</param>
public readonly record struct PlayerSample(int PlayerIndex, Vector3 Position, bool Alive);

/// <summary>Health to remove from one player this tick.</summary>
public readonly record struct GasDamageTick(int PlayerIndex, uint Amount);

/// <summary>
/// The result of one controller tick. A <c>ref struct</c> so <see cref="Damage"/> can point at the
/// controller's own reused buffer — the steady state allocates nothing.
/// </summary>
public readonly ref struct GasTickResult
{
    internal GasTickResult(
        GasTickEvents events,
        int phaseIndex,
        GasCircle revealed,
        uint revealClosesInMs,
        GasCircle active,
        ReadOnlySpan<GasDamageTick> damage)
    {
        Events = events;
        PhaseIndex = phaseIndex;
        Revealed = revealed;
        RevealClosesInMs = revealClosesInMs;
        Active = active;
        Damage = damage;
    }

    /// <summary>Which of the events below are set.</summary>
    public GasTickEvents Events { get; }

    /// <summary>1-based phase currently revealed; 0 before the first reveal.</summary>
    public int PhaseIndex { get; }

    /// <summary>The newly revealed circle; only meaningful with <see cref="GasTickEvents.RevealSafeZone"/>.</summary>
    public GasCircle Revealed { get; }

    /// <summary>Milliseconds from this tick until the revealed circle is fully closed.</summary>
    public uint RevealClosesInMs { get; }

    /// <summary>The circle the server is damaging against right now.</summary>
    public GasCircle Active { get; }

    /// <summary>Per-player damage for this tick; empty unless <see cref="GasTickEvents.DamageTick"/> is set.</summary>
    public ReadOnlySpan<GasDamageTick> Damage { get; }

    public bool Has(GasTickEvents flag) => (Events & flag) != 0;
}
