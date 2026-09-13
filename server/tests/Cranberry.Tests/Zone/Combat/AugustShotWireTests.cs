using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// <b>The first real shot, byte for byte off the August wire</b> (docs/107 §10, session
/// <c>captures\wire-20260903-174857.txt</c>, 17:51:37-17:52:17).
/// <para>
/// Every packet quoted here is a whole c2s zone packet the August client sent, copied out of the
/// capture with only the leading channel byte removed. They settle three things the project had been
/// guessing at: <c>82 03 Fire</c> DOES arrive at 1148 and the owner's layout reads it exactly;
/// <c>82 20 WeaponFireHint</c> is a separate BARE packet carrying the same shot's direction; and the
/// client's <c>82 01 FireStateUpdate</c> never once said the magazine was dry while this server was
/// refusing all 53 pulls for an empty one.
/// </para>
/// </summary>
public sealed class AugustShotWireTests
{
    /// <summary>
    /// Item 2229, "AK-47", instance <c>0x310000000000000C</c> - the guid this server itself put in
    /// the <c>94 02</c> that drew it into body slot 7.
    /// </summary>
    private const ulong CapturedAk = 0x3100_0000_0000_000Cul;

    /// <summary>Item 1374, "12GA Pump Shotgun", instance <c>0x310000000000000E</c>.</summary>
    private const ulong CapturedShotgun = 0x3100_0000_0000_000Eul;

    private const uint AkItemId = 2229;

    /// <summary>Item 2325, "7.62x39 Round" - what the owner had 60 of in the bag.</summary>
    private const uint SevenSixTwo = 2325;

    /// <summary>17:51:37.391 - <c>82 1f</c> carrying <c>82 01 FireStateUpdate</c>, state 17.</summary>
    private const string CapturedFireState =
        "82000000001F01000000100000008292DEF216010C000000000000311100";

    /// <summary>17:51:37.395 - <c>82 1f</c> carrying the <c>82 03 Fire</c> for projectile 1.</summary>
    private const string CapturedFire =
        "82000000001F01000000260000008292DEF216030C000000000000318C1BCF433C16A141F9CD"
        + "E2C4010000000100000000000000";

    /// <summary>17:51:37.395 - the bare <c>82 20 WeaponFireHint</c> that pairs with it.</summary>
    private const string CapturedHintOne =
        "8292DEF216200C00000000000031FF8C1BCF433C16A141F9CDE2C4"
        + "0100000001000000F616623F068F703E34DFCF3E00000000";

    /// <summary>
    /// 17:52:17.106 - the shotgun's hint, and <b>the reason the direction gate accepts all zeros</b>:
    /// the client really does send a hint with no aim in it.
    /// </summary>
    private const string CapturedZeroDirectionHint =
        "82AF79F316200E00000000000031FF5D7AC043C6D79E417B0CE5C4"
        + "010000002100000000000000000000000000000000000000";

    /// <summary>The five consecutive bare <c>82 20</c> packets of the first burst.</summary>
    public static TheoryData<string, ulong, uint, float, float, float, float, float, float>
        CapturedHints() => new()
        {
            {
                "8292DEF216200C00000000000031FF8C1BCF433C16A141F9CDE2C4"
                    + "0100000001000000F616623F068F703E34DFCF3E00000000",
                CapturedAk, 1u,
                414.21521f, 20.135857f, -1814.4366f, 0.88316286f, 0.23492059f, 0.40599978f
            },
            {
                "8261DFF216200C00000000000031FFCDD1CE432D08A141EBDCE2C4"
                    + "01000000020000001CA2573F7C3A513E565CFF3E00000000",
                CapturedAk, 2u,
                413.63907f, 20.128992f, -1814.9037f, 0.84231734f, 0.20432466f, 0.49875134f
            },
            {
                "8220E0F216200C00000000000031FF9BF6CE432B01A141F9EBE2C4"
                    + "0100000003000000CA3A533FBA3F403E5069083F00000000",
                CapturedAk, 3u,
                413.92661f, 20.125570f, -1815.3741f, 0.82511580f, 0.18774310f, 0.53285694f
            },
            {
                "82CAE0F216200C00000000000031FF09FDCE43A6F7A0414DEEE2C4"
                    + "0100000004000000D3734F3F9A66273EB60A103F00000000",
                CapturedAk, 4u,
                413.97684f, 20.120922f, -1815.4469f, 0.81036109f, 0.16347733f, 0.56266344f
            },
            {
                "829EE2F216200C00000000000031FFD28ACE43F7DCA0410801E3C4"
                    + "0100000005000000DD0F683F2C65A13D1861D43E00000000",
                CapturedAk, 5u,
                413.08453f, 20.107893f, -1816.0322f, 0.90649205f, 0.07880625f, 0.41480327f
            },
        };

    // ------------------------------------------------------------------ what actually arrived

    [Fact]
    public void TheShotArrivesAsEightyTwoOhThreeInsideMultiWeapon()
    {
        // The finding this test exists to correct: the 17:51 session was read as "no 82 03 on this
        // build". There is one for every trigger pull, wrapped, and this layout reads it exactly.
        byte[] packet = Convert.FromHexString(CapturedFire);
        var members = new List<Range>();

        Assert.True(WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader outer));
        Assert.Equal(WeaponBaseDecoder.SubMultiWeapon, outer.Sub);
        Assert.True(WeaponBaseDecoder.TryReadMultiWeapon(packet, members));

        byte[] member = packet[Assert.Single(members)];
        Assert.True(WeaponBaseDecoder.TryReadHeader(member, out WeaponBaseHeader inner));
        Assert.Equal(WeaponBaseDecoder.SubFire, inner.Sub);
        Assert.Equal(385_015_442u, inner.GameTime);

        Assert.True(WeaponBaseDecoder.TryReadFire(member, out WeaponFire fire));
        Assert.Equal(CapturedAk, fire.WeaponGuid);
        Assert.Equal(414.21521f, fire.X, 3);
        Assert.Equal(20.135857f, fire.Y, 3);
        Assert.Equal(-1814.4366f, fire.Z, 3);
        Assert.Equal([1u], fire.ProjectileIds);
    }

    [Fact]
    public void TheClientNeverSaidTheMagazineWasDry()
    {
        // 112 FireStateUpdates in that session, values 17 and 0, and not one 64 - while the server
        // refused all 53 pulls against a magazine of 0. That contradiction is D223's whole evidence.
        byte[] packet = Convert.FromHexString(CapturedFireState);
        var members = new List<Range>();

        Assert.True(WeaponBaseDecoder.TryReadMultiWeapon(packet, members));
        byte[] member = packet[Assert.Single(members)];

        Assert.True(WeaponBaseDecoder.TryReadFireStateUpdate(member, out FireStateUpdate update));
        Assert.Equal(CapturedAk, update.WeaponGuid);
        Assert.Equal(17, update.FireState);
        Assert.NotEqual(WeaponBaseDecoder.EmptyFireState, update.FireState);
        Assert.Equal(0, update.Unknown);
    }

    // ------------------------------------------------------------------ 82 20, field for field

    [Theory]
    [MemberData(nameof(CapturedHints))]
    public void EveryCapturedFireHintDecodesFieldForField(
        string hex,
        ulong weaponGuid,
        uint projectileId,
        float x,
        float y,
        float z,
        float dx,
        float dy,
        float dz)
    {
        byte[] packet = Convert.FromHexString(hex);
        Assert.Equal(51, packet.Length);

        Assert.True(WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header));
        Assert.Equal(WeaponBaseDecoder.SubWeaponFireHint, header.Sub);

        Assert.True(WeaponBaseDecoder.TryReadWeaponFireHint(packet, out WeaponFireHint hint));
        Assert.Equal(weaponGuid, hint.WeaponGuid);
        Assert.Equal(WeaponBaseDecoder.WeaponFireHintMarkerByte, hint.Marker);
        Assert.Equal(x, hint.X, 3);
        Assert.Equal(y, hint.Y, 3);
        Assert.Equal(z, hint.Z, 3);

        // One entry, because a rifle fires one projectile - the u32 a flat reading takes for a
        // constant 1 is this LIST's count (FUN_140e5b6f0, stride 0x28).
        WeaponFireHintEntry entry = Assert.Single(hint.Hints);
        Assert.Equal(projectileId, entry.ProjectileId);
        Assert.Equal(dx, entry.DirectionX, 5);
        Assert.Equal(dy, entry.DirectionY, 5);
        Assert.Equal(dz, entry.DirectionZ, 5);

        // ...and the trailing u32 is this entry's own candidate-target list, empty because the
        // owner was alone in the match.
        Assert.Empty(entry.CandidateTargets);

        // The arithmetic no wrong field boundary survives.
        Assert.Equal(1f, entry.DirectionLength, 3);
    }

    [Fact]
    public void TheHintCarriesTheSameMuzzlePointAndProjectileAsItsFire()
    {
        byte[] fire = Convert.FromHexString(CapturedFire);
        var members = new List<Range>();
        Assert.True(WeaponBaseDecoder.TryReadMultiWeapon(fire, members));
        Assert.True(WeaponBaseDecoder.TryReadFire(fire[members[0]], out WeaponFire shot));

        Assert.True(WeaponBaseDecoder.TryReadWeaponFireHint(
            Convert.FromHexString(CapturedHintOne), out WeaponFireHint hint));

        // Bit for bit, which is what proves the pair is one trigger pull and not two - the client
        // builds both objects in FUN_140e74b30 from the same origin vector.
        Assert.Equal(shot.WeaponGuid, hint.WeaponGuid);
        Assert.Equal(shot.X, hint.X);
        Assert.Equal(shot.Y, hint.Y);
        Assert.Equal(shot.Z, hint.Z);
        Assert.Equal(shot.ProjectileIds.Length, hint.Hints.Count);
        Assert.Equal(shot.ProjectileIds[0], hint.Hints[0].ProjectileId);
    }

    [Fact]
    public void AZeroDirectionHintIsARealValueAndIsAccepted()
    {
        byte[] packet = Convert.FromHexString(CapturedZeroDirectionHint);

        Assert.True(WeaponBaseDecoder.TryReadWeaponFireHint(packet, out WeaponFireHint hint));
        Assert.Equal(CapturedShotgun, hint.WeaponGuid);

        WeaponFireHintEntry entry = Assert.Single(hint.Hints);
        Assert.Equal(33u, entry.ProjectileId);
        Assert.True(entry.DirectionIsZero);
        Assert.Equal(0f, entry.DirectionLength);

        // FUN_140e74b30 pushes the entry unconditionally, before any of the projectile's state
        // tests, and nothing normalises proj+0x3d0 first - so an all-zero direction is a value the
        // client really sends, not a boundary that moved. Its ORIGIN is still real.
        Assert.Equal(384.956f, hint.X, 2);
    }

    [Fact]
    public void AHintWithTheWrongLengthOrAnImpossibleDirectionIsRefused()
    {
        byte[] good = Convert.FromHexString(CapturedZeroDirectionHint);

        Assert.False(WeaponBaseDecoder.TryReadWeaponFireHint(good.AsSpan(0, 50).ToArray(), out _));
        Assert.False(WeaponBaseDecoder.TryReadWeaponFireHint([.. good, (byte)0], out _));

        // A direction that is neither zero nor unit length means a boundary moved.
        byte[] skewed = [.. good];
        BitConverter.GetBytes(7.5f).CopyTo(
            skewed,
            WeaponBaseDecoder.HeaderLength + WeaponBaseDecoder.WeaponFireHintFixedLength + 4);
        Assert.False(WeaponBaseDecoder.TryReadWeaponFireHint(skewed, out _));

        // A count the body cannot pay for is refused rather than walked off the end.
        byte[] overcount = [.. good];
        BitConverter.GetBytes(9u).CopyTo(overcount, WeaponBaseDecoder.HeaderLength + 21);
        Assert.False(WeaponBaseDecoder.TryReadWeaponFireHint(overcount, out _));

        // ...and a zero guid is never an item instance.
        byte[] guidless = [.. good];
        BitConverter.GetBytes(0ul).CopyTo(guidless, WeaponBaseDecoder.HeaderLength);
        Assert.False(WeaponBaseDecoder.TryReadWeaponFireHint(guidless, out _));
    }

    [Fact]
    public void AShotgunBlastCarriesOneHintEntryPerPelletAndItsCandidateTargets()
    {
        // No captured hint has more than one entry - the owner fired a rifle, alone. This is the
        // shape FUN_140e74b30 and FUN_140e5b6f0 describe, built here so a flat 45-byte reading can
        // never come back: the entry list is what a shotgun grows, and the nested guid list is what
        // a second player in the match grows.
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(CapturedShotgun));
        body.Add(WeaponBaseDecoder.WeaponFireHintMarkerByte);
        body.AddRange(BitConverter.GetBytes(1f));
        body.AddRange(BitConverter.GetBytes(2f));
        body.AddRange(BitConverter.GetBytes(3f));
        body.AddRange(BitConverter.GetBytes(3u));           // three pellets

        for (uint pellet = 0; pellet < 3; pellet++)
        {
            body.AddRange(BitConverter.GetBytes(100u + pellet));
            body.AddRange(BitConverter.GetBytes(1f));
            body.AddRange(BitConverter.GetBytes(0f));
            body.AddRange(BitConverter.GetBytes(0f));
            body.AddRange(BitConverter.GetBytes(pellet));   // 0, 1 and 2 candidate targets

            for (uint target = 0; target < pellet; target++)
            {
                body.AddRange(BitConverter.GetBytes(0x4400_0000_0000_0001ul + target));
            }
        }

        byte[] packet =
        [
            .. ShootingPacketBuilder.Header(WeaponBaseDecoder.SubWeaponFireHint),
            .. body,
        ];

        Assert.True(WeaponBaseDecoder.TryReadWeaponFireHint(packet, out WeaponFireHint hint));
        Assert.Equal(3, hint.Hints.Count);
        Assert.Equal([100u, 101u, 102u], hint.Hints.Select(h => h.ProjectileId));
        Assert.Empty(hint.Hints[0].CandidateTargets);
        Assert.Equal(0x4400_0000_0000_0001ul, Assert.Single(hint.Hints[1].CandidateTargets));
        Assert.Equal(2, hint.Hints[2].CandidateTargets.Count);
    }

    [Fact]
    public void TheSubTableIsTheClientsOwnRegistrationTable()
    {
        // FUN_1413d3520 registers every name with the composed id sub<<24 | 0x008200. These are the
        // four the 1087 lists never had, and the reason this table replaced the ported one.
        Assert.Equal("FireStateTargetedUpdate", WeaponBaseDecoder.NameOfSub(0x02));
        Assert.Equal("FireWithDefinitionMapping", WeaponBaseDecoder.NameOfSub(0x04));
        Assert.Equal("_UNUSED_10", WeaponBaseDecoder.NameOfSub(0x0a));
        Assert.Equal("ReloadRejected", WeaponBaseDecoder.NameOfSub(0x0b));

        // ...and the ones the loop acts on, unchanged.
        Assert.Equal("FireStateUpdate", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubFireStateUpdate));
        Assert.Equal("Fire", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubFire));
        Assert.Equal("ProjectileHitReport", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubProjectileHitReport));
        Assert.Equal("ReloadRequest", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubReloadRequest));
        Assert.Equal("MultiWeapon", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubMultiWeapon));
        Assert.Equal("WeaponFireHint", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubWeaponFireHint));
        Assert.Equal("AmmoCountAcknowledge", WeaponBaseDecoder.NameOfSub(WeaponBaseDecoder.SubAmmoCountAcknowledge));
        Assert.Equal("unnamed", WeaponBaseDecoder.NameOfSub(0x7f));
    }

    [Fact]
    public void TheClientHasNoWayToSayTheMagazineIsDry()
    {
        // 82 01's state byte is bit 0 = trigger down, and the two writers hard-code the rest: 17 on
        // the pull, 0 on the release, and no bit 6 anywhere. The 53 refused pulls were not the
        // client agreeing that the gun was empty - it had no word for it.
        Assert.Equal(0x01, WeaponBaseDecoder.FireStateTriggerDown);
        Assert.NotEqual(0, 17 & WeaponBaseDecoder.FireStateTriggerDown);
        Assert.Equal(0, 0 & WeaponBaseDecoder.FireStateTriggerDown);
        Assert.NotEqual(WeaponBaseDecoder.EmptyFireState, (byte)17);
    }

    // ------------------------------------------------------------------ the live arm

    [Fact]
    public void TheHintIsActedOnAndNeverSpendsARound()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results);
        Handle(session, ammo, Convert.FromHexString(CapturedFire), results);
        Assert.Equal(29, session.Shooter.AmmoOf(CapturedAk));

        Handle(session, ammo, Convert.FromHexString(CapturedHintOne), results);

        WeaponArmResult only = Assert.Single(results);
        Assert.Contains("82 20 WeaponFireHint", only.Line, StringComparison.Ordinal);
        Assert.Contains("AK-47", only.Line, StringComparison.Ordinal);
        Assert.Contains("#1", only.Line, StringComparison.Ordinal);
        Assert.Contains("the shot is corroborated", only.Line, StringComparison.Ordinal);

        // The whole point: the direction half of a pull costs nothing.
        Assert.Equal(29, session.Shooter.AmmoOf(CapturedAk));
    }

    [Fact]
    public void AHintWithNoShotBehindItSaysSoWithoutPayingAnything()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        var results = new List<WeaponArmResult>();

        Handle(session, ammo, Convert.FromHexString(CapturedHintOne), results);

        Assert.Contains("0/1 projectile(s) match a live fire hint", results[0].Line,
            StringComparison.Ordinal);
        Assert.Null(results[0].Marker);
    }

    [Fact]
    public void TheHintSwitchRestoresTheOldSeenNotActedOnLine()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        var results = new List<WeaponArmResult>();
        CombatOptions off = CombatOptions.Default with { ActOnWeaponFireHint = false };

        Handle(session, ammo, Convert.FromHexString(CapturedHintOne), results, options: off);

        Assert.Contains("seen, not acted on", results[0].Line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ D223, the magazine resync

    [Fact]
    public void TheCapturedSessionReplayedEndToEndFiresAndHits()
    {
        // Draw an empty AK (GunsSpawnEmpty), five trigger pulls exactly as the client sent them,
        // then the client's own hit report. Before D223 every one of these was refused.
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        PracticeTarget target = session.Targets.Spawn(Vector3.Zero, 0f, 1, 4f, 10_000)[0];
        var results = new List<WeaponArmResult>();
        int fired = 0;

        for (uint shot = 1; shot <= 5; shot++)
        {
            Handle(
                session, ammo, Convert.FromHexString(CapturedFireState), results, nowMs: shot * 200);
            Handle(session, ammo, Fire(shot), results, nowMs: shot * 200);
            fired += results.Count(r => r.Line.Contains("Fire FIRED", StringComparison.Ordinal));
        }

        Assert.Equal(5, fired);
        Assert.Equal(25, session.Shooter.AmmoOf(CapturedAk));
        Assert.Equal(5, session.Shooter.ShotsFired);
        Assert.Equal(0, session.Shooter.ShotsRefused);

        Handle(
            session,
            ammo,
            ShootingPacketBuilder.HitReport(3, target.WorldGuid, "SPINE"),
            results,
            nowMs: 1_200);

        Assert.Equal(1, session.Shooter.HitsRegistered);
        Assert.True(target.Health < 10_000);
        Assert.NotNull(results[0].Marker);
    }

    [Fact]
    public void TheResyncHappensOnceAndOnlyWhileTheClientIsNotDry()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        var results = new List<WeaponArmResult>();

        // The captured state update rides an 82 1f, so results[0] is the wrapper and [1] the member.
        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results);
        Assert.Contains("MAGAZINE RESYNC", results[^1].Line, StringComparison.Ordinal);
        Assert.Equal(30, session.Shooter.AmmoOf(CapturedAk));

        // Fire it dry, then say "not dry" again: a weapon that has fired is never resynced.
        for (uint shot = 1; shot <= 30; shot++)
        {
            Handle(session, ammo, Fire(shot), results, nowMs: shot * 200);
        }

        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, nowMs: 10_000);
        Assert.DoesNotContain("MAGAZINE RESYNC", results[^1].Line, StringComparison.Ordinal);
        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
    }

    [Fact]
    public void ADryClientIsBelievedAndGetsNothing()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        var results = new List<WeaponArmResult>();

        Handle(
            session,
            ammo,
            ShootingPacketBuilder.FireStateUpdate(CapturedAk, WeaponBaseDecoder.EmptyFireState),
            results);

        Assert.Contains("magazine is DRY", results[0].Line, StringComparison.Ordinal);
        Assert.DoesNotContain("MAGAZINE RESYNC", results[0].Line, StringComparison.Ordinal);

        // -1 is "combat has never heard of this instance": the dry arm declares nothing, so the
        // weapon is not even given the empty magazine the trigger would have given it.
        Assert.Equal(-1, session.Shooter.AmmoOf(CapturedAk));
        Assert.False(session.Shooter.Knows(CapturedAk));
    }

    [Fact]
    public void TheResyncSwitchRestoresTheRefusedSession()
    {
        (SessionCombat session, PlayerAmmoContext ammo) = AkSession(60);
        var results = new List<WeaponArmResult>();
        CombatOptions off = CombatOptions.Default with { MagazineResync = false };

        Handle(session, ammo, Convert.FromHexString(CapturedFireState), results, options: off);
        Handle(session, ammo, Convert.FromHexString(CapturedFire), results, options: off);

        Assert.Contains("Fire REFUSED", results[1].Line, StringComparison.Ordinal);
        Assert.Contains("magazine empty", results[1].Line, StringComparison.Ordinal);
        Assert.Equal(0, session.Shooter.AmmoOf(CapturedAk));
    }

    [Fact]
    public void BothSwitchesAreOnByDefaultAndNameThemselvesInTheBanner()
    {
        Assert.True(CombatOptions.Default.MagazineResync);
        Assert.True(CombatOptions.Default.ActOnWeaponFireHint);
        Assert.Contains(
            "magazineResync=ON", CombatOptions.Default.Describe(), StringComparison.Ordinal);
        Assert.Contains("fireHint=ON", CombatOptions.Default.Describe(), StringComparison.Ordinal);

        CombatOptions reverted = CombatOptions.FromEnvironment(
            name => name switch
            {
                CombatOptions.MagazineResyncVariable => "0",
                CombatOptions.WeaponFireHintVariable => "0",
                _ => null,
            });

        Assert.False(reverted.MagazineResync);
        Assert.False(reverted.ActOnWeaponFireHint);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The captured shot with a fresh projectile id, so the refire gate is the only clock.</summary>
    private static byte[] Fire(uint projectileId) =>
        ShootingPacketBuilder.Fire(
            CapturedAk, 414.21521f, 20.135857f, -1814.4366f, [projectileId]);

    /// <summary>
    /// The owner's own session: an AK-47 bound to the hand with an EMPTY magazine
    /// (<c>GunsSpawnEmpty</c>), and 7.62 in the bag.
    /// </summary>
    private static (SessionCombat Session, PlayerAmmoContext Ammo) AkSession(int rounds)
    {
        ulong next = 0x9200_0000_0000_0001;
        var inventory = new PlayerInventory(0x1001, () => next++);
        inventory.Bootstrap();

        next = CapturedAk;
        InventoryItemInstance gun = inventory.CreateInstance(AkItemId, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        next = 0x9300_0000_0000_0001;

        if (rounds > 0)
        {
            inventory.TryStow(inventory.CreateInstance(SevenSixTwo, (uint)rounds));
        }

        return (
            new SessionCombat(),
            new PlayerAmmoContext(inventory, 0x1001, AmmoOptions.Default));
    }

    private static void Handle(
        SessionCombat session,
        PlayerAmmoContext ammo,
        byte[] packet,
        List<WeaponArmResult> results,
        long nowMs = 0,
        CombatOptions? options = null) =>
        WeaponFireArm.Handle(
            session,
            packet,
            options ?? CombatOptions.Default,
            AkItemId,
            CapturedAk,
            Vector3.Zero,
            nowMs,
            results,
            ammo);
}
