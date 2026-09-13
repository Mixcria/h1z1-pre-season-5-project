using System.Numerics;

namespace Cranberry.Zone.Gas;

/// <summary>
/// Drives one match's gas from a caller-supplied clock. It is deterministic (everything it decides
/// is a function of the schedule, the start time and the tick time), allocation-free in the steady
/// state (the per-tick damage list is one reused buffer) and does no packet I/O at all — the
/// caller maps <see cref="GasTickResult"/> onto <see cref="GasPackets"/> sends. Ticking faster than
/// <see cref="GasSettings.TickPeriodMs"/> is expected: the damage tick and the safe-zone re-send
/// are rate-limited internally, so a 100 ms or 250 ms host timer produces the same match as a
/// 1000 ms one.
/// </summary>
public sealed class GasController
{
    private readonly GasSettings _settings;
    private GasDamageTick[] _damage = new GasDamageTick[8];
    private readonly Dictionary<int, GasToxicity> _toxicity = new();
    private GasSchedule? _schedule;
    private long _startMs;
    private long _nextDamageAtMs;
    private long _nextUpdateAtMs;
    private int _phaseIndex;
    private bool _closing;
    private bool _finished;
    private long? _pausedAtMs;

    public GasController(GasSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;
    }

    public GasSettings Settings => _settings;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool Running => _schedule is not null;
    public bool Paused => Running && _pausedAtMs.HasValue;
    public long? PausedAtMs => _pausedAtMs;

    /// <summary>The plan of the running match; null before <see cref="Start"/>.</summary>
    public GasSchedule? Schedule => _schedule;

    /// <summary>The clock value <see cref="Start"/> was given — match clock zero.</summary>
    public long StartMs => _startMs;

    /// <summary>
    /// Opens a match. <paramref name="matchClockMs"/> is the caller's own monotonic clock at the
    /// moment the match starts (the <c>ce 16</c> StartMatch send); <paramref name="seed"/> makes
    /// the circle placement reproducible.
    /// </summary>
    public GasSchedule Start(long matchClockMs, ulong seed)
    {
        _schedule = GasSchedule.Create(_settings, seed);
        _startMs = matchClockMs;
        _nextDamageAtMs = matchClockMs + _settings.TickPeriodMs;
        _nextUpdateAtMs = matchClockMs;
        _phaseIndex = 0;
        _closing = false;
        _finished = false;
        _pausedAtMs = null;
        _toxicity.Clear();
        return _schedule;
    }

    /// <summary>Opens a match with <see cref="GasSettings.Seed"/>.</summary>
    public GasSchedule Start(long matchClockMs) => Start(matchClockMs, _settings.Seed);

    /// <summary>Closes the match; further ticks produce nothing until the next <see cref="Start"/>.</summary>
    public void Stop()
    {
        _schedule = null;
        _pausedAtMs = null;
        _toxicity.Clear();
    }

    /// <summary>Freeze geometry, phase time, damage and toxicity without losing the plan.</summary>
    public bool Pause(long nowMs)
    {
        if (!Running || Paused) return false;
        _pausedAtMs = nowMs;
        return true;
    }

    /// <summary>Continue at the frozen instant; never replay damage missed during the pause.</summary>
    public bool Resume(long nowMs)
    {
        if (!Running || _pausedAtMs is not long pausedAt) return false;
        _startMs += Math.Max(0, nowMs - pausedAt);
        _pausedAtMs = null;
        _nextUpdateAtMs = nowMs;
        _nextDamageAtMs = nowMs + _settings.TickPeriodMs;
        return true;
    }

    /// <summary>The authoritative resource-611 value; HUD options cannot change it.</summary>
    public uint ToxicityForPlayer(int playerIndex) =>
        _toxicity.TryGetValue(playerIndex, out GasToxicity meter) ? meter.Value : 0;

    /// <summary>Clear exposure when a caller reuses a player slot for a different character.</summary>
    public void ForgetPlayer(int playerIndex) => _toxicity.Remove(playerIndex);

    /// <summary>Advance to the next scheduled reveal, movement or close, without replaying damage ticks.</summary>
    public bool AdvanceToNextEvent(long nowMs)
    {
        if (_schedule is not GasSchedule schedule || Paused) return false;
        long clock = MatchClockAt(nowMs);
        long next = schedule.NextEventAtMs(clock);
        if (next == long.MaxValue) return false;
        _startMs -= next - clock;
        _nextUpdateAtMs = nowMs;
        _nextDamageAtMs = nowMs + _settings.TickPeriodMs;
        return true;
    }

    /// <summary>Milliseconds since the match started; 0 while stopped.</summary>
    public long MatchClockAt(long nowMs) => _schedule is null ? 0 : Math.Max(0, (_pausedAtMs ?? nowMs) - _startMs);

    /// <summary>The circle the gas is damaging against at a wall-clock time.</summary>
    public GasCircle ActiveCircleAt(long nowMs) =>
        _schedule is null ? default : _schedule.ActiveCircleAt(MatchClockAt(nowMs));

    /// <summary>Whether a world position is inside the damaging circle at a wall-clock time.</summary>
    public bool IsInsideSafeZone(long nowMs, Vector3 position) =>
        _schedule is null || _schedule.IsInsideSafeZone(MatchClockAt(nowMs), position);

    /// <summary>
    /// Advances the match to <paramref name="nowMs"/> and reports what the caller should send.
    /// The returned spans point at controller-owned storage and are valid until the next call.
    /// </summary>
    public GasTickResult Tick(long nowMs, ReadOnlySpan<PlayerSample> players)
    {
        if (_schedule is not GasSchedule schedule || Paused)
        {
            return default;
        }

        long clock = Math.Max(0, nowMs - _startMs);
        GasTickEvents events = GasTickEvents.None;
        GasCircle revealed = default;
        uint closesInMs = 0;

        // One walk of the ladder per tick, threaded down. Tick used to reach PhaseIndexAt five
        // separate ways (ActiveCircleAt, PhaseIndexAt, IsClosingAt, DamagePerTickAt); the loops are
        // ten elements long so the cost was nanoseconds, but one pass is also one statement of what
        // this tick is looking at (docs/77 S9.1).
        int phaseIndex = schedule.PhaseIndexAt(clock);
        GasPhase? current = phaseIndex == 0 ? null : schedule.Phase(phaseIndex);
        GasCircle active = current is GasPhase live ? live.CircleAt(clock) : schedule.InitialCircle;

        if (phaseIndex != _phaseIndex)
        {
            _phaseIndex = phaseIndex;
            _closing = false;
            events |= GasTickEvents.PhaseChanged;
            if (current is GasPhase phase)
            {
                revealed = phase.Target;
                closesInMs = (uint)Math.Max(0, phase.ClosedAtMs - clock);
                events |= GasTickEvents.RevealSafeZone;

                // Reset the re-send rate limiter onto the new phase. It cannot fire on this tick —
                // IsClosingAt needs clock >= ShrinkStartAtMs, which is a whole warning head away —
                // but this stops the previous phase's limiter from delaying the first update of
                // the new one.
                _nextUpdateAtMs = nowMs;
            }
        }

        bool closing = current is GasPhase moving
            && clock >= moving.ShrinkStartAtMs
            && clock < moving.ClosedAtMs;

        // The false-to-true edge of "this phase's ring is travelling". It is what turns the HUD
        // label from "Gas advances in" to "Gas is spreading!", and under GasPreMoveRing.None it is
        // also the first moment a ce 01 may be drawn at all (docs/77 S5, S6).
        if (closing && !_closing)
        {
            events |= GasTickEvents.ShrinkStarted;
        }

        if (_closing && !closing)
        {
            // The last scheduled 500 ms update may precede the exact close. Send the endpoint
            // even during an inter-phase hold so the visible wall reaches the damaging circle.
            events |= GasTickEvents.SafeZoneUpdate;
        }

        _closing = closing;

        if (closing && nowMs >= _nextUpdateAtMs)
        {
            events |= GasTickEvents.SafeZoneUpdate;
            _nextUpdateAtMs = nowMs + _settings.SafeZoneUpdateIntervalMs;
        }

        int damageCount = 0;
        if (nowMs >= _nextDamageAtMs)
        {
            // Never replay a backlog of missed ticks after a stall: one tick per pass, next tick a
            // full period from now.
            _nextDamageAtMs = nowMs + _settings.TickPeriodMs;

            // Nothing is lethal before the gas is real: under GasPreMoveRing.None the play-area
            // boundary is neither drawn nor burning until phase 1's ring first moves, and one
            // predicate answers both questions so they cannot come apart (docs/77 S6).
            bool lethal = schedule.IsLethalAt(clock);
            EnsureDamageCapacity(players.Length);
            foreach (PlayerSample player in players)
            {
                if (!player.Alive)
                {
                    _toxicity.Remove(player.PlayerIndex);
                    continue;
                }

                bool inGas = lethal && !active.Contains(player.Position);
                _toxicity.TryGetValue(player.PlayerIndex, out GasToxicity meter);
                meter.Tick(_settings, inGas, _settings.TickPeriodMs);
                if (meter.Value == 0)
                {
                    // Keep fractional low-rate accumulation even while the rounded wire value is 0.
                    if (inGas) _toxicity[player.PlayerIndex] = meter;
                    else _toxicity.Remove(player.PlayerIndex);
                }
                else
                {
                    _toxicity[player.PlayerIndex] = meter;
                }

                if (inGas)
                {
                    uint amount = _settings.DamageForPhase(
                        phaseIndex, meter.Value >= _settings.ToxicityMaxValue);
                    _damage[damageCount++] = new GasDamageTick(player.PlayerIndex, amount);
                }
            }

            if (damageCount > 0)
            {
                events |= GasTickEvents.DamageTick;
            }
        }

        if (!_finished && clock >= schedule.FinishedAtMs)
        {
            _finished = true;
            events |= GasTickEvents.Finished | GasTickEvents.SafeZoneUpdate;
        }

        return new GasTickResult(
            events,
            phaseIndex,
            revealed,
            closesInMs,
            active,
            _damage.AsSpan(0, damageCount));
    }

    private void EnsureDamageCapacity(int count)
    {
        if (_damage.Length < count)
        {
            _damage = new GasDamageTick[Math.Max(count, _damage.Length * 2)];
        }
    }
}
