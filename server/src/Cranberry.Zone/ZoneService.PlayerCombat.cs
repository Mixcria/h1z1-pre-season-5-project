using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Contact feedback is valid in pregame too. Health loss remains governed by
    // CanTakeGameplayDamage, independently of visible/audible punch contact.
    private static bool CanReceiveMeleeContact(GatewaySessionState state) =>
        state.Match is MatchStep.Lobby or MatchStep.InMatch && !state.DeathSent && state.Hitpoints > 0;

    private void PrepareMeleeContext(GatewaySessionState shooter)
    {
        shooter.Combat.MeleeHeading = shooter.Movement.Player?.Orientation;
        shooter.Combat.MeleePlayers.Clear();
        shooter.Combat.MeleeGlass = null;
        if (!CanReceiveMeleeContact(shooter) || shooter.BountyAdmission.MatchId == 0) return;
        if (shooter.Match == MatchStep.InMatch && shooter.DestructiblesGeneration == shooter.WorldGeneration
            && shooter.Movement.Player?.Position is Vector3 origin
            && _sharedLootMembership.TryGetValue(shooter, out ulong matchId))
            shooter.Combat.MeleeGlass = Destructibles.GlassMeleeCatalog.Default.Find(origin,
                shooter.Combat.MeleeHeading, _sharedLootMatches[matchId].Destructibles,
                shooter.Movement.Player.LookPitch ?? 0);
        foreach (PeerSession peer in _peers.Sessions)
        {
            if (peer.CharacterGuid == shooter.Guid || shooter.Peer?.View.Knows(peer.Key) != true
                || peer.Sink is not SessionPeerSink sink || !sink.IsOpen
                || sink.Connection.Tag is not GatewaySessionState victim
                || victim.BountyAdmission.MatchId != shooter.BountyAdmission.MatchId
                || victim.Match != shooter.Match || !CanReceiveMeleeContact(victim)
                || victim.Movement.Player?.Position is not Vector3 position) continue;
            shooter.Combat.MeleePlayers.Add(new(victim.Guid, position));
        }
    }

    private void ApplyPeerMeleeHit(SoeConnection connection, GatewaySessionState shooter, MeleePlayerHit hit)
    {
        PeerSession? peer = _peers.Find(hit.Guid);
        if (!_options.Combat.MeleeDamage || peer?.Sink is not SessionPeerSink sink || !sink.IsOpen
            || sink.Connection.Tag is not GatewaySessionState victim || ReferenceEquals(shooter, victim)
            || !CanReceiveMeleeContact(shooter) || victim.Match != shooter.Match
            || shooter.BountyAdmission.MatchId == 0
            || shooter.BountyAdmission.MatchId != victim.BountyAdmission.MatchId
            || shooter.Peer?.View.Knows(peer.Key) != true || !CanReceiveMeleeContact(victim)
            || shooter.Movement.Player?.Position is not Vector3 origin
            || victim.Movement.Player?.Position is not Vector3 target
            || !MeleeArm.CanReach(origin, shooter.Movement.Player.Orientation, target)) return;

        uint damage = Math.Min(hit.Damage, (uint)MeleeArm.MeleeDamageFor(hit.ItemDefinitionId));
        if (damage == 0) return;
        bool killed = false;
        uint appliedDamage = 0;
        if (_options.Combat.EnableCombatDamage && CanTakeGameplayDamage(victim))
        {
            appliedDamage = Math.Min(damage, victim.Hitpoints);
            victim.LastDamageWeapon = hit.ItemDefinitionId;
            victim.LastDamageHeadshot = false;
            killed = ApplyDamage(sink.Connection, victim, damage, DamageCause.Melee,
                shooter.Guid, shooter.CharacterName, shooter.Hitpoints);
        }
        if (appliedDamage > 0) shooter.Combat.Shooter.CountHit((int)appliedDamage);
        if (!killed) SendPeerMeleeReaction(sink.Connection, victim, origin, target);
        if (shooter.Match == MatchStep.Lobby)
            // Lobby contact is cosmetic. The combined UI packet shows a red marker
            // even at zero damage; post only the native punch impact sound instead.
            SendTunnel(connection, new HitFeedbackSound(IsMelee: true).WriteTo);
        else if (_options.Combat.SendHitMarker)
            SendHitFeedback(connection, WeaponHitFeedback.For(
                new HitOutcome((int)appliedDamage, false, false, false, true, "melee"), killed, melee: true));
    }

    private void SendPeerMeleeReaction(SoeConnection victimConnection, GatewaySessionState victim,
        Vector3 attackerPosition, Vector3 victimPosition)
    {
        Vector3 towardAttacker = attackerPosition - victimPosition;
        float facing = victim.Movement.Player?.Orientation ?? 0;
        float direction = float.IsFinite(facing)
            ? MathF.IEEERemainder((facing - MathF.Atan2(towardAttacker.X, towardAttacker.Z)) * 180 / MathF.PI, 360)
            : 0;
        // Native 140c5eb30 sets FlinchDirection then dispatches Flinch through the
        // same animation slots as Character.PlayAnimation (140c63bc0).
        var reaction = new CharacterPlayAnimation(victim.Guid, "Flinch", 200, "FlinchDirection", direction);
        SendTunnel(victimConnection, reaction.WriteTo);
        if (victim.Peer is not { } subject) return;
        var viewers = new List<PeerViewer>();
        _peers.CollectViewers(subject, viewers);
        foreach (var viewer in viewers)
            if (viewer.Viewer.Sink is SessionPeerSink { IsOpen: true } observer
                && !ReferenceEquals(observer.Connection, victimConnection))
                SendTunnel(observer.Connection, reaction.WriteTo);
    }

    private int CountSharedAlive(SharedLootMatch match) => match.Members.Count(pair =>
        IsSharedAlive(pair.Key, pair.Value)) + CountCombatBots(match);

    private void PublishSharedAlive(SharedLootMatch match)
    {
        if (match.DamageBatchDepth != 0)
        {
            match.AliveCountPending = true;
            return;
        }
        match.AliveCountPending = false;
        int alive = CountSharedAlive(match);
        foreach (var (member, link) in match.Members)
        {
            if (link.State != ConnectionState.Open
                || member.Match != MatchStep.InMatch && !_pendingTeamDeaths.ContainsKey(member)) continue;
            PublishAliveCount(link, member, alive);
            if (alive == 1 && match.HadMultiplePlayers && member.BountyAdmission.Mode == MatchMode.Solo
                && !member.DeathSent)
                SendVictory(link, member);
        }
        ResolveTeamResults(match);
    }

    private void PublishSharedPlayerDeath(GatewaySessionState victim, ulong killerGuid)
    {
        PublishTeamRoster(victim);
        if (victim.Peer is PeerSession peer)
        {
            var viewers = new List<PeerViewer>();
            _peers.CollectViewers(peer, viewers);
            foreach (var viewer in viewers)
            {
                if (viewer.Viewer.Sink is not SessionPeerSink sink) continue;
                SendTunnel(sink.Connection, w => StartMultiStateDeath.RagdollFor(victim.Guid, viewer.Viewer.CharacterGuid).WriteTo(w));
                // ce/0e is the retail feed. 0f/48 is retained only on the victim's death-screen link.
            }
        }
        if (_sharedLootMembership.TryGetValue(victim, out ulong id)) PublishSharedAlive(_sharedLootMatches[id]);
    }

    // The fire arm has already verified and consumed the projectile hint. This bridge owns
    // live-session authority; it must never re-consume the hint or fall through to a vehicle
    // when a known player is an ineligible target.
    private bool TryDamagePeerWithBullet(SoeConnection connection, GatewaySessionState shooter, WeaponArmResult shot)
    {
        PeerSession? peer = _peers.Find(shot.TargetGuid);
        if (peer?.Sink is not SessionPeerSink sink || sink.Connection.Tag is not GatewaySessionState victim)
            return false;
        if (shot.UnresolvedHit is not ProjectileHitReport report
            || ReferenceEquals(shooter, victim)
            || shooter.Match != MatchStep.InMatch || victim.Match != MatchStep.InMatch
            || shooter.DeathSent || victim.DeathSent || shooter.Hitpoints == 0 || victim.Hitpoints == 0
            || shooter.BountyAdmission.MatchId == 0
            || shooter.BountyAdmission.MatchId != victim.BountyAdmission.MatchId
            || shooter.Peer?.View.Knows(peer.Key) != true
            || !sink.IsOpen || victim.DevConsole.Invulnerable
            || shooter.Movement.Player?.Position is not Vector3 origin
            || victim.Movement.Player?.Position is not Vector3 target)
            return true;

        double distance = Vector3.Distance(origin, target);
        if (!double.IsFinite(distance) || distance > _options.Combat.MaxHitDistance) return true;

        bool head = HitRule.IsHead(report.HitLocation);
        InventoryItemInstance? protection = victim.Inventory?.EquipmentSlots.Values.FirstOrDefault(item =>
            head ? ArmourModel.IsHelmet(item.DefinitionId) : ArmourModel.TierOf(item.DefinitionId) != ArmourTier.None);
        Armour armour = protection is null ? default : victim.PlayerArmour.GetValueOrDefault(protection.Guid);
        Armour beforeHit = armour;
        bool helmetHit = head && beforeHit.WearingHelmet(protection?.DefinitionId ?? 0);
        HitOutcome outcome = HitRule.Resolve(ref armour,
            !head && protection is not null ? protection.DefinitionId : 0,
            head && protection is not null ? protection.DefinitionId : 0,
            RetailBalance.WeaponDefinitionIdFor(shot.AcceptedFire.ItemDefinitionId), report.HitLocation,
            distance, _options.Combat.UnmappedWeaponBodyUnits);
        shooter.Combat.Shooter.CountHit(outcome.DamageUnits);

        if (_options.Combat.EnableCombatDamage)
        {
            var victimPosition = victim.Movement.Player!.Position!.Value;
            var attackerPosition = shooter.Movement.Player!.Position!.Value;
            float bearing = MathF.Atan2(attackerPosition.Z - victimPosition.Z, attackerPosition.X - victimPosition.X);
            SendTunnel(sink.Connection, new IncomingDamage((uint)Math.Max(1, outcome.DamageUnits), bearing,
                (float)outcome.DamageUnits / _options.Gas.MaxHitpoints,
                helmetHit || outcome.DamagedArmour ? 1f : 0f, head).WriteTo);
            if (protection is not null)
            {
                victim.PlayerArmour[protection.Guid] = armour;
                if (outcome.BrokeArmour && victim.Inventory is PlayerInventory inventory)
                {
                    inventory.RemoveUnits(protection.Guid, 0);
                    SendTunnel(sink.Connection, writer => new ItemDelete(victim.Guid, protection.Guid).WriteTo(writer));
                    SendLoadoutSlots(sink.Connection, inventory);
                    SendCharacterAppearance(sink.Connection, victim, "bullet broke protection");
                }
            }
            if (outcome.DamageUnits > 0)
            {
                victim.LastDamageWeapon = shot.AcceptedFire.KillFeedItemDefinitionId;
                victim.LastDamageHeadshot = head;
                ApplyDamage(sink.Connection, victim, (uint)outcome.DamageUnits, DamageCause.Bullet,
                    shooter.Guid, shooter.CharacterName, shooter.Hitpoints);
            }
            if (outcome.Bleeds || helmetHit && outcome.BrokeArmour)
                WoundPlayer(sink.Connection, victim, shooter, shot.AcceptedFire.KillFeedItemDefinitionId);
        }
        if (_options.Combat.SendHitMarker)
            SendHitFeedback(connection, WeaponHitFeedback.For(in outcome, victim.Hitpoints == 0,
                armourHit: helmetHit || outcome.DamagedArmour));
        return true;
    }
}
