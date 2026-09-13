using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Gas;

namespace Cranberry.Tests.Zone.Gas;

/// <summary>
/// D280 (docs/118 §5), the audit's G-h: <b><c>ce 01</c>'s blend constant is the server's own send
/// period.</b>
/// <para>
/// The field at gas object <c>+0x54</c> is the time constant of the client's own exponential ease
/// (<c>FUN_140bbed00</c>: <c>alpha = min(frameΔms, 1000) / blendMs</c>, docs/18 §1, G-07 closed).
/// Cranberry wrote a flat 1 000 ms into every ring while re-stating the travelling ring every
/// 500 ms, so the drawn wall eased with a one-second constant against a circle that moved twice a
/// second — a wall that permanently trails the circle it burns against, which is exactly the shape
/// of "the gas killed me before it reached me".
/// </para>
/// <para>
/// These are byte-exact assertions on the 31-byte packet, not on a setting.
/// </para>
/// </summary>
public sealed class GasRingBlendTests
{
    private const int BlendOffset = 3 + (4 * 4) + 4;   // header + f32x4 centre + f32 radius

    private static GasSettings Settings() => new();

    private static byte[] Ring(GasSettings settings, bool advancing)
    {
        using var writer = new PacketWriter();
        GasPackets.WriteRing(writer, settings, new GasCircle(new Vector3(-250f, 0f, 100f), 2_750f), advancing);
        return writer.Written.ToArray();
    }

    private static uint BlendOf(byte[] ring) => BitConverter.ToUInt32(ring, BlendOffset);

    /// <summary>
    /// The default: a travelling ring carries the interval to the next one, so the client's ease
    /// finishes exactly as that next one lands. Every other send carries
    /// <see cref="GasSettings.RingBlendMs"/>, because nothing follows it.
    /// </summary>
    [Fact]
    public void ATravellingRingCarriesTheSendPeriodAndAHeldOneDoesNot()
    {
        GasSettings settings = Settings();
        Assert.Equal(GasRingBlendMode.SendPeriod, settings.RingBlendMode);

        byte[] advancing = Ring(settings, advancing: true);
        byte[] holding = Ring(settings, advancing: false);

        Assert.Equal(GasPackets.RingLength, advancing.Length);
        Assert.Equal(GasPackets.RingLength, holding.Length);
        Assert.Equal(settings.SafeZoneUpdateIntervalMs, BlendOf(advancing));
        Assert.Equal(settings.RingBlendMs, BlendOf(holding));
        Assert.Equal(500u, BlendOf(advancing));
        Assert.Equal(1_000u, BlendOf(holding));

        // Nothing else on the packet moved: same centre, same radius, same trailing field.
        Assert.Equal(advancing.Take(BlendOffset), holding.Take(BlendOffset));
        Assert.Equal(
            GasPackets.RingUnusedFieldDefault,
            BitConverter.ToUInt32(advancing, BlendOffset + 4));
    }

    /// <summary>
    /// The blend constant follows <see cref="GasSettings.SafeZoneUpdateIntervalMs"/> rather than
    /// being a second copy of it. That is the whole ruling: if the pump ever moves to 10 Hz, the
    /// field moves with it and no one has to remember.
    /// </summary>
    [Theory]
    [InlineData(100u)]
    [InlineData(250u)]
    [InlineData(500u)]
    [InlineData(1_000u)]
    public void TheBlendTracksTheSendPeriodWhateverItIs(uint period)
    {
        GasSettings settings = Settings() with { SafeZoneUpdateIntervalMs = period };
        settings.Validate();
        Assert.Equal(period, BlendOf(Ring(settings, advancing: true)));
        Assert.Equal(period, GasPackets.BlendMsFor(settings, advancing: true));
    }

    /// <summary>
    /// <c>CRANBERRY_GAS_BLEND_MODE=Fixed</c> restores the pre-D280 bytes exactly: one constant on
    /// every ring, whatever the cadence.
    /// </summary>
    [Fact]
    public void FixedModeRestoresTheOldFlatConstant()
    {
        GasSettings fixedMode = Settings() with { RingBlendMode = GasRingBlendMode.Fixed };
        fixedMode.Validate();

        Assert.Equal(fixedMode.RingBlendMs, BlendOf(Ring(fixedMode, advancing: true)));
        Assert.Equal(fixedMode.RingBlendMs, BlendOf(Ring(fixedMode, advancing: false)));
        Assert.Equal(Ring(fixedMode, advancing: true), Ring(fixedMode, advancing: false));
    }

    /// <summary>
    /// The client divides by this field with no zero guard (docs/18 §1a field 6), so it can never be
    /// 0 — including through a send period a knob set to something silly.
    /// </summary>
    [Fact]
    public void TheBlendIsNeverZero()
    {
        Assert.Equal(1u, GasPackets.BlendMsFor(Settings() with { RingBlendMs = 0 }, advancing: false));
        Assert.Equal(
            1u,
            GasPackets.BlendMsFor(
                Settings() with { RingBlendMode = GasRingBlendMode.SendPeriod, SafeZoneUpdateIntervalMs = 0 },
                advancing: true));
    }

    /// <summary>
    /// The environment override parses, and an unknown word is reported rather than thrown.
    /// </summary>
    [Fact]
    public void TheModeIsSwitchableFromTheEnvironment()
    {
        GasSettings fixedMode = GasTuning.FromEnvironment(
            name => name == GasTuning.BlendModeVariable ? "Fixed" : null,
            out string? note);
        Assert.Equal(GasRingBlendMode.Fixed, fixedMode.RingBlendMode);
        Assert.Contains("RingBlendMode=Fixed", note, StringComparison.Ordinal);

        GasSettings bad = GasTuning.FromEnvironment(
            name => name == GasTuning.BlendModeVariable ? "Lerp" : null,
            out string? complaint);
        Assert.Equal(GasRingBlendMode.SendPeriod, bad.RingBlendMode);
        Assert.Contains("IGNORED", complaint, StringComparison.Ordinal);
    }
}
