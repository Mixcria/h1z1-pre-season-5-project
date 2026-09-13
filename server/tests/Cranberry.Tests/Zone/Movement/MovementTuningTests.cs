using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

// docs/49 §I2. Every movement number is [DESIGN] — the August client ships none (§5) — so the owner
// has to be able to retune them without a rebuild, and a typo in a launch script must never take the
// host down or silently ship a nonsense speed.
public sealed class MovementTuningTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out string? value) ? value : null;
    }

    [Fact]
    public void TheDefaultPresetIsTheProfileDefaultInstanceItself()
    {
        // Not merely equal: ZoneService.SendMovementStats short-circuits on
        // ReferenceEquals(tracker.Profile, options.Movement), so a second equal-but-distinct default
        // would call SetProfile — which clears StatsDelivered — and re-arm the 0f 40 burst on every
        // single resync (docs/49 §I3).
        Assert.Same(MovementProfile.Default, MovementTuning.Aug2017Default);
        Assert.Same(MovementProfile.Default, MovementTuning.FromNameOrDefault(null));
        Assert.Same(MovementProfile.Default, MovementTuning.FromNameOrDefault("Aug2017Default"));
        Assert.Same(MovementProfile.Default, MovementTuning.FromNameOrDefault("  aug2017default  "));
        Assert.Same(MovementProfile.Default, MovementTuning.FromEnvironment(Env(), out string? note));
        Assert.Null(note);
    }

    [Fact]
    public void Wave3LegacyReproducesTheBurstTheCaptureWasDerivedFrom()
    {
        // captures\wire-20260829-220829.txt line 290 carried sprint 1.45, backpedal 0.65,
        // strafe 0.80 on a base of 5.50, and the client's own reported speeds landed on all four
        // (docs/49 §2.2). This preset is the A/B control, the movement lane's LegacyD18Grey.
        MovementProfile legacy = MovementTuning.Wave3Legacy;
        Assert.Equal(5.50f, legacy.MaxMovementSpeed, 3);
        Assert.Equal(1.45f, legacy.SprintSpeedModifier, 3);
        Assert.Equal(0.65f, legacy.BackpedalSpeedModifier, 3);
        Assert.Equal(0.80f, legacy.StrafeSpeedModifier, 3);
        Assert.Equal(7.975f, legacy.SprintSpeed, 3);
        Assert.Equal(3.575f, legacy.BackpedalSpeed, 3);
        Assert.Equal(4.400f, legacy.StrafeSpeed, 3);

        // And — the point of docs/76 §7.3 — every OTHER field is stated absolutely too, so that
        // moving the live default (as wave 8 did) cannot silently redefine this historical preset.
        // These are wave 3's values, not the default's, and asserting them against literals rather
        // than against MovementProfile.Default is deliberate: the previous version of this test
        // compared them to the default and would have gone on passing while the preset drifted.
        Assert.Equal(0.55f, legacy.CrouchSpeedModifier, 5);
        Assert.Equal(0.45f, legacy.WalkSpeedModifier, 5);
        Assert.Equal(0.25f, legacy.ProneSpeedModifier, 5);
        Assert.Equal(0.75f, legacy.WaterSpeedModifier, 5);
        Assert.Equal(0.35f, legacy.SprintAccelerationTime, 5);
        Assert.Equal(0.25f, legacy.SprintDecelerationTime, 5);
        Assert.Equal(0.20f, legacy.ForwardAccelerationTime, 5);
        Assert.Equal(0.15f, legacy.ForwardDecelerationTime, 5);
        Assert.Equal(0.20f, legacy.BackAccelerationTime, 5);
        Assert.Equal(0.15f, legacy.BackDecelerationTime, 5);
        Assert.Equal(0.15f, legacy.StrafeAccelerationTime, 5);
        Assert.Equal(0.15f, legacy.StrafeDecelerationTime, 5);

        // Not one field of it is the wave-8 default any more.
        Assert.NotEqual(MovementProfile.Default, legacy);
    }

    [Fact]
    public void Wave5LegacyIsTheOneWordRevertOfTheZ1Port()
    {
        // docs/76 §7.3: the set the owner actually played through waves 4-7, kept so the Z1 port can
        // be A/B'd against it in two restarts rather than a rebuild.
        MovementProfile legacy = MovementTuning.Wave5Legacy;
        Assert.Equal(5.50f, legacy.MaxMovementSpeed, 5);
        Assert.Equal(1.20f, legacy.SprintSpeedModifier, 5);
        Assert.Equal(0.45f, legacy.WalkSpeedModifier, 5);
        Assert.Equal(0.55f, legacy.CrouchSpeedModifier, 5);
        Assert.Equal(0.50f, legacy.BackpedalSpeedModifier, 5);
        Assert.Equal(0.75f, legacy.StrafeSpeedModifier, 5);
        Assert.Equal(6.600f, legacy.SprintSpeed, 0.001f);
        Assert.Equal(2.750f, legacy.BackpedalSpeed, 0.001f);

        // Stated absolutely, for the same reason Wave3Legacy is.
        Assert.Equal(0.35f, legacy.SprintAccelerationTime, 5);
        Assert.Equal(0.25f, legacy.SprintDecelerationTime, 5);
        Assert.Equal(0.15f, legacy.StrafeAccelerationTime, 5);
        Assert.Equal(0.15f, legacy.StrafeDecelerationTime, 5);
        Assert.Same(legacy, MovementTuning.FromNameOrDefault("wave5legacy"));
    }

    [Fact]
    public void GroundedIsStillOneStepSlowerThanTheDefault()
    {
        // docs/76 §7.3 item 4: it used to be base 5.00 / sprint 1.25, which was "one step slower"
        // only while the default was 5.50 — against the owner's 4.10 it was FASTER than the thing it
        // claimed to slow down. Re-pointed at 3.70 / 1.30, and asserted RELATIVE to the default so
        // the next retune cannot make its doc-comment a lie again.
        MovementProfile grounded = MovementTuning.Grounded;
        MovementProfile def = MovementProfile.Default;
        Assert.True(grounded.RunSpeed < def.RunSpeed, "Grounded must jog slower than the default.");
        Assert.True(grounded.SprintSpeed < def.SprintSpeed, "Grounded must sprint slower than the default.");
        Assert.Equal(3.70f, grounded.MaxMovementSpeed, 5);
        Assert.Equal(4.810f, grounded.SprintSpeed, 0.001f);

        // It deliberately tracks the owner's modifiers and blend times — it is his movement dialled
        // down, not a frozen historical set.
        Assert.Equal(def.WalkSpeedModifier, grounded.WalkSpeedModifier, 5);
        Assert.Equal(def.CrouchSpeedModifier, grounded.CrouchSpeedModifier, 5);
        Assert.Equal(def.SprintAccelerationTime, grounded.SprintAccelerationTime, 5);
    }

    [Fact]
    public void EveryPresetIsSendableAndNamed()
    {
        Assert.Equal(
            new[] { "Aug2017Default", "Wave5Legacy", "Wave3Legacy", "Grounded" },
            MovementTuning.PresetNames);
        foreach ((string name, MovementProfile profile) in MovementTuning.All)
        {
            profile.Validate();
            Assert.Equal(MovementProfile.StatCount, profile.ToStats().Count);
            Assert.Equal(name, MovementTuning.NameOf(profile));
            Assert.True(MovementTuning.IsKnownPreset(name));
            Assert.Same(profile, MovementTuning.FromNameOrDefault(name.ToUpperInvariant()));
        }
    }

    [Fact]
    public void AnUnknownPresetFallsBackAndSaysSo()
    {
        MovementProfile profile = MovementTuning.FromEnvironment(
            Env((MovementTuning.PresetVariable, "Fast")), out string? note);

        Assert.Same(MovementProfile.Default, profile);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note);
        Assert.Contains("CRANBERRY_MOVE_PRESET='Fast'", note);
        Assert.Contains("Wave3Legacy", note);
    }

    [Fact]
    public void OverridesApplyOnTopOfThePreset()
    {
        MovementProfile profile = MovementTuning.FromEnvironment(
            Env(
                (MovementTuning.PresetVariable, "Grounded"),
                ("CRANBERRY_MOVE_SPRINT", "1.30"),
                ("CRANBERRY_MOVE_BACK", "0.55")),
            out string? note);

        Assert.Equal(3.70f, profile.MaxMovementSpeed, 3);   // from the preset
        Assert.Equal(1.30f, profile.SprintSpeedModifier, 3); // from the override
        Assert.Equal(0.55f, profile.BackpedalSpeedModifier, 3);
        Assert.Equal(0.30f, profile.WalkSpeedModifier, 3);  // untouched by either

        Assert.NotNull(note);
        Assert.Contains("preset Grounded", note);
        Assert.Contains("SprintSpeedModifier=1.3", note);
        Assert.DoesNotContain("IGNORED", note);
        Assert.Contains("sprint 4.81", note);
    }

    [Fact]
    public void EveryDocumentedKnobExistsAndMovesItsOwnValueOnly()
    {
        string[] expected =
        [
            "CRANBERRY_MOVE_BASE", "CRANBERRY_MOVE_SPRINT", "CRANBERRY_MOVE_WALK",
            "CRANBERRY_MOVE_CROUCH", "CRANBERRY_MOVE_BACK", "CRANBERRY_MOVE_STRAFE",
            "CRANBERRY_MOVE_SWIM", "CRANBERRY_MOVE_WATER",
            "CRANBERRY_MOVE_SPRINT_ACCEL", "CRANBERRY_MOVE_SPRINT_DECEL",
            "CRANBERRY_MOVE_FWD_ACCEL", "CRANBERRY_MOVE_BACK_ACCEL", "CRANBERRY_MOVE_STRAFE_ACCEL",
        ];
        Assert.Equal(expected, MovementTuning.Knobs.Select(k => k.Variable));

        foreach (MovementTuning.MovementKnob knob in MovementTuning.Knobs)
        {
            float value = (knob.Minimum + knob.Maximum) / 2f;
            MovementProfile tuned = MovementTuning.FromEnvironment(
                Env((knob.Variable, value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture))),
                out string? note);

            Assert.Equal(value, knob.Read(tuned), 3);
            Assert.NotNull(note);
            Assert.Contains(knob.Property, note);

            // Nothing else moved.
            foreach (MovementTuning.MovementKnob other in MovementTuning.Knobs.Where(k => k != knob))
            {
                Assert.Equal(other.Read(MovementProfile.Default), other.Read(tuned), 5);
            }
        }
    }

    [Theory]
    [InlineData("CRANBERRY_MOVE_SPRINT", "banana", "not a number")]
    [InlineData("CRANBERRY_MOVE_SPRINT", "1,45", "not a number")]      // invariant culture only
    [InlineData("CRANBERRY_MOVE_SPRINT", "NaN", "not a number")]
    [InlineData("CRANBERRY_MOVE_SPRINT", "0", "outside")]
    [InlineData("CRANBERRY_MOVE_SPRINT", "-1", "outside")]
    [InlineData("CRANBERRY_MOVE_BASE", "99", "outside")]               // above the client's 12.0 ceiling
    [InlineData("CRANBERRY_MOVE_BASE", "0.1", "outside")]
    [InlineData("CRANBERRY_MOVE_SPRINT_ACCEL", "-0.5", "outside")]
    [InlineData("CRANBERRY_MOVE_SPRINT_ACCEL", "60", "outside")]
    public void ATypoIsIgnoredWithANoteAndNeverThrows(string variable, string value, string reason)
    {
        // A bad launch-script variable must not brick movement or stop the host: the profile that
        // comes back is the untouched preset, and the note says which variable was dropped and why.
        MovementProfile profile = MovementTuning.FromEnvironment(Env((variable, value)), out string? note);

        Assert.Same(MovementProfile.Default, profile);
        Assert.NotNull(note);
        Assert.Contains("IGNORED", note);
        Assert.Contains(variable, note);
        Assert.Contains(reason, note);
    }

    [Fact]
    public void AnEmptyVariableIsNotAnOverride()
    {
        // PowerShell's `$env:X = ''` and an unset variable must behave the same way.
        Assert.Same(
            MovementProfile.Default,
            MovementTuning.FromEnvironment(Env(("CRANBERRY_MOVE_SPRINT", "   ")), out string? note));
        Assert.Null(note);
    }

    [Fact]
    public void ZeroIsALegalBlendTimeBecauseItIsTheClientsOwnDefault()
    {
        // docs/40 §2.4: absent ⇒ 0 ⇒ the sprint blend snaps instantly. It is ugly, not invalid.
        MovementProfile profile = MovementTuning.FromEnvironment(
            Env(("CRANBERRY_MOVE_SPRINT_ACCEL", "0")), out string? note);

        Assert.Equal(0f, profile.SprintAccelerationTime, 5);
        Assert.NotNull(note);
        Assert.DoesNotContain("IGNORED", note);
    }

    [Fact]
    public void TheNoteCarriesTheSpeedsAMatchWillActuallyRunAt()
    {
        // The speeds the owner feels, in the units he feels them in. (The exact decimal a float
        // rounds to at two places is not the subject here — the ladder itself is pinned to
        // 0.001 m/s in MovementProfileTests.)
        string now = MovementTuning.DescribeSpeeds(MovementProfile.Default);
        Assert.StartsWith("run 4.10, sprint 5.74,", now);

        // 3.07, not 3.08: 4.10f x 0.75f is 3.0749998 as a float and F2 rounds it down. Worth
        // knowing before reading the host banner against docs/76's 3.08 (docs/76 Built).
        Assert.Contains("backpedal 3.07 m/s", now);
        Assert.Contains("strafe 3.07", now);

        string wave3 = MovementTuning.DescribeSpeeds(MovementTuning.Wave3Legacy);
        Assert.StartsWith("run 5.50, sprint 7.9", wave3);
        Assert.Contains("strafe 4.40", wave3);
        Assert.Contains("backpedal 3.5", wave3);
    }

    [Fact]
    public void ATunedProfileStillSerialisesAsTheEighteenEntryBurst()
    {
        MovementProfile profile = MovementTuning.FromEnvironment(
            Env(("CRANBERRY_MOVE_BASE", "7.25"), ("CRANBERRY_MOVE_SPRINT", "1.05")), out _);

        CharacterStatPackets.UpdateStat burst = CharacterStatPackets.UpdateStat.ForProfile(0x1003UL, profile);
        Assert.Equal(248, burst.Length);
        Assert.Equal(
            7.25f,
            burst.Stats.Single(s => s.StatId == CharacterStatId.MaxMovementSpeed).EffectiveValue,
            3);
        Assert.Equal(7.6125f, profile.SprintSpeed, 0.0005f);
    }
}
