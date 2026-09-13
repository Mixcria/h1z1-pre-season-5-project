using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// docs/89 — wave 9's weapons/melee lane. What is pinned here is the port itself: the
/// <c>0xa0</c> ability wire, the swing state machine, the melee damage table, the reload reply and
/// the ability ids taken from August's own datasheet rather than the owner's 2016 sheet.
/// <para>
/// <b>Nothing in this file is LIVE-VERIFIED.</b> There are zero <c>0xa0</c> packets in either
/// direction across all 111 captures, so these tests pin what the server builds, never what a
/// client did with it (D29).
/// </para>
/// </summary>
public sealed class Wave9MeleeAndWieldTests
{
    private const uint Machete = 84;      // blade, x3 in the owner's table
    private const uint Crowbar = 66;      // blunt, x2
    private const uint Fists = 85;        // x1
    private const uint HuntingRifle = 2229;
    private static readonly CombatOptions AbilityFallback = CombatOptions.Default with { MeleeOnTrigger = false };

    // ------------------------------------------------------------------ the ability wire

    /// <summary>
    /// <c>a0 03 UninitAbility</c> is 14 bytes and is the owner's own captured packet with the
    /// base byte shifted down by one: his fists bytes are
    /// <c>a1 03 04000000 75f41000 03000000</c>.
    /// </summary>
    [Fact]
    public void UninitAbilityIsHisCapturedPacketWithTheBaseShiftedByOne()
    {
        byte[] packet = AbilityPackets.UninitAbility(1_111_157);

        Assert.Equal(
            Convert.FromHexString("A003040000007" + "5F41000" + "03000000"),
            packet);
        Assert.Equal(14, packet.Length);
        Assert.Equal(ZoneOpcodes.AbilitiesBase, packet[0]);
        Assert.Equal(AbilityOpcodes.UninitAbilitySub, packet[1]);
    }

    /// <summary>
    /// The manager always leads with the client's own hardcoded head entry — loadout slot 1, item
    /// 83, ability 1111164 — and that ability is <b>August's own</b>
    /// <c>ACTIVATABLE_ABILITY_ID</c> for item 83, which is the strongest corroboration available
    /// that the owner's writer carries correct data rather than inherited guesswork.
    /// </summary>
    [Fact]
    public void TheHeadEntryAbilityIsAugustsOwnRowForItemEightyThree()
    {
        Assert.Equal(
            AbilityPackets.HeadEntryAbilityId,
            AugustAbilityFacts.AbilityIdOf(AbilityPackets.HeadEntryItemDefinitionId));

        byte[] packet = AbilityPackets.SetActivatableAbilityManager(
            new List<(uint, uint)> { (1, AbilityPackets.HeadEntryItemDefinitionId) });

        Assert.Equal(ZoneOpcodes.AbilitiesBase, packet[0]);
        Assert.Equal(AbilityOpcodes.SetActivatableAbilityManagerSub, packet[1]);
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 2));

        // slot, abilityLineId, arrayCount=1, ability, ability, 0, 2, itemDefinitionId, 0x40
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 6));
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 10));
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 14));
        Assert.Equal(AbilityPackets.HeadEntryAbilityId, BitConverter.ToUInt32(packet, 18));
        Assert.Equal(AbilityPackets.HeadEntryAbilityId, BitConverter.ToUInt32(packet, 22));
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 26));
        Assert.Equal(2u, BitConverter.ToUInt32(packet, 30));
        Assert.Equal(AbilityPackets.HeadEntryItemDefinitionId, BitConverter.ToUInt32(packet, 34));
        Assert.Equal(0x40, packet[38]);
        Assert.Equal(39, packet.Length);
    }

    /// <summary>
    /// The fists entry — and only the fists entry — carries an extra element in front of its own
    /// ability, so its entry is three dwords longer than any other.
    /// </summary>
    [Fact]
    public void OnlyTheFistsEntryCarriesTheExtraElement()
    {
        byte[] plain = AbilityPackets.SetActivatableAbilityManager(
            new List<(uint, uint)> { (7, Machete) });
        byte[] fists = AbilityPackets.SetActivatableAbilityManager(
            new List<(uint, uint)> { (7, Fists) });

        Assert.Equal(plain.Length + 12, fists.Length);
        Assert.Equal(2u, BitConverter.ToUInt32(fists, 14));                      // arrayCount
        Assert.Equal(AbilityPackets.FistsExtraAbilityId, BitConverter.ToUInt32(fists, 18));
        Assert.Equal(AbilityPackets.FistsExtraAbilityId, BitConverter.ToUInt32(fists, 22));
    }

    /// <summary>The empty item column must not suppress the August punch combo's ability.</summary>
    [Fact]
    public void FistsAdvertiseTheSamePunchAbilityAsTheirFireMode()
    {
        Assert.Equal(0u, AugustAbilityFacts.AbilityIdOf(Fists));
        Assert.Equal(AbilityPackets.Z1FistsAbilityId, AugustAbilityFacts.AbilityIdOf(113));
        Assert.Equal(1_111_157u, AbilityPackets.AbilityIdOf(Fists));

        Assert.Equal(1_111_157u, AbilityPackets.FistsAbilityOverride);
        Assert.Equal(AugustAbilityFacts.AbilityIdOf(Machete), AbilityPackets.AbilityIdOf(Machete));
        Assert.Equal(1_111_157u, AbilityPackets.Z1FistsAbilityId);
    }

    /// <summary>
    /// An occupant with no <c>ClientItemDefinitions</c> row is skipped, exactly as the client's own
    /// writer skips it — otherwise the two sides disagree on the entry count and so on the length.
    /// </summary>
    [Fact]
    public void ALoadoutOccupantWithNoDatasheetRowIsSkipped()
    {
        var loadout = new SetLoadoutSlots(
            CharacterGuid: 0x3100_0000_0000_0001,
            LoadoutId: 3,
            Slots:
            [
                new LoadoutSlotEntry(1, new LoadoutSlotRecord(3, 1, 0x11, Machete)),
                new LoadoutSlotEntry(2, new LoadoutSlotRecord(3, 2, 0x12, 999_999)),   // no row
                new LoadoutSlotEntry(3, new LoadoutSlotRecord(3, 3, 0x13, 0)),         // empty
            ],
            CurrentSlotId: SurvivorLoadout.Fists);

        Assert.False(AugustAbilityFacts.HasRow(999_999));

        byte[] packet = AbilityPackets.SetActivatableAbilityManager(loadout);

        // The head entry plus the one occupant that resolves.
        Assert.Equal(2u, BitConverter.ToUInt32(packet, 2));
    }

    // ------------------------------------------------------------------ the swing

    /// <summary>
    /// <b>The state machine is the whole of the port's correctness.</b> The client sends an ability
    /// twice per hit — once on the click, once on the collision — and only the second is the hit.
    /// An <c>a0 02</c> with no <c>a0 01</c> before it is ignored, which is what stops one click
    /// paying damage twice.
    /// </summary>
    [Fact]
    public void OnlyTheSecondPacketIsTheHitAndAnUnarmedUpdateIsIgnored()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        PracticeTarget dummy = session.Targets.Spawn(Vector3.Zero, 0f, 1, 2f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        // An UpdateAbility with nothing before it does nothing at all.
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.UpdateAbilitySub, 1_111_165), AbilityFallback,
            Machete, Vector3.Zero, 1_000, results);

        Assert.Single(results);
        Assert.Contains("no InitAbility before it", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(10_000, dummy.Health);
        Assert.Equal(0, session.MeleeSwings);

        // Arm, then land.
        results.Clear();
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.InitAbilitySub, 1_111_165), AbilityFallback,
            Machete, Vector3.Zero, 1_100, results);
        Assert.Contains("swing STARTED", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(10_000, dummy.Health);

        results.Clear();
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.UpdateAbilitySub, 1_111_165), AbilityFallback,
            Machete, Vector3.Zero, 1_200, results);

        Assert.Equal(3_000, 10_000 - dummy.Health);       // a blade is x3 the 1000 base
        Assert.Equal(1, session.MeleeSwings);
        Assert.NotNull(results[0].Marker);
        Assert.Same(dummy, results[0].HitTarget);

        // ...and the swing is spent, so a repeated UpdateAbility is refused again.
        results.Clear();
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.UpdateAbilitySub, 1_111_165), AbilityFallback,
            Machete, Vector3.Zero, 1_300, results);
        Assert.Equal(3_000, 10_000 - dummy.Health);
    }

    /// <summary>The owner's damage table, with fixed damage regardless of the ability's hit-location claim.</summary>
    [Fact]
    public void TheDamageTableIsHisBladesTimesThreeBluntTimesTwoFistsTimesOne()
    {
        Assert.Equal(3_000, MeleeArm.MeleeDamageFor(Machete));
        Assert.Equal(3_000, MeleeArm.MeleeDamageFor(83));     // combat knife
        Assert.Equal(2_000, MeleeArm.MeleeDamageFor(Crowbar));
        Assert.Equal(1_000, MeleeArm.MeleeDamageFor(Fists));
        Assert.Equal(1_000, MeleeArm.MeleeDamageFor(HuntingRifle));   // unlisted: the default arm

        var session = new SessionCombat { MeleeHeading = 0f };
        PracticeTarget dummy = session.Targets.Spawn(Vector3.Zero, 0f, 1, 2f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        Swing(session, results, Fists, "HEAD", Vector3.Zero);
        Assert.Equal(1_000, 10_000 - dummy.Health);           // no client-controlled headshot bonus
        Assert.DoesNotContain("headshot x2", results[^1].Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The range gate is the owner's requested close contact — the reference has none at all — and a swing
    /// that reaches nothing says so rather than silently doing nothing.
    /// </summary>
    [Fact]
    public void ASwingOutOfRangeHitsAir()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        PracticeTarget dummy = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        Swing(session, results, Machete, "None", new Vector3(0, 0, -20));

        Assert.Equal(10_000, dummy.Health);
        Assert.Contains("hit AIR", results[^1].Line, StringComparison.Ordinal);
        Assert.Null(results[^1].HitTarget);
    }

    /// <summary>A dead dummy is not swung at again, and a kill is reported once.</summary>
    [Fact]
    public void AMacheteKillsInFourSwingsAndTheCorpseIsNotHitAgain()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        PracticeTarget dummy = session.Targets.Spawn(Vector3.Zero, 0f, 1, 2f, 10_000)[0];
        var results = new List<WeaponArmResult>();

        for (int i = 0; i < 3; i++)
        {
            Swing(session, results, Machete, "None", Vector3.Zero);
            Assert.True(dummy.IsAlive);
            Assert.False(results[^1].Killed);
        }

        Swing(session, results, Machete, "None", Vector3.Zero);
        Assert.False(dummy.IsAlive);
        Assert.True(results[^1].Killed);

        Swing(session, results, Machete, "None", Vector3.Zero);
        Assert.Contains("hit AIR", results[^1].Line, StringComparison.Ordinal);
    }

    /// <summary><c>CRANBERRY_MELEE_DAMAGE=0</c> parses and logs the swing and hurts nobody.</summary>
    [Fact]
    public void TheMeleeDamageSwitchParsesAndLogsAndHurtsNobody()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        PracticeTarget dummy = session.Targets.Spawn(Vector3.Zero, 0f, 1, 2f, 10_000)[0];
        var results = new List<WeaponArmResult>();
        CombatOptions off = AbilityFallback with { MeleeDamage = false };

        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.InitAbilitySub, 1), off, Machete, Vector3.Zero, 1, results);
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.UpdateAbilitySub, 1), off, Machete, Vector3.Zero, 2, results);

        Assert.Equal(10_000, dummy.Health);
        Assert.Contains("NOT applied", results[^1].Line, StringComparison.Ordinal);
        Assert.Equal(1, session.MeleeSwings);
    }

    /// <summary>
    /// A packet no reader can make sense of logs its untruncated hex and does nothing. Those bytes
    /// are the only client-originated evidence this lane can produce, so they are never truncated.
    /// </summary>
    [Fact]
    public void AnUnreadableAbilityPacketPrintsAllOfItsBytesAndDoesNothing()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        var results = new List<WeaponArmResult>();

        MeleeArm.Handle(
            session, new byte[] { ZoneOpcodes.AbilitiesBase, 0x02, 0x00 }, CombatOptions.Default,
            Machete, Vector3.Zero, 1, results);

        Assert.Single(results);
        Assert.Contains("THESE BYTES ARE EVIDENCE", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("A00200", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(1, session.Undecodable);
    }

    /// <summary>
    /// The trailing hit-location string is read from the END, because the two packets have
    /// different fixed prefixes and neither middle is settled. A packet with no plausible trailer
    /// yields an empty string rather than a misread one.
    /// </summary>
    [Fact]
    public void TheHitLocationIsReadFromTheEnd()
    {
        Assert.True(AbilityPackets.TryReadAbilityRequest(
            Ability(AbilityOpcodes.UpdateAbilitySub, 1_111_164, "GLASSES"),
            out byte sub, out uint ability, out string where));

        Assert.Equal(AbilityOpcodes.UpdateAbilitySub, sub);
        Assert.Equal(1_111_164u, ability);
        Assert.Equal("GLASSES", where);
        Assert.True(HitRule.IsHead(where));

        Assert.True(AbilityPackets.TryReadAbilityRequest(
            Ability(AbilityOpcodes.InitAbilitySub, 7, string.Empty), out _, out _, out string none));
        Assert.Equal(string.Empty, none);
    }

    // ------------------------------------------------------------------ the reload reply

    /// <summary>
    /// <b>Cranberry answered a reload with nothing at all until wave 9.</b> It now answers
    /// <c>82 08</c>. The native little-endian counter must match the client's expected value:
    /// the former big-endian workaround bypassed the pump's reserve-staging branch.
    /// </summary>
    [Fact]
    public void AReloadRequestIsAnsweredAndTheCounterMatchesTheNativeLittleEndianValue()
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        session.Shooter.DeclareWeapon(0x4242, AugustHeldWeapon.ItemDefinitionId);
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(
            session, Weapon(0x07, BitConverter.GetBytes(0x4242UL)), AbilityFallback,
            AugustHeldWeapon.ItemDefinitionId, Vector3.Zero, 1, results);

        byte[] reply = Assert.IsType<byte[]>(results[0].Reply);
        Assert.Equal(ZoneOpcodes.WeaponBase, reply[0]);
        Assert.Equal(0x08, reply[5]);
        Assert.Equal(0x4242UL, BitConverter.ToUInt64(reply, 6));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, reply[^8..]);
        Assert.Contains("answered 82 08 Reload", results[0].Line, StringComparison.Ordinal);

        // Even an unknown guid gets a terminal refusal so its client cannot wait forever.
        results.Clear();
        WeaponFireArm.Handle(
            session, Weapon(0x07, BitConverter.GetBytes(0x9999UL)), AbilityFallback,
            AugustHeldWeapon.ItemDefinitionId, Vector3.Zero, 2, results);
        Assert.Equal(WeaponReplyPackets.ReloadRejected(0x9999UL), results[0].Reply);
    }

    /// <summary>
    /// The nine subs the owner answers and Cranberry did not are now NAMED in the log rather than
    /// dumped as unattributable hex — and none of them pays damage.
    /// </summary>
    [Theory]
    [InlineData(0x09, "ReloadInterrupt")]
    [InlineData(0x0c, "SwitchFireModeRequest")]
    [InlineData(0x19, "GuidedExplode")]
    [InlineData(0x21, "ProjectileContactReport")]
    [InlineData(0x22, "MeleeHitMaterial")]
    [InlineData(0x26, "GrenadeBounceReport")]
    [InlineData(0x27, "AimBlockedNotify")]
    public void TheOwnersOtherWeaponSubsAreNamedAndInert(byte sub, string name)
    {
        var session = new SessionCombat { MeleeHeading = 0f };
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(
            session, Weapon(sub, [1, 2, 3, 4]), AbilityFallback,
            AugustHeldWeapon.ItemDefinitionId, Vector3.Zero, 1, results);

        Assert.Single(results);
        Assert.Contains(name, results[0].Line, StringComparison.Ordinal);
        Assert.Null(results[0].Marker);
        Assert.Null(results[0].Reply);
    }

    // ------------------------------------------------------------------ the switches

    /// <summary>
    /// <b>Nothing in this lane needs an environment variable.</b> That is the whole point of the
    /// wave: the owner should not have to set four switches to hold a gun. Combat is enabled while
    /// the diagnostic practice target is opt-in, and the boot banner names both - unlike before,
    /// when
    /// <c>CombatOptions.FromEnvironment()</c> was never called and <c>Describe()</c> was never
    /// printed (docs/89 §1d).
    /// </summary>
    [Fact]
    public void CombatIsEnabledAndDiagnosticTargetsAreOptInAndTheBannerNamesBoth()
    {
        CombatOptions shipped = CombatOptions.FromEnvironment(_ => null);

        Assert.True(shipped.Enabled);
        Assert.True(shipped.EnableCombatDamage);
        Assert.True(shipped.SendHitMarker);
        Assert.True(shipped.MeleeDamage);
        Assert.True(shipped.SendAbilityManager);
        Assert.False(shipped.PracticeTarget);
        Assert.Equal(WeaponFireLayout.Z1Candidate, shipped.Layout);

        string banner = shipped.Describe();
        Assert.Contains("melee 0xa0=ON", banner, StringComparison.Ordinal);
        Assert.Contains("abilityManager=ON", banner, StringComparison.Ordinal);
        Assert.Contains("practiceTarget=off", banner, StringComparison.Ordinal);

        // ...and each one reverts on its own, with "0" and nothing else.
        Assert.False(CombatOptions
            .FromEnvironment(name => name == CombatOptions.AbilityManagerVariable ? "0" : null)
            .SendAbilityManager);
        Assert.False(CombatOptions
            .FromEnvironment(name => name == CombatOptions.MeleeDamageVariable ? "0" : null)
            .MeleeDamage);
        Assert.True(CombatOptions
            .FromEnvironment(name => name == CombatOptions.MeleeDamageVariable ? "false" : null)
            .MeleeDamage);
    }

    // ------------------------------------------------------------------ helpers

    private static void Swing(
        SessionCombat session,
        List<WeaponArmResult> results,
        uint heldItem,
        string where,
        Vector3 from)
    {
        long now = session.MeleeSwings * 1_000L + 1;
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.InitAbilitySub, 1, where), AbilityFallback,
            heldItem, from, now, results);
        MeleeArm.Handle(
            session, Ability(AbilityOpcodes.UpdateAbilitySub, 1, where), AbilityFallback,
            heldItem, from, now + 1, results);
    }

    /// <summary>
    /// One <c>0xa0</c> request shaped as the owner's reader expects it: the ability id at wire
    /// offset 10, and a counted string last.
    /// </summary>
    private static byte[] Ability(byte sub, uint abilityId, string hitLocation = "None")
    {
        using var writer = new PacketWriter(48);
        writer.WriteByte(ZoneOpcodes.AbilitiesBase);
        writer.WriteByte(sub);
        writer.WriteUInt64(0);              // the unsettled middle: offsets 2..9
        writer.WriteUInt32(abilityId);      // offset 10
        writer.WriteUInt64(0);              // targetCharacterId, zero on every one of his packets

        if (hitLocation.Length > 0)
        {
            writer.WriteUInt32((uint)hitLocation.Length);
            writer.WriteRaw(System.Text.Encoding.ASCII.GetBytes(hitLocation));
        }

        return writer.Written.ToArray();
    }

    /// <summary>One bare <c>0x82</c> packet: base, gameTime, sub, body.</summary>
    private static byte[] Weapon(byte sub, ReadOnlySpan<byte> body)
    {
        using var writer = new PacketWriter(16 + body.Length);
        writer.WriteByte(ZoneOpcodes.WeaponBase);
        writer.WriteUInt32(0);
        writer.WriteByte(sub);
        writer.WriteRaw(body);
        return writer.Written.ToArray();
    }
}
