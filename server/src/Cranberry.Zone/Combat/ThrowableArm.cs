using System.Numerics;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Combat;

/// <summary>
/// <b>Which client report gave a grenade the position it goes off at</b> (D315, docs/125 §5) -
/// the owner's own three tiers, adopted because the 2026-09-04 19:09 molotov proved this server
/// needed them: it landed, said so twice, and never sent an <c>82 19</c> at all.
/// </summary>
public enum ImpactSource : byte
{
    /// <summary>
    /// Tier 3, the floor. The <c>82 03 Fire</c>'s own position - the hand it left. This is the
    /// position D306's fallback detonates at, and the reason that detonation spares the thrower.
    /// </summary>
    ThrowPoint = 0,

    /// <summary>
    /// Tier 2. The last <c>82 21 ProjectileContactReport</c> (docs/125 §2), or a <c>82 06
    /// ProjectileHitReport</c> naming this projectile. Where the client says the thing touched
    /// something. Good enough to place a bang, and a great deal better than the hand.
    /// </summary>
    ContactReport = 1,

    /// <summary>
    /// Tier 1, the final word. <c>82 19 GuidedExplode</c> (docs/120 §2.5) - the client's own
    /// detonation report at the actor's own position. Nothing overrides it.
    /// </summary>
    GuidedExplode = 2,
}

/// <summary>
/// One grenade in the air, from the <c>82 03 Fire</c> that threw it to the detonation that ends it
/// (docs/120 §6).
/// </summary>
public sealed class LiveGrenade
{
    /// <summary>The projectile id the client's <c>82 03</c> named - what its <c>82 19</c> and <c>82 26</c> name again.</summary>
    public required uint ProjectileId { get; init; }

    /// <summary>What was thrown.</summary>
    public required ThrowableFact Fact { get; init; }

    /// <summary>The stack it left.</summary>
    public required ulong ItemGuid { get; init; }

    /// <summary>The <c>82 03</c>'s position - the hand it left.</summary>
    public required Vector3 ThrowPoint { get; init; }

    /// <summary>Server clock at the throw.</summary>
    public required long ThrownAtMs { get; init; }

    /// <summary>The native Fire timestamp distinguishes a new throw after projectile numbering restarts.</summary>
    public uint ThrowGameTime { get; init; }

    /// <summary><see cref="ThrownAtMs"/> plus the fuse.</summary>
    public required long FuseDueAtMs { get; init; }

    /// <summary><c>82 26</c> reports seen for it.</summary>
    public int Bounces { get; set; }

    /// <summary>Set once it has gone off, by either path.</summary>
    public bool Detonated { get; set; }

    /// <summary>
    /// D315: where this server currently believes the grenade is, and
    /// <see cref="ImpactSource"/> says which report put it there. Starts at
    /// <see cref="ThrowPoint"/> and only ever moves up the tiers.
    /// </summary>
    public Vector3 ImpactPoint { get; private set; }

    /// <summary>Which report placed <see cref="ImpactPoint"/>.</summary>
    public ImpactSource Source { get; private set; } = ImpactSource.ThrowPoint;

    /// <summary><c>82 21</c> / <c>82 06</c> contacts counted for it, for the log.</summary>
    public int Contacts { get; set; }

    /// <summary>
    /// Moves the impact point, but never DOWN a tier: a contact report arriving after the client's
    /// own <c>82 19</c> must not undo the final word (the owner's own guard). Returns true when the
    /// position moved.
    /// </summary>
    public bool Place(Vector3 where, ImpactSource source)
    {
        if (source < Source)
        {
            return false;
        }

        ImpactPoint = where;
        Source = source;
        return true;
    }

    /// <summary>Called once by the ledger when the record is built, to seed tier 3.</summary>
    internal void SeedThrowPoint() => ImpactPoint = ThrowPoint;
}

/// <summary>The grenades one session has in the air.</summary>
public sealed class ThrowableLedger
{
    /// <summary>More than this many live grenades is a client in a loop, not a player; the oldest is dropped.</summary>
    public const int Capacity = 16;

    private readonly List<LiveGrenade> _live = [];
    private readonly HashSet<(ulong Item, uint Projectile, uint GameTime)> _recentThrows = [];
    private readonly Queue<(ulong Item, uint Projectile, uint GameTime)> _throwOrder = [];
    private const int ReplayHistoryCapacity = 256;

    /// <summary>Keep a bounded replay history after settlement; a delayed Fire is not a new stack spend.</summary>
    public bool WasThrown(ulong itemGuid, uint projectileId, uint gameTime) =>
        _recentThrows.Contains((itemGuid, projectileId, gameTime));

    /// <summary>Every grenade thrown this session that has not gone off.</summary>
    public IReadOnlyList<LiveGrenade> Live => _live;

    /// <summary>Grenades thrown this session, detonated or not.</summary>
    public long Thrown { get; private set; }

    /// <summary>Detonations triggered by a client explosion or contact report.</summary>
    public long ClientDetonations { get; private set; }

    /// <summary>Detonations the server had to force (D306).</summary>
    public long FallbackDetonations { get; private set; }

    public void Add(LiveGrenade grenade)
    {
        ArgumentNullException.ThrowIfNull(grenade);
        if (_live.Count >= Capacity)
        {
            _live.RemoveAt(0);
        }

        grenade.SeedThrowPoint();
        _live.Add(grenade);
        var identity = (grenade.ItemGuid, grenade.ProjectileId, grenade.ThrowGameTime);
        if (_recentThrows.Add(identity)) _throwOrder.Enqueue(identity);
        while (_throwOrder.Count > ReplayHistoryCapacity)
            _recentThrows.Remove(_throwOrder.Dequeue());
        Thrown++;
    }

    /// <summary>The live grenade a projectile id names, or null.</summary>
    public LiveGrenade? Find(uint projectileId)
    {
        foreach (LiveGrenade grenade in _live)
        {
            if (grenade.ProjectileId == projectileId && !grenade.Detonated)
            {
                return grenade;
            }
        }

        return null;
    }

    /// <summary>The oldest live grenade, or null - what a report that names nothing known is matched to.</summary>
    public LiveGrenade? Oldest()
    {
        foreach (LiveGrenade grenade in _live)
        {
            if (!grenade.Detonated)
            {
                return grenade;
            }
        }

        return null;
    }

    /// <summary>Marks a grenade detonated and forgets it.</summary>
    public void Settle(LiveGrenade grenade, bool fromClient)
    {
        ArgumentNullException.ThrowIfNull(grenade);
        grenade.Detonated = true;
        _live.Remove(grenade);
        if (fromClient)
        {
            ClientDetonations++;
        }
        else
        {
            FallbackDetonations++;
        }
    }

    /// <summary>The live grenades whose fuse plus <paramref name="graceMs"/> has passed.</summary>
    public List<LiveGrenade> Overdue(long nowMs, int graceMs)
    {
        var overdue = new List<LiveGrenade>();
        foreach (LiveGrenade grenade in _live)
        {
            long due = grenade.FuseDueAtMs + graceMs;
            if (grenade.Fact.ContactTimedActivation && grenade.Source == ImpactSource.ThrowPoint)
                due = Math.Max(due, grenade.ThrownAtMs
                    + (long)(grenade.Fact.ProjectileLifespanSeconds * 1000) + graceMs);
            if (!grenade.Detonated && nowMs >= due)
            {
                overdue.Add(grenade);
            }
        }

        return overdue;
    }

    public void Clear()
    {
        _live.Clear();
        _recentThrows.Clear();
        _throwOrder.Clear();
    }
}

/// <summary>What <see cref="ThrowableArm.Throw"/> hands the caller: the grenade is in the air and owes the world a stack decrement, a relay and a fallback timer.</summary>
/// <param name="Grenade">The live record.</param>
/// <param name="ItemDefinitionId">The stack's item.</param>
public readonly record struct ThrownGrenade(LiveGrenade Grenade, uint ItemDefinitionId);

/// <summary>
/// A detonation the caller must put on the wire (docs/120 §6.3): the effect at the position, the
/// damage inside the radius, and - for a cloud - the linger.
/// </summary>
/// <param name="Fact">What went off.</param>
/// <param name="Position">Where.</param>
/// <param name="FromClient">True when the client's own <c>82 19</c> placed it; false for the server's fallback.</param>
/// <param name="SpareThrower">D306: a fallback detonation is at the thrower's own hand, where the grenade provably is not, so he is not hurt by it.</param>
/// <param name="ProjectileId">The projectile it was.</param>
public sealed record Detonation(
    ThrowableFact Fact,
    Vector3 Position,
    bool FromClient,
    bool SpareThrower,
    uint ProjectileId)
{
    /// <summary>
    /// D315: <b>which client report put the bang here</b>. The log line says it in words, so a
    /// click-test can tell "the client told us where it went off" from "the client told us where it
    /// touched" from "we never heard and used his hand" without reading the hex.
    /// </summary>
    public ImpactSource Source { get; init; } = ImpactSource.ThrowPoint;

    /// <summary>The one phrase every detonation log line ends with.</summary>
    public string SourceWord => Source switch
    {
        ImpactSource.GuidedExplode => "positioned by the client's own 82 19 GuidedExplode",
        ImpactSource.ContactReport => "positioned by the client's 82 21/82 06 contact report (no 82 19 arrived)",
        _ => "positioned at the THROW POINT - no 82 19 and no contact report ever arrived (D306)",
    };
}

/// <summary>
/// <b>The throwables arm of the <c>0x82</c> family (docs/120 §6).</b> Three subs reach it: the
/// <c>82 03 Fire</c> of a throwable item (a throw, not a shot), <c>82 26 GrenadeBounceReport</c>
/// (counted), and <c>82 19 GuidedExplode</c> (the client's own detonation report, which places the
/// blast). Everything here is pure - the caller (<c>ZoneService.Throwables.cs</c>) owns the wire,
/// the inventory and the timers - so every rule is unit-testable without a socket.
/// </summary>
public static class ThrowableArm
{
    /// <summary>The grep tag, the family's own.</summary>
    public const string Tag = WeaponFireArm.Tag;

    /// <summary>True for the five items of <see cref="AugustThrowables"/>.</summary>
    public static bool IsThrowable(uint itemDefinitionId) => AugustThrowables.IsThrowable(itemDefinitionId);

    /// <summary>
    /// <c>82 03 Fire</c> on a throwable: <b>a throw</b>. No magazine is consulted and no refire gate
    /// applies - the client already spent its own 1-round charge and drew the arc, and refusing
    /// would un-throw a grenade the player has watched leave his hand. A throw names exactly one
    /// projectile and consumes one stack unit; exact recent Fire identities are ignored even after settlement.
    /// </summary>
    public static WeaponArmResult Throw(
        SessionCombat session,
        CombatOptions options,
        in WeaponFire fire,
        uint itemDefinitionId,
        long nowMs,
        uint gameTime)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        if (!AugustThrowables.TryGet(itemDefinitionId, out ThrowableFact fact))
        {
            return new WeaponArmResult(
                $"{Tag} 82 03 Fire on item {itemDefinitionId} is not a throwable", null, false, null);
        }

        if (fire.ProjectileIds.Length != 1)
        {
            session.Shooter.CountRefusedShot();
            return new WeaponArmResult(
                $"{Tag} 82 03 Fire REFUSED - a grenade throw must name one projectile, "
                + $"received {fire.ProjectileIds.Length}; no grenade consumed", null, false, null);
        }

        uint projectileId = fire.ProjectileIds[0];
        if (session.Grenades.Find(projectileId) is not null
            || session.Grenades.WasThrown(fire.WeaponGuid, projectileId, gameTime))
        {
            session.Shooter.CountRefusedShot();
            return new WeaponArmResult(
                $"{Tag} 82 03 Fire REFUSED - grenade projectile #{projectileId} was already thrown; "
                + "no grenade consumed", null, false, null);
        }

        var point = new Vector3(fire.X, fire.Y, fire.Z);
        long fuseMs = (long)Math.Round(fact.FuseSeconds * 1000.0);
        var grenade = new LiveGrenade
        {
            ProjectileId = projectileId,
            Fact = fact,
            ItemGuid = fire.WeaponGuid,
            ThrowPoint = point,
            ThrownAtMs = nowMs,
            ThrowGameTime = gameTime,
            FuseDueAtMs = nowMs + fuseMs,
        };
        session.Grenades.Add(grenade);

        session.Shooter.EnsureDeclared(fire.WeaponGuid, itemDefinitionId, 0);

        string line =
            $"{Tag} 82 03 Fire THROWN gameTime={gameTime} {fact.Name} (item {itemDefinitionId}) "
            + $"weapon={fire.WeaponGuid} from ({fire.X:0.0}, {fire.Y:0.0}, {fire.Z:0.0}) - projectile "
            + $"#{string.Join(',', fire.ProjectileIds)} ({fact.ProjectileModel}), fuse {fact.FuseSeconds:0.#} s"
            + (fact.DetonatesOnContact ? " or on contact" : string.Empty)
            + $"; waiting for the client's 82 19 GuidedExplode, fallback at the throw point after "
            + $"+{Rulings.Throwables.FallbackGraceMs} ms (D306); {session.Grenades.Live.Count} in the air";

        return new WeaponArmResult(line, null, false, null)
        {
            Thrown = new ThrownGrenade(grenade, itemDefinitionId),

            // D315 (docs/125 §6): a throw is an accepted 82 03 like any other, so its viewers get
            // the same 82 15 / 04 / 0b the guns' shots do. One relay path, one switch.
            LaunchRelay = true,
        };
    }

    /// <summary>
    /// <c>82 26 GrenadeBounceReport</c>: counted against the grenade it names. The client sends
    /// one per audible bounce; the log line is what tells "flew and bounced" from "never left".
    /// </summary>
    public static WeaponArmResult OnBounce(SessionCombat session, ReadOnlySpan<byte> packet, in WeaponBaseHeader header)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!WeaponBaseDecoder.TryReadGrenadeBounceReport(packet, out GrenadeBounceReport report))
        {
            session.Undecodable++;
            return new WeaponArmResult(
                $"{Tag} 82 26 GrenadeBounceReport gameTime={header.GameTime} - body is not "
                + $"u32;u32;u64 ({packet.Length - WeaponBaseDecoder.HeaderLength} B). "
                + WeaponBasePacketTrace.Format(packet),
                null,
                false,
                null);
        }

        LiveGrenade? grenade = session.Grenades.Find(report.ProjectileId) ?? session.Grenades.Oldest();
        if (grenade is not null)
        {
            grenade.Bounces++;
        }

        return new WeaponArmResult(
            $"{Tag} 82 26 GrenadeBounceReport gameTime={header.GameTime} projectile #{report.ProjectileId} "
            + $"effect {report.EffectId} character {report.CharacterGuid}"
            + (grenade is null
                ? " - no live grenade to count it against"
                : $" - {grenade.Fact.Name} bounce {grenade.Bounces}"),
            null,
            false,
            null);
    }

    /// <summary>
    /// <b><c>82 21 ProjectileContactReport</c>: where the grenade LANDED</b> (D315, docs/125 §5) -
    /// the tier-2 impact position, and the whole reason this lane exists.
    /// <para>
    /// The 2026-09-04 19:09 molotov is the case: thrown at 12.774 from <c>(-2053.5, -3.0, 1346.9)</c>,
    /// it reported a contact 485 ms later at <c>(-2048.8, -4.6, 1340.3)</c> - and then <b>never sent
    /// an <c>82 19 GuidedExplode</c> at all</b>. Its actor simply lived out its <c>LIFESPAN</c> and
    /// was destroyed (<c>82 1a</c>) five seconds after the throw. With only tier 3 to fall back on,
    /// this server put the fire in the player's hand. The owner's server reads exactly this packet
    /// for exactly this reason (<c>ZoneCombatThrowables.OnContactReport</c>).
    /// </para>
    /// <para>
    /// A molotov ignites immediately. Smoke, gas and stun can activate at a reported position
    /// after their fuse; earlier contacts update the fallback position. Further reports for a
    /// settled grenade cannot trigger a second detonation.
    /// </para>
    /// </summary>
    public static WeaponArmResult OnContactReport(
        SessionCombat session, ReadOnlySpan<byte> packet, in WeaponBaseHeader header, long nowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!WeaponBaseDecoder.TryReadProjectileContactReport(
                packet, out ProjectileContactReport report))
        {
            session.Undecodable++;
            return new WeaponArmResult(
                $"{Tag} 82 21 ProjectileContactReport gameTime={header.GameTime} - body is not the "
                + $"75-byte u32;u64;quat;pos;dir;dir;u32;u16;u32;u8 "
                + $"({packet.Length - WeaponBaseDecoder.HeaderLength} B). "
                + WeaponBasePacketTrace.Format(packet),
                null,
                false,
                null);
        }

        string seen =
            $"{Tag} 82 21 ProjectileContactReport gameTime={header.GameTime} projectile "
            + $"#{report.ProjectileId} target=0x{report.CharacterId:x16} at ({report.X:0.0}, "
            + $"{report.Y:0.0}, {report.Z:0.0}) normal ({report.NormalX:0.00}, {report.NormalY:0.00}, "
            + $"{report.NormalZ:0.00}) material="
            + (report.NoMaterial ? "none (-1)" : report.Material.ToString())
            + $" hdr=0x{report.LocationHeader:x4} u8={report.Trailer}";

        return Place(session, seen, report.ProjectileId, new Vector3(report.X, report.Y, report.Z),
            isContact: true, nowMs);
    }

    /// <summary>
    /// <b><c>82 06 ProjectileHitReport</c> for a THROWN projectile</b> (D315): the same tier-2
    /// placement <see cref="OnContactReport"/> makes. A grenade's <c>82 06</c> is not a shot - no
    /// fire hint was ever registered for a throw, so the gun path would refuse it for want of one
    /// and drop the position on the floor. It carries the same impact point the paired <c>82 21</c>
    /// did (identical to the bit in tonight's evidence), and this arm keeps it.
    /// <para>
    /// Returns null when the projectile id names no live grenade of this session's, which is the
    /// caller's signal to hand the packet to the gun path instead.
    /// </para>
    /// </summary>
    public static WeaponArmResult? OnHitReport(
        SessionCombat session, in ProjectileHitReport report, in WeaponBaseHeader header, long nowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Grenades.Find(report.ProjectileId) is null)
        {
            return null;
        }

        string seen =
            $"{Tag} 82 06 ProjectileHitReport gameTime={header.GameTime} projectile "
            + $"#{report.ProjectileId} is a THROWN grenade's, not a shot's - target=0x"
            + $"{report.CharacterId:x16}"
            + (report.HitTheWorld ? " (the WORLD - it hit terrain, not an entity)" : string.Empty)
            + $" at ({report.X:0.0}, {report.Y:0.0}, {report.Z:0.0}) shots={report.TotalShotCount} "
            + $"flags=0x{report.Flags:x2}";

        return Place(session, seen, report.ProjectileId, new Vector3(report.X, report.Y, report.Z),
            isContact: true, nowMs);
    }

    /// <summary>
    /// <c>82 1a DestroyNpcProjectile</c> (docs/125 §3): the client's own end-of-life notice for a
    /// projectile actor. It is <b>not</b> a detonation - tonight's arrived five seconds after the
    /// throw, at the actor's <c>LIFESPAN</c>, for a molotov that had already landed and reported it
    /// - so this arm records the position at tier 2 (it is the client's, and it is the same point
    /// its contact report gave) and lets the fuse decide when the thing goes off. Returns null when
    /// the id names no live grenade, so an unknown one still prints its hex.
    /// </summary>
    public static WeaponArmResult? OnDestroyNpcProjectile(
        SessionCombat session, ReadOnlySpan<byte> packet, in WeaponBaseHeader header, long nowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!WeaponBaseDecoder.TryReadDestroyNpcProjectile(packet, out DestroyNpcProjectile destroy)
            || session.Grenades.Find(destroy.ProjectileId) is null)
        {
            return null;
        }

        string seen =
            $"{Tag} 82 1a DestroyNpcProjectile gameTime={header.GameTime} projectile "
            + $"#{destroy.ProjectileId} guid=0x{destroy.CharacterGuid:x16} at ({destroy.X:0.0}, "
            + $"{destroy.Y:0.0}, {destroy.Z:0.0}) f32={destroy.Trailer:0.###} - the client's actor "
            + "expired; checking its activation fuse";

        return Place(session, seen, destroy.ProjectileId, new Vector3(destroy.X, destroy.Y, destroy.Z),
            isContact: false, nowMs);
    }

    /// <summary>
    /// The one place a tier-2 report moves a grenade, so the three arms above cannot disagree about
    /// what it means.
    /// </summary>
    private static WeaponArmResult Place(
        SessionCombat session, string seen, uint projectileId, Vector3 where, bool isContact, long nowMs)
    {
        LiveGrenade? grenade = session.Grenades.Find(projectileId);
        if (grenade is null)
        {
            return new WeaponArmResult(
                $"{seen} - names no live grenade of this session's; nothing moves", null, false, null);
        }

        grenade.Contacts++;

        if (!grenade.Place(where, ImpactSource.ContactReport))
        {
            return new WeaponArmResult(
                $"{seen} - {grenade.Fact.Name} is already placed by its own 82 19 GuidedExplode; "
                + "the final word stands", null, false, null);
        }

        double flew = Vector3.Distance(grenade.ThrowPoint, where);

        // August's physics projectile can report contact without GuidedExplode (docs/125).
        // A bottle breaks on its first ground, wall or entity hit; its fuse is only a backstop.
        // Settle before returning so paired 82 21/82 06 reports and the scheduled fallback
        // cannot start another patch. A DestroyNpcProjectile notice alone is not a contact.
        if ((isContact && grenade.Fact.DetonatesOnContact)
            || (grenade.Fact.ContactTimedActivation && nowMs >= grenade.FuseDueAtMs))
        {
            session.Grenades.Settle(grenade, fromClient: true);
            return new WeaponArmResult(
                $"{seen} - {grenade.Fact.Name} #{projectileId} DETONATES at reported position, "
                + $"{flew:0.0} u from the hand; contact/fuse ready", null, false, null)
            {
                Detonation = new Detonation(
                    grenade.Fact, where, FromClient: true, SpareThrower: false, projectileId)
                {
                    Source = ImpactSource.ContactReport,
                },
            };
        }

        long dueIn = grenade.FuseDueAtMs - grenade.ThrownAtMs;

        return new WeaponArmResult(
            $"{seen} - {grenade.Fact.Name} #{projectileId} moved to the contact point, {flew:0.0} u "
            + $"from the hand ({grenade.Contacts} contact(s), {grenade.Bounces} bounce(s)). If no "
            + $"82 19 arrives it goes off THERE at fuse {dueIn} ms + "
            + $"{Rulings.Throwables.FallbackGraceMs} ms grace, and the thrower is NOT spared (D315)",
            null,
            false,
            null);
    }

    /// <summary>
    /// <c>82 19 GuidedExplode</c>: the client's own detonation, at its own position. The grenade
    /// it names is settled and a <see cref="Detonation"/> handed back; a report naming a projectile
    /// this session never threw is logged and produces nothing - a detonation this server did not
    /// see thrown is not one it will pay damage for.
    /// </summary>
    public static WeaponArmResult OnGuidedExplode(
        SessionCombat session,
        CombatOptions options,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        long nowMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        if (!WeaponBaseDecoder.TryReadGuidedExplode(packet, out GuidedExplode explode))
        {
            session.Undecodable++;
            return new WeaponArmResult(
                $"{Tag} 82 19 GuidedExplode gameTime={header.GameTime} - body did not read as "
                + "packed;packed;u32;packed;f32x3;f32x3;u8. " + WeaponBasePacketTrace.Format(packet),
                null,
                false,
                null);
        }

        string seen =
            $"{Tag} 82 19 GuidedExplode gameTime={header.GameTime} projectile #{explode.ProjectileId} at "
            + $"({explode.X:0.0}, {explode.Y:0.0}, {explode.Z:0.0}) dir ({explode.DirectionX:0.00}, "
            + $"{explode.DirectionY:0.00}, {explode.DirectionZ:0.00}) net ids {explode.OwnerNetworkId}/"
            + $"{explode.SecondNetworkId}/{explode.TargetNetworkId} u8={explode.Trailer}";

        LiveGrenade? grenade = session.Grenades.Find(explode.ProjectileId);
        if (grenade is null)
        {
            return new WeaponArmResult(
                $"{seen} - names no live grenade of this session's; nothing detonates", null, false, null);
        }

        var position = new Vector3(explode.X, explode.Y, explode.Z);
        double flew = Vector3.Distance(grenade.ThrowPoint, position);
        grenade.Place(position, ImpactSource.GuidedExplode);
        session.Grenades.Settle(grenade, fromClient: true);

        return new WeaponArmResult(
            $"{seen} - {grenade.Fact.Name} DETONATES {flew:0.0} u from the hand after "
            + $"{nowMs - grenade.ThrownAtMs} ms and {grenade.Bounces} bounce(s)",
            null,
            false,
            null)
        {
            Detonation = new Detonation(
                grenade.Fact, position, FromClient: true, SpareThrower: false, grenade.ProjectileId)
            {
                Source = ImpactSource.GuidedExplode,
            },
        };
    }

    /// <summary>
    /// D306 - the owner's own rule: <b>a grenade always goes off</b>. Every live grenade whose fuse
    /// plus <see cref="Rulings.Throwables.FallbackGraceMs"/> has passed without an <c>82 19</c> is
    /// detonated where this server last believed it to be.
    /// <para>
    /// <b>D315 changes which position that is.</b> Before, it was always the throw point, and the
    /// second half of the owner's rule - spare the thrower - followed from that: the throw point is
    /// his hand and the grenade is provably not there. Now a <c>82 21 ProjectileContactReport</c>
    /// or a <c>82 06 ProjectileHitReport</c> may have moved it to where the client says it landed
    /// (<see cref="ImpactSource.ContactReport"/>), and when it has, the bang goes off THERE and the
    /// thrower is <b>not</b> spared - because the position is no longer a stand-in for one, it is a
    /// place the client reported. The sparing rule is kept exactly where it was earned: a
    /// detonation still standing at <see cref="ImpactSource.ThrowPoint"/>.
    /// </para>
    /// </summary>
    public static List<Detonation> Overdue(SessionCombat session, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        var detonations = new List<Detonation>();
        foreach (LiveGrenade grenade in session.Grenades.Overdue(nowMs, Rulings.Throwables.FallbackGraceMs))
        {
            session.Grenades.Settle(grenade, fromClient: false);
            bool atTheHand = grenade.Source == ImpactSource.ThrowPoint;
            detonations.Add(new Detonation(
                grenade.Fact,
                grenade.ImpactPoint,
                FromClient: false,
                SpareThrower: atTheHand,
                grenade.ProjectileId)
            {
                Source = grenade.Source,
            });
        }

        return detonations;
    }

    /// <summary>
    /// D315 (was D308) - what a detonation does to something <paramref name="distance"/> units from
    /// it, in HIT POINTS. The owner's own rule, adopted whole: a bang is
    /// <see cref="ThrowableFact.DamageHp"/> inside
    /// <see cref="Rulings.Throwables.ExplosionFalloffPivotUnits"/> and
    /// <c>DamageHp / distance</c> beyond it, cut off at the radius; a cloud (gas, fire) is flat
    /// inside its radius and is the PER-TICK amount; the stun and the smoke hurt nobody.
    /// <para>
    /// This replaces D308's linear ramp, which reached 0 exactly at the rim. Inverse-distance is
    /// harsher close in and gentler at the edge - 100 hp inside 1 u, 50 at 2 u, 20 at the 5-u rim -
    /// so a grenade at your feet kills and one across the room does not, which is the shape the
    /// owner's server has and the one a player recognises.
    /// </para>
    /// </summary>
    public static int DamageHpAt(in ThrowableFact fact, double distance)
    {
        if (!fact.Hurts || distance > fact.Radius)
        {
            return 0;
        }

        // Only the one-shot bang falls off. A cloud's amount is PER TICK and flat inside its
        // radius - the owner's gas is a flat 500 a second anywhere in the 7 units, and Cranberry's
        // kept molotov patch (D308, the one row D315 does not follow) is the same shape.
        if (fact.Kind != ThrowableKind.Frag)
        {
            return fact.DamageHp;
        }

        return distance > Rulings.Throwables.ExplosionFalloffPivotUnits
            ? (int)Math.Round(fact.DamageHp / distance, MidpointRounding.AwayFromZero)
            : fact.DamageHp;
    }

    /// <summary>The <see cref="DamageCause"/> a kill by this throwable is reported under.</summary>
    public static World.DamageCause CauseOf(in ThrowableFact fact) => fact.Kind switch
    {
        ThrowableKind.Gas => World.DamageCause.ToxicGas,
        ThrowableKind.Molotov => World.DamageCause.Fire,
        _ => World.DamageCause.Explosion,
    };
}
