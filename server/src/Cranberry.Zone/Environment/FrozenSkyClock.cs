using System.Globalization;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Lighting;

/// <summary>
/// The frozen time of day the sky is lit by: the u64 of every <c>GameTimeSync</c> reply, expressed
/// as a date and an hour instead of a magic epoch number (docs/38 §5.2).
/// </summary>
/// <remarks>
/// <para>
/// The client gmtime's the u64 into seconds-of-day (<c>FUN_142099a70</c>, first reply only) and
/// derives the sun position from the hour, so the <b>UTC hour</b> is the only part that matters
/// visually. D16 froze it at 14:00; docs/38 §4.5 moves it to <b>12:00</b> because
/// <c>Lighting_Z2.txt</c>'s <c>[SunAngleTimes] DayAngle = 0.700</c> is calibrated to this map's own
/// solar-noon sun pitch of 43.8° (sin 0.692), which <c>sky_Z.xml</c> reaches at 12:00 and not at
/// 14:00 (36.4°, sin 0.593).
/// </para>
/// <para>
/// <b>Status: DERIVED-from-data, not LIVE-VERIFIED.</b> docs/38 §4.5 flags the weight ramp between
/// the <c>[SunAngleTimes]</c> thresholds as INFERRED, and docs/38 §6 step 4 is a four-shot clock
/// sweep (09/12/14/17) that settles it. <see cref="ZSunArc"/> exists so that sweep can be reasoned
/// about without re-opening the client's assets.
/// </para>
/// </remarks>
public sealed record FrozenSkyClock
{
    /// <summary>D16's date, kept: 2017-08-15, inside the August 2017 KOTK window.</summary>
    public DateOnly DateUtc { get; init; } = DateOnly.Parse(Rulings.Sky.FrozenDateUtc, CultureInfo.InvariantCulture);

    /// <summary>
    /// UTC hour the sun is frozen at. Default <b>12</b> (docs/38 §5.2) rather than D18's 14.
    /// This is the one knob docs/38 §6 step 4 sweeps.
    /// </summary>
    public int HourUtc { get; init; } = Rulings.Sky.FrozenHourUtc;

    /// <summary>UTC minute; 0 keeps the hour exactly on an authored <c>sky_Z.xml</c> keyframe.</summary>
    public int MinuteUtc { get; init; }

    /// <summary>
    /// The clock stays frozen at <see cref="FixedUnixTime"/>: the <c>GameTimeSync</c> reply's u32 is
    /// the cycle scalar (float bits, 0.0 = no advance) and its flag is the freeze byte
    /// (<c>FUN_140ff2250</c>). D16's reproducibility requirement — kept true by default; nothing in
    /// docs/38's evidence requires a moving sun.
    /// </summary>
    public bool Freeze { get; init; } = Rulings.Sky.FreezeClock;

    /// <summary>
    /// The value to put in <c>ZoneOptions.FixedUnixTime</c>. The default works out to
    /// <c>1502798400</c> = 2017-08-15T12:00:00Z (D18 sent <c>1502805600</c> = 14:00Z).
    /// </summary>
    public ulong FixedUnixTime =>
        (ulong)new DateTimeOffset(
            DateUtc.Year, DateUtc.Month, DateUtc.Day, HourUtc, MinuteUtc, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();

    /// <summary>The authored sun pitch at <see cref="HourUtc"/>, from <see cref="ZSunArc"/>.</summary>
    public float AuthoredSunPitchDegrees => ZSunArc.PitchDegreesAtHour(HourUtc);

    /// <summary>
    /// <c>sin(pitch)</c> at <see cref="HourUtc"/> — the quantity <c>Lighting_Z2.txt</c>'s
    /// <c>[SunAngleTimes]</c> thresholds are expressed in.
    /// </summary>
    public float AuthoredSunElevationSine => ZSunArc.ElevationSineAtHour(HourUtc);

    /// <summary>Cranberry's default: solar noon on the Z map (docs/38 §5.2).</summary>
    public static FrozenSkyClock SolarNoon { get; } = new();

    /// <summary>D18's clock, kept for the before/after pair of docs/38 §6.</summary>
    public static FrozenSkyClock LegacyD18Afternoon { get; } = new() { HourUtc = Rulings.Sky.LegacyAfternoonHourUtc };
}

/// <summary>
/// The Z map's own authored sun arc and the phase thresholds the client grades it against — the two
/// tables docs/38 §4.5 uses to argue for a 12:00 clock. Reference data, not wire data.
/// </summary>
/// <remarks>
/// Sun arc: <c>sky_Z.xml</c> (Assets_034.pack, 15,289 B, crc <c>0xfc0e5711</c>)
/// <c>&lt;SunLight&gt;&lt;Direction Time=… Pitch=…/&gt;</c>, one keyframe per hour.
/// Thresholds: <c>Lighting_Z2.txt</c> (Assets_218.pack, crc <c>0xaf1c566f</c>) <c>[SunAngleTimes]</c>.
/// Both extracted to <c>C:\Aug2017\out\lighting-research\</c>.
/// </remarks>
public static class ZSunArc
{
    /// <summary>
    /// Authored sun pitch in degrees for UTC hours 0-23, in order
    /// (<c>sky_Z.xml &lt;Direction&gt;</c>). Index = hour.
    /// </summary>
    private static readonly float[] PitchDegreesByHour =
    [
        36.0f, 36.0f, 33.0f, 16.0f, 8.0f, -10.0f, -10.0f, 9.4f,
        19.7f, 29.0f, 36.7f, 42.0f, 43.8f, 41.8f, 36.4f, 28.6f,
        19.2f, 8.9f, -15.0f, -15.0f, 0.0f, 18.0f, 30.0f, 36.0f,
    ];

    /// <summary>
    /// <c>[SunAngleTimes] DayAngle = 0.700000</c> — the elevation sine at which
    /// <c>Lighting_Z2.txt</c> reaches its pure <c>[Day]</c> grade, and the only Z2 phase with a
    /// non-identity colour-grading LUT (docs/38 §4.4).
    /// </summary>
    public const float DayAngle = 0.70f;

    /// <summary><c>[SunAngleTimes] NightAngle = -0.550000</c>.</summary>
    public const float NightAngle = -0.55f;

    /// <summary><c>[SunAngleTimes] DawnStart = 0.020000</c> / <c>DawnEnd = 0.400000</c>.</summary>
    public const float DawnStart = 0.02f;

    /// <inheritdoc cref="DawnStart"/>
    public const float DawnEnd = 0.40f;

    /// <summary><c>[SunAngleTimes] DuskStart = 0.200000</c> / <c>DuskEnd = 0.050000</c>.</summary>
    public const float DuskStart = 0.20f;

    /// <inheritdoc cref="DuskStart"/>
    public const float DuskEnd = 0.05f;

    /// <summary><c>[SunAngleTimes] TwilightStart = -0.150000</c> / <c>TwilightEnd = -0.300000</c>.</summary>
    public const float TwilightStart = -0.15f;

    /// <inheritdoc cref="TwilightStart"/>
    public const float TwilightEnd = -0.30f;

    /// <summary><c>[SunAngleTimes] FalseDawnStart = -0.300000</c> / <c>FalseDawnEnd = -0.150000</c>.</summary>
    public const float FalseDawnStart = -0.30f;

    /// <inheritdoc cref="FalseDawnStart"/>
    public const float FalseDawnEnd = -0.15f;

    /// <summary>Authored pitch in degrees at a UTC hour (0-23).</summary>
    public static float PitchDegreesAtHour(int hourUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hourUtc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hourUtc, 23);
        return PitchDegreesByHour[hourUtc];
    }

    /// <summary><c>sin(pitch)</c> at a UTC hour — comparable directly against <see cref="DayAngle"/>.</summary>
    public static float ElevationSineAtHour(int hourUtc) =>
        (float)Math.Sin(PitchDegreesAtHour(hourUtc) * Math.PI / 180.0);

    /// <summary>
    /// The UTC hour whose authored elevation sine sits closest to <see cref="DayAngle"/>, i.e. the
    /// hour at which <c>Lighting_Z2.txt</c>'s <c>[Day]</c> grade is fully engaged. docs/38 §4.5
    /// argues this is 12; the method computes it rather than asserting it.
    /// </summary>
    public static int HourClosestToDayAngle()
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int hour = 0; hour < PitchDegreesByHour.Length; hour++)
        {
            float distance = Math.Abs(ElevationSineAtHour(hour) - DayAngle);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = hour;
            }
        }

        return best;
    }
}
