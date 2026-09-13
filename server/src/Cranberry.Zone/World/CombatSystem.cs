using System.Numerics;
using Cranberry.Zone.Combat;

namespace Cranberry.Zone.World;

/// <summary>Why a weapon packet did nothing. Every arm is counted, so a silent drop is impossible.</summary>
public enum CombatRefusal : byte
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>The payload is not a <c>0x82</c>, or a sub the loop does not act on.</summary>
    NotHandled,

    /// <summary>A field failed <see cref="WeaponBaseDecoder"/>'s plausibility gate.</summary>
    Undecodable,

    /// <summary><see cref="CombatOptions.Layout"/> is <c>Unknown</c>: decode and log, never damage.</summary>
    LayoutNotTrusted,

    /// <summary>The magazine is empty and the weapon has one.</summary>
    EmptyMagazine,

    /// <summary>Inside the weapon's own <c>REFIRE_TIME_MS</c>.</summary>
    RateOfFire,

    /// <summary>No live fire hint for this projectile: a hit that did not follow an accepted shot.</summary>
    NoFireHint,

    /// <summary>The named character is not a player in this match.</summary>
    UnknownTarget,

    /// <summary>A player's own round.</summary>
    SelfHit,

    /// <summary>The target is already down.</summary>
    TargetNotAlive,

    /// <summary>Past <see cref="CombatOptions.MaxHitDistance"/> at the shot's tick.</summary>
    OutOfRange,

    /// <summary>The shooter was never told about the target.</summary>
    TargetNotStreamed,
}

/// <summary>What one weapon packet did. Returned so a caller can log it without re-deriving it.</summary>
/// <param name="Refusal">Why nothing happened, or <see cref="CombatRefusal.None"/>.</param>
/// <param name="Outcome">The resolved hit, when one resolved.</param>
/// <param name="Distance">Shooter-to-victim distance at the shot's tick.</param>
/// <param name="Victim">Who was hit, when a player was.</param>
public readonly record struct HitResolution(
    CombatRefusal Refusal,
    HitOutcome Outcome,
    double Distance,
    MatchPlayer? Victim)
{
    public bool Accepted => Refusal == CombatRefusal.None;
}

/// <summary>
/// <b>Fire, hit, damage.</b> The owner's own arbitration, re-expressed against Cranberry's packet
/// layer and its one-resolver damage spine.
/// <para>
/// <b>What is his.</b> The order of the refusal ladder, and each rung's reason: the fire-hint
/// anti-replay rule (a hit must name a projectile from a shot this server accepted), one hint per
/// pellet consumed exactly once, an empty-magazine refusal, a rate-of-fire gate, a "the shooter must
/// have the target streamed in" gate, armour consumed exactly once per projectile immediately before
/// the hint is spent, and a hit marker sent to the shooter <em>whether or not the damage model is
/// on</em>.
/// </para>
/// <para>
/// <b>What is Cranberry's, and better.</b> The range gate rewinds the victim through
/// <see cref="PoseHistory.TryRewind"/> to the shot's own tick instead of measuring them where they
/// stand now - his gate measures a target who has run 8 m since the shot at their current position,
/// and Cranberry built the rewind for exactly this and never wired it. The refire gate is one table
/// lookup against the August client's own <c>REFIRE_TIME_MS</c> rather than a branch chain on
/// definition id. Damage <b>queues</b>: nothing here writes health, because
/// <see cref="Match.ResolveDamage"/> is the only code that does, which is what guarantees that gas
/// and a bullet on the same tick produce one death and one <c>ce 04</c>.
/// </para>
/// <para>
/// <b>Grade (D29).</b> Everything below is BUILT and unit-TESTED. Not one byte of it has been on a
/// wire: the c2s layouts are the owner's 1087 recovery and are UNCERTAIN at 1148, which is why
/// <see cref="WeaponBaseDecoder"/> refuses rather than guesses and why
/// <see cref="CombatOptions.Layout"/> exists.
/// </para>
/// </summary>
public sealed class CombatSystem : ISystem
{
    private readonly List<StagedPacket> _staged = [];
    private readonly List<Range> _unwrapped = [];
    private byte[] _arena = new byte[4096];
    private int _arenaUsed;

    public CombatSystem(CombatOptions? options = null) => Options = options ?? CombatOptions.Default;

    /// <summary>The switches this loop runs under.</summary>
    public CombatOptions Options { get; }

    /// <summary>Weapon packets waiting for this tick's pass.</summary>
    public int StagedShots => _staged.Count;

    /// <summary>Weapon packets seen, in total.</summary>
    public long ShotsSeen { get; private set; }

    /// <summary>Packets no reader could decode. Each one is a finding, not a nuisance.</summary>
    public long Undecodable { get; private set; }

    /// <summary>Hits that passed every gate.</summary>
    public long HitsResolved { get; private set; }

    /// <summary>Hits refused, by any gate.</summary>
    public long HitsRefused { get; private set; }

    /// <summary>The last resolution, so a test or a log can read it without re-deriving it.</summary>
    public HitResolution Last { get; private set; }

    /// <summary>
    /// Stages one inbound <c>0x82</c> packet, verbatim. A <c>82 1f MultiWeapon</c> is unwrapped
    /// here and its members staged individually - the wrapper carries most of the client's weapon
    /// traffic and its bodies are already complete packets, so they are dispatched <b>as they
    /// stand</b> and never re-framed.
    /// </summary>
    public void Stage(MatchPlayer player, ReadOnlySpan<byte> packet)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header))
        {
            Undecodable++;
            return;
        }

        if (header.Sub == WeaponBaseDecoder.SubMultiWeapon)
        {
            bool clean = WeaponBaseDecoder.TryReadMultiWeapon(packet, _unwrapped);

            foreach (Range member in _unwrapped)
            {
                Copy(player, packet[member]);
            }

            if (!clean)
            {
                Undecodable++;
            }

            return;
        }

        Copy(player, packet);
    }

    /// <summary>Kept for the scaffold's command path; the payload carries the packet.</summary>
    public void StageShot(MatchPlayer player, ReadOnlySpan<byte> payload) => Stage(player, payload);

    public void Tick(in TickContext context)
    {
        ShotsSeen += _staged.Count;

        foreach (StagedPacket staged in _staged)
        {
            Dispatch(in context, staged);
        }

        _staged.Clear();
        _arenaUsed = 0;

        PumpMedical(in context);
    }

    private void Dispatch(in TickContext context, in StagedPacket staged)
    {
        ReadOnlySpan<byte> packet = _arena.AsSpan(staged.Offset, staged.Length);

        if (!WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header))
        {
            Undecodable++;
            return;
        }

        MatchPlayer? shooter = context.World.PlayerAt(staged.Slot);

        if (shooter is null)
        {
            return;
        }

        switch (header.Sub)
        {
            case WeaponBaseDecoder.SubFire:
                OnFire(in context, shooter, packet);
                break;
            case WeaponBaseDecoder.SubProjectileHitReport:
                OnHitReport(in context, shooter, packet);
                break;
            case WeaponBaseDecoder.SubFireStateUpdate:
                OnFireState(shooter, packet);
                break;
            case WeaponBaseDecoder.SubReloadRequest:
                OnReload(shooter, packet);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// <c>82 03 Fire</c>. The order is the behaviour: cancel a running heal, refuse an empty
    /// magazine, refuse a shot inside the refire time, then spend the round and record one hint per
    /// projectile.
    /// </summary>
    private void OnFire(in TickContext context, MatchPlayer shooter, ReadOnlySpan<byte> packet)
    {
        if (!WeaponBaseDecoder.TryReadFire(packet, out WeaponFire fire))
        {
            Undecodable++;
            return;
        }

        // A shot fired mid-bandage cancels the bandage.
        shooter.Medical.CancelHeal();

        FireResult result = shooter.Combat.Fire(
            in fire, shooter.HeldWeaponItemDefinitionId, context.Time.ElapsedMs, Options);

        LastFire = result;

        if (result.Verdict != FireVerdict.Accepted)
        {
            Last = new HitResolution(
                result.Verdict == FireVerdict.EmptyMagazine
                    ? CombatRefusal.EmptyMagazine
                    : CombatRefusal.RateOfFire,
                default,
                0,
                null);
        }
    }

    /// <summary>The most recent <c>Fire</c> verdict, so a test or a log can read it.</summary>
    public FireResult LastFire { get; private set; }

    /// <summary>
    /// <c>82 06 ProjectileHitReport</c> - the refusal ladder, in the owner's own order, and the only
    /// place weapon damage enters this server.
    /// </summary>
    private void OnHitReport(in TickContext context, MatchPlayer shooter, ReadOnlySpan<byte> packet)
    {
        if (!WeaponBaseDecoder.TryReadProjectileHitReport(packet, out ProjectileHitReport report))
        {
            Undecodable++;
            Last = new HitResolution(CombatRefusal.Undecodable, default, 0, null);
            return;
        }

        if (Options.Layout == WeaponFireLayout.Unknown)
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.LayoutNotTrusted, default, 0, null);
            return;
        }

        long nowMs = context.Time.ElapsedMs;

        if (!shooter.Combat.TryConsumeHint(
                report.ProjectileId, nowMs, Options.FireHintLifetimeMs, out FireHint hint))
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.NoFireHint, default, 0, null);
            return;
        }

        MatchPlayer? victim = FindByCharacterGuid(context.World, report.CharacterId);

        if (victim is null)
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.UnknownTarget, default, 0, null);
            return;
        }

        if (ReferenceEquals(victim, shooter))
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.SelfHit, default, 0, victim);
            return;
        }

        if (!victim.IsAlive)
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.TargetNotAlive, default, 0, victim);
            return;
        }

        // Lag compensation: the victim is measured where they were when the shot was fired, not
        // where they have run to since. Z1 has no equivalent; Cranberry built the ring for this.
        Vector3 muzzle = new(hint.X, hint.Y, hint.Z);
        Vector3 at = victim.History.TryRewind(ShotTick(hint, context), out PoseSnapshot snapshot)
            ? snapshot.Position
            : victim.Position;
        double distance = Vector3.Distance(muzzle, at);

        if (distance > Options.MaxHitDistance)
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.OutOfRange, default, distance, victim);
            return;
        }

        if (!shooter.View.Knows(victim.Id))
        {
            HitsRefused++;
            Last = new HitResolution(CombatRefusal.TargetNotStreamed, default, distance, victim);
            return;
        }

        // Armour is consumed HERE, exactly once per projectile - after every refusal gate and with
        // the hint already spent, so a duplicated report can never spend a plate twice.
        HitOutcome outcome = HitRule.Resolve(
            ref victim.Armour,
            victim.WornBodyArmourItemId,
            victim.WornHelmetItemId,
            RetailBalance.WeaponDefinitionIdFor(hint.ItemDefinitionId),
            report.HitLocation,
            distance,
            Options.UnmappedWeaponBodyUnits);

        shooter.Combat.CountHit(outcome.DamageUnits);
        HitsResolved++;
        Last = new HitResolution(CombatRefusal.None, outcome, distance, victim);

        if (Options.EnableCombatDamage && outcome.DamageUnits > 0)
        {
            context.Match.Damage(
                victim,
                outcome.DamageUnits,
                DamageCause.Bullet,
                shooter.Id,
                bleeds: outcome.Bleeds,
                headshot: outcome.Headshot);
        }
    }

    /// <summary><c>82 01 FireStateUpdate</c>. Recorded, so the log can say the client thinks the
    /// magazine is dry; nothing on the wire answers it yet.</summary>
    private void OnFireState(MatchPlayer shooter, ReadOnlySpan<byte> packet)
    {
        if (!WeaponBaseDecoder.TryReadFireStateUpdate(packet, out FireStateUpdate update))
        {
            Undecodable++;
            return;
        }

        LastFireState = update;

        if (update.FireState == WeaponBaseDecoder.EmptyFireState)
        {
            ClientReportedEmpty++;
        }
    }

    /// <summary>The most recent <c>82 01</c>.</summary>
    public FireStateUpdate LastFireState { get; private set; }

    /// <summary>Times the client has told this server a magazine ran dry.</summary>
    public long ClientReportedEmpty { get; private set; }

    /// <summary>
    /// <c>82 07 ReloadRequest</c> - one field, and it names an ITEM guid. The magazine refills on
    /// the server's own model; the s2c <c>82 08 Reload</c> that drives the animation is a later
    /// lane, and the owner's own rule for it is that it goes out ONCE at the END of a shell-by-shell
    /// loop, never once per shell, because the client counts the shells it animates itself.
    /// </summary>
    private void OnReload(MatchPlayer shooter, ReadOnlySpan<byte> packet)
    {
        ReadOnlySpan<byte> body = WeaponBaseDecoder.Body(packet);

        if (body.Length < 8)
        {
            Undecodable++;
            return;
        }

        if (shooter.Combat.Reload(BitConverter.ToUInt64(body[..8])))
        {
            Reloads++;
        }
    }

    /// <summary>Reloads this server accepted.</summary>
    public long Reloads { get; private set; }

    /// <summary>
    /// Bleeding and healing, both queued through the one resolver. A bleed is damage with the same
    /// cause a bullet has, because it is the bullet still doing its work; a heal is negative damage
    /// and goes through <see cref="Match.Heal"/> for the same reason - nothing but
    /// <c>ResolveDamage</c> writes health.
    /// </summary>
    private void PumpMedical(in TickContext context)
    {
        long nowMs = context.Time.ElapsedMs;

        foreach (MatchPlayer? player in context.World.Players)
        {
            if (player is not { Life: LifeState.Alive })
            {
                continue;
            }

            int bleed = player.Medical.PumpBleed(nowMs);

            if (bleed > 0)
            {
                context.Match.Damage(player, bleed, DamageCause.Bullet, player.LastWoundedBy);
            }

            int heal = player.Medical.PumpHeal(nowMs);

            if (heal > 0)
            {
                context.Match.Heal(player, heal);
            }
        }
    }

    /// <summary>
    /// The tick the shot was fired on. The hint carries match-clock milliseconds, which the fixed
    /// step turns back into a tick exactly.
    /// </summary>
    private static long ShotTick(in FireHint hint, in TickContext context)
    {
        _ = context;
        return hint.AtMs / MatchClock.FixedDeltaMs;
    }

    /// <summary>
    /// The player a hit report names. A report carries the character guid the client knows, which is
    /// the login roster guid the self record was built with.
    /// </summary>
    private static MatchPlayer? FindByCharacterGuid(World world, ulong characterId)
    {
        foreach (MatchPlayer? player in world.Players)
        {
            if (player is not null && player.AccountGuid == characterId)
            {
                return player;
            }
        }

        return null;
    }

    private void Copy(MatchPlayer player, ReadOnlySpan<byte> packet)
    {
        if (_arenaUsed + packet.Length > _arena.Length)
        {
            Array.Resize(ref _arena, Math.Max(_arena.Length * 2, _arenaUsed + packet.Length));
        }

        packet.CopyTo(_arena.AsSpan(_arenaUsed));
        _staged.Add(new StagedPacket(player.Slot, _arenaUsed, packet.Length));
        _arenaUsed += packet.Length;
    }

    private readonly record struct StagedPacket(int Slot, int Offset, int Length);
}
