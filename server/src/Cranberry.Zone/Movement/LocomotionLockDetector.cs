using System.Globalization;
using System.Numerics;

namespace Cranberry.Zone.Movement;

/// <summary>What the channel-2 stream currently says about the player's ability to walk.</summary>
public enum LocomotionLockState
{
    /// <summary>Not enough of the stream has been seen yet to say anything.</summary>
    Unknown,

    /// <summary>The player has moved with input recently: locomotion works.</summary>
    Clear,

    /// <summary>Input is live and the character is not moving: the wield freeze signature.</summary>
    Locked,
}

/// <summary>The knobs of <see cref="LocomotionLockDetector"/>, all measured from the captures.</summary>
/// <param name="Window">How far back samples are kept. Longer than <paramref name="MinimumSpan"/>
/// so a verdict is taken on a window that really is that long.</param>
/// <param name="MinimumSpan">The shortest span a LOCK may be declared over.</param>
/// <param name="MinimumInputRecords">How many records must carry live input.</param>
/// <param name="MinimumStoppedGroundedRecords">How many grounded records must carry the STOP bit.</param>
/// <param name="MinimumPositionRecords">How many position samples the window must contain.</param>
/// <param name="LockDisplacement">Horizontal metres the window must stay under to be a LOCK.</param>
/// <param name="ClearDisplacement">Horizontal metres that clear a LOCK.</param>
/// <param name="MinimumYawRecords">How many yaw readings the yaw sub-signal needs.</param>
/// <param name="MaximumPinnedYawValues">Distinct yaw values that still count as pinned.</param>
public sealed record LocomotionLockThresholds(
    TimeSpan Window,
    TimeSpan MinimumSpan,
    int MinimumInputRecords,
    int MinimumStoppedGroundedRecords,
    int MinimumPositionRecords,
    float LockDisplacement,
    float ClearDisplacement,
    int MinimumYawRecords,
    int MaximumPinnedYawValues)
{
    /// <summary>
    /// The values the three capture fixtures were tuned on: a 4 s buffer judged over at least 3 s,
    /// three records of live input, three grounded STOP records, 5 cm, 50 cm.
    /// </summary>
    public static LocomotionLockThresholds Default { get; } = new(
        Window: TimeSpan.FromSeconds(4),
        MinimumSpan: TimeSpan.FromSeconds(3),
        MinimumInputRecords: 3,
        MinimumStoppedGroundedRecords: 3,
        MinimumPositionRecords: 1,
        LockDisplacement: 0.05f,
        ClearDisplacement: 0.5f,
        MinimumYawRecords: 3,
        MaximumPinnedYawValues: 1);
}

/// <summary>Everything the detector measured over its window when it last spoke.</summary>
public readonly record struct LocomotionWindow(
    LocomotionLockState State,
    int Records,
    TimeSpan Span,
    float Displacement,
    int PositionRecords,
    int InputRecords,
    int StoppedGroundedRecords,
    int MovingGroundedRecords,
    int YawRecords,
    int DistinctYawValues,
    bool YawPinned)
{
    /// <summary>The host-log line, one per state change. Machine-greppable, human-readable.</summary>
    public string ToLogLine()
    {
        string verdict = State switch
        {
            LocomotionLockState.Locked => "LOCOMOTION LOCK",
            LocomotionLockState.Clear => "LOCOMOTION CLEAR",
            _ => "LOCOMOTION UNKNOWN",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{verdict} window={Span.TotalSeconds:F2}s records={Records} displacement={Displacement:F2}m "
                + $"positions={PositionRecords} input={InputRecords} stopGrounded={StoppedGroundedRecords} "
                + $"moveGrounded={MovingGroundedRecords} yawRecords={YawRecords} distinctYaw={DistinctYawValues} "
                + $"yawPinned={(YawPinned ? "yes" : "no")}");
    }
}

/// <summary>
/// <b>The owner-free grade for every hand experiment (OVERHAUL-PLAN §2, insight 12).</b>
///
/// <para>Between 2026-08-31 and 2026-09-02 this project was twice told the wield was fixed when it
/// was not, because the only instrument that could tell was the owner sitting at the client. The
/// August client's channel-2 stream can say it instead: in all five recorded wields the client kept
/// reporting keys — the posture word's <see cref="MovementPostureBits.HasMovementInput"/> bit, the
/// sprint/crouch/backwards bits, the <c>s144</c> input level, mouse-look rotation records, and in one
/// window a complete jump — while the horizontal position changed by <b>exactly 0.00 m</b> and the
/// <see cref="MovementPostureBits.Stopped"/> bit was set on every grounded record
/// (<c>out\overhaul-20260901\S5a-freeze-wire-forensics.md</c> §0, §2, §3). The same pickup stowed
/// instead of drawn produced none of it. So "the player cannot walk" is a wire fact, and this class
/// is the function that states it.</para>
///
/// <para><b>What it is not.</b> It cannot see the cause, only the shape; it grades the local
/// player's own stream and nothing else; and it deliberately says nothing until the player has been
/// seen to move at least once (a <see cref="LocomotionLockState.Clear"/> must precede any
/// <see cref="LocomotionLockState.Locked"/>). Without that rule the pre-match staging compound —
/// where the client stands still, presses keys and sends almost no position records — reads exactly
/// like a freeze; with it, the whole of the 17:41 control capture stays silent while the two wield
/// captures each report their freezes.</para>
///
/// <para><b>Purity.</b> The class holds a ring of decoded samples and nothing else: no clock, no
/// logger, no connection. The caller supplies the timestamp and the caller logs the line the
/// detector hands back. That is what makes the three capture fixtures a real test.</para>
/// </summary>
public sealed class LocomotionLockDetector
{
    private readonly List<Sample> _window = [];
    private readonly LocomotionLockThresholds _thresholds;
    private bool _everMoved;

    public LocomotionLockDetector(LocomotionLockThresholds? thresholds = null) =>
        _thresholds = thresholds ?? LocomotionLockThresholds.Default;

    /// <summary>The state as of the last observation.</summary>
    public LocomotionLockState State { get; private set; } = LocomotionLockState.Unknown;

    /// <summary>The window behind the last observation, for a caller that wants the numbers.</summary>
    public LocomotionWindow Last { get; private set; }

    /// <summary>How many times this session has entered <see cref="LocomotionLockState.Locked"/>.</summary>
    public int LockCount { get; private set; }

    /// <summary>Drops the window; used when the player is teleported or the match restarts.</summary>
    public void Reset()
    {
        _window.Clear();
        _everMoved = false;
        State = LocomotionLockState.Unknown;
        Last = default;
    }

    /// <summary>
    /// Feeds one decoded channel-2 record. Returns the host-log line when — and only when — the
    /// verdict changed, so a caller can log it unconditionally on the 20 Hz path.
    /// </summary>
    /// <param name="update">The record <c>ClientMovementUpdate.Parse</c> produced.</param>
    /// <param name="timestampMs">A monotonic millisecond clock; the caller owns it.</param>
    public string? Observe(ClientMovementUpdate update, long timestampMs)
    {
        ArgumentNullException.ThrowIfNull(update);

        _window.Add(Sample.From(update, timestampMs));

        // A time window, not a count window: the frozen client sends 1-6 records a second and the
        // running one 22-26 (S5a §3.2), so a count window would mean two different durations.
        long oldest = timestampMs - (long)_thresholds.Window.TotalMilliseconds;
        int drop = 0;
        while (drop < _window.Count && _window[drop].At < oldest)
        {
            drop++;
        }

        if (drop > 0)
        {
            _window.RemoveRange(0, drop);
        }

        LocomotionWindow measured = Measure(timestampMs);
        Last = measured;

        if (measured.State == State)
        {
            return null;
        }

        State = measured.State;
        if (measured.State == LocomotionLockState.Locked)
        {
            LockCount++;
        }

        return measured.ToLogLine();
    }

    /// <summary>The verdict over the current window, without feeding a record.</summary>
    public LocomotionWindow Measure(long timestampMs)
    {
        int records = _window.Count;
        int input = 0;
        int stoppedGrounded = 0;
        int movingGrounded = 0;
        int positions = 0;
        int yawRecords = 0;
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        var yawValues = new HashSet<float>();

        foreach (Sample sample in _window)
        {
            bool inputBit = sample.Posture is uint raw && new MovementPosture(raw).HasMovementInput;
            if (inputBit || sample.InputLevel > 0f)
            {
                input++;
            }

            if (sample.Posture is uint word)
            {
                var posture = new MovementPosture(word);
                if (posture.IsOnGround)
                {
                    if (posture.IsStopped)
                    {
                        stoppedGrounded++;
                    }
                    else
                    {
                        movingGrounded++;
                    }
                }
            }

            if (sample.Position is Vector3 position)
            {
                positions++;
                minX = MathF.Min(minX, position.X);
                maxX = MathF.Max(maxX, position.X);
                minZ = MathF.Min(minZ, position.Z);
                maxZ = MathF.Max(maxZ, position.Z);
            }

            if (sample.Yaw is float yaw)
            {
                yawRecords++;
                // Quantised to the coarser of the two ways the client states the angle. A full
                // record carries it as an IEEE float (refute-2 §3.2 reads -1.5142624378204346 in
                // all 13 records of one window); a rotation-only record carries the SAME angle as
                // a packed hundredth (-1.51). Rounding to 0.01 rad is what makes those one value
                // instead of two, and 0.01 rad is 0.57 degrees, far below any real turn.
                yawValues.Add(MathF.Round(yaw, 2));
            }
        }

        float displacement = positions == 0
            ? 0f
            : MathF.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxZ - minZ) * (maxZ - minZ)));

        TimeSpan span = records == 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(timestampMs - _window[0].At);

        bool yawPinned = yawRecords >= _thresholds.MinimumYawRecords
            && yawValues.Count <= _thresholds.MaximumPinnedYawValues;

        LocomotionLockState state = State;
        if (displacement > _thresholds.ClearDisplacement && input >= 1)
        {
            // Moving with input is the definition of working locomotion, and it is also what arms
            // the detector: nothing may be called a LOCK until this has been true once.
            _everMoved = true;
            state = LocomotionLockState.Clear;
        }
        else if (_everMoved
            && span >= _thresholds.MinimumSpan
            && input >= _thresholds.MinimumInputRecords
            && stoppedGrounded >= _thresholds.MinimumStoppedGroundedRecords
            && movingGrounded == 0
            && positions >= _thresholds.MinimumPositionRecords
            && displacement < _thresholds.LockDisplacement)
        {
            // S5a §0: "the STOP bit was set on every grounded posture record". One grounded record
            // without it means the client reported a moving intent it was allowed to act on, and
            // this window is not the freeze.
            state = LocomotionLockState.Locked;
        }

        return new LocomotionWindow(
            state,
            records,
            span,
            displacement,
            positions,
            input,
            stoppedGrounded,
            movingGrounded,
            yawRecords,
            yawValues.Count,
            yawPinned);
    }

    /// <summary>Only the four things a verdict is taken on, so the window stays small.</summary>
    private readonly record struct Sample(long At, uint? Posture, Vector3? Position, float? Yaw, float InputLevel)
    {
        public static Sample From(ClientMovementUpdate update, long at) => new(
            at,
            update.Posture,
            update.EffectivePosition,
            // refute-2 §3.2 reads the pinned angle off the ordinary orientation float and off the
            // first component of a rotation-only record; they are the same angle.
            update.Orientation ?? update.Rotation?.X,
            update.Scalar144 ?? 0f);
    }
}
