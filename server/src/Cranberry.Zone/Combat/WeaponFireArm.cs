using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Combat;

/// <summary>
/// One live session's combat state: its shooter book-keeping and its practice dummies. Hung off the
/// gateway session so <c>ZoneService</c> keeps exactly one new field.
/// </summary>
public sealed class SessionCombat
{
    public WeaponDrawState Draw { get; } = new();
    /// <summary>Fire hints, magazines and the refire clock.</summary>
    public ShooterCombatState Shooter { get; } = new();

    /// <summary>The active reload; replacing or clearing it invalidates its scheduled work.</summary>
    public PendingWeaponReload? Reload { get; set; }

    /// <summary>The dummies this session has in the world.</summary>
    public PracticeTargetPack Targets { get; } = new();

    /// <summary>The practice dummies have been spawned once for this session's match.</summary>
    public bool TargetsArmed { get; set; }

    /// <summary><c>0x82</c> packets received, including the ones inside a <c>MultiWeapon</c>.</summary>
    public long PacketsSeen { get; set; }

    /// <summary>Packets no reader could decode - each one is evidence, not noise.</summary>
    public long Undecodable { get; set; }

    /// <summary><c>0xa0</c> ability packets received. docs/89: the census counted zero.</summary>
    public long AbilityPacketsSeen { get; set; }

    /// <summary>
    /// Server clock at the <c>a0 01 InitAbility</c> that armed the current melee swing, or 0 when
    /// no swing is armed. The owner calls this <c>abilityInitTime</c>; an <c>a0 02 UpdateAbility</c>
    /// arriving with this at 0 is ignored, which is what stops one click paying damage twice.
    /// </summary>
    public long MeleeArmedAtMs { get; set; }

    /// <summary>The hit location the arming <c>a0 01</c> carried, used when the <c>a0 02</c>
    /// carries none.</summary>
    public string MeleeHitLocation { get; set; } = string.Empty;

    /// <summary>Swings resolved this session.</summary>
    public int MeleeSwings { get; set; }

    /// <summary>Latest authoritative heading; null until a movement heading is available.</summary>
    public float? MeleeHeading { get; set; }

    /// <summary>Nearby eligible players supplied by the live session before arbitration.</summary>
    public List<MeleePlayerTarget> MeleePlayers { get; } = [];
    public Destructibles.GlassMeleeHit? MeleeGlass { get; set; }

    public long? LastMeleeSwingMs { get; set; }

    /// <summary>Scratch list for <c>MultiWeapon</c> unwrapping; reused so the arm allocates nothing.</summary>
    internal List<Range> Unwrapped { get; } = [];

    /// <summary>docs/120: the grenades this session has in the air.</summary>
    public ThrowableLedger Grenades { get; } = new();
}

/// <summary>What the live arm did with one packet, so the caller can log one line.</summary>
/// <param name="Line">The log line, always non-empty.</param>
/// <param name="HitTarget">The practice dummy this packet killed or hurt, if any.</param>
/// <param name="Killed">That dummy died on this packet.</param>
/// <param name="Marker">The hit marker to send to the shooter, or null.</param>
/// <param name="Reply">
/// A whole zone packet to send straight back to this client, already serialised, or null. Used for
/// the answers that are not hit markers - today only <c>82 08 Reload</c>. Defaulted so the dozen
/// existing construction sites are untouched.
/// </param>
/// <param name="Replies">
/// Further whole zone packets, in order, for the arms that owe the client more than one - a reload
/// that empties an ammunition stack owes a <c>11 04 ItemDelete</c> as well as its <c>82 08</c>, and
/// a shell-by-shell reload owes one <c>82 08</c> per shell. Defaulted to null so that every existing
/// construction site is untouched.
/// </param>
/// <param name="TargetGuid">
/// The guid the client's own <c>82 06 ProjectileHitReport</c> named, when this arm could not
/// resolve it to a practice target or a player. The caller resolves it against the match's vehicle
/// fleet - the branch the hit path never had (AUDIT-vehicles gap 3), and the reason shooting a car
/// used to do nothing at all. Zero when there is nothing to resolve.
/// </param>
/// <param name="ReplyDelaysMs">
/// One delay per entry of <paramref name="Replies"/>, in milliseconds, or null for immediate.
/// Retained for scheduled packet batches. Timed reloads now use ReloadWork so their ammunition
/// changes at completion, rather than preparing stale inventory packets at the request.
/// </param>
/// <param name="Relay">
/// docs/121 §7 (D335): what the peers should be told about this packet - a fire-state
/// <see cref="PeerFireRelay.Start"/> with <paramref name="RelayAimPoint"/> on a corroborated
/// <c>82 20</c>, a <see cref="PeerFireRelay.Stop"/> on a gun's trigger-up, nothing otherwise.
/// </param>
/// <param name="RelayAimPoint">The world point the relayed <c>82 15 04 01</c> aims at.</param>
/// <param name="TargetDamageUnits">
/// What the gun does to an unarmoured body at this range, for that unresolved guid. Deliberately
/// NOT a vehicle-specific scale: <c>ResistTypes</c> names the channels one would come from and
/// <c>ResistInfo</c> holds 48 populated collision-typed rows, but the join
/// (<c>VehicleResistMappings.txt</c>) is header-only and <c>DamageLevelMappings</c> keys off an
/// <c>Npcs.txt</c> that has never been extracted, so inventing a multiplier would put a Cranberry
/// number in front of a client fact.
/// </param>
public readonly record struct WeaponArmResult(
    string Line,
    PracticeTarget? HitTarget,
    bool Killed,
    WeaponHitFeedback? Marker,
    byte[]? Reply = null,
    IReadOnlyList<byte[]>? Replies = null,
    ulong TargetGuid = 0,
    uint TargetDamageUnits = 0,
    IReadOnlyList<int>? ReplyDelaysMs = null,
    PeerFireRelay Relay = PeerFireRelay.None,
    Vector3 RelayAimPoint = default)
{
    /// <summary>A player chosen by the same reach/facing gate as practice dummies.</summary>
    public MeleePlayerHit? MeleeHit { get; init; }
    public uint? MeleeGlassObjectId { get; init; }
    /// <summary>An unresolved target after this projectile's accepted fire hint was consumed.</summary>
    public ProjectileHitReport? UnresolvedHit { get; init; }
    public FireHint AcceptedFire { get; init; }
    /// <summary>Reload work to run on the listener thread at its due time.</summary>
    public PendingWeaponReload? ReloadWork { get; init; }
    /// <summary>The item that earned this observer event, retained across result batching.</summary>
    public ulong RelayWeaponGuid { get; init; }
    public RemoteWeaponPackets.WeaponUpdateType? RemoteUpdate { get; init; }
    public byte RelayFireGroup { get; init; }
    public byte RelayFireMode { get; init; }
    /// <summary>A trigger release overtook its aim hint; do not leave late playback firing.</summary>
    public bool StopAfterRelay { get; init; }

    /// <summary>
    /// D315 (docs/125 §6): this packet was an <b>accepted <c>82 03 Fire</c></b> - a shot or a
    /// throw - and its viewers are owed one <c>82 15 / 04 / 0b ProjectileLaunch</c>. It is the one
    /// thing the owner's server sends on every trigger that Cranberry sent on none.
    /// </summary>
    public bool LaunchRelay { get; init; }

    /// <summary>
    /// docs/120: a throwable's <c>82 03</c> put a grenade in the air. The caller takes one from the
    /// stack, relays the launch to viewers and arms the D306 fallback timer.
    /// </summary>
    public ThrownGrenade? Thrown { get; init; }

    /// <summary>
    /// docs/120: a grenade went off - the client said where (<c>82 19</c>) or the fallback fired.
    /// The caller plays the effect, applies the radius damage and, for a cloud, starts the linger.
    /// </summary>
    public Detonation? Detonation { get; init; }
}

/// <summary>What a bystander is told about one of the shooter's packets (docs/121 §7).</summary>
public enum PeerFireRelay : byte
{
    /// <summary>Nothing - the packet says nothing a viewer needs.</summary>
    None = 0,

    /// <summary><c>82 15 04 01</c> with bit 0 clear: the proxied weapon starts firing at the aim point.</summary>
    Start = 1,

    /// <summary><c>82 15 04 01</c> with bit 0 set: the proxied weapon stops.</summary>
    Stop = 2,
}

/// <summary>
/// <b>The live answer to <c>0x82 WeaponBase</c></b>, and the closing of D52 - "no <c>0x82</c> packet
/// is ever answered ... the c2s <c>Fire</c> and <c>ProjectileHitReport</c> arrive and fall on the
/// floor."
/// <para>
/// <b>Every arm still logs.</b> docs/56 §1.3 counted <b>zero</b> <c>0x82</c> packets across 96
/// captured sessions, so a single <c>WEAPONFIRE</c> line is by itself proof that the attack path
/// came alive - and the untruncated hex on a decode failure is the only client-originated evidence
/// this lane can produce on its own. A packet that fails any plausibility gate keeps exactly the
/// pre-D52 behaviour: one line, full hex, no reply.
/// </para>
/// <para>
/// <b>Grade (D29).</b> BUILT and unit-TESTED. Nothing here is LIVE-VERIFIED: the layouts are the
/// owner's <c>ClientProtocol_1087</c> recovery on a family whose sub numbering is identical at 1148,
/// which makes them candidates, not facts. The first real <c>82 03</c> from the August client is the
/// experiment, and this arm is built so that its result is readable either way.
/// </para>
/// </summary>
public static partial class WeaponFireArm
{
    /// <summary>The grep tag every line carries, kept identical to the pre-D52 trace.</summary>
    public const string Tag = WeaponBasePacketTrace.Tag;

    // The sub ids the owner names in ZoneCombatWire.cs:66-80. Every one of them is a clean base -1
    // from his 0x83 with the sub byte unchanged, which the wave-9 bridge confirmed across all 47
    // WeaponPacket:: registrations.
    private const byte SubReload = 0x08;
    private const byte SubReloadInterrupt = 0x09;
    private const byte SubSwitchFireModeRequest = WeaponBaseDecoder.SubSwitchFireModeRequest;
    private const byte SubGuidedExplode = 0x19;
    private const byte SubProjectileContactReport = 0x21;
    private const byte SubMeleeHitMaterial = 0x22;
    private const byte SubGrenadeBounceReport = 0x26;
    private const byte SubAimBlockedNotify = 0x27;

    /// <summary>
    /// Handles one inbound <c>0x82</c>, unwrapping a <c>82 1f MultiWeapon</c> into its members and
    /// dispatching each as it stands.
    /// </summary>
    /// <param name="session">The shooter's combat state.</param>
    /// <param name="packet">The whole inbound packet.</param>
    /// <param name="options">The switches.</param>
    /// <param name="heldWeaponItemDefinitionId">What the shooter is holding; 0 = fists.</param>
    /// <param name="shooterPosition">Where the shooter is, for the range gate.</param>
    /// <param name="nowMs">Server clock.</param>
    /// <param name="results">One entry per packet handled, in order.</param>
    public static void Handle(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        Vector3 shooterPosition,
        long nowMs,
        List<WeaponArmResult> results)
        => Handle(
            session,
            packet,
            options,
            heldWeaponItemDefinitionId,
            heldWeaponItemGuid: 0,
            shooterPosition,
            nowMs,
            results);

    /// <summary>
    /// Handles one weapon packet while also enforcing the inventory instance currently in RHand.
    /// A zero <paramref name="heldWeaponItemGuid"/> preserves the older decode-only callers; a
    /// live zone session supplies the actual RHand guid so a weapon that was switched away cannot
    /// continue to fire or reload.
    /// </summary>
    public static void Handle(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        Vector3 shooterPosition,
        long nowMs,
        List<WeaponArmResult> results)
        => Handle(
            session,
            packet,
            options,
            heldWeaponItemDefinitionId,
            heldWeaponItemGuid,
            shooterPosition,
            nowMs,
            results,
            ammo: null);

    /// <summary>
    /// The live-session overload: the same loop, plus the player's own bag.
    /// <para>
    /// <b>A null <paramref name="ammo"/> is D118's world, exactly</b> - a reload refills the
    /// magazine for free and no ammunition item is ever touched. That is what every decode-only
    /// caller gets, and what <c>CRANBERRY_AMMO_FROM_BAG=0</c> restores, so the bag-fed path can be
    /// reverted without changing a line of this file.
    /// </para>
    /// </summary>
    /// <param name="ammo">The shooter's ammunition, or null for the decode-only behaviour.</param>
    /// <param name="heldWeaponDisplayDefinitionId">The active weapon's skin item, captured when firing.</param>
    public static void Handle(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        Vector3 shooterPosition,
        long nowMs,
        List<WeaponArmResult> results,
        PlayerAmmoContext? ammo,
        uint heldWeaponDisplayDefinitionId = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(results);

        results.Clear();
        session.PacketsSeen++;

        if (!WeaponBaseDecoder.TryReadHeader(packet, out WeaponBaseHeader header))
        {
            session.Undecodable++;
            results.Add(new WeaponArmResult(
                $"{Tag} not a 0x82 family packet. {WeaponBasePacketTrace.Format(packet)}", null, false, null));
            return;
        }

        if (header.Sub != WeaponBaseDecoder.SubMultiWeapon)
        {
            results.Add(One(
                session,
                packet,
                header,
                options,
                heldWeaponItemDefinitionId,
                heldWeaponItemGuid,
                shooterPosition,
                nowMs,
                ammo,
                heldWeaponDisplayDefinitionId));
            return;
        }

        bool clean = WeaponBaseDecoder.TryReadMultiWeapon(packet, session.Unwrapped);

        // MultiWeapon carries most of the client's weapon traffic - the owner counted 274 of 428
        // c2s weapon packets in one eight-minute session arriving inside it. Each body is ALREADY a
        // complete `82 | gameTime | sub | fields` packet and is dispatched as it stands: his own
        // code carries the scar of round 24, which prepended a second base here and shifted every
        // field two bytes right.
        results.Add(new WeaponArmResult(
            $"{Tag} 0x82 sub=0x1f (MultiWeapon) gameTime={header.GameTime} "
            + $"members={session.Unwrapped.Count}{(clean ? string.Empty : " TRUNCATED")} "
            + $"len={packet.Length}{(clean ? string.Empty : $" hex={Convert.ToHexString(packet)}")}",
            null,
            false,
            null));

        if (!clean)
        {
            session.Undecodable++;
        }

        foreach (Range member in session.Unwrapped)
        {
            ReadOnlySpan<byte> body = packet[member];

            if (!WeaponBaseDecoder.TryReadHeader(body, out WeaponBaseHeader inner))
            {
                session.Undecodable++;
                continue;
            }

            results.Add(One(
                session,
                body,
                inner,
                options,
                heldWeaponItemDefinitionId,
                heldWeaponItemGuid,
                shooterPosition,
                nowMs,
                ammo,
                heldWeaponDisplayDefinitionId));
        }

        // Every member has been arbitrated before these replies leave the handler. A later Fire
        // must not be overwritten by an earlier completion, cancellation or acknowledgement.
        // Keep each counter and the positive pump prediction inputs in their original order.
        foreach (WeaponArmResult result in results)
        {
            RepairReloadSnapshot(result.Reply);
            if (result.Replies is { } replies)
                foreach (byte[] reply in replies) RepairReloadSnapshot(reply);
        }

        void RepairReloadSnapshot(byte[]? reply)
        {
            if (reply is not { Length: 34 } || reply[0] != 0x82 || reply[5] != WeaponReplyPackets.SubReload
                || BitConverter.ToUInt32(reply, 14) != 0) return;
            ulong guid = BitConverter.ToUInt64(reply, 6);
            int magazine = session.Shooter.AmmoOf(guid);
            if (magazine < 0) return;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(18), (uint)magazine);
            if (ammo is not null)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(22),
                    (uint)Math.Max(0, ammo.Count(AmmoTypes.AmmoItemFor(session.Shooter.ItemDefinitionOf(guid)))));
        }
    }

    private static WeaponArmResult One(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        Vector3 shooterPosition,
        long nowMs,
        PlayerAmmoContext? ammo,
        uint heldWeaponDisplayDefinitionId)
    {
        // UDP actions are drained before scheduled callbacks. The native magazine-completion
        // callback (14148a7b0) also sends 82/09, so a due completion must win over that packet
        // or a first post-reload Fire even when its scheduled callback has not run yet.
        WeaponArmResult? completion = null;
        PendingWeaponReload? pending = session.Reload;
        if (options.Enabled && options.Layout != WeaponFireLayout.Unknown
            && pending is not null && nowMs >= pending.DueAtMs
            && heldWeaponItemGuid == pending.WeaponGuid && ammo is not null)
        {
            ReadOnlySpan<byte> actionBody = WeaponBaseDecoder.Body(packet);
            bool currentWeaponAction = header.Sub == WeaponBaseDecoder.SubFire
                ? WeaponBaseDecoder.TryReadFire(packet, out WeaponFire fire)
                    && fire.WeaponGuid == pending.WeaponGuid
                : header.Sub == SubReloadInterrupt && actionBody.Length == sizeof(ulong)
                    && BitConverter.ToUInt64(actionBody) == pending.WeaponGuid;
            if (currentWeaponAction)
                completion = AdvanceReload(session, pending, nowMs, heldWeaponItemGuid, ammo.Inventory);
        }

        WeaponArmResult action = OneCore(session, packet, header, options, heldWeaponItemDefinitionId,
            heldWeaponItemGuid, shooterPosition, nowMs, ammo, heldWeaponDisplayDefinitionId);
        if (completion is not { } completed) return action;

        // An accepted shot may have spent one of the newly committed rounds. Emit the final
        // authoritative count, not the pre-shot snapshot created by AdvanceReload. Keep the
        // completion counter and packet order: a later cancellation snapshot must follow it.
        var replies = new List<byte[]>();
        void AddCompletion(byte[] reply)
        {
            if (reply.Length == 34 && reply[0] == 0x82 && reply[5] == WeaponReplyPackets.SubReload)
                reply = WeaponReplyPackets.Reload(WeaponReplyPackets.ImmediateGameTime,
                    pending!.WeaponGuid, 0, (uint)Math.Max(0, session.Shooter.AmmoOf(pending.WeaponGuid)),
                    (uint)Math.Max(0, pending.Ammo.Count(pending.AmmoItemId)),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(26)));
            replies.Add(reply);
        }
        if (completed.Reply is { } first) AddCompletion(first);
        if (completed.Replies is { } completedReplies)
            foreach (byte[] reply in completedReplies) AddCompletion(reply);
        if (action.Reply is { } actionReply) replies.Add(actionReply);
        if (action.Replies is { } actionReplies) replies.AddRange(actionReplies);
        return action with { Line = completed.Line + "; " + action.Line, Reply = null, Replies = replies,
            ReloadWork = action.ReloadWork ?? (ReferenceEquals(session.Reload, pending) ? completed.ReloadWork : null) };
    }

    private static WeaponArmResult OneCore(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        Vector3 shooterPosition,
        long nowMs,
        PlayerAmmoContext? ammo,
        uint heldWeaponDisplayDefinitionId)
    {
        switch (header.Sub)
        {
            case WeaponBaseDecoder.SubFireStateUpdate:
                return OnFireState(
                    session, packet, header, options, heldWeaponItemDefinitionId,
                    heldWeaponItemGuid, shooterPosition, nowMs, ammo);

            case WeaponBaseDecoder.SubWeaponFireHint when options.ActOnWeaponFireHint:
                return OnWeaponFireHint(
                    session, packet, header, options, heldWeaponItemGuid, nowMs);
            case WeaponBaseDecoder.SubFire:
                return OnFire(
                    session,
                    packet,
                    header,
                    options,
                    heldWeaponItemDefinitionId,
                    heldWeaponItemGuid,
                    nowMs,
                    ammo,
                    heldWeaponDisplayDefinitionId);
            case WeaponBaseDecoder.SubProjectileHitReport:
                return OnHitReport(session, packet, header, options, shooterPosition, nowMs);

            case WeaponBaseDecoder.SubReloadRequest:
                return OnReload(
                    session, packet, header, options, heldWeaponItemDefinitionId,
                    heldWeaponItemGuid, nowMs, ammo);

            case SubReloadInterrupt:
                return OnReloadInterrupt(session, packet, header);

            case 0x16: // ChamberRound: FUN_1414894c0 -> FUN_141483390, one item GUID.
            case 0x18: // ChamberInterrupt: FUN_141489400, same body.
                return OnChamber(session, packet, header, options, heldWeaponItemDefinitionId, heldWeaponItemGuid);

            case WeaponBaseDecoder.SubAmmoCountAcknowledge:
                return OnAmmoCountAcknowledge(session, packet, header);

            case WeaponBaseDecoder.SubSwitchFireModeRequest:
                return OnSwitchFireMode(
                    session, packet, header, options, heldWeaponItemDefinitionId, heldWeaponItemGuid);

            // docs/120 §6. The two subs only a thrown grenade produces. Before this lane they sat
            // in the log-only group below; with the arm on, the bounce is counted and the
            // detonation report PLACES the blast (D306, D309).
            case SubGuidedExplode when options.Throwables:
                return ThrowableArm.OnGuidedExplode(session, options, packet, header, nowMs);
            case SubGrenadeBounceReport when options.Throwables:
                return ThrowableArm.OnBounce(session, packet, header);

            // D315 (docs/125 §5). The two subs that say where a thrown thing WENT, which is what
            // the 2026-09-04 molotov needed and never got: it landed, reported the contact twice,
            // and sent no 82 19 at all, so the fallback put the fire back in the player's hand.
            // 82 1a falls through to the log-only group below when it names no live grenade.
            case WeaponBaseDecoder.SubProjectileContactReport when options.Throwables:
                return ThrowableArm.OnContactReport(session, packet, header, nowMs);
            case WeaponBaseDecoder.SubDestroyNpcProjectile when options.Throwables:
                return ThrowableArm.OnDestroyNpcProjectile(session, packet, header, nowMs)
                    ?? SeenNotActedOn(packet, header);

            // docs/89 §2c. The owner answers thirteen subs of this family and Cranberry answered
            // four; these are the rest, ported as NAMED, LOG-ONLY arms from his
            // ZoneCombat.cs:775-836. Naming a sub costs nothing and is worth a great deal the first
            // time one of them arrives, because the alternative is a bare hex dump nobody can
            // attribute. 82 22 MeleeHitMaterial in particular stays log-only on purpose: his own
            // handler refuses to apply it, because a melee hit resolves on 0xa0 (see MeleeArm).
            case SubReload:
            case SubGuidedExplode:
            case WeaponBaseDecoder.SubWeaponFireHint:
            case SubProjectileContactReport:
            case WeaponBaseDecoder.SubDestroyNpcProjectile:
            case SubMeleeHitMaterial:
            case SubGrenadeBounceReport:
            case SubAimBlockedNotify:
                return SeenNotActedOn(packet, header);

            default:
                // Unchanged pre-D52 behaviour: name it, print all of it, answer nothing.
                return new WeaponArmResult(WeaponBasePacketTrace.Format(packet), null, false, null);
        }
    }

    /// <summary>The named, log-only answer: one line, the whole packet's hex, no reply.</summary>
    private static WeaponArmResult SeenNotActedOn(ReadOnlySpan<byte> packet, in WeaponBaseHeader header) =>
        new(
            $"{Tag} 82 {header.Sub:x2} {NameOf(header.Sub)} gameTime={header.GameTime} "
            + $"- seen, not acted on. {WeaponBasePacketTrace.Format(packet)}",
            null,
            false,
            null);

    private static WeaponArmResult OnFireState(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        Vector3 shooterPosition,
        long nowMs,
        PlayerAmmoContext? ammo)
    {
        if (!WeaponBaseDecoder.TryReadFireStateUpdate(packet, out FireStateUpdate update))
        {
            session.Undecodable++;
            return Unreadable(packet, "FireStateUpdate", header);
        }

        bool clientSaysDry = update.FireState == WeaponBaseDecoder.EmptyFireState;
        bool triggerDown = !clientSaysDry
            && (update.FireState & WeaponBaseDecoder.FireStateTriggerDown) != 0;
        if (triggerDown && !session.Draw.IsReady(nowMs))
            return new WeaponArmResult($"{Tag} trigger ignored while drawing weapon", null, false, null);
        string dry = clientSaysDry
            ? " (the client says the magazine is DRY)"
            : triggerDown
                ? " (trigger DOWN)"
                : " (trigger up)";

        // MELEE (docs/89 addendum, report 2). The August client resolves a fists / melee-weapon
        // swing through THIS packet, not the abilities family: it sends 82 01 trigger-down and never
        // an 82 03 Fire or an a0 01/a0 02 (zero 0xa0 inbound across the 2026-09-03 19:48 session).
        // So a swing is arbitrated here, on the trigger-down of a melee item - and only a melee
        // item, so a gun's own state-17 (which is followed by an 82 03) and the binoculars are left
        // alone. It runs before the magazine resync because a melee row has no magazine to resync.
        if (options.MeleeOnTrigger && triggerDown
            && MeleeArm.IsMeleeItem(heldWeaponItemDefinitionId)
            && (heldWeaponItemGuid == 0 || update.WeaponGuid == heldWeaponItemGuid))
        {
            return MeleeArm.ResolveTriggerSwing(
                session,
                options,
                heldWeaponItemDefinitionId,
                update.WeaponGuid,
                shooterPosition,
                nowMs,
                header.GameTime);
        }

        // THE MAGAZINE RESYNC (D223, docs/107 §10). This packet is the first thing the client sends
        // on a trigger pull - before the 82 1f that carries the Fire - and the trigger going down at
        // all is the client saying it has something to fire.
        //
        // The 2026-09-03 17:51 session pulled the trigger 53 times against a magazine this server
        // was holding at 0, and refused every one. The client's own reload gate FUN_1411c7330 says
        // why it never asked to reload: predicate 3 is the weapon component's vtbl+0xb0, which
        // resolves through FUN_14228f8e0 to min(reserveInBag, clipSize - roundsNow) - and it returns
        // 0, silently, with no console text, when the client's own magazine is FULL. The client was
        // full, the server was empty, and nothing on this wire could tell either of them.
        // Adopting the client's view once, here, is what turns 53 refused pulls into 53 shots.
        string resync = string.Empty;
        List<byte[]>? stackUpdates = null;

        if (options.MagazineResync && ammo is not null && !clientSaysDry
            && (heldWeaponItemGuid == 0 || update.WeaponGuid == heldWeaponItemGuid))
        {
            session.Shooter.EnsureDeclared(
                update.WeaponGuid,
                heldWeaponItemDefinitionId,
                ShooterCombatState.DefaultMagazineFor(heldWeaponItemDefinitionId, options.Ammo));

            uint resyncItemId = session.Shooter.ItemDefinitionOf(update.WeaponGuid);
            uint resyncAmmoItemId = AmmoTypes.AmmoItemFor(resyncItemId);
            int clip = RetailBalance.ClipSize(resyncItemId);

            // docs/121 §5 (D333). THE ROUNDS COME OUT OF THE BAG. Both sessions that ever needed
            // this adoption had picked ammunition up first, and on 2026-09-03 21:35:46 the AR-15 was
            // handed 30 while the bag kept its 60 - ninety rounds from sixty. Whichever side loads
            // the first magazine, retail conserves ammunition: the adopted rounds are taken off the
            // shooter's own stack, and with no stack there is nothing to adopt and the refusal
            // stands - which is the state the client's own hint 15204 describes.
            bool payFromBag = options.MagazineResyncFromBag && options.Ammo.AmmoFromBag
                && resyncAmmoItemId != 0 && clip > 0;
            int carried = payFromBag ? ammo.Count(resyncAmmoItemId) : 0;

            if (payFromBag && carried <= 0)
            {
                if (session.Shooter.AmmoOf(update.WeaponGuid) == 0)
                {
                    resync = $" - MAGAZINE RESYNC WITHHELD: this server has 0 and the client is not "
                        + $"dry, but the bag holds no item {resyncAmmoItemId} for "
                        + $"{RetailBalance.NameFor(resyncItemId)}; the refusal stands (hint 15204; "
                        + $"{CombatOptions.MagazineResyncFromBagVariable}=0 hands the clip out free)";
                }
            }
            else
            {
                int adopted = session.Shooter.AdoptClientMagazine(update.WeaponGuid);

                if (adopted > 0 && payFromBag)
                {
                    stackUpdates = [];
                    int took = ammo.Take(resyncAmmoItemId, Math.Min(adopted, carried), stackUpdates);
                    session.Shooter.SetMagazine(update.WeaponGuid, Math.Max(0, took));
                    int left = ammo.Count(resyncAmmoItemId);
                    resync = $" - MAGAZINE RESYNC: this server had 0 and the client is not dry, so "
                        + $"{RetailBalance.NameFor(resyncItemId)} is now {took}/{clip}, PAID FROM THE "
                        + $"BAG ({took} of item {resyncAmmoItemId} taken, {left} left, "
                        + $"{stackUpdates.Count} stack packet(s)) "
                        + $"({CombatOptions.MagazineResyncVariable}=0 restores the refusal)";
                }
                else if (adopted > 0)
                {
                    resync = $" - MAGAZINE RESYNC: this server had 0 and the client is not dry, so "
                        + $"{RetailBalance.NameFor(resyncItemId)} is now {adopted}/{adopted} "
                        + $"({CombatOptions.MagazineResyncVariable}=0 restores the refusal)";
                }
            }
        }

        // docs/121 §7 (D335). A gun's trigger-up is what stops the proxied weapon on every
        // bystander's screen; the START rides the 82 20 hint, which carries the aim. Melee items
        // returned above, and the fists never reach here.
        PeerFireRelay relay = !triggerDown && !clientSaysDry
            && heldWeaponItemDefinitionId != 0
            && (heldWeaponItemGuid == 0 || update.WeaponGuid == heldWeaponItemGuid)
            && RetailBalance.ClipSize(heldWeaponItemDefinitionId) > 0
                ? PeerFireRelay.Stop
                : PeerFireRelay.None;
        if (relay == PeerFireRelay.Stop) session.Shooter.NoteTriggerStop(update.WeaponGuid);

        return new WeaponArmResult(
            $"{Tag} 82 01 FireStateUpdate gameTime={header.GameTime} weapon={update.WeaponGuid} "
            + $"state={update.FireState}{dry} u8={update.Unknown}{resync}",
            null,
            false,
            null,
            Replies: stackUpdates,
            Relay: relay) { RelayWeaponGuid = update.WeaponGuid };
    }

    /// <summary>
    /// <c>82 20 WeaponFireHint</c> - the direction half of a trigger pull (docs/107 §10). It is
    /// decoded, gated and <b>cross-checked</b> against the shot this server accepted; it never
    /// spends a round, because the paired <c>82 03 Fire</c> already did.
    /// </summary>
    private static WeaponArmResult OnWeaponFireHint(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        ulong heldWeaponItemGuid,
        long nowMs)
    {
        if (!WeaponBaseDecoder.TryReadWeaponFireHint(packet, out WeaponFireHint hint))
        {
            session.Undecodable++;
            return Unreadable(packet, "WeaponFireHint", header);
        }

        uint itemId = session.Shooter.ItemDefinitionOf(hint.WeaponGuid);
        string name = itemId == 0 ? "an undeclared weapon" : RetailBalance.NameFor(itemId);

        // Damage eligibility is diagnostic only: a hit may already have consumed it. Playback
        // claims the separately retained accepted-shot receipt below, once per trigger.
        int corroborated = 0;
        int candidates = 0;
        var described = new List<string>(hint.Hints.Count);

        foreach (WeaponFireHintEntry entry in hint.Hints)
        {
            if (session.Shooter.HasLiveHint(entry.ProjectileId, nowMs, options.FireHintLifetimeMs))
            {
                corroborated++;
            }

            candidates += entry.CandidateTargets.Count;
            described.Add(
                $"#{entry.ProjectileId} "
                + (entry.DirectionIsZero
                    ? "no direction"
                    : $"({entry.DirectionX:0.000}, {entry.DirectionY:0.000}, {entry.DirectionZ:0.000})")
                + (entry.CandidateTargets.Count == 0
                    ? string.Empty
                    : " -> " + string.Join(
                        '/', entry.CandidateTargets.Select(target => $"0x{target:x16}"))));
        }

        bool activeHand = hint.WeaponGuid == heldWeaponItemGuid || heldWeaponItemGuid == 0;
        bool stopped = false;
        bool present = activeHand && session.Shooter.TryPresentFire(hint, nowMs, options.FireHintLifetimeMs, out stopped);
        string verdict = !activeHand ? "NOT the active-hand instance - ignored"
            : present ? "the shot is corroborated; first observer playback"
            : "no new playback: Fire refused or never arrived, or receipt expired, mismatched or already presented";
        verdict += $"; {corroborated}/{hint.Hints.Count} projectile(s) match a live fire hint (damage eligibility only)";

        // docs/121 §7 (D335). A corroborated hint is the one packet of a pull that carries the AIM,
        // so it is the one the bystanders' 82 15 04 01 FireState(start) is built from: the muzzle
        // point plus the first projectile's direction, RelayReachUnits out. A zero direction (the
        // 17:52:17 shotgun hint) aims at the muzzle itself, which the point form tolerates.
        PeerFireRelay relay = PeerFireRelay.None;
        Vector3 aim = default;
        if (present)
        {
            WeaponFireHintEntry first = hint.Hints[0];
            aim = new Vector3(
                hint.X + (first.DirectionX * RelayReachUnits),
                hint.Y + (first.DirectionY * RelayReachUnits),
                hint.Z + (first.DirectionZ * RelayReachUnits));
            relay = PeerFireRelay.Start;
        }

        return new WeaponArmResult(
            $"{Tag} 82 20 WeaponFireHint gameTime={header.GameTime} weapon={hint.WeaponGuid} "
            + $"({name}) from ({hint.X:0.0}, {hint.Y:0.0}, {hint.Z:0.0}) marker={hint.Marker}, "
            + $"{hint.Hints.Count} projectile(s) [{string.Join(", ", described)}], "
            + $"{candidates} candidate target(s) - {verdict}",
            null,
            false,
            null,
            Relay: relay,
            RelayAimPoint: aim) { RelayWeaponGuid = hint.WeaponGuid, StopAfterRelay = present && stopped };
    }

    /// <summary>
    /// How far along the shot's own direction the relayed aim point sits, in world units. A
    /// Cranberry design value: far enough that the proxied weapon's aim is the shot's line rather
    /// than the muzzle, and inside <see cref="CombatOptions.MaxHitDistance"/>.
    /// </summary>
    public const float RelayReachUnits = 100f;

    private static WeaponArmResult OnFire(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        long nowMs,
        PlayerAmmoContext? ammo,
        uint heldWeaponDisplayDefinitionId)
    {
        if (!WeaponBaseDecoder.TryReadFire(packet, out WeaponFire fire))
        {
            session.Undecodable++;
            return Unreadable(packet, "Fire", header);
        }

        if (heldWeaponItemGuid != 0 && fire.WeaponGuid != heldWeaponItemGuid)
        {
            session.Shooter.CountRefusedShot();
            return new WeaponArmResult(
                $"{Tag} 82 03 Fire REFUSED - weapon={fire.WeaponGuid} is not the active-hand "
                + $"instance {heldWeaponItemGuid}; no shot was declared",
                null,
                false,
                null);
        }

        if (!session.Draw.IsReady(nowMs))
        {
            session.Shooter.CountRefusedShot();
            return new WeaponArmResult(
                $"{Tag} Fire REFUSED - weapon draw needs {session.Draw.ReadyAtMs - nowMs} ms",
                null, false, null,
                WeaponReplyPackets.FireRejected(fire.WeaponGuid, 0, 0, fire.ProjectileIds));
        }

        // docs/120 §6.1 (D305). A throwable's 82 03 is a THROW: the client raised THROW_END, spawned
        // its physics projectile and sent this - there is no magazine to spend (the weapon
        // definition has no ammo slot) and no refire gate to apply. It never reaches the gun
        // path below, which would have refused the second grenade on its 1-round CLIP_SIZE.
        if (options.Throwables && ThrowableArm.IsThrowable(heldWeaponItemDefinitionId))
        {
            return ThrowableArm.Throw(
                session, options, in fire, heldWeaponItemDefinitionId, nowMs, header.GameTime);
        }

        // THE PARITY RULE, and the only place it can live. Combat sees a weapon for the first time
        // when its trigger is pulled, so that first sight IS the pickup: under
        // AmmoOptions.GunsSpawnEmpty a gun the session has never met is declared with an EMPTY
        // magazine (the owner's ParityGunsSpawnEmpty, S6 3.1), and the client's own hint 15204 -
        // "That gun isn't going to load itself. Remember to load your weapon once you pick up some
        // ammo." - is retail saying the same thing. Only the live path does this: a decode-only
        // caller has no bag to reload from, so emptying its magazine would leave it stuck.
        if (ammo is not null && options.Ammo.GunsSpawnEmpty)
        {
            session.Shooter.EnsureDeclared(
                fire.WeaponGuid,
                heldWeaponItemDefinitionId,
                ShooterCombatState.DefaultMagazineFor(heldWeaponItemDefinitionId, options.Ammo));
        }

        FireResult result = session.Shooter.Fire(in fire, heldWeaponItemDefinitionId, nowMs, options,
            heldWeaponDisplayDefinitionId, clientGameTime: header.GameTime);
        uint itemId = session.Shooter.ItemDefinitionOf(fire.WeaponGuid);
        string name = RetailBalance.NameFor(itemId);

        string pellets = fire.ProjectileIds.Length != RetailBalance.RetailShotgunPellets
            && RetailBalance.RowForItem(itemId)?.Class == WeaponClass.Shotgun
                ? $" (retail says {RetailBalance.RetailShotgunPellets} pellets, the client declared "
                    + $"{fire.ProjectileIds.Length} - RECORDED, not overridden)"
                : string.Empty;

        string line = result.Verdict switch
        {
            FireVerdict.EmptyMagazine =>
                $"{Tag} 82 03 Fire REFUSED - {name} magazine empty (clip {result.ClipSize})",
            FireVerdict.RateOfFire =>
                $"{Tag} 82 03 Fire REFUSED - {result.SinceLastShotMs} ms since the last shot, "
                + $"{name} needs {result.GateMs} ms (rate-of-fire gate, REFIRE_TIME_MS)",
            FireVerdict.DuplicateProjectile =>
                $"{Tag} 82 03 Fire REFUSED - duplicate projectile identity; no ammunition or damage repeated",
            _ =>
                $"{Tag} 82 03 Fire FIRED gameTime={header.GameTime} {name} weapon={fire.WeaponGuid} at "
                + $"({fire.X:0.0}, {fire.Y:0.0}, {fire.Z:0.0}) - {result.Projectiles} projectile(s) "
                + $"#{string.Join(',', fire.ProjectileIds)}, ammo {result.AmmoLeft}/{result.ClipSize}, "
                + $"gate {result.GateMs} ms{pellets}",
        };

        if (result.Verdict != FireVerdict.Accepted)
        {
            // A DRY TRIGGER IS ANSWERED. The client had already decremented its own magazine and
            // drawn its own tracers when it sent this, so silence leaves the two copies apart for
            // good. 82 1e FireRejected is the client's own recovery: FUN_140dc0770 refunds the
            // rounds into its ammo slot and flags every projectile id named here as
            // rejected/hidden (S5c 5.3). Nothing else on the wire can undraw a shot.
            //
            // ONLY on an empty magazine, deliberately. The rate-of-fire gate is max(40 ms,
            // REFIRE_TIME_MS) against a sheet whose fastest weapon is 120 ms, which is close enough
            // to a real automatic's cadence that a client firing a frame early would have a
            // legitimate shot refunded and un-drawn on it. An empty magazine is not a matter of
            // milliseconds, so the refusal that is safe to answer is answered and the one that is
            // not stays silent - exactly as it was before this lane.
            byte[]? refusal =
                ammo is null || !options.Enabled || result.Verdict != FireVerdict.EmptyMagazine
                    ? null
                    : WeaponReplyPackets.FireRejected(
                        fire.WeaponGuid, fireGroupIndex: 0, fireModeIndex: 0, fire.ProjectileIds);

            return new WeaponArmResult(
                refusal is null ? line : $"{line}; answered 82 1e FireRejected",
                null,
                false,
                null,
                refusal);
        }

        // Every shot costs the gun five points of durability, floor 0, and the gun is NEVER removed
        // when it reaches zero - the owner's rule, verbatim (S6 4.5). The 11 03 that carries it is
        // the packet lane 1F derived; CRANBERRY_ITEM_UPDATE=0 keeps the arithmetic and drops the
        // packet.
        byte[]? durability = null;
        if (ammo is not null && options.Ammo.DurabilityLossPerShot > 0)
        {
            int left = session.Shooter.SpendDurability(
                fire.WeaponGuid, options.Ammo.DurabilityLossPerShot, options.Ammo.MaxDurability);

            if (left >= 0)
            {
                durability = ammo.WeaponDurability(fire.WeaponGuid, left);
                line += $", durability {left}/{options.Ammo.MaxDurability}"
                    + (durability is null ? string.Empty : " (11 03 sent)");
            }
        }

        // D315 (docs/125 §6): the bystanders' 82 15 04 0b, on every ACCEPTED trigger. Set here and
        // not on the refusals above, so a dry click relays nothing.
        WeaponArmResult? interrupted = CancelReload(session, fire.WeaponGuid, "fired during reload");
        return new WeaponArmResult(line, null, false, null, durability,
            interrupted?.Reply is { } reloadReply ? [reloadReply] : null)
        {
            LaunchRelay = true, RelayWeaponGuid = fire.WeaponGuid,
            RemoteUpdate = interrupted?.RemoteUpdate,
        };
    }

    private static WeaponArmResult OnHitReport(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        Vector3 shooterPosition,
        long nowMs)
    {
        if (!WeaponBaseDecoder.TryReadProjectileHitReport(packet, out ProjectileHitReport report))
        {
            session.Undecodable++;
            return Unreadable(packet, "ProjectileHitReport", header);
        }

        // D315 (docs/125 §5). A THROWN projectile's hit report before the gun path sees it: no
        // fire hint is ever registered for a throw, so the anti-replay gate below would refuse it
        // and drop the impact position the grenade needs.
        if (options.Throwables && ThrowableArm.OnHitReport(session, in report, header, nowMs) is { } thrown)
        {
            return thrown;
        }

        string location = report.HitLocationIsDictionaryId
            ? $"#{report.HitLocationId} (string-table id, no text on the wire)"
            : $"\"{report.HitLocation}\"";

        string seen =
            $"{Tag} 82 06 ProjectileHitReport gameTime={header.GameTime} projectile "
            + $"#{report.ProjectileId} target=0x{report.CharacterId:x16} location={location} "
            + $"pos=({report.X:0.0}, {report.Y:0.0}, {report.Z:0.0}) shots={report.TotalShotCount} "
            + $"hdr=0x{report.HitLocationHeader:x4} entries={report.HitEntryCount} "
            + $"unk={report.UnknownDword} flags=0x{report.Flags:x2}";

        if (options.Layout == WeaponFireLayout.Unknown)
        {
            return new WeaponArmResult(
                $"{seen} - NO DAMAGE, {CombatOptions.LayoutVariable}=0 (decode and log only)",
                null, false, null);
        }

        // D315: a report that names NO entity is a hit on the WORLD. It decodes (that is the fix),
        // it is logged, and it pays nothing - there is no guid to resolve and nothing to hurt.
        if (report.HitTheWorld)
        {
            return new WeaponArmResult(
                $"{seen} - the projectile hit the WORLD (target guid 0), not an entity. Decoded and "
                + "logged; nothing takes damage from terrain.",
                null, false, null);
        }

        if (!session.Shooter.TryConsumeHint(
                report.ProjectileId, nowMs, options.FireHintLifetimeMs, out FireHint hint))
        {
            return new WeaponArmResult(
                $"{seen} - REFUSED, no live fire hint for this projectile. A hit must follow a shot "
                + "this server accepted (anti-replay).",
                null, false, null);
        }

        PracticeTarget? target = session.Targets.Find(report.CharacterId);

        if (target is null)
        {
            // AUDIT-vehicles gap 3. This used to end here with "no damage model for it yet", and
            // that sentence was why shooting a car did nothing: the arbitration resolved the guid
            // against practice targets and players only. The guid and the gun's own body damage are
            // handed up instead, and the CALLER resolves them against the vehicle fleet - so this
            // arm still knows nothing about vehicles, and a guid that names neither still costs
            // exactly one log line.
            //
            // No range gate: MaxHitDistance is measured against a target position this arm does not
            // have for an unresolved guid. The fire hint has already been spent above, so the
            // anti-replay guard is unchanged.
            int units = RetailBalance.BodyDamageUnits(
                RetailBalance.WeaponDefinitionIdFor(hint.ItemDefinitionId),
                distance: 0d,
                options.UnmappedWeaponBodyUnits);

            return new WeaponArmResult(
                $"{seen} - unresolved target; offered to live player resolution, then the vehicle fleet at "
                + $"{units} unit(s). The hint is spent.",
                null,
                false,
                null,
                TargetGuid: report.CharacterId,
                TargetDamageUnits: units > 0 ? (uint)units : 0u)
            { UnresolvedHit = report, AcceptedFire = hint };
        }

        if (!target.IsAlive)
        {
            return new WeaponArmResult($"{seen} - the target is already down.", target, false, null);
        }

        double distance = Vector3.Distance(shooterPosition, target.Position);

        if (distance > options.MaxHitDistance)
        {
            return new WeaponArmResult(
                $"{seen} - REFUSED, the target is {distance:0} units away, over the "
                + $"{options.MaxHitDistance:0}-unit registration limit.",
                target, false, null);
        }

        // Feedback describes the surface hit, including helmet-penetrating weapons.
        // Inspect a copy so shotgun/sniper feedback cannot change armour durability.
        Armour beforeHit = target.Armour;
        bool helmetHit = HitRule.IsHead(report.HitLocation) && beforeHit.WearingHelmet(target.HelmetItemId);
        HitOutcome outcome = HitRule.Resolve(
            ref target.Armour,
            target.BodyArmourItemId,
            target.HelmetItemId,
            RetailBalance.WeaponDefinitionIdFor(hint.ItemDefinitionId),
            report.HitLocation,
            distance,
            options.UnmappedWeaponBodyUnits);

        session.Shooter.CountHit(outcome.DamageUnits);

        target.LastWeaponItemId = hint.KillFeedItemDefinitionId;
        target.LastHeadshot = HitRule.IsHead(report.HitLocation);
        bool killed = options.EnableCombatDamage
            && outcome.DamageUnits > 0
            && target.Damage(outcome.DamageUnits, nowMs);

        // Confirm accepted contact even when damage is disabled, including fully absorbed hits.
        WeaponHitFeedback? marker = options.SendHitMarker
            ? WeaponHitFeedback.For(in outcome, killed, armourHit: helmetHit || outcome.DamagedArmour) : null;

        return new WeaponArmResult(
            $"{seen} - {outcome.Why}; target {target.WorldGuid} at "
            + $"{target.Health}/{target.MaxHealth}{(killed ? " - DOWN" : string.Empty)}"
            + $"{(options.EnableCombatDamage ? string.Empty : " (damage model OFF)")}",
            target,
            killed,
            marker);
    }

    private static WeaponArmResult OnReload(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid,
        long nowMs,
        PlayerAmmoContext? ammo)
    {
        ReadOnlySpan<byte> body = WeaponBaseDecoder.Body(packet);

        if (body.Length < 8)
        {
            session.Undecodable++;
            return Unreadable(packet, "ReloadRequest", header);
        }

        ulong guid = BitConverter.ToUInt64(body[..8]);
        if (heldWeaponItemGuid != 0 && guid != heldWeaponItemGuid)
        {
            // A delayed request can arrive after a hand change. The old component still
            // needs a terminal reply or its native reload state blocks subsequent firing.
            return Rejected(header, guid, $"the active-hand instance is {heldWeaponItemGuid}");
        }

        if (ammo is null || !options.Ammo.AmmoFromBag)
        {
            return OnReloadFromNothing(session, header, guid);
        }

        if (heldWeaponItemDefinitionId == 0 && session.Shooter.AmmoOf(guid) < 0)
            return Rejected(header, guid, "UNKNOWN weapon guid; nothing refilled");

        // The weapon's FIRST sight, if this is it: an empty magazine, so that a player who picks a
        // gun up and immediately presses R is told he has nothing to put in it rather than being
        // handed a free clip.
        session.Shooter.EnsureDeclared(
            guid,
            heldWeaponItemDefinitionId,
            ShooterCombatState.DefaultMagazineFor(heldWeaponItemDefinitionId, options.Ammo));

        uint itemId = session.Shooter.ItemDefinitionOf(guid);
        string name = RetailBalance.NameFor(itemId);
        int clip = RetailBalance.ClipSize(itemId);
        int magazine = session.Shooter.AmmoOf(guid);

        if (magazine < 0)
        {
            return Rejected(header, guid, "UNKNOWN weapon guid; nothing refilled");
        }

        uint ammoItemId = AmmoTypes.AmmoItemFor(itemId);

        if (clip <= 0 || ammoItemId == 0)
        {
            // A throwable, the fists, a melee weapon: the owner chambers one and answers at once,
            // because there is no magazine and nothing to take out of the bag (S6 4.2, "throwable
            // -> chamber 1/1 + immediate Weapon.Reload").
            session.Shooter.SetMagazine(guid, 1);
            session.Shooter.Load(guid, 0);
            byte[] chambered = WeaponReplyPackets.Reload(
                WeaponReplyPackets.ImmediateGameTime,
                guid,
                projectileCount: 0,
                ammoCount: (uint)Math.Max(session.Shooter.AmmoOf(guid), 0),
                inventoryAmmoCount: 0,
                reloadCount: session.Shooter.ReloadCountOf(guid));

            return new WeaponArmResult(
                $"{Tag} 82 07 ReloadRequest gameTime={header.GameTime} weapon={guid} "
                + $"- {name} has no magazine in the August sheet; chambered and answered 82 08",
                null,
                false,
                null,
                chambered);
        }

        // D342: a client retry must not reject or restart a reload already in progress.
        // The completion token is the authoritative lifetime now; a second estimated deadline
        // would survive cancellation and suppress a legitimate replacement reload.
        if (session.Reload is { } existing)
        {
            if (existing.WeaponGuid != guid)
                return Rejected(header, guid, $"reload already pending for {existing.WeaponGuid}");
            return new WeaponArmResult(
                $"{Tag} 82 07 ReloadRequest weapon={guid} - reload already pending for {existing.WeaponGuid}",
                null, false, null, ReloadAcknowledgement(session, existing));
        }

        if (magazine >= clip)
        {
            return Rejected(header, guid, $"{name}'s magazine is already full ({magazine}/{clip})");
        }

        int carried = ammo.Count(ammoItemId);

        if (carried <= 0)
        {
            // The August client's own words for this state, hint 15204: "That gun isn't going to
            // load itself. Remember to load your weapon once you pick up some ammo."
            return Rejected(header, guid, $"no item {ammoItemId} in the bag for {name} (hint 15204)");
        }

        var replies = new List<byte[]>();
        bool shellByShell = AmmoTypes.IsShellByShell(itemId);
        int room = clip - magazine;
        int loaded = 0;

        int reloadMs = RetailBalance.ReloadTimeMs(
            itemId, session.Shooter.FireModeOf(guid), options.ShippedReloadTime, options.Z1LiveGunplay);
        if (options.TimedReload && reloadMs > 0)
        {
            var pending = new PendingWeaponReload(
                guid, itemId, ammoItemId, ammo, reloadMs, shellByShell, nowMs);
            session.Reload = pending;
            // August keeps interactions/hotbar changes behind comp+1e9 until 82/08 acknowledges
            // this request. Acknowledge the current counts now; spend rounds only at completion.
            // The later completion/interrupt advances the count again so it cannot be discarded
            // as the duplicate of this acknowledgement (FUN_1414887e0).
            session.Shooter.Load(guid, 0);
            return new WeaponArmResult(
                $"{Tag} 82 07 ReloadRequest weapon={guid} - {name} reload started; "
                + $"first {(shellByShell ? "shell" : "magazine")} due in {pending.IntervalMs} ms; no rounds moved yet",
                null, false, null, ReloadAcknowledgement(session, pending))
            {
                ReloadWork = pending, RelayWeaponGuid = guid,
                RemoteUpdate = RemoteWeaponPackets.WeaponUpdateType.Reload,
            };
        }

        // Explicit immediate mode, also used by callers without a listener dispatcher.
        // Timed reloads returned above; their state and replies advance together at each step.
        int perStep = shellByShell ? 1 : room;

        while (loaded < room)
        {
            int want = Math.Min(perStep, room - loaded);
            int took = ammo.Take(ammoItemId, want, replies);

            if (took <= 0)
            {
                break;
            }

            loaded += session.Shooter.Load(guid, took);

            replies.Add(WeaponReplyPackets.Reload(
                WeaponReplyPackets.ImmediateGameTime,
                guid,
                projectileCount: 0,
                ammoCount: (uint)Math.Max(session.Shooter.AmmoOf(guid), 0),
                inventoryAmmoCount: (uint)Math.Max(ammo.Count(ammoItemId), 0),
                reloadCount: session.Shooter.ReloadCountOf(guid)));

            if (!shellByShell)
            {
                break;
            }
        }

        if (loaded <= 0)
        {
            return Rejected(header, guid, $"nothing could be loaded into {name}");
        }

        int after = session.Shooter.AmmoOf(guid);
        string partial = after < clip
            ? $" (PARTIAL - the bag ran out, {ammo.Count(ammoItemId)} round(s) left)"
            : string.Empty;

        return new WeaponArmResult(
            $"{Tag} 82 07 ReloadRequest gameTime={header.GameTime} weapon={guid} - {name} loaded "
            + $"{loaded} round(s) of item {ammoItemId} from the bag, magazine {after}/{clip}"
            + $"{partial}{(shellByShell ? "; shell-by-shell" : string.Empty)}; answered "
            + $"{replies.Count} packet(s), reloadCount={session.Shooter.ReloadCountOf(guid)}",
            null,
            false,
            null,
            null,
            replies);
    }

    /// <summary>
    /// D118's answer, kept verbatim for the decode-only callers and for
    /// <c>CRANBERRY_AMMO_FROM_BAG=0</c>: the magazine is refilled out of nothing and one
    /// <c>82 08</c> goes back.
    /// </summary>
    private static WeaponArmResult OnReloadFromNothing(
        SessionCombat session, in WeaponBaseHeader header, ulong guid)
    {
        if (!session.Shooter.Reload(guid))
        {
            return Rejected(header, guid, "UNKNOWN weapon guid; nothing refilled");
        }

        // docs/89 D1, ported from ZoneCombat.cs:1352-1361. Before wave 9 this arm refilled a
        // server-side counter and sent NOTHING, so a reload could only ever appear to do nothing.
        int rounds = session.Shooter.AmmoOf(guid);
        ulong reloads = session.Shooter.ReloadCountOf(guid);

        byte[] reply = WeaponReplyPackets.Reload(
            WeaponReplyPackets.ImmediateGameTime,
            guid,
            projectileCount: 0,
            ammoCount: (uint)Math.Max(rounds, 0),
            inventoryAmmoCount: 0,
            reloadCount: reloads);

        return new WeaponArmResult(
            $"{Tag} 82 07 ReloadRequest gameTime={header.GameTime} weapon={guid} "
            + $"- magazine refilled to {rounds}; answered 82 08 Reload (reloadCount={reloads})",
            null,
            false,
            null,
            reply);
    }

    /// <summary>One refused reload: the log line and the <c>82 0b</c> that unsticks the client.</summary>
    private static WeaponArmResult Rejected(in WeaponBaseHeader header, ulong guid, string why) =>
        new(
            $"{Tag} 82 07 ReloadRequest gameTime={header.GameTime} weapon={guid} REFUSED - {why}; "
            + "answered 82 0b ReloadRejected",
            null,
            false,
            null,
            WeaponReplyPackets.ReloadRejected(guid));

    /// <summary>
    /// <b><c>82 0c SwitchFireModeRequest</c> - the ADS packet (docs/107 §2).</b> The busiest c2s
    /// weapon packet after the trigger: 145 across the owner's three 2026-09-02 sessions, one per
    /// right-click press and one per release, every one of them thrown away before D189.
    /// <para>
    /// <b>What this does, and what it deliberately does not.</b> It records the mode on the
    /// shooter's own weapon runtime, because the client's shot resolves through the mode it just
    /// selected and a server whose refire gate, projectile choice and rounds-per-shot are answering
    /// about mode 0 while the player is in mode 1 is answering about a different weapon. It does
    /// <b>not</b> send an acknowledgement to the shooter, and that is settled from the binary
    /// three independent ways (docs/107 §2, dumps under <c>out\ghidra-aug\w82-c2sbuilders</c> and
    /// <c>out\ghidra-aug\w82-ads</c>):
    /// </para>
    /// <list type="number">
    /// <item>The c2s writer <c>FUN_14148c1c0</c> calls the LOCAL setter
    /// <c>FUN_1422935d0(comp, group, mode, silent)</c> at <c>:26</c> and <b>only builds the packet
    /// at all when that returned 1</b> - the send is downstream of the apply, with no pending flag
    /// and no callback.</item>
    /// <item>That setter writes <c>comp+0x64 = group</c> and <c>comp+0x68 = mode</c> itself
    /// (<c>:55-56</c>), and <c>FUN_14228d970</c> resolves the live mode record from exactly those
    /// two fields every frame. The client is authoritative over its own fire mode.</item>
    /// <item>At 1148 the local <c>0x82</c> receive switch is exactly
    /// <c>{08 0b 0f 11 12 13 14 15 19 1b 1c 1e 24 25 26}</c> (<c>FUN_140b07010</c>, S5c §5.3) and
    /// <b><c>0x0c</c> has no receive case at all</b> - an <c>82 0c</c> sent back would fall through
    /// the default and be discarded. <c>82 15</c> is the viewer relay and is dropped for self
    /// (S5c §2), so it is not the answer either, and the owner's own Z1 answers the shooter with
    /// nothing in every branch (<c>ZoneCombat.cs:1775-1806</c>).</item>
    /// </list>
    /// <para>
    /// <b>Corollary, and it corrects FIRE-PATH-DIAGNOSIS §3.2:</b> the mode switch is not failing
    /// and <c>IRON_SIGHTS = 0</c> is not what stops it. The client only sends this packet after its
    /// own setter succeeded, so on every one of the owner's 145 presses the weapon really did enter
    /// mode 1. What is missing is the mode's VISIBLE consequence - list-2 columns 6
    /// <c>IRON_SIGHTS</c>, 43 <c>DEFAULT_ZOOM</c>, 49 <c>ARMS_FOV_SCALAR</c>, 187-190 the FP camera
    /// FOV set, and list-0 <c>def+0x38/+0x3c</c> - all of which this server ships as 0. Dead ADS is a
    /// DATA gap, not a protocol one.
    /// </para>
    /// <para>
    /// <see cref="CombatOptions.SwitchFireModeReply"/> is nevertheless the switch this arm ships
    /// behind, because "record it" is itself a behaviour change on a packet that has never been
    /// acted on: off, the arm falls back to the pre-wave-13 named log line and nothing is recorded.
    /// </para>
    /// </summary>
    private static WeaponArmResult OnSwitchFireMode(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header,
        CombatOptions options,
        uint heldWeaponItemDefinitionId,
        ulong heldWeaponItemGuid)
    {
        if (!WeaponBaseDecoder.TryReadSwitchFireModeRequest(
                packet, out SwitchFireModeRequest request))
        {
            session.Undecodable++;
            return Unreadable(packet, "SwitchFireModeRequest", header);
        }

        string aim = request.FireModeIndex == AimDownSightsFireModeIndex
            ? " (AIM DOWN SIGHTS)"
            : request.FireModeIndex == 0 ? " (hip)" : string.Empty;

        string seen =
            $"{Tag} 82 0c SwitchFireModeRequest gameTime={header.GameTime} "
            + $"weapon={request.WeaponGuid} group={request.FireGroupIndex} "
            + $"mode={request.FireModeIndex}{aim} u8={request.Unknown}";

        if (!options.SwitchFireModeReply)
        {
            return new WeaponArmResult(
                $"{seen} - seen, not acted on ({CombatOptions.SwitchFireModeReplyVariable}=0)",
                null, false, null);
        }

        if (heldWeaponItemGuid != 0 && request.WeaponGuid != heldWeaponItemGuid)
        {
            return new WeaponArmResult(
                $"{seen} - REFUSED, the active-hand instance is {heldWeaponItemGuid}",
                null, false, null);
        }

        // The mode switch can be a weapon's first sight, exactly as the trigger can (the owner's
        // sessions send 82 0c before the first 82 01), so the parity rule runs here too or the
        // switch would be dropped for an undeclared guid and the first shot would then re-declare
        // the weapon in mode 0.
        session.Shooter.EnsureDeclared(
            request.WeaponGuid,
            heldWeaponItemDefinitionId,
            ShooterCombatState.DefaultMagazineFor(heldWeaponItemDefinitionId, options.Ammo));

        // The descriptor we actually send contains one group and two modes. The decoder's
        // signed-byte ceiling is a wire check, not evidence that other indices exist.
        if (request.FireGroupIndex != 0 || request.FireModeIndex > 1
            || !AugustWeaponTable.HasFireGroup(heldWeaponItemDefinitionId, out _))
            return new WeaponArmResult($"{seen} - REFUSED, mode absent from the weapon descriptor", null, false, null);

        bool changed = session.Shooter.FireGroupOf(request.WeaponGuid) != request.FireGroupIndex
            || session.Shooter.FireModeOf(request.WeaponGuid) != request.FireModeIndex;

        if (!session.Shooter.SelectFireMode(
                request.WeaponGuid, request.FireGroupIndex, request.FireModeIndex))
        {
            return new WeaponArmResult(
                $"{seen} - UNKNOWN weapon guid; nothing recorded", null, false, null);
        }

        return new WeaponArmResult(
            $"{seen} - recorded; this weapon is now in group "
            + $"{session.Shooter.FireGroupOf(request.WeaponGuid)} mode "
            + $"{session.Shooter.FireModeOf(request.WeaponGuid)} "
            + $"({session.Shooter.FireModeSwitchesOf(request.WeaponGuid)} switch(es) this session). "
            + "The local switch needs no s2c answer at 1148 - sub 0x0c has no receive case",
            null, false, null)
        {
            RelayWeaponGuid = request.WeaponGuid,
            RemoteUpdate = changed ? RemoteWeaponPackets.WeaponUpdateType.SwitchFireMode : null,
            RelayFireGroup = request.FireGroupIndex, RelayFireMode = request.FireModeIndex,
        };
    }

    private static WeaponArmResult OnChamber(SessionCombat session, ReadOnlySpan<byte> packet,
        in WeaponBaseHeader header, CombatOptions options, uint heldItem, ulong heldGuid)
    {
        ReadOnlySpan<byte> body = WeaponBaseDecoder.Body(packet);
        if (body.Length != sizeof(ulong))
        {
            session.Undecodable++;
            return Unreadable(packet, "Chamber", header);
        }
        ulong guid = BitConverter.ToUInt64(body);
        bool interrupt = header.Sub == 0x18;
        if (guid == 0 || guid != heldGuid || RetailBalance.ClipSize(heldItem) <= 0
            || !AugustWeaponTable.HasFireGroup(heldItem, out _))
            return new WeaponArmResult($"{Tag} chamber notification ignored for weapon={guid}", null, false, null);
        session.Shooter.EnsureDeclared(guid, heldItem, ShooterCombatState.DefaultMagazineFor(heldItem, options.Ammo));
        if (!session.Shooter.NoteChamber(guid, header.GameTime, interrupt))
            return new WeaponArmResult($"{Tag} chamber notification ignored for weapon={guid}", null, false, null);
        return new WeaponArmResult($"{Tag} chamber {(interrupt ? "interrupt" : "start")} weapon={guid}", null, false, null)
        {
            RelayWeaponGuid = guid,
            RemoteUpdate = interrupt ? RemoteWeaponPackets.WeaponUpdateType.ChamberInterrupt
                : RemoteWeaponPackets.WeaponUpdateType.Chamber,
        };
    }

    /// <summary>
    /// The fire-mode index the August client selects on right-click. The owner's Z1 names mode 1 as
    /// the aim-down-sights mode (<c>ZoneCombat.cs:1786-1793</c>), and the August wire agrees: the
    /// index is 1 on every press and 0 on every release across 145 packets.
    /// </summary>
    public const byte AimDownSightsFireModeIndex = 1;

    /// <summary>
    /// <c>82 28 AmmoCountAcknowledge</c> - the client reporting that the magazine it was just handed
    /// in <c>82 08</c> is not the one it had.
    /// <para>
    /// <b>WAVE 13 CORRECTION (docs/107 1, D197), and it is the difference between a working reload
    /// and a magazine that snaps back to empty.</b> This arm used to adopt
    /// <c>acknowledge.ClientAmmo</c> - "the client's number wins: it is what the player can see".
    /// <b>The client's number here is stale by construction.</b> Its writer is the <c>82 08</c>
    /// applier <c>FUN_1414887e0</c>, and the disassembly gives the order exactly: it reads its own
    /// count (<c>call FUN_14228e710; mov r15d,eax</c>), compares that with our <c>ammoCount</c> and
    /// sets the "differs" flag, and only THEN calls
    /// <c>FUN_1422938d0(comp, modeDef+0x30, ammoCount)</c> to write our number into the ammo slot.
    /// So by the time this packet reaches us the client has ALREADY taken our count; the field it
    /// reports is the value it just discarded.
    /// </para>
    /// <para>
    /// Adopting it would roll the server back to the pre-reload number - and once the tail carries a
    /// magazine this path fires on almost every first reload of a session, because the client starts
    /// at whatever the tail said and we answer with a full one. The reconcile is therefore a LOG:
    /// both numbers are printed and the server keeps its own, which is also the number the client is
    /// now displaying. If the two ever genuinely drift, the only corrective in the image is another
    /// <c>82 08</c> with a strictly rising <c>reloadCount</c>; there is no cheaper resync packet, and
    /// <c>82 28</c> itself is c2s only (no receive case in <c>FUN_140b07010</c>).
    /// </para>
    /// </summary>
    private static WeaponArmResult OnAmmoCountAcknowledge(
        SessionCombat session, ReadOnlySpan<byte> packet, in WeaponBaseHeader header)
    {
        if (!WeaponBaseDecoder.TryReadAmmoCountAcknowledge(
                packet, out AmmoCountAcknowledge acknowledge))
        {
            session.Undecodable++;
            return Unreadable(packet, "AmmoCountAcknowledge", header);
        }

        int held = session.Shooter.AmmoOf(acknowledge.WeaponGuid);

        return new WeaponArmResult(
            $"{Tag} 82 28 AmmoCountAcknowledge gameTime={header.GameTime} "
            + $"weapon={acknowledge.WeaponGuid} - the client counted {acknowledge.ClientAmmo} "
            + $"BEFORE applying the {acknowledge.ServerAmmo} we sent"
            + (held >= 0
                ? $"; server magazine stays {held} (its field is the pre-refill value it discarded, "
                    + "FUN_1414887e0 - adopting it would undo the reload)"
                : "; UNKNOWN weapon guid, nothing reconciled"),
            null,
            false,
            null);
    }

    /// <summary>The owner's own name for each sub, so a first sighting is attributable.</summary>
    /// <summary>
    /// <b>The client's own name for a sub of this family</b> - see
    /// <see cref="WeaponBaseDecoder.NameOfSub"/>, which is the August binary's registration table
    /// verbatim and replaced this arm's hand-kept list on 2026-09-03.
    /// </summary>
    private static string NameOf(byte sub) => WeaponBaseDecoder.NameOfSub(sub);

    private static WeaponArmResult Unreadable(
        ReadOnlySpan<byte> packet, string what, in WeaponBaseHeader header) =>
        new(
            $"{Tag} {what} could not be read (gameTime={header.GameTime}). NO DAMAGE. "
            + $"*** THESE BYTES ARE EVIDENCE *** {WeaponBasePacketTrace.Format(packet)}",
            null,
            false,
            null);
}
