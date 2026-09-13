using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

// docs/49 §3 — the channel-2 Posture word, decoded by correlating each bit against the client's own
// reported horizontal speed over the 745 speed samples in captures\wire-20260829-220829.txt. This
// closes docs/17 §2.1's [BLOCKED] ("no bit position was tied to crouch/prone/sprint/walk/run").
// The posture values below are the ones that actually appear in that capture.
public sealed class MovementPostureTests
{
    [Fact]
    public void TheProvenBitPositionsAreTheOnesTheCaptureCorrelated()
    {
        Assert.Equal(1, MovementPostureBits.Crouching);
        Assert.Equal(2, MovementPostureBits.Sprinting);
        Assert.Equal(5, MovementPostureBits.Airborne);
        Assert.Equal(6, MovementPostureBits.Stopped);
        Assert.Equal(10, MovementPostureBits.OnGround);
        Assert.Equal(15, MovementPostureBits.MovingBackwards);
        Assert.Equal(0x0002u, MovementPostureBits.Mask(MovementPostureBits.Crouching));
        Assert.Equal(0x0400u, MovementPostureBits.Mask(MovementPostureBits.OnGround));
        Assert.Equal(0x8000u, MovementPostureBits.Mask(MovementPostureBits.MovingBackwards));
    }

    [Theory]
    // posture,      stance,                     backwards, on ground
    [InlineData(0x0401u, MovementStance.Standing, false, true)]   // forward run — plateau 5.5
    [InlineData(0x0405u, MovementStance.Sprinting, false, true)]  // sprint — plateau 8.0, n = 57
    [InlineData(0x0403u, MovementStance.Crouching, false, true)]  // crouch — plateau 3.0
    [InlineData(0x8401u, MovementStance.Standing, true, true)]    // backpedal — plateau 3.6
    [InlineData(0x8403u, MovementStance.Crouching, true, true)]   // crouch + backpedal — plateau 2.0
    [InlineData(0x0021u, MovementStance.Standing, false, false)]  // airborne, free-fall
    public void ThePlateauPosturesReadBackAsTheModeTheyMeasured(
        uint word,
        MovementStance stance,
        bool backwards,
        bool onGround)
    {
        var posture = new MovementPosture(word);
        Assert.Equal(stance, posture.Stance);
        Assert.Equal(backwards, posture.IsMovingBackwards);
        Assert.Equal(backwards ? MovementAxis.Backward : MovementAxis.Forward, posture.Axis);
        Assert.Equal(onGround, posture.IsOnGround);
        Assert.Equal(!onGround, posture.IsAirborne);
        Assert.True(posture.IsPresent);
    }

    [Fact]
    public void EachPlateauPostureNamesTheModifierThatProducedItsPlateau()
    {
        // The correlation that identified the bits, replayed against the burst the capture carried
        // (MovementTuning.Wave3Legacy): every observed plateau is base × the modifier for that bit.
        MovementProfile sent = MovementTuning.Wave3Legacy;

        Assert.Equal(5.500f, Speed(sent, 0x0401u), 0.001f);  // observed 5.5
        Assert.Equal(7.975f, Speed(sent, 0x0405u), 0.001f);  // observed 8.0
        Assert.Equal(3.025f, Speed(sent, 0x0403u), 0.001f);  // observed 3.0
        Assert.Equal(3.575f, Speed(sent, 0x8401u), 0.001f);  // observed 3.6
        Assert.Equal(1.966f, Speed(sent, 0x8403u), 0.001f);  // observed 2.0

        static float Speed(MovementProfile profile, uint word)
        {
            var posture = new MovementPosture(word);
            return profile.SpeedFor(posture.Stance, posture.Axis);
        }
    }

    [Fact]
    public void CrouchBeatsSprintBecauseTheClientsScalarChainIsExclusive()
    {
        // FUN_1411acfa0 is an else-if chain that tests crouch before the sprint blend, so a crouching
        // character can never pick up SprintSpeedModifier (docs/40 §2.1).
        var both = new MovementPosture(0x0407u);
        Assert.True(both.IsCrouching);
        Assert.True(both.IsSprinting);
        Assert.Equal(MovementStance.Crouching, both.Stance);
    }

    [Fact]
    public void TheStopFlagIsBitSixAndAirborneIsComplementaryToOnGround()
    {
        // 209/209 zero-speed samples carried bit 6 and 0/536 moving ones did — independent wire
        // confirmation of docs/17 §2.1's FUN_1423391e0 "STOP FLAG".
        Assert.True(new MovementPosture(0x0441u).IsStopped);
        Assert.False(new MovementPosture(0x0401u).IsStopped);
        Assert.False(new MovementPosture(0x0401u).IsAirborne);
        Assert.True(new MovementPosture(0x0021u).IsAirborne);
        Assert.False(new MovementPosture(0x0021u).IsOnGround);
    }

    [Fact]
    public void StrafeAndWalkAreNotReadableAndTheApiSaysSoByOmission()
    {
        // There is no lateral bit: a strafe reports the same 0x0401 as a forward run, and only the
        // reported speed separates them. The walk toggle has no bit at all. Both facts are limits of
        // the wire, so neither may ever be inferred from a posture.
        var forward = new MovementPosture(0x0401u);
        Assert.Equal(MovementAxis.Forward, forward.Axis);
        Assert.NotEqual(MovementAxis.Strafe, forward.Axis);
        Assert.NotEqual(MovementStance.Walking, forward.Stance);

        foreach (uint word in new[] { 0x0401u, 0x0405u, 0x0403u, 0x8401u, 0x8403u, 0x0021u, 0x0441u })
        {
            MovementAxis axis = new MovementPosture(word).Axis;
            Assert.True(axis is MovementAxis.Forward or MovementAxis.Backward);
            Assert.NotEqual(MovementStance.Walking, new MovementPosture(word).Stance);
        }
    }

    [Fact]
    public void ItPrintsTheWayTheCaptureDecodersPrint()
    {
        Assert.Equal("0x00010405", new MovementPosture(0x00010405u).ToString());
        Assert.Equal("Sprinting", new MovementPosture(0x0405u).Describe());
        Assert.Equal("Crouching backwards", new MovementPosture(0x8403u).Describe());
        Assert.Equal("Standing backwards", new MovementPosture(0x8401u).Describe());
    }
}
