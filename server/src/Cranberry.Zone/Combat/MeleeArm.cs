using System.Numerics;

namespace Cranberry.Zone.Combat;

public readonly record struct MeleePlayerTarget(ulong Guid, Vector3 Position);
public readonly record struct MeleePlayerHit(ulong Guid, uint Damage, uint ItemDefinitionId);

/// <summary>
/// Resolves close-contact melee against practice targets and eligible live players. September 6
/// August captures carry both weapon trigger and ability packets for a punch, so only one family
/// applies damage at a time. Reach, facing and cadence apply equally to either entry point.
/// See docs/combat-gameplay-20260906.md for evidence and the owner's gameplay tuning.
/// </summary>
public static class MeleeArm
{
    /// <summary>The grep tag every line this arm writes carries.</summary>
    public const string Tag = "[MELEE]";

    /// <summary>
    /// The reference's own <c>baseDamage</c>, on the same 10,000-unit bar a player has - so a bare
    /// fist is a ten-punch kill and a machete a four-swing one. His own comment calls 1000 a number
    /// that still needs figuring out; it is copied rather than improved, so the two servers hurt
    /// the same (D53).
    /// </summary>
    public const int BaseDamageUnits = 1_000;

    /// <summary>
    /// Close-contact reach and a forward 90-degree cone, per the owner's gameplay correction.
    /// These are server tuning values, not a claim about recovered retail constants.
    /// </summary>
    public const double MeleeRange = 2.0;
    public const float MinimumFacingDot = 0.70710677f;
    public const int MinimumSwingIntervalMs = 500;

    /// <summary>
    /// Blades and knives: x3. The owner's list, verbatim
    /// (<c>ZoneCombat.MeleeDamageFor</c>). Item 83 is the combat knife, 84 the machete.
    /// </summary>
    private static readonly uint[] Blades =
        [76, 82, 83, 84, 1373, 1441, 1723, 1724, 2271, 2272];

    /// <summary>Blunt weapons: x2. The owner's list, verbatim.</summary>
    private static readonly uint[] Blunt = [58, 66, 67, 68, 92, 113, 1428, 1429];

    /// <summary>
    /// <c>handleMeleeHit</c>'s damage switch: x3 for blades, x2 for blunt, x1 for the fists and for
    /// anything unlisted. A melee packet does not establish a verified head contact, so the
    /// amount is fixed for the wielded item on both packet families.
    /// </summary>
    public static int MeleeDamageFor(uint itemDefinitionId)
    {
        if (Array.IndexOf(Blades, itemDefinitionId) >= 0)
        {
            return BaseDamageUnits * 3;
        }

        return Array.IndexOf(Blunt, itemDefinitionId) >= 0
            ? BaseDamageUnits * 2
            : BaseDamageUnits;
    }

    /// <summary>
    /// <b>Is this item a thing that melees on a trigger pull?</b> The fists (item 85, or 0 = an
    /// empty active hand, which the client also swings with), and every blade or blunt row the
    /// damage table knows. Deliberately NOT "any weapon with no ammunition": a utility Weapon row
    /// like the binoculars (item 1542) has no calibre either but is not a weapon that swings, so it
    /// is excluded, which is what keeps a right-click on binoculars from dealing melee damage.
    /// </summary>
    public static bool IsMeleeItem(uint itemDefinitionId) =>
        itemDefinitionId == 0
        || itemDefinitionId == AbilityPackets.FistsItemDefinitionId
        || Array.IndexOf(Blades, itemDefinitionId) >= 0
        || Array.IndexOf(Blunt, itemDefinitionId) >= 0;

    /// <summary>
    /// <b>Resolve one melee swing off the WEAPON fire path (docs/89 addendum, report 2).</b> The
    /// August client sends an <c>82 01 FireStateUpdate</c> trigger-down for a swing, so
    /// <see cref="WeaponFireArm.OnFireState"/> calls this when the trigger goes down on a
    /// <see cref="IsMeleeItem"/> item. It reuses the same damage table, range gate and hit marker as
    /// the fallback <c>0xa0</c> path.
    /// <para>
    /// <b>No headshot doubling.</b> The <c>82 01</c> body carries a guid and a state byte only - no
    /// hit-location string - so there is nothing on the wire that could say the swing hit a head,
    /// and inventing one would be a Cranberry number in front of a client fact.
    /// </para>
    /// </summary>
    /// <param name="session">The swinger's combat state.</param>
    /// <param name="options">The switches.</param>
    /// <param name="heldItemDefinitionId">What is in the active hand; 0 means the fists.</param>
    /// <param name="weaponGuid">The wielded instance the trigger update named, for the log line.</param>
    /// <param name="swingerPosition">Where the swinger is, for the range gate.</param>
    /// <param name="nowMs">Server clock.</param>
    /// <param name="gameTime">The <c>82 01</c> game time, for the log line.</param>
    public static WeaponArmResult ResolveTriggerSwing(
        SessionCombat session,
        CombatOptions options,
        uint heldItemDefinitionId,
        ulong weaponGuid,
        Vector3 swingerPosition,
        long nowMs,
        uint gameTime)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        uint weaponItemId = heldItemDefinitionId == 0
            ? AbilityPackets.FistsItemDefinitionId
            : heldItemDefinitionId;

        int damage = MeleeDamageFor(weaponItemId);

        string seen =
            $"{Tag} 82 01 melee swing gameTime={gameTime} item={weaponItemId} weapon={weaponGuid} "
            + $"dmg={damage} (trigger-down; ability copies do not apply damage)";

        return ResolveHit(session, options, weaponItemId, swingerPosition, nowMs, seen);
    }

    /// <summary>
    /// Handles one inbound <c>0xa0</c>. Appends one <see cref="WeaponArmResult"/> per packet so the
    /// caller can reuse the <c>0x82</c> arm's loop verbatim - hit markers and despawns then behave
    /// identically however the damage arrived.
    /// </summary>
    /// <param name="session">The swinger's combat state.</param>
    /// <param name="packet">The whole inbound packet, starting at the <c>0xa0</c> base byte.</param>
    /// <param name="options">The switches.</param>
    /// <param name="heldItemDefinitionId">What is in the active hand; 0 means the fists.</param>
    /// <param name="swingerPosition">Where the swinger is, for the range gate.</param>
    /// <param name="nowMs">Server clock.</param>
    /// <param name="results">One entry per packet handled, in order.</param>
    public static void Handle(
        SessionCombat session,
        ReadOnlySpan<byte> packet,
        CombatOptions options,
        uint heldItemDefinitionId,
        Vector3 swingerPosition,
        long nowMs,
        List<WeaponArmResult> results)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(results);

        // The caller reuses one list across every combat packet of the session, exactly as the
        // 0x82 arm does, so it is emptied here or a swing is logged and answered twice.
        results.Clear();

        if (!AbilityPackets.TryReadAbilityRequest(
                packet, out byte sub, out uint abilityId, out string hitLocation))
        {
            session.Undecodable++;
            results.Add(new WeaponArmResult(
                $"{Tag} 0xa0 could not be read. NO DAMAGE. *** THESE BYTES ARE EVIDENCE *** "
                + $"len={packet.Length} hex={Convert.ToHexString(packet)}",
                null,
                false,
                null));
            return;
        }

        session.AbilityPacketsSeen++;

        // September 6 August-client traffic contains BOTH families for one punch. The
        // trigger is authoritative when enabled; the ability copy must never pay again,
        // even when it arrives much later than its matching trigger.
        if (options.MeleeOnTrigger)
        {
            session.MeleeArmedAtMs = 0;
            session.MeleeHitLocation = string.Empty;
            results.Add(new WeaponArmResult(
                $"{Tag} 0xa0 sub=0x{sub:x2} ability={abilityId} - melee handled by weapon trigger",
                null, false, null));
            return;
        }

        switch (sub)
        {
            case AbilityOpcodes.InitAbilitySub:
                session.MeleeArmedAtMs = nowMs;
                session.MeleeHitLocation = hitLocation;
                results.Add(new WeaponArmResult(
                    $"{Tag} a0 01 InitAbility ability={abilityId} "
                    + $"hitLocation=\"{hitLocation}\" - swing STARTED, waiting for a0 02",
                    null,
                    false,
                    null));
                return;

            case AbilityOpcodes.UpdateAbilitySub:
                results.Add(Resolve(
                    session, abilityId, hitLocation, options, heldItemDefinitionId,
                    swingerPosition, nowMs));
                return;

            default:
                results.Add(new WeaponArmResult(
                    $"{Tag} 0xa0 sub=0x{sub:x2} ability={abilityId} - no arm for this sub. "
                    + $"len={packet.Length} hex={Convert.ToHexString(packet)}",
                    null,
                    false,
                    null));
                return;
        }
    }

    private static WeaponArmResult Resolve(
        SessionCombat session,
        uint abilityId,
        string hitLocation,
        CombatOptions options,
        uint heldItemDefinitionId,
        Vector3 swingerPosition,
        long nowMs)
    {
        if (session.MeleeArmedAtMs == 0)
        {
            // The reference refuses an update with no init before it, and so does he, and so does
            // this: otherwise the click's own UpdateAbility pays damage a second time.
            return new WeaponArmResult(
                $"{Tag} a0 02 UpdateAbility ability={abilityId} with no InitAbility before it - "
                + "ignored, exactly as processAbilityUpdate does",
                null,
                false,
                null);
        }

        long armedForMs = nowMs - session.MeleeArmedAtMs;
        session.MeleeArmedAtMs = 0;

        string where = hitLocation.Length > 0 ? hitLocation : session.MeleeHitLocation;
        session.MeleeHitLocation = string.Empty;

        uint weaponItemId = heldItemDefinitionId == 0
            ? AbilityPackets.FistsItemDefinitionId
            : heldItemDefinitionId;

        int damage = MeleeDamageFor(weaponItemId);

        string seen =
            $"{Tag} a0 02 UpdateAbility ability={abilityId} item={weaponItemId} "
            + $"hitLocation=\"{where}\" swing={armedForMs} ms dmg={damage}";

        return ResolveHit(session, options, weaponItemId, swingerPosition, nowMs, seen);
    }

    private static WeaponArmResult ResolveHit(SessionCombat session, CombatOptions options,
        uint weaponItemId, Vector3 origin, long nowMs, string seen)
    {
        if (!IsMeleeItem(weaponItemId))
            return new WeaponArmResult($"{seen} - active item cannot melee", null, false, null);
        if (session.LastMeleeSwingMs is long previous && nowMs - previous < MinimumSwingIntervalMs)
            return new WeaponArmResult($"{seen} - duplicate/early swing", null, false, null);
        session.LastMeleeSwingMs = nowMs;
        session.MeleeSwings++;

        if (!options.MeleeDamage)
        {
            return new WeaponArmResult(
                $"{seen} - NOT applied, {CombatOptions.MeleeDamageVariable}=0", null, false, null);
        }

        int damage = MeleeDamageFor(weaponItemId);
        PracticeTarget? victim = null;
        MeleePlayerTarget? player = null;
        float bestDistance = (float)(MeleeRange * MeleeRange);
        foreach (PracticeTarget candidate in session.Targets.All)
        {
            if (!candidate.IsAlive || !CanReach(origin, session.MeleeHeading, candidate.Position)) continue;
            float distance = Vector3.DistanceSquared(origin, candidate.Position);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            victim = candidate;
        }
        foreach (MeleePlayerTarget candidate in session.MeleePlayers)
        {
            if (!CanReach(origin, session.MeleeHeading, candidate.Position)) continue;
            float distance = Vector3.DistanceSquared(origin, candidate.Position);
            if (distance > bestDistance || (distance == bestDistance && victim is not null)) continue;
            bestDistance = distance;
            victim = null;
            player = candidate;
        }

        if (options.EnableCombatDamage && session.MeleeGlass is { } glass
            && glass.Distance * glass.Distance <= bestDistance)
            return new WeaponArmResult($"{seen} - glass {glass.ObjectId}", null, false, null)
            { MeleeGlassObjectId = glass.ObjectId };

        if (player is { } peer)
            return new WeaponArmResult($"{seen} - player {peer.Guid}", null, false, null)
            { MeleeHit = new(peer.Guid, (uint)damage, weaponItemId) };

        if (victim is null)
        {
            return new WeaponArmResult(
                $"{seen} - hit AIR; no target in the forward reach within {MeleeRange:0.#} units",
                null,
                false,
                null);
        }

        bool killed = options.EnableCombatDamage && victim.Damage(damage, nowMs);
        if (options.EnableCombatDamage)
        {
            victim.LastWeaponItemId = weaponItemId;
            victim.LastHeadshot = false;
        }

        // The marker goes to the swinger whether or not the damage model is on: it is UI, and it is
        // the only feedback a melee gives. Armour flags stay clear - melee does not consult armour,
        // in this build or in his (his ApplyDamage takes a plain number).
        WeaponHitFeedback? marker = options.SendHitMarker
            ? WeaponHitFeedback.For(new HitOutcome(damage, false, false, false, true, "melee"), killed, melee: true)
            : null;

        return new WeaponArmResult(
            $"{seen} - target {victim.WorldGuid} at {victim.Health}/{victim.MaxHealth}"
            + (killed ? " - DOWN" : string.Empty)
            + (options.EnableCombatDamage ? string.Empty : " (damage model OFF)"),
            victim,
            killed,
            marker);
    }

    /// <summary>Finite, close contact within the attacker's forward cone.</summary>
    public static bool CanReach(Vector3 from, float? heading, Vector3 target)
    {
        if (heading is not float yaw || !float.IsFinite(yaw)) return false;
        Vector3 offset = target - from;
        float distanceSquared = offset.LengthSquared();
        if (!float.IsFinite(distanceSquared) || distanceSquared > MeleeRange * MeleeRange) return false;
        float horizontalSquared = offset.X * offset.X + offset.Z * offset.Z;
        // An overlapping/vertically aligned centre has no trustworthy facing direction.
        if (horizontalSquared < 0.0001f) return false;
        float dot = (MathF.Sin(yaw) * offset.X + MathF.Cos(yaw) * offset.Z)
            / MathF.Sqrt(horizontalSquared);
        return dot >= MinimumFacingDot;
    }
}
