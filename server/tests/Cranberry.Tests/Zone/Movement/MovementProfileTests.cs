using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

// The profile is docs/40 §6 — Cranberry's own [DESIGN] numbers, because the August client ships no
// value for any of them (§4.1). These tests pin the two things that are not design choices: the
// mechanism (which scalar applies in which branch, per FUN_1411acfa0 / FUN_1411ad740 / FUN_1411acb10)
// and the client-side constants the server must not contradict.
public sealed class MovementProfileTests
{
    [Fact]
    public void TheSixModesAreDistinctSpeeds()
    {
        // The owner's brief: walking, sprinting, crouching, strafing and reversing must feel
        // different. Before this lane every one of them ran at the base speed, because GetStat
        // returns the caller's 1.0f default for an absent stat (docs/40 §0).
        //
        // Wave 8 re-ordered them again and, for the first time, made two of them EQUAL: the owner's
        // own server ships backpedal and strafe at the same 0.75 (docs/76 §2.1), so the ladder is
        // walk < crouch < backpedal == strafe < run < sprint. That is a real, deliberate property
        // and not a rounding accident — it also makes AxisScalar's BackwardStrafe min() a no-op —
        // so this asserts the ordering it actually has rather than pretending to six distinct
        // speeds. Wave 4's ordering claim (backpedal below crouch) is superseded.
        MovementProfile profile = MovementProfile.Default;
        float[] speeds =
        [
            profile.WalkSpeed,
            profile.CrouchSpeed,
            profile.BackpedalSpeed,
            profile.StrafeSpeed,
            profile.RunSpeed,
            profile.SprintSpeed,
        ];

        Assert.True(speeds.SequenceEqual(speeds.Order()), "The six modes should be ordered slowest to fastest.");
        Assert.All(speeds, s => Assert.InRange(s, 0.5f, 12f));

        // Five distinct values out of six, and the collision is exactly the backpedal/strafe pair.
        Assert.Equal(5, speeds.Distinct().Count());
        Assert.Equal(profile.StrafeSpeed, profile.BackpedalSpeed, 5);
        Assert.Equal(
            profile.BackpedalSpeedModifier,
            profile.AxisScalar(MovementAxis.BackwardStrafe, sprinting: false),
            5);

        // Every other neighbouring pair is strictly separated, and by a margin a player can feel.
        Assert.True(profile.CrouchSpeed - profile.WalkSpeed > 1f);
        Assert.True(profile.BackpedalSpeed - profile.CrouchSpeed > 0.15f);
        Assert.True(profile.RunSpeed - profile.StrafeSpeed > 1f);
        Assert.True(profile.SprintSpeed - profile.RunSpeed > 1.5f);
    }

    /// <summary>
    /// The four headline numbers of the wave-8 Z1 port (docs/76 §7.1), pinned <b>together with the
    /// little-endian float bytes they become on the wire</b>, so a change to either the value or the
    /// encoding fails here.
    /// <para>
    /// Base 4.10 is the owner's own <c>ZoneMovement.RetailJogSpeed</c>; sprint 1.40 and backpedal
    /// 0.75 are rows 5 and 3 of his <c>ZoneSendSelf.BuildStats3()</c>; strafe 0.75 is unchanged and
    /// is the one number the August client itself ships. The mechanism these replace is unchanged
    /// and still proven: the client applies every modifier once and nothing twice (docs/49 §2.2).
    /// </para>
    /// </summary>
    [Theory]
    // The stat ordinals are the ones read back out of the captured payload in docs/49 §1.2
    // (02 05 ... 03 07), not re-typed from a header: CharacterStatId resolves them from the
    // client's own StringHashToValue table, and this pins that resolution too.
    [InlineData(2u, 4.10f, "33338340")]
    [InlineData(5u, 1.40f, "3333B33F")]
    [InlineData(3u, 0.75f, "0000403F")]
    [InlineData(7u, 0.75f, "0000403F")]
    public void TheCorrectedValuesReachTheWireAsTheDocumentedFloatBytes(uint statId, float expected, string wire)
    {
        Assert.Contains(
            statId,
            new[]
            {
                CharacterStatId.MaxMovementSpeed,
                CharacterStatId.SprintSpeedModifier,
                CharacterStatId.BackpedalSpeedModifier,
                CharacterStatId.StrafeSpeedModifier,
            });

        Assert.Equal(
            expected,
            MovementProfile.Default.ToStats().Single(s => s.StatId == statId).EffectiveValue,
            5);

        using var writer = new PacketWriter();
        CharacterStatPackets.UpdateStat.ForProfile(0x1003UL, MovementProfile.Default).WriteTo(writer);
        string payload = Convert.ToHexString(writer.Written.ToArray());

        // statId u32 LE, valueType 1 (float), the base float, modifier 0 — the 13-byte entry.
        Assert.Contains(
            Convert.ToHexString(BitConverter.GetBytes(statId)) + "01" + wire + "00000000",
            payload);
    }

    [Fact]
    public void TheLadderIsTheOneDocsSeventySixPublished()
    {
        // Exact products, not the rounded figures docs/76 §2.5 prints (5.74 / 3.08 / 2.87 / 2.15).
        MovementProfile profile = MovementProfile.Default;
        Assert.Equal(5.740f, profile.SprintSpeed, 0.001f);
        Assert.Equal(4.100f, profile.RunSpeed, 0.001f);
        Assert.Equal(3.075f, profile.StrafeSpeed, 0.001f);
        Assert.Equal(2.870f, profile.CrouchSpeed, 0.001f);
        Assert.Equal(3.075f, profile.BackpedalSpeed, 0.001f);
        Assert.Equal(1.230f, profile.WalkSpeed, 0.001f);
        Assert.Equal(3.280f, profile.MaxMovementSpeed * profile.WaterSpeedModifier, 0.001f);
        Assert.Equal(1.640f, profile.MaxMovementSpeed * profile.ProneSpeedModifier, 0.001f);

        // The composites the client derives for itself.
        Assert.Equal(2.153f, profile.SpeedFor(MovementStance.Crouching, MovementAxis.Strafe), 0.001f);
        Assert.Equal(2.153f, profile.SpeedFor(MovementStance.Crouching, MovementAxis.Backward), 0.001f);
        Assert.Equal(3.075f, profile.SpeedFor(MovementStance.Standing, MovementAxis.BackwardStrafe), 0.001f);
    }

    [Fact]
    public void ForwardAndDiagonalForwardTakeNoAxisPenaltyAndBackwardsSidewaysTakesTheSmaller()
    {
        // docs/49 §4.1, proven twice: FUN_141593e80 branches
        // `if (fwd > 0 && lat != 0) goto <skip the axis scalar>`, and the client reported a 5.5 m/s
        // forward plateau against the 5.50 then being sent — not the 4.4 that docs/40 §2's literal
        // transcription predicted. (That measurement is wave 4's; the base is 4.10 since wave 8, and
        // the branch it proved is unchanged.) Backwards
        // AND sideways takes min(strafe, backpedal), which no earlier version modelled at all.
        MovementProfile profile = MovementProfile.Default;
        Assert.Equal(1f, profile.AxisScalar(MovementAxis.Forward, sprinting: false), 5);
        Assert.Equal(1f, profile.AxisScalar(MovementAxis.Forward, sprinting: true), 5);
        Assert.Equal(profile.RunSpeed, profile.SpeedFor(MovementStance.Standing), 4);

        Assert.Equal(
            MathF.Min(profile.StrafeSpeedModifier, profile.BackpedalSpeedModifier),
            profile.AxisScalar(MovementAxis.BackwardStrafe, sprinting: false),
            5);
        Assert.Equal(
            profile.BackpedalSpeedModifier,
            profile.AxisScalar(MovementAxis.BackwardStrafe, sprinting: false),
            5);

        // And with a strafe penalty harsher than the backpedal one, the min flips.
        var reversed = profile with { StrafeSpeedModifier = 0.3f };
        Assert.Equal(0.3f, reversed.AxisScalar(MovementAxis.BackwardStrafe, sprinting: false), 5);
    }

    [Fact]
    public void SprintStrafeIsTheClientsHardCodedThreeQuarters()
    {
        // Movement.SprintStrafeMultiplier = .75, read by inline hash 0x489772F6 in FUN_140c4c1f0;
        // while sprinting the client bypasses StatId.StrafeSpeedModifier entirely (docs/40 §2.3).
        Assert.Equal(0.75f, MovementProfile.SprintStrafeMultiplier, 5);
        Assert.Equal(
            0.75f,
            float.Parse(
                StringHashValues.Entries.Single(e => e.Name == "Movement.SprintStrafeMultiplier").Value,
                System.Globalization.CultureInfo.InvariantCulture),
            5);

        MovementProfile profile = MovementProfile.Default;
        Assert.Equal(profile.StrafeSpeedModifier, profile.AxisScalar(MovementAxis.Strafe, sprinting: false), 5);
        Assert.Equal(0.75f, profile.AxisScalar(MovementAxis.Strafe, sprinting: true), 5);
        Assert.Equal(profile.SprintSpeed * 0.75f, profile.SprintStrafeSpeed, 4);
    }

    [Fact]
    public void CrouchWalkAndProneSuppressTheSprintBlend()
    {
        // FUN_1411acfa0 is an exclusive else-if chain: a crouching, prone or walking character can
        // never pick up SprintSpeedModifier (docs/40 §2.1).
        MovementProfile profile = MovementProfile.Default;
        Assert.Equal(profile.CrouchSpeedModifier, profile.StanceScalar(MovementStance.Crouching), 5);
        Assert.Equal(profile.WalkSpeedModifier, profile.StanceScalar(MovementStance.Walking), 5);
        Assert.Equal(profile.ProneSpeedModifier, profile.StanceScalar(MovementStance.Prone), 5);
        Assert.Equal(profile.SprintSpeedModifier, profile.StanceScalar(MovementStance.Sprinting), 5);
        Assert.Equal(1f, profile.StanceScalar(MovementStance.Standing), 5);
        Assert.True(profile.SpeedFor(MovementStance.Crouching) < profile.SpeedFor(MovementStance.Standing));
    }

    [Fact]
    public void BackpedalIsAnAxisScalarOnTopOfTheStance()
    {
        // FUN_1411acb10 multiplies the axis term, so backpedalling while crouched compounds.
        MovementProfile profile = MovementProfile.Default;
        Assert.Equal(
            profile.MaxMovementSpeed * profile.CrouchSpeedModifier * profile.BackpedalSpeedModifier,
            profile.SpeedFor(MovementStance.Crouching, MovementAxis.Backward),
            4);
        Assert.Equal(profile.BackpedalSpeed, profile.SpeedFor(MovementStance.Standing, MovementAxis.Backward), 4);
        Assert.Equal(profile.RunSpeed, profile.SpeedFor(MovementStance.Standing, MovementAxis.Forward), 4);
    }

    [Fact]
    public void TheStaminaTiersAreTheClientsOwnFallbacks()
    {
        // docs/40 §4.3, read out of FUN_1411acfa0's .rdata operands: ≥50 % none, 25-50 % ×0.75,
        // 5-25 % ×0.5, <5 % ×0.1. Cranberry sends no stamina stat, so these are what the client uses.
        Assert.Equal(1f, MovementProfile.StaminaScalar(100f), 5);
        Assert.Equal(1f, MovementProfile.StaminaScalar(50f), 5);
        Assert.Equal(0.75f, MovementProfile.StaminaScalar(49.9f), 5);
        Assert.Equal(0.75f, MovementProfile.StaminaScalar(25f), 5);
        Assert.Equal(0.5f, MovementProfile.StaminaScalar(24.9f), 5);
        Assert.Equal(0.5f, MovementProfile.StaminaScalar(5f), 5);
        Assert.Equal(0.1f, MovementProfile.StaminaScalar(4.9f), 5);
        Assert.Equal(0.1f, MovementProfile.StaminaScalar(0f), 5);
    }

    [Fact]
    public void MaxLegalSpeedIsSprintAndBoundsEveryOtherMode()
    {
        // The only bound a speed check can rely on: the client never reports its stance (docs/40 §5).
        MovementProfile profile = MovementProfile.Default;
        Assert.Equal(profile.SprintSpeed, profile.MaxLegalSpeed, 4);
        foreach (MovementStance stance in Enum.GetValues<MovementStance>())
        {
            foreach (MovementAxis axis in Enum.GetValues<MovementAxis>())
            {
                Assert.True(profile.SpeedFor(stance, axis) <= profile.MaxLegalSpeed + 0.001f);
            }
        }
    }

    [Fact]
    public void ToStatsEmitsEveryValueOnceAsAFloatWithNoModifier()
    {
        IReadOnlyList<CharacterStat> stats = MovementProfile.Default.ToStats();

        Assert.Equal(MovementProfile.StatCount, stats.Count);
        Assert.Equal(stats.Count, stats.Select(s => s.StatId).Distinct().Count());
        Assert.All(stats, s => Assert.Equal(CharacterStatValueType.Float, s.ValueType));
        Assert.All(stats, s => Assert.Equal(0u, s.Modifier));
        Assert.All(CharacterStatId.RemoteAnimationTimes, id => Assert.Contains(stats, s => s.StatId == id));
        Assert.Contains(stats, s => s.StatId == CharacterStatId.MaxMovementSpeed && s.EffectiveValue == 4.10f);
        Assert.Equal(MovementProfile.StatCount, CharacterStatPackets.UpdateStat
            .ForProfile(1UL, MovementProfile.Default).Stats.Count);
    }

    [Fact]
    public void ATunedProfileFlowsThroughToTheStats()
    {
        var profile = MovementProfile.Default with { MaxMovementSpeed = 6f, SprintSpeedModifier = 1.6f };
        Assert.Equal(9.6f, profile.SprintSpeed, 4);
        Assert.Equal(
            1.6f,
            profile.ToStats().Single(s => s.StatId == CharacterStatId.SprintSpeedModifier).EffectiveValue,
            4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void AnUnusableBaseSpeedIsRefused(float speed) =>
        Assert.Throws<InvalidOperationException>(() =>
            (MovementProfile.Default with { MaxMovementSpeed = speed }).ToStats());

    [Fact]
    public void ANegativeBlendTimeIsRefusedButZeroIsLegal()
    {
        // Zero is the client's own default and means "snap instantly" (docs/40 §2.4).
        Assert.Throws<InvalidOperationException>(() =>
            (MovementProfile.Default with { SprintAccelerationTime = -0.1f }).ToStats());
        (MovementProfile.Default with { SprintAccelerationTime = 0f }).Validate();
    }
}
