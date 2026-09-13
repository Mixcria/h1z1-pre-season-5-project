using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

/// <summary>
/// <b>docs/120 - the wire half of a throw.</b> <see cref="ThrowableArm"/> decides; this file sends:
/// the stack decrement when a grenade leaves the hand, the launch relay to viewers, the D306
/// fallback timer, and - when it goes off - the effect, the radius damage and the cloud.
/// </summary>
public sealed partial class ZoneService
{
    /// <summary>
    /// Every admitted session by character guid, so a detonation can reach the players inside its
    /// radius with their own connection and health bar. Filled beside the peer registry
    /// (<c>RegisterPeerSession</c>) and emptied with it (<c>PeerLinkClosed</c>).
    /// </summary>
    private readonly Dictionary<ulong, (SoeConnection Connection, GatewaySessionState State)> _throwableSessions = [];

    /// <summary>Cloud anchors: a namespace of their own, above the practice targets' 0x4800....</summary>
    private const ulong CloudAnchorGuidBase = 0x4900_0000_0000_0001UL;

    /// <summary>Above the practice targets' 3,000,000 block.</summary>
    private const uint CloudAnchorTransientIdBase = 3_100_000;

    private ulong _nextCloudAnchorGuid = CloudAnchorGuidBase;

    private uint _nextCloudAnchorTransientId = CloudAnchorTransientIdBase;

    private void RegisterThrowableSession(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Guid != 0)
        {
            _throwableSessions[state.Guid] = (connection, state);
        }
    }

    private void ForgetThrowableSession(GatewaySessionState state)
    {
        if (state.Guid != 0)
        {
            _throwableSessions.Remove(state.Guid);
        }
    }

    /// <summary>
    /// D305 - the grenade has left the hand: one unit leaves the stack (<c>11 02</c> re-announce
    /// for a partial stack, <c>11 04 ItemDelete</c> + the rebuilt loadout + a re-dress when it was
    /// the last), viewers are told the launch (D310), and the fallback timer is armed (D306).
    /// </summary>
    private void OnGrenadeThrown(SoeConnection connection, GatewaySessionState state, in ThrownGrenade thrown)
    {
        LiveGrenade grenade = thrown.Grenade;

        if (state.Inventory is PlayerInventory inventory
            && inventory.Items.TryGetValue(grenade.ItemGuid, out InventoryItemInstance? stack))
        {
            uint before = stack.Count;
            uint taken = inventory.RemoveUnits(grenade.ItemGuid, 1);
            uint left = before - taken;

            if (left == 0)
            {
                // The last one: the instance is gone, so its clearance, its tile, its loadout
                // binding and its place in the hand go with it - the drop path's own order
                // (ItemDelete, then the whole loadout, then the dress).
                ulong gone = grenade.ItemGuid;
                state.Weapons.Ledger.Forget(gone);
                SendTunnel(connection, writer => new ItemDelete(state.Guid, gone).WriteTo(writer));
                SendLoadoutSlots(connection, inventory);
                if (state.HeldWeaponItemGuid == gone)
                {
                    state.HeldWeaponItemGuid = 0;
                }

                SendCharacterAppearance(connection, state, $"threw the last {grenade.Fact.Name}");
                _log.Info($"{connection} throwables: {grenade.Fact.Name} x1 thrown - the stack "
                    + $"{gone} is empty; 11 04 ItemDelete, the loadout and the dress re-sent");
            }
            else
            {
                InventoryItem updated = inventory.Items[grenade.ItemGuid].ToRecord(state.Guid);
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, updated));
                _log.Info($"{connection} throwables: {grenade.Fact.Name} x1 thrown - {left} left on "
                    + $"stack {grenade.ItemGuid} (11 02 re-announced at the new count)");
            }
        }
        else
        {
            _log.Info($"{connection} throwables: {grenade.Fact.Name} thrown from an instance this "
                + $"inventory does not hold ({grenade.ItemGuid}) - nothing taken from any stack");
        }

        long fuseMs = grenade.FuseDueAtMs - grenade.ThrownAtMs;
        int wait = (int)Math.Clamp(fuseMs + Rulings.Throwables.FallbackGraceMs, 0, int.MaxValue);
        int worldGeneration = state.WorldGeneration;
        Later(connection, wait, () => PumpOverdueGrenades(connection, state, worldGeneration));
        int flightWait = (int)(grenade.Fact.ProjectileLifespanSeconds * 1000) + Rulings.Throwables.FallbackGraceMs;
        if (flightWait > wait)
            Later(connection, flightWait, () => PumpOverdueGrenades(connection, state, worldGeneration));
    }

    /// <summary>D306 - the fallback: every grenade past its fuse plus the grace goes off at its throw point.</summary>
    private void PumpOverdueGrenades(SoeConnection connection, GatewaySessionState state, int worldGeneration)
    {
        if (worldGeneration != state.WorldGeneration) return;

        foreach (Detonation detonation in ThrowableArm.Overdue(state.Combat, Environment.TickCount64))
        {
            _log.Info($"{connection} throwables: no 82 19 GuidedExplode for projectile "
                + $"#{detonation.ProjectileId} within the fuse + {Rulings.Throwables.FallbackGraceMs} ms - "
                + $"{detonation.Fact.Name} detonated at ({detonation.Position.X:0.0}, "
                + $"{detonation.Position.Y:0.0}, {detonation.Position.Z:0.0}), {detonation.SourceWord}"
                + (detonation.SpareThrower ? ", thrower spared (D306)" : ", thrower NOT spared (D315)"));
            Detonate(connection, state, detonation);
        }
    }

    /// <summary>
    /// A grenade went off (docs/120 §6.3): the one-shot effect at the position for the thrower and
    /// every viewer (<c>0f 43</c>, D307), the radius damage (D308) and, for a cloud, the anchor
    /// actor with its looping effect tag and its damage ticks.
    /// </summary>
    private void Detonate(SoeConnection connection, GatewaySessionState state, Detonation detonation)
    {
        ThrowableFact fact = detonation.Fact;
        Vector3 at = detonation.Position;

        if (fact.DetonatesOnContact || fact.ContactTimedActivation)
        {
            // The local physics bottle has no network actor guid. Remove it by the client's
            // projectile id before starting the fire, without refunding the consumed stack.
            byte[] retire = WeaponReplyPackets.RetireProjectile(detonation.ProjectileId);
            SendTunnel(connection, writer => writer.WriteRaw(retire));
        }

        if (fact.EffectId != 0)
        {
            byte[] effect = new PlayWorldCompositeEffect(state.Guid, fact.EffectId, at).ToArray();
            SendTunnel(connection, writer => writer.WriteRaw(effect));
            int viewers = 0;
            int far = 0;
            if (_options.Peers.Relay && state.Peer is PeerSession subject)
            {
                _peers.CollectViewers(subject, _peerViewers);
                foreach (PeerViewer viewer in _peerViewers)
                {
                    // D315: the owner's getClientsInRange(200, position) - a viewer further from
                    // the bang than SpawnRangeUnits is not told about it.
                    if (viewer.Viewer.HasPose
                        && Vector3.Distance(viewer.Viewer.Position, at)
                            > Rulings.Throwables.SpawnRangeUnits)
                    {
                        far++;
                        continue;
                    }

                    viewer.Viewer.Sink.Send(effect);
                    viewers++;
                }
            }

            _log.Info($"{connection} throwables: {fact.Name} 0f 43 PlayWorldCompositeEffect "
                + $"{fact.EffectId} at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}) - {detonation.SourceWord}; "
                + $"sent to the thrower and {viewers} viewer(s)"
                + (far == 0
                    ? string.Empty
                    : $", {far} skipped past {Rulings.Throwables.SpawnRangeUnits:0} u (D315)"));
        }

        if (fact.Lingers)
        {
            StartCloud(connection, state, detonation);
            return;
        }

        ApplyBlast(connection, state, detonation, perTick: false);
    }

    /// <summary>
    /// D308 - everything inside the radius takes what <see cref="ThrowableArm.DamageHpAt"/> says:
    /// the practice targets (through the same die-and-despawn the guns use), the other players
    /// (through <c>ApplyDamage</c>, so a kill is a kill), and the thrower himself unless D306
    /// spared him.
    /// </summary>
    private void ApplyBlast(SoeConnection connection, GatewaySessionState state, Detonation detonation, bool perTick)
    {
        ThrowableFact fact = detonation.Fact;
        if (!fact.Hurts)
        {
            return;
        }

        if (!_options.Combat.ThrowableDamage || !_options.Combat.EnableCombatDamage)
        {
            _log.Info($"{connection} throwables: {fact.Name} damage NOT applied - "
                + $"{CombatOptions.ThrowableDamageVariable}=0 or {CombatOptions.DamageVariable}=0");
            return;
        }

        Vector3 at = detonation.Position;
        long now = Environment.TickCount64;
        DamageCause cause = ThrowableArm.CauseOf(in fact);
        int hits = 0;

        foreach (PracticeTarget target in state.Combat.Targets.All)
        {
            if (!target.IsAlive)
            {
                continue;
            }

            int hp = ThrowableArm.DamageHpAt(in fact, Vector3.Distance(at, target.Position));
            if (hp <= 0)
            {
                continue;
            }

            hits++;
            bool killed = target.Damage(RetailBalance.Units(hp), now);
            _log.Info($"{connection} throwables: {fact.Name} -{hp} hp to practice target "
                + $"{target.WorldGuid} at {Vector3.Distance(at, target.Position):0.0} u -> "
                + $"{target.Health}/{target.MaxHealth}{(killed ? " - DOWN" : string.Empty)}");
            if (killed)
            {
                KillPracticeTarget(connection, state, target);
            }
        }

        // The thrower. A fallback detonation is at his own hand and the grenade is provably not
        // there (D306), so it never hurts him; a client-placed one does, exactly as a real one would.
        if (!detonation.SpareThrower && GameplayDamagePosition(state) is Vector3 self)
        {
            int hp = ThrowableArm.DamageHpAt(in fact, Vector3.Distance(at, self));
            if (hp > 0)
            {
                hits++;
                if (fact.Kind == ThrowableKind.Molotov) TouchCharacterFire(connection, state);
                ApplyDamage(connection, state, (uint)RetailBalance.Units(hp), cause);
            }
        }

        // Other players, by their own connection and health bar.
        foreach ((ulong guid, (SoeConnection victimConnection, GatewaySessionState victim)) in _throwableSessions)
        {
            if (guid == state.Guid || GameplayDamagePosition(victim) is not Vector3 where
                || victim.Hitpoints == 0 || victim.DeathSent
                || victim.BountyAdmission.MatchId != state.BountyAdmission.MatchId)
            {
                continue;
            }

            int hp = ThrowableArm.DamageHpAt(in fact, Vector3.Distance(at, where));
            if (hp <= 0)
            {
                continue;
            }

            hits++;
            if (fact.Kind == ThrowableKind.Molotov) TouchCharacterFire(victimConnection, victim);
            ApplyDamage(victimConnection, victim, (uint)RetailBalance.Units(hp), cause,
                state.Guid, state.CharacterName, state.Hitpoints);
        }

        if (hits == 0 && !perTick)
        {
            _log.Info($"{connection} throwables: {fact.Name} hurt nobody - nothing inside {fact.Radius:0.#} u");
        }
    }

    /// <summary>
    /// A cloud (gas, smoke, fire): an anchor actor at the detonation point - the spent grenade's
    /// ground model (invisible for a shattered Molotov) carrying the looping composite as an effect tag (<c>0f 15</c>), ticking
    /// <see cref="ApplyBlast"/> every <see cref="Rulings.Throwables.CloudTickMs"/> for the linger,
    /// then the tag removed (<c>0f 16</c>) and the anchor despawned (<c>0f 01</c>). Service-wide
    /// cloud IDs let nearby members of this match share the same visual and lifetime.
    /// </summary>
    private void StartCloud(SoeConnection connection, GatewaySessionState state, Detonation detonation)
    {
        ThrowableFact fact = detonation.Fact;
        Vector3 at = detonation.Position;

        // The bottle has shattered. Keep the fire's shared lifetime/interest anchor,
        // without spawning another intact bottle at the impact point.
        uint modelId = fact.Kind == ThrowableKind.Molotov ? 33u : 0u; // InvisibleTriangle.adr
        if (modelId == 0 && state.Inventory is PlayerInventory inventory
            && TryResolveGroundActor(state, inventory, fact.ItemDefinitionId, out uint groundModel, out _, out _))
        {
            modelId = groundModel;
        }

        ulong anchorGuid = 0;
        if (modelId != 0 && fact.CloudEffectId != 0)
        {
            anchorGuid = StartCloudVisual(connection, state, modelId, detonation);
            _log.Info($"{connection} throwables: {fact.Name} cloud anchor {anchorGuid} (model {modelId}) "
                + $"at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}) carrying effect tag "
                + $"{fact.CloudEffectId} for {fact.LingerSeconds} s");
        }
        else
        {
            _log.Info($"{connection} throwables: {fact.Name} cloud has no anchor (ground model {modelId}, "
                + $"effect {fact.CloudEffectId}) - it still ticks, unseen");
        }

        int tickMs = Math.Max(100, Rulings.Throwables.CloudTickMs);
        int ticks = Math.Max(1, (fact.LingerSeconds * 1000) / tickMs);
        CloudTick(connection, state, detonation, anchorGuid, ticks, tickMs, state.WorldGeneration);
    }

    private void CloudTick(
        SoeConnection connection,
        GatewaySessionState state,
        Detonation detonation,
        ulong anchorGuid,
        int ticksLeft,
        int tickMs,
        int worldGeneration)
    {
        if (worldGeneration != state.WorldGeneration)
        {
            EndCloudVisual(anchorGuid);
            return;
        }
        if (anchorGuid != 0 && !_cloudVisuals.ContainsKey(anchorGuid)) return;
        SyncCloudVisual(anchorGuid);

        ApplyBlast(connection, state, detonation, perTick: true);

        if (ticksLeft <= 1)
        {
            EndCloudVisual(anchorGuid);

            _log.Info($"{connection} throwables: {detonation.Fact.Name} cloud at "
                + $"({detonation.Position.X:0.0}, {detonation.Position.Y:0.0}, {detonation.Position.Z:0.0}) "
                + "is spent");
            return;
        }

        Later(connection, tickMs, () => CloudTick(connection, state, detonation, anchorGuid,
            ticksLeft - 1, tickMs, worldGeneration));
    }
}
