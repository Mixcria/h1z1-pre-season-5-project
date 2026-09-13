using System.Globalization;
using System.Numerics;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Weapons;
using Xunit.Abstractions;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// <b>docs/125 - the three c2s subs a thrown grenade produces, pinned to the bytes the August
/// client actually sent.</b>
/// <para>
/// Every packet in this file is verbatim from <c>logs\host-20260904-190216.log</c>, the 19:09 click
/// session: a molotov (item 14) left the hand at 19:09:12.774 as projectile <b>#63</b>, reported a
/// contact at 19:09:13.259 (<c>82 21</c>), a hit at 19:09:13.261 (<c>82 06</c>, which Cranberry
/// refused to decode), and its actor's destruction at 19:09:17.772 (<c>82 1a</c>) - and never sent
/// an <c>82 19 GuidedExplode</c> at all.
/// </para>
/// <para>
/// <b>Why these hex strings are the test.</b> No serializer for sub <c>0x06</c>, <c>0x21</c> or
/// <c>0x1a</c> exists in any Ghidra dump of the August client: its packet registrar
/// (<c>FUN_1413d3520</c>) binds names to composed ids and nothing else, and the sub-to-serializer
/// slot lives in a packet-class vtable that is not exported. So the layouts are derived from the
/// wire, and what makes them more than arithmetic is that each is consumed <b>to the byte</b>, the
/// vector fields all come out of unit length, the same position appears in all three subs, and the
/// owner's independently-written 1087 server reads the same fields at the same offsets.
/// </para>
/// </summary>
public sealed class ThrowableReportEvidenceTests(ITestOutputHelper output)
{
    private const ulong MolotovStack = 3_530_822_107_858_468_891;
    private const uint Molotov = 14;
    private const uint Projectile = 63;

    /// <summary>19:09:13.259, <c>82 21 ProjectileContactReport</c>, 81 bytes on the wire.</summary>
    private const string ContactHex =
        "828C45601C213F0000000000000000000000DC9E063F333398BC4049103E4DAF563FB10C00C5140E93C0C0"
        + "8AA744EC4677BF00000000008484BEC3FD5D3E9380D0BE172163BFFFFFFFFF00A00000000001";

    /// <summary>19:09:13.261, <c>82 06 ProjectileHitReport</c>, 42 bytes on the wire.</summary>
    private const string HitHex =
        "828C45601C063F0000000000000000000000B10C00C5140E93C0C08AA74400A000000000000000003F80";

    /// <summary>19:09:17.772, <c>82 1a DestroyNpcProjectile</c>, 34 bytes on the wire.</summary>
    private const string DestroyHex =
        "822657601C1A00000000000000003F000000B10C00C5140E93C0C08AA7440000803F";

    /// <summary>
    /// The impact point all three reports carry, <b>to the bit</b> - decoded from the raw words
    /// <c>b10c00c5 140e93c0 c08aa744</c> rather than typed as decimal literals, because a decimal
    /// literal of a captured float is a rounding of the evidence and this file is the evidence.
    /// About <c>(-2048.8, -4.6, 1340.3)</c>.
    /// </summary>
    private static readonly Vector3 Impact = new(
        BitConverter.UInt32BitsToSingle(0xc5000cb1),
        BitConverter.UInt32BitsToSingle(0xc0930e14),
        BitConverter.UInt32BitsToSingle(0x44a78ac0));

    /// <summary>The hand the molotov left, from the same session's <c>82 03 Fire</c>.</summary>
    private static readonly Vector3 Hand = new(-2053.4788f, -2.9784086f, 1346.9147f);

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static List<WeaponArmResult> Handle(
        SessionCombat session, byte[] packet, uint held, long nowMs = 1_000)
    {
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(
            session, packet, CombatOptions.Default, held, Vector3.Zero, nowMs, results);
        return results;
    }

    /// <summary>
    /// <c>82 06</c>, field by field. The two closing bytes are the proof: under this layout they
    /// land on <c>totalShotCount = 0x3f</c> and <c>flags = 0x80</c> - and <c>0x80</c> is the bit the
    /// owner's decompiled 1087 sender sets from its own last argument. A layout that were wrong by
    /// one byte anywhere could not put that bit there.
    /// </summary>
    [Fact]
    public void TheMolotovsHitReportDecodesFieldByField()
    {
        byte[] packet = Bytes(HitHex);
        Assert.Equal(42, packet.Length);

        Assert.True(WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header));
        Assert.Equal(WeaponBaseDecoder.SubProjectileHitReport, header.Sub);
        Assert.Equal(476_071_308u, header.GameTime);
        Assert.Equal(36, WeaponBaseDecoder.Body(packet).Length);

        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(
            packet, out ProjectileHitReport report));

        Assert.Equal(Projectile, report.ProjectileId);
        Assert.Equal(0ul, report.CharacterId);
        Assert.True(report.HitTheWorld);
        Assert.Equal(Impact.X, report.X);
        Assert.Equal(Impact.Y, report.Y);
        Assert.Equal(Impact.Z, report.Z);

        // 0xa000 = the SoeUtil::IString dictionary form (0x8000) with the top-bit marker (0x2000)
        // and an id of 0: no bone, no text on the wire. Exactly what a hit on terrain is.
        Assert.Equal((ushort)0xa000, report.HitLocationHeader);
        Assert.True(report.HitLocationIsDictionaryId);
        Assert.Equal(string.Empty, report.HitLocation);

        Assert.Equal(0u, report.UnknownDword);
        Assert.Equal(0, report.HitEntryCount);
        Assert.Equal((byte)0x3f, report.TotalShotCount);
        Assert.Equal((byte)0x80, report.Flags);

        output.WriteLine($"82 06 #{report.ProjectileId} at ({report.X}, {report.Y}, {report.Z})");
    }

    /// <summary>
    /// <b>The regression that produced tonight's "could not be read ... NO DAMAGE".</b> The reader
    /// refused any report whose <c>characterId</c> was 0, on the reasoning that a hit names a
    /// target. A projectile that lands on terrain names none, and the packet is still a real one
    /// carrying a real position.
    /// </summary>
    [Fact]
    public void AZeroTargetGuidIsAWorldHitAndNotADecodeFailure()
    {
        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(
            Bytes(HitHex), out ProjectileHitReport world));
        Assert.True(world.HitTheWorld);

        // The same body with a real guid still reads, and reads as an entity hit - so the fix
        // widened the reader rather than loosening it.
        byte[] entity = Bytes(HitHex);
        BitConverter.GetBytes(0x0510_0000_0000_0007ul).CopyTo(entity, WeaponBaseDecoder.HeaderLength + 4);
        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(
            entity, out ProjectileHitReport hit));
        Assert.False(hit.HitTheWorld);
        Assert.Equal(0x0510_0000_0000_0007ul, hit.CharacterId);

        // The gates that DO refuse are still there: a body one byte short, and a position that is
        // not a place.
        Assert.False(WeaponBaseDecoder.TryReadProjectileHitReport(
            Bytes(HitHex)[..^1], out _));

        byte[] nowhere = Bytes(HitHex);
        BitConverter.GetBytes(float.NaN).CopyTo(nowhere, WeaponBaseDecoder.HeaderLength + 12);
        Assert.False(WeaponBaseDecoder.TryReadProjectileHitReport(nowhere, out _));
    }

    /// <summary>
    /// <c>82 21</c>, field by field, and the four arithmetic checks that fix the framing: the body
    /// is consumed exactly, the quaternion and both direction triples have a norm of 1.0000, and
    /// the position sits at <c>[28..40]</c> - the offset the owner's own 1087 server reads it at,
    /// derived on a different build by a different person.
    /// </summary>
    [Fact]
    public void TheMolotovsContactReportDecodesFieldByField()
    {
        byte[] packet = Bytes(ContactHex);
        Assert.Equal(81, packet.Length);
        Assert.Equal(
            WeaponBaseDecoder.ProjectileContactReportBodyLength,
            WeaponBaseDecoder.Body(packet).Length);

        Assert.True(WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header));
        Assert.Equal(WeaponBaseDecoder.SubProjectileContactReport, header.Sub);
        Assert.Equal(476_071_308u, header.GameTime);

        Assert.True(WeaponBaseDecoder.TryReadProjectileContactReport(
            packet, out ProjectileContactReport contact));

        Assert.Equal(Projectile, contact.ProjectileId);
        Assert.Equal(0ul, contact.CharacterId);
        Assert.True(contact.HitTheWorld);

        Assert.Equal(Impact.X, contact.X);
        Assert.Equal(Impact.Y, contact.Y);
        Assert.Equal(Impact.Z, contact.Z);

        // Unit quaternion.
        float quat = (contact.RotationX * contact.RotationX) + (contact.RotationY * contact.RotationY)
            + (contact.RotationZ * contact.RotationZ) + (contact.RotationW * contact.RotationW);
        Assert.InRange(quat, 0.9999f, 1.0001f);

        // Two unit vectors. The first is (-cos 15 deg, 0, -sin 15 deg) to five places, which is
        // what a direction in the horizontal plane looks like and what noise does not.
        Assert.True(WeaponBaseDecoder.PlausibleDirection(
            contact.NormalX, contact.NormalY, contact.NormalZ));
        Assert.True(WeaponBaseDecoder.PlausibleDirection(
            contact.SecondX, contact.SecondY, contact.SecondZ));
        Assert.Equal(-0.9659258f, contact.NormalX, 5);
        Assert.Equal(0f, contact.NormalY);
        Assert.Equal(-0.25881904f, contact.NormalZ, 5);

        Assert.True(contact.NoMaterial);
        Assert.Equal(uint.MaxValue, contact.Material);

        // The SAME IString header 82 06 carries, in the same relative place in the tail.
        Assert.Equal((ushort)0xa000, contact.LocationHeader);
        Assert.Equal(0u, contact.UnknownDword);
        Assert.Equal((byte)1, contact.Trailer);

        // The owner's 1087 read: position at body [28..40], nothing else assumed.
        ReadOnlySpan<byte> body = WeaponBaseDecoder.Body(packet);
        Assert.Equal(contact.X, BitConverter.ToSingle(body[28..32]));
        Assert.Equal(contact.Y, BitConverter.ToSingle(body[32..36]));
        Assert.Equal(contact.Z, BitConverter.ToSingle(body[36..40]));

        // Length is exact: one byte either way is a refusal, not a shorter read.
        Assert.False(WeaponBaseDecoder.TryReadProjectileContactReport(packet[..^1], out _));
        Assert.False(WeaponBaseDecoder.TryReadProjectileContactReport([.. packet, 0], out _));
    }

    /// <summary>
    /// <c>82 1a</c>. The client's own registrar names this sub
    /// <c>WeaponPacket::cIdDestroyNpcProjectile</c> (<c>thunk_FUN_1413d1df0(0x1a008200, ...)</c>),
    /// and the packet's timing settles what it means: it arrived <b>4.998 s</b> after the throw,
    /// i.e. at the projectile record's own 5.0 s <c>LIFESPAN</c>, for a molotov that had landed
    /// four and a half seconds earlier - the actor expiring, not a detonation.
    /// </summary>
    [Fact]
    public void TheMolotovsDestroyNoticeDecodesAndIsNotADetonation()
    {
        byte[] packet = Bytes(DestroyHex);
        Assert.Equal(34, packet.Length);

        Assert.True(WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header));
        Assert.Equal(WeaponBaseDecoder.SubDestroyNpcProjectile, header.Sub);
        Assert.Equal("DestroyNpcProjectile", WeaponBaseDecoder.NameOfSub(header.Sub));

        // 5,001 ms after the 82 03 Fire's own gameTime of 476,070,813 - the projectile record's
        // 5.0 s LIFESPAN, to three milliseconds. That is what makes this the actor expiring.
        Assert.Equal(476_075_814u, header.GameTime);
        Assert.Equal(5_001u, header.GameTime - 476_070_813u);

        Assert.True(WeaponBaseDecoder.TryReadDestroyNpcProjectile(
            packet, out DestroyNpcProjectile destroy));
        Assert.Equal(Projectile, destroy.ProjectileId);
        Assert.Equal(0ul, destroy.CharacterGuid);
        Assert.Equal(Impact.X, destroy.X);
        Assert.Equal(Impact.Y, destroy.Y);
        Assert.Equal(Impact.Z, destroy.Z);
        Assert.Equal(1f, destroy.Trailer);

        Assert.False(WeaponBaseDecoder.TryReadDestroyNpcProjectile(packet[..^1], out _));
    }

    /// <summary>
    /// All three reports carry the <b>identical</b> impact triple. Three independent sub layouts
    /// agreeing on twelve bytes is the single strongest check available without a serializer, and
    /// it is 8.3 units from the hand the molotov left - a thrown distance, not a decode artefact.
    /// </summary>
    [Fact]
    public void TheThreeReportsAgreeOnOnePosition()
    {
        Assert.True(WeaponBaseDecoder.TryReadProjectileContactReport(
            Bytes(ContactHex), out ProjectileContactReport contact));
        Assert.True(WeaponBaseDecoder.TryReadProjectileHitReport(
            Bytes(HitHex), out ProjectileHitReport hit));
        Assert.True(WeaponBaseDecoder.TryReadDestroyNpcProjectile(
            Bytes(DestroyHex), out DestroyNpcProjectile destroy));

        var a = new Vector3(contact.X, contact.Y, contact.Z);
        var b = new Vector3(hit.X, hit.Y, hit.Z);
        var c = new Vector3(destroy.X, destroy.Y, destroy.Z);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(Impact, a);

        Assert.Equal(Projectile, contact.ProjectileId);
        Assert.Equal(Projectile, hit.ProjectileId);
        Assert.Equal(Projectile, destroy.ProjectileId);

        // The 82 03 Fire the log records for this throw.
        double flew = Vector3.Distance(Hand, a);
        output.WriteLine($"the molotov flew {flew:0.0} u from the hand");
        Assert.InRange(flew, 6.0, 12.0);
    }

    /// <summary>
    /// <b>The whole lane in one test.</b> Replay the 19:09 session's four packets in order against
    /// the live arm: throw, contact report, hit report, destroy notice - and no <c>82 19</c>, ever.
    /// The first contact must ignite immediately at the reported point. Paired hit/destruction
    /// reports and the scheduled fallback must not start additional patches.
    /// </summary>
    [Fact]
    public void TheEveningsMolotovDetonatesWhereItLandedAndNotInTheHand()
    {
        var session = new SessionCombat();

        List<WeaponArmResult> thrown = Handle(
            session,
            ShootingPacketBuilder.Fire(MolotovStack, Hand.X, Hand.Y, Hand.Z, [Projectile]),
            Molotov);
        Assert.Contains("THROWN", Assert.Single(thrown).Line, StringComparison.Ordinal);
        Assert.True(thrown[0].LaunchRelay);

        LiveGrenade grenade = Assert.Single(session.Grenades.Live);
        Assert.Equal(ImpactSource.ThrowPoint, grenade.Source);
        Assert.Equal(Hand, grenade.ImpactPoint);

        WeaponArmResult contact = Assert.Single(Handle(session, Bytes(ContactHex), Molotov));
        Assert.Contains("ProjectileContactReport", contact.Line, StringComparison.Ordinal);
        Assert.Contains("DETONATES at reported position", contact.Line, StringComparison.Ordinal);
        Assert.Equal(ImpactSource.ContactReport, grenade.Source);
        Assert.Equal(Impact, grenade.ImpactPoint);
        Detonation bang = Assert.IsType<Detonation>(contact.Detonation);
        Assert.True(grenade.Detonated);
        Assert.Empty(session.Grenades.Live);

        // Duplicate/paired lifecycle reports cannot burn twice or affect a newer grenade.
        Handle(session, ShootingPacketBuilder.Fire(MolotovStack, 1, 2, 3, [Projectile + 1]), Molotov);
        Assert.Null(Assert.Single(Handle(session, Bytes(ContactHex), Molotov)).Detonation);
        WeaponArmResult hit = Assert.Single(Handle(session, Bytes(HitHex), Molotov));
        Assert.Null(hit.Detonation);

        WeaponArmResult destroy = Assert.Single(Handle(session, Bytes(DestroyHex), Molotov));
        Assert.Null(destroy.Detonation);
        Assert.Null(Assert.Single(Handle(session,
            ThrowableArmTests.GuidedExplode(Projectile, 40, 5, 60), Molotov)).Detonation);
        LiveGrenade newer = Assert.Single(session.Grenades.Live);
        Assert.Equal(Projectile + 1, newer.ProjectileId);
        Assert.Equal(0, newer.Contacts);
        session.Grenades.Settle(newer, fromClient: false);

        Assert.Equal(1, grenade.Contacts);
        long due = grenade.FuseDueAtMs + Rulings.Throwables.FallbackGraceMs;
        Assert.Empty(ThrowableArm.Overdue(session, due - 1));
        Assert.Empty(ThrowableArm.Overdue(session, due));

        Assert.Equal(Impact, bang.Position);
        Assert.Equal(ImpactSource.ContactReport, bang.Source);
        Assert.True(bang.FromClient);
        Assert.False(bang.SpareThrower);
        Assert.Contains("82 21/82 06 contact report", bang.SourceWord, StringComparison.Ordinal);
        Assert.Equal(Molotov, bang.Fact.ItemDefinitionId);

        output.WriteLine(bang.SourceWord);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"the fire lands {Vector3.Distance(Hand, bang.Position):0.0} u from the hand"));
    }

    [Theory]
    [InlineData(false, 0ul)]
    [InlineData(true, 0ul)]
    [InlineData(false, 0x510000000000007ul)]
    [InlineData(true, 0x510000000000007ul)]
    public void EitherContactReportIgnitesOnWorldOrEntityContact(bool useHitReport, ulong target)
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(MolotovStack, Hand.X, Hand.Y, Hand.Z, [Projectile]), Molotov);
        byte[] packet = Bytes(useHitReport ? HitHex : ContactHex);
        BitConverter.GetBytes(target).CopyTo(packet, WeaponBaseDecoder.HeaderLength + 4);

        Detonation fire = Assert.IsType<Detonation>(Assert.Single(Handle(session, packet, 0)).Detonation);

        Assert.Equal(Impact, fire.Position);
        Assert.False(fire.SpareThrower);
        Assert.True(fire.Fact.Lingers);
        Assert.Equal(Rulings.Throwables.MolotovPersistEffectId, fire.Fact.CloudEffectId);
        Assert.True(ThrowableArm.DamageHpAt(fire.Fact, 0) > 0);
        Assert.Empty(session.Grenades.Live);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(100L)]
    [InlineData(500L)]
    public void SmokeActivatesOnItsFirstContactWithoutWaitingForFuseOrExpiry(long afterThrowMs)
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(MolotovStack, Hand.X, Hand.Y, Hand.Z, [Projectile]), 2236);
        var grenade = Assert.Single(session.Grenades.Live);
        var projectile = Cranberry.Zone.Weapons.AugustThrowables.ProjectileRecordFor(grenade.Fact, 30);
        Assert.True(projectile.DetonatesOnContact);
        Assert.True(projectile.Lifespan >= 3); // Long throws retain their full flight window.
        var result = Assert.Single(Handle(session, Bytes(ContactHex), 2236, grenade.ThrownAtMs + afterThrowMs));
        var cloud = Assert.IsType<Detonation>(result.Detonation);
        Assert.Equal(Impact, cloud.Position);
        Assert.Equal(ImpactSource.ContactReport, cloud.Source);
        Assert.Equal(0, cloud.Fact.DamageHp);
        Assert.Empty(session.Grenades.Live);
        Assert.Null(Assert.Single(Handle(session, Bytes(HitHex), 2236)).Detonation);
        Assert.Null(Assert.Single(Handle(session, Bytes(DestroyHex), 2236)).Detonation);
        Assert.Empty(ThrowableArm.Overdue(session, long.MaxValue));
    }

    [Theory]
    [InlineData(65u)]
    [InlineData(2235u)]
    [InlineData(2237u)]
    public void TimedGrenadesRetainTheirFuseAfterWorldContact(uint item)
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(MolotovStack, Hand.X, Hand.Y, Hand.Z, [Projectile]), item);
        Assert.Null(Assert.Single(Handle(session, Bytes(ContactHex), item)).Detonation);
        Assert.Null(Assert.Single(Handle(session, Bytes(HitHex), item)).Detonation);
        LiveGrenade grenade = Assert.Single(session.Grenades.Live);
        long due = grenade.FuseDueAtMs + Rulings.Throwables.FallbackGraceMs;
        Assert.Empty(ThrowableArm.Overdue(session, due - 1));
        Assert.Equal(Impact, Assert.Single(ThrowableArm.Overdue(session, due)).Position);
    }

    [Theory]
    [InlineData(2235u)]
    [InlineData(2236u)]
    [InlineData(2237u)]
    public void UtilityGrenadeWaitsForFlightPositionThenActivatesOnContactAfterItsFuse(uint item)
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(MolotovStack, Hand.X, Hand.Y, Hand.Z, [Projectile]), item);
        var grenade = Assert.Single(session.Grenades.Live);
        long now = grenade.FuseDueAtMs + Rulings.Throwables.FallbackGraceMs;
        Assert.Empty(ThrowableArm.Overdue(session, now));
        Detonation blast = Assert.IsType<Detonation>(
            Assert.Single(Handle(session, Bytes(ContactHex), item, now)).Detonation);
        Assert.Equal(Impact, blast.Position);
        Assert.False(blast.SpareThrower);
        Assert.Empty(session.Grenades.Live);
        Assert.Null(Assert.Single(Handle(session, Bytes(HitHex), item, now + 1)).Detonation);
        Assert.Empty(ThrowableArm.Overdue(session, long.MaxValue));
    }

    [Fact]
    public void ExpirationNoticeAloneDoesNotPretendTheBottleHitASurface()
    {
        var session = new SessionCombat();
        Handle(session, ShootingPacketBuilder.Fire(MolotovStack, Hand.X, Hand.Y, Hand.Z, [Projectile]), Molotov);
        Assert.Null(Assert.Single(Handle(session, Bytes(DestroyHex), Molotov)).Detonation);
        Assert.Single(session.Grenades.Live);
    }

    /// <summary>
    /// The tier order, in one place: a contact report never overrides the client's own <c>82 19</c>,
    /// and a detonation still standing at the throw point still spares the thrower (D306, unchanged).
    /// </summary>
    [Fact]
    public void TheClientsOwnExplodeIsTheFinalWord()
    {
        var session = new SessionCombat();
        Handle(
            session,
            ShootingPacketBuilder.Fire(MolotovStack, 1, 2, 3, [Projectile]),
            Molotov);

        WeaponArmResult explode = Assert.Single(Handle(
            session, ThrowableArmTests.GuidedExplode(Projectile, 40, 5, 60), Molotov));
        Assert.NotNull(explode.Detonation);
        Detonation bang = explode.Detonation;
        Assert.Equal(ImpactSource.GuidedExplode, bang.Source);
        Assert.Contains("82 19 GuidedExplode", bang.SourceWord, StringComparison.Ordinal);
        Assert.Empty(session.Grenades.Live);

        // A late contact report for a settled grenade names nothing live and moves nothing.
        WeaponArmResult late = Assert.Single(Handle(session, Bytes(ContactHex), Molotov));
        Assert.Contains("names no live grenade", late.Line, StringComparison.Ordinal);

        // And with no report of any kind, D306 stands: the throw point, thrower spared.
        var quiet = new SessionCombat();
        Handle(quiet, ShootingPacketBuilder.Fire(MolotovStack, 7, 8, 9, [99]), Molotov);
        LiveGrenade lone = Assert.Single(quiet.Grenades.Live);
        Detonation fallback = Assert.Single(
            ThrowableArm.Overdue(quiet, lone.FuseDueAtMs + Rulings.Throwables.FallbackGraceMs));
        Assert.Equal(new Vector3(7, 8, 9), fallback.Position);
        Assert.Equal(ImpactSource.ThrowPoint, fallback.Source);
        Assert.True(fallback.SpareThrower);
        Assert.Contains("THROW POINT", fallback.SourceWord, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>CRANBERRY_THROWABLES=0</c> the two new subs fall back to the named, log-only answer
    /// they had before this lane - the one-word revert the plan asks every arm to keep.
    /// </summary>
    [Fact]
    public void TurningTheArmOffPutsBothSubsBackToLogOnly()
    {
        var session = new SessionCombat();
        CombatOptions off = CombatOptions.Default with { Throwables = false };
        var one = new List<WeaponArmResult>();
        var two = new List<WeaponArmResult>();

        WeaponFireArm.Handle(session, Bytes(ContactHex), off, Molotov, Vector3.Zero, 1_000, one);
        WeaponFireArm.Handle(session, Bytes(DestroyHex), off, Molotov, Vector3.Zero, 1_000, two);

        WeaponArmResult contact = Assert.Single(one);
        WeaponArmResult destroy = Assert.Single(two);
        Assert.Contains("seen, not acted on", contact.Line, StringComparison.Ordinal);
        Assert.Contains("seen, not acted on", destroy.Line, StringComparison.Ordinal);
        Assert.Contains("ProjectileContactReport", contact.Line, StringComparison.Ordinal);
        Assert.Contains("DestroyNpcProjectile", destroy.Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// D315 §6: the switch exists, defaults ON, reverts on <c>=0</c>, and <b>the boot banner says
    /// which way it went</b> - the rule this project applies to every arm that puts a new packet on
    /// the wire, so a click-test can never be in doubt about what was live.
    /// </summary>
    [Fact]
    public void TheLaunchRelaySwitchDefaultsOnAndTheBannerSaysSo()
    {
        Assert.True(Cranberry.Zone.Match.PeerOptions.Default.ProjectileLaunch);
        Assert.Contains(
            "projectileLaunch 82 15 04 0b=ON",
            Cranberry.Zone.Match.PeerOptions.Default.Describe(),
            StringComparison.Ordinal);

        Cranberry.Zone.Match.PeerOptions off = Cranberry.Zone.Match.PeerOptions.FromEnvironment(
            name => name == Cranberry.Zone.Match.PeerOptions.ProjectileLaunchVariable ? "0" : null);
        Assert.False(off.ProjectileLaunch);
        Assert.Contains(
            "projectileLaunch 82 15 04 0b=off", off.Describe(), StringComparison.Ordinal);

        // It requires the relay, exactly as the fire relay does: with peers frozen there is nothing
        // to forward to, and the banner must not claim otherwise.
        Cranberry.Zone.Match.PeerOptions frozen =
            Cranberry.Zone.Match.PeerOptions.Default with { Relay = false };
        Assert.Contains(
            "projectileLaunch 82 15 04 0b=off", frozen.Describe(), StringComparison.Ordinal);

        // The payload is 12 bytes of zero and the packet is 29 (docs/02, 2026-09-02: S5c's byte
        // count was 5 too high for every 82 15 04 row).
        byte[] wire = Cranberry.Zone.Combat.RemoteWeaponPackets.ProjectileLaunch(
            ownerTransientId: 7, equipmentRowGuid: MolotovStack, projectileId: 0);
        Assert.Equal(29, wire.Length);
        Assert.All(wire[^12..], b => Assert.Equal(0, b));
    }
}
