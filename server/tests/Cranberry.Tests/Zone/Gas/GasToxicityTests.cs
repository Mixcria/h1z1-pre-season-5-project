using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Gas;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Gas;

/// <summary>
/// D279 (docs/118 §4): the August client's own <b>toxicity meter</b>, resource 611.
/// <para>
/// The audit's G-c: the mechanic was live in retail August 2017, the client ships the resource
/// (<c>Resources.txt:145</c>, <c>MAX_VALUE</c> 180 000, 180 s to fill) <i>and</i> already draws the
/// bar (<c>HudPlayerResourcesWindow.gfx</c>, <c>ResourceType.TOXICITY</c> / <c>m_toxicity</c>), and
/// Cranberry never sent a byte for it — so a bar the client renders every frame was empty for every
/// second of every match.
/// </para>
/// <para>
/// Every rate asserted here is read off the client's own row except the drain, whose
/// <c>BURN_PER_MSEC</c> is 0 — see <see cref="TheDrainIsTheOneNumberTheClientDoesNotSupply"/>.
/// </para>
/// </summary>
public sealed class GasToxicityTests(ITestOutputHelper output)
{
    private static GasSettings Settings() => new();

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ------------------------------------------------------------------ the client's own numbers

    /// <summary>
    /// <c>out/data_aug/Resources.txt:145</c>, column for column:
    /// <c>Generic^611^75^ResourceToxicity^14118^14119^…^0^180000^…^1^0^0^1000^0^1000^1000^1^…</c>.
    /// The id and the TYPE differ on this row — unlike health (1/1) and stamina (6/6) — and the
    /// <c>8d</c> carries both, so swapping them is a silent no-op rather than an error.
    /// </summary>
    [Fact]
    public void TheMeterIsTheClientsOwnResourceRow()
    {
        GasSettings settings = Settings();

        Assert.Equal(611u, settings.ToxicityResourceId);
        Assert.Equal(75u, settings.ToxicityResourceType);
        Assert.Equal(180_000u, settings.ToxicityMaxValue);
        Assert.Equal(1f, settings.ToxicityRegenPerMs);
        Assert.Equal(1_000u, settings.ToxicityRegenTickMs);

        // REGEN_PER_MS x REGEN_TICK_MSEC = 1,000 a second = 180 s from empty to full, which is the
        // fill the owner's ruling (d) names. It is derived, not typed.
        Assert.Equal(1_000u, settings.ToxicityFillPerSecond);
        Assert.Equal(180u, settings.ToxicityMaxValue / settings.ToxicityFillPerSecond);

        // Z1's own cap is 100 (ZoneGasRetail.cs:348). The client's row contradicts it and wins.
        Assert.NotEqual(100u, settings.ToxicityMaxValue);
    }

    /// <summary>
    /// The one toxicity number that is a ruling. The client's row ships
    /// <c>BURN_PER_MSEC = 0</c> and <c>FLAG_INIT_WITH_DISABLED_BURN = 1</c>, so a client-side drain
    /// off its own columns would drain nothing; D279 mirrors the fill instead, 180 s each way.
    /// </summary>
    [Fact]
    public void TheDrainIsTheOneNumberTheClientDoesNotSupply()
    {
        GasSettings settings = Settings();
        Assert.Equal(settings.ToxicityFillPerSecond, settings.ToxicityDrainPerSecond);
        Assert.Equal(1_000u, settings.ToxicityDrainPerSecond);
    }

    // ------------------------------------------------------------------ the accumulator

    /// <summary>180 s in the gas fills the bar exactly, and not a tick sooner.</summary>
    [Fact]
    public void ThreeMinutesInTheGasFillsTheMeter()
    {
        GasSettings settings = Settings();
        var meter = new GasToxicity();
        meter.Reset();
        meter.MarkArmed();

        for (int second = 1; second <= 179; second++)
        {
            Assert.True(meter.Tick(settings, inGas: true, settings.TickPeriodMs));
            meter.MarkSent();
            Assert.Equal((uint)second * 1_000u, meter.Value);
            Assert.True(meter.FractionOf(settings) < 1f);
        }

        Assert.True(meter.Tick(settings, inGas: true, settings.TickPeriodMs));
        meter.MarkSent();
        Assert.Equal(settings.ToxicityMaxValue, meter.Value);
        Assert.Equal(1f, meter.FractionOf(settings));

        // Full is full: a further tick changes nothing, so it sends nothing.
        Assert.False(meter.Tick(settings, inGas: true, settings.TickPeriodMs));
        Assert.Equal(settings.ToxicityMaxValue, meter.Value);
    }

    /// <summary>And 180 s outside it empties the bar, then stops.</summary>
    [Fact]
    public void ThreeMinutesOutOfTheGasEmptiesIt()
    {
        GasSettings settings = Settings();
        var meter = new GasToxicity();
        meter.Reset();
        meter.MarkArmed();
        for (int second = 0; second < 180; second++)
        {
            meter.Tick(settings, inGas: true, settings.TickPeriodMs);
            meter.MarkSent();
        }

        Assert.Equal(settings.ToxicityMaxValue, meter.Value);

        for (int second = 0; second < 180; second++)
        {
            Assert.True(meter.Tick(settings, inGas: false, settings.TickPeriodMs));
            meter.MarkSent();
        }

        Assert.Equal(0u, meter.Value);

        // An empty meter outside the gas is the steady state of nearly every match: no change, so
        // no packet, so no cost.
        Assert.False(meter.Tick(settings, inGas: false, settings.TickPeriodMs));
    }

    /// <summary><c>CRANBERRY_GAS_TOXICITY_DRAIN=0</c> makes the meter one-way.</summary>
    [Fact]
    public void AZeroDrainMakesTheMeterOneWay()
    {
        GasSettings settings = Settings() with { ToxicityDrainPerSecond = 0 };
        var meter = new GasToxicity();
        meter.Reset();
        meter.MarkArmed();
        meter.Tick(settings, inGas: true, settings.TickPeriodMs);
        meter.MarkSent();

        Assert.Equal(1_000u, meter.Value);
        Assert.False(meter.Tick(settings, inGas: false, settings.TickPeriodMs));
        Assert.Equal(1_000u, meter.Value);
    }

    // ------------------------------------------------------------------ the wire

    /// <summary>
    /// The arming send, byte for byte. 611 is <b>not</b> in <c>CharacterResource.Starter</c>, so
    /// this row is what creates the client's missing
    /// <c>Resources.PlayerResourceDataSource</c> entry and puts the bar on screen at zero
    /// (docs/02 2026-08-29, <c>FUN_140ce52c0</c>). It is the same 101-byte carrier health and
    /// stamina already use — no new packet, no new registration.
    /// </summary>
    [Fact]
    public void TheArmingRowIsTheSameHundredAndOneByteCarrierHealthUses()
    {
        GasSettings settings = Settings();
        byte[] arm = Bytes(w => new CharacterResourceUpdate(
            SubjectGuid: 0x0102_0304_0506_0708UL,
            ResourceId: settings.ToxicityResourceId,
            ResourceType: settings.ToxicityResourceType,
            Value: 0,
            PreviousValue: 0).WriteTo(w));

        Assert.Equal(CharacterResourceUpdate.WireLength, arm.Length);
        Assert.Equal(ZoneOpcodes.ResourceEventBase, arm[0]);
        Assert.Equal(CharacterResourceUpdate.EventType, arm[5]);

        // opcode(1) + gameTime(4) + eventType(1) + guid(8) = 14, then id, type, value, previous.
        Assert.Equal(611u, BitConverter.ToUInt32(arm, 14));
        Assert.Equal(75u, BitConverter.ToUInt32(arm, 18));
        Assert.Equal(0u, BitConverter.ToUInt32(arm, 22));
        Assert.Equal(0u, BitConverter.ToUInt32(arm, 26));
    }

    /// <summary>
    /// <b>The fake session.</b> A whole match on the shipped ladder with a player who stands still
    /// 3 000 m from the play-area centre: the gas reaches him, the meter fills, he walks back inside
    /// and it drains. This drives the exact predicate <c>ZoneService.PumpToxicity</c> uses —
    /// <c>IsLethalAt</c> and <c>ActiveCircleAt(...).Contains</c> — and counts the packets that would
    /// go out, so the cost claim in the doc comment is measured rather than asserted.
    /// </summary>
    [Fact]
    public void AFakeSessionFillsTheBarInTheGasAndDrainsItOutside()
    {
        GasSettings settings = Settings();
        GasSchedule schedule = GasSchedule.Create(settings, 0xC0FF_EE00_1234_5678UL);
        var meter = new GasToxicity();
        meter.Reset();
        meter.MarkArmed();

        // He stands on the far side of the play area from wherever this match is heading, so the
        // wall is guaranteed to reach him.
        Vector3 heading = schedule.FinalCircle.Centre - schedule.InitialCircle.Centre;
        float length = MathF.Sqrt((heading.X * heading.X) + (heading.Z * heading.Z));
        Vector3 stood = length > 0f
            ? schedule.InitialCircle.Centre with
            {
                X = schedule.InitialCircle.Centre.X - (heading.X / length * 3_000f),
                Z = schedule.InitialCircle.Centre.Z - (heading.Z / length * 3_000f),
            }
            : schedule.InitialCircle.Centre with { X = schedule.InitialCircle.Centre.X + 3_000f };

        int sends = 0;
        long firstBurn = -1;
        long fullAt = -1;

        for (long clock = 0; clock <= schedule.FinishedAtMs; clock += settings.TickPeriodMs)
        {
            bool inGas = schedule.IsLethalAt(clock) && !schedule.ActiveCircleAt(clock).Contains(stood);
            if (inGas && firstBurn < 0)
            {
                firstBurn = clock;
            }

            if (meter.Tick(settings, inGas, settings.TickPeriodMs))
            {
                sends++;
                meter.MarkSent();
            }

            if (fullAt < 0 && meter.Value == settings.ToxicityMaxValue)
            {
                fullAt = clock;
            }
        }

        output.WriteLine($"fake session: first in the gas at {GasTuning.Clock(firstBurn)}, meter full at "
            + $"{GasTuning.Clock(fullAt)}, {sends} ResourceEvent(s) "
            + $"({sends * CharacterResourceUpdate.WireLength:N0} B for the whole match)");

        Assert.True(firstBurn > 0, "the wall never reached him");
        Assert.True(fullAt > firstBurn, "the meter never filled");
        Assert.Equal(180L * settings.TickPeriodMs, fullAt - firstBurn + settings.TickPeriodMs);
        Assert.Equal(settings.ToxicityMaxValue, meter.Value);

        // Now he gets back inside: 180 s of drain, and then silence.
        int drains = 0;
        while (meter.Tick(settings, inGas: false, settings.TickPeriodMs))
        {
            meter.MarkSent();
            drains++;
        }

        Assert.Equal(180, drains);
        Assert.Equal(0u, meter.Value);
    }

    /// <summary>
    /// <c>CRANBERRY_GAS_TOXICITY=0</c> is a real revert: the setting is off and nothing else in the
    /// gas model reads the meter, so the wire is what it was before D279.
    /// </summary>
    [Fact]
    public void TheSwitchIsAWholeRevert()
    {
        Assert.True(Settings().SendToxicity);

        GasSettings off = GasTuning.FromEnvironment(
            name => name == "CRANBERRY_GAS_TOXICITY" ? "0" : null,
            out string? note);

        Assert.False(off.SendToxicity);
        Assert.NotNull(note);
        Assert.Contains("toxicity off", note, StringComparison.Ordinal);

        // The damage curve is untouched by the meter (the owner's ruling (c): the ladder and the
        // per-phase damage ship as they are).
        Assert.Equal(Settings().DamagePerPhase, off.DamagePerPhase);
        for (int phase = 1; phase <= off.PhaseCount; phase++)
        {
            Assert.Equal(Settings().DamageForPhase(phase), off.DamageForPhase(phase));
        }
    }
}
