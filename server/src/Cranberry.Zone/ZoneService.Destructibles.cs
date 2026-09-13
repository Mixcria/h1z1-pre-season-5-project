using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Destructibles;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    // Animation identity is from August ClientEffects.txt, ability 1111157.
    // These wind-ups are server presentation policy, not recovered retail hit times.
    // PARAM2 is an animation recovery window (1411b7ea0), not a damage event.
    private static int PunchWindupMs(uint clientEffect, uint serverEffect) => (clientEffect, serverEffect) switch
    {
        (2, 110755) => 200,       // LeftJab
        (3, 110756) => 400,       // RightStraight
        (4, 110757) => 450,       // LeftHook
        (5, 110758) => 400,       // RightHook
        (90124, 110759) => 350,   // RightUppercut
        _ => 0
    };
    private const int PunchPacketPairWindowMs = 250;
    private sealed class PunchAnimation(uint effectId, int worldGeneration, ulong weaponGuid, long startedMs, int windupMs)
    {
        public uint EffectId { get; } = effectId;
        public int WorldGeneration { get; } = worldGeneration;
        public ulong WeaponGuid { get; } = weaponGuid;
        public long StartedMs { get; } = startedMs;
        public int WindupMs { get; } = windupMs;
        public bool Claimed { get; set; }
    }
    private sealed class PendingMeleeGlass(uint objectId, DestructibleWorld world, int generation,
        ulong weaponGuid, long requestedMs)
    {
        public uint ObjectId { get; } = objectId;
        public DestructibleWorld World { get; } = world;
        public int WorldGeneration { get; } = generation;
        public ulong WeaponGuid { get; } = weaponGuid;
        public long RequestedMs { get; } = requestedMs;
        public long? DueMs { get; set; }
    }

    private static bool UsesPunchAnimation(uint item) => item is 0 or 85 or 113 or 1380;

    private void ScheduleMeleeGlassHit(SoeConnection connection, GatewaySessionState state, uint objectId)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)) return;
        long now = Environment.TickCount64;
        // At most one contact can be pending. Trigger repeats must not replace a swing
        // whose animation is still winding up.
        if (state.PendingGlassSwing is { } prior && prior.WorldGeneration == state.WorldGeneration
            && now <= (prior.DueMs ?? prior.RequestedMs + PunchPacketPairWindowMs)) return;
        var pending = new PendingMeleeGlass(objectId, _sharedLootMatches[matchId].Destructibles,
            state.WorldGeneration, HeldItemGuid(state), now);
        state.PendingGlassSwing = pending;
        if (!UsesPunchAnimation(HeldItemDefinitionId(state)))
        {
            pending.DueMs = now + 200; // Existing non-fist melee gets a short contact wind-up.
            ScheduleGlassContact(connection, state, pending, 200);
        }
        else if (state.LastPunchAnimation is { Claimed: false } animation
            && animation.WorldGeneration == state.WorldGeneration && animation.WeaponGuid == pending.WeaponGuid
            && now - animation.StartedMs <= PunchPacketPairWindowMs)
            BindGlassAnimation(connection, state, pending, animation);
        else if (!Later(connection, PunchPacketPairWindowMs + 1, () =>
        {
            if (ReferenceEquals(state.PendingGlassSwing, pending) && pending.DueMs is null)
                state.PendingGlassSwing = null;
        })) state.PendingGlassSwing = null;
    }

    private bool HandlePunchAnimation(SoeConnection connection, GatewaySessionState state, EffectRequest request)
    {
        int windup = PunchWindupMs(request.Head.EffectId1, request.Head.EffectId2);
        if (windup == 0) return false;
        if (!request.IsAdd || request.Head.Dword1 != 1 || request.SourceCharacterId != state.Guid
            || request.TargetCharacterId != state.Guid || !UsesPunchAnimation(HeldItemDefinitionId(state))
            || state.Match != MatchStep.InMatch || state.DeathSent || state.Hitpoints == 0) return true;
        long now = Environment.TickCount64;
        if (state.LastPunchAnimation is { } previous && previous.WorldGeneration == state.WorldGeneration
            && previous.WeaponGuid == HeldItemGuid(state) && previous.EffectId == request.Head.EffectId1
            && now - previous.StartedMs < MinimumPunchAnimationIntervalMs)
            return true; // Replayed animation reports cannot restart or shorten a pending contact.
        var animation = new PunchAnimation(request.Head.EffectId1, state.WorldGeneration, HeldItemGuid(state), now, windup);
        state.LastPunchAnimation = animation;
        if (state.PendingGlassSwing is { DueMs: null } pending && pending.WorldGeneration == state.WorldGeneration
            && pending.WeaponGuid == animation.WeaponGuid && now - pending.RequestedMs <= PunchPacketPairWindowMs)
            BindGlassAnimation(connection, state, pending, animation);
        return true;
    }

    private const int MinimumPunchAnimationIntervalMs = MeleeArm.MinimumSwingIntervalMs;

    private void BindGlassAnimation(SoeConnection connection, GatewaySessionState state,
        PendingMeleeGlass pending, PunchAnimation animation)
    {
        animation.Claimed = true;
        pending.DueMs = animation.StartedMs + animation.WindupMs;
        ScheduleGlassContact(connection, state, pending, (int)Math.Max(1, pending.DueMs.Value - Environment.TickCount64));
    }

    private void ScheduleGlassContact(SoeConnection connection, GatewaySessionState state, PendingMeleeGlass pending, int delay)
    {
        if (!Later(connection, delay, () => CompleteMeleeGlassHit(connection, state, pending, Environment.TickCount64)))
            state.PendingGlassSwing = null;
    }

    private void CompleteMeleeGlassHit(SoeConnection connection, GatewaySessionState state, PendingMeleeGlass pending, long nowMs)
    {
        if (!ReferenceEquals(state.PendingGlassSwing, pending) || pending.DueMs is not long due) return;
        if (nowMs < due)
        {
            ScheduleGlassContact(connection, state, pending, (int)(due - nowMs));
            return;
        }
        state.PendingGlassSwing = null;
        if (connection.State != ConnectionState.Open || state.WorldGeneration != pending.WorldGeneration
            || HeldItemGuid(state) != pending.WeaponGuid || !MeleeArm.IsMeleeItem(HeldItemDefinitionId(state))
            || !_sharedLootMembership.TryGetValue(state, out ulong id)
            || !ReferenceEquals(_sharedLootMatches[id].Destructibles, pending.World)
            || state.Movement.Player?.Position is not System.Numerics.Vector3 position
            || GlassMeleeCatalog.Default.Find(position, state.Movement.Player.Orientation, pending.World,
                state.Movement.Player.LookPitch ?? 0)?.ObjectId != pending.ObjectId)
            return;
        ApplyMeleeGlassHit(connection, state, pending.ObjectId);
    }

    private void ApplyMeleeGlassHit(SoeConnection connection, GatewaySessionState state, uint objectId)
    {
        if (!_options.Combat.EnableCombatDamage || !_options.Combat.MeleeDamage
            || state.Match != MatchStep.InMatch || state.DeathSent || state.Hitpoints == 0
            || state.DestructiblesGeneration != state.WorldGeneration
            || !_sharedLootMembership.TryGetValue(state, out ulong id)) return;
        var match = _sharedLootMatches[id];
        if (match.Destructibles.BreakGlass(DestructibleCatalog.Default, objectId) is not { } damage) return;
        foreach (var (viewer, link) in match.Members)
            if (link.State == ConnectionState.Open && viewer.DestructiblesGeneration == viewer.WorldGeneration)
                SendTunnel(link, writer => DestructiblePackets.WriteDestroyed(writer, damage.Prop));
        _log.Info($"{connection} destructibles: melee broke glass object={objectId} model={damage.Prop.Model}");
    }

    private void InitializeDestructibles(SoeConnection connection, GatewaySessionState state)
    {
        if (state.DestructiblesGeneration == state.WorldGeneration
            || state.Match is not (MatchStep.Zoning or MatchStep.Lobby or MatchStep.Dropping or MatchStep.InMatch)
            || !string.Equals(_options.MatchZoneName, "Z2", StringComparison.OrdinalIgnoreCase)
            || !_sharedLootMembership.TryGetValue(state, out ulong id)) return;
        var match = _sharedLootMatches[id];
        DestructibleCatalog catalog;
        DestructibleCatalog vehicleProps;
        try { catalog = DestructibleCatalog.Default; vehicleProps = VehicleFencePolicy.Catalog; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _log.Info($"{connection} destructibles unavailable: {ex.Message}");
            return;
        }
        SendTunnel(connection, VehicleFencePolicy.WriteModels);
        // Initial packets replace the name-hash list but add individual overrides. Repeat the
        // same names while bounding a late joiner's snapshots to 256 replacements each.
        var destroyed = match.Destructibles.Destroyed;
        if (destroyed.Count == 0)
            SendTunnel(connection, writer => DestructiblePackets.WriteInitial(writer, catalog, []));
        else
            foreach (var batch in destroyed.Chunk(256))
                SendTunnel(connection, writer => DestructiblePackets.WriteInitial(writer, catalog, batch));
        // Models handle future stream-ins; BA/05 also flags actors already loaded by this point.
        foreach (uint[] ids in vehicleProps.Props.Keys.Chunk(512))
            SendTunnel(connection, writer => VehicleFencePolicy.WriteLoadedInstances(writer, ids));
        state.DestructiblesGeneration = state.WorldGeneration;
        _log.Info($"{connection} destructibles: enabled {catalog.Models.Count} shooting families / {catalog.Props.Count} Z2 objects; vehicle impacts {VehicleFencePolicy.ModelIds.Count} families / {vehicleProps.Props.Count} objects; restored {match.Destructibles.Destroyed.Count} broken objects");
    }

    private void HandleDestructibleHit(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (!DestructiblePackets.TryReadHit(payload, out var report))
        {
            _log.Info($"{connection} destructibles: unrecognized report {Convert.ToHexString(payload)}");
            return;
        }
        if (state.Match != MatchStep.InMatch || state.DeathSent || state.Hitpoints == 0
            || state.DestructiblesGeneration != state.WorldGeneration
            || state.Movement.Player?.Position is not System.Numerics.Vector3 position
            || !_sharedLootMembership.TryGetValue(state, out ulong id)) return;
        var match = _sharedLootMatches[id];
        var outcome = report.SourceGuid != 0 && state.Fleet?.TryGet(report.SourceGuid, out _) == true
            ? match.Destructibles.HitByVehicle(VehicleFencePolicy.Catalog, report, state.Fleet, state.Guid, Environment.TickCount64)
            : match.Destructibles.Hit(DestructibleCatalog.Default, report,
                state.Combat.Shooter, _options.Combat, position, Environment.TickCount64);
        if (outcome is not { } damage)
        {
            _log.Info($"{connection} destructibles: refused object={report.ObjectId} model={report.Model} projectile={report.ProjectileId}");
            return;
        }
        _log.Info($"{connection} destructibles: object={report.ObjectId} model={report.Model} projectile={report.ProjectileId} damage={damage.Damage} health={damage.RemainingHealth}/{damage.Prop.Health}");
        if (!damage.Destroyed) return;
        foreach (var (viewer, link) in match.Members)
        {
            if (link.State != ConnectionState.Open || viewer.DestructiblesGeneration != viewer.WorldGeneration) continue;
            SendTunnel(link, writer => DestructiblePackets.WriteDestroyed(writer, damage.Prop));
        }
        // HitByVehicle has already checked authority, proximity, pose freshness and the shared
        // object's intact state. A replay cannot reach this charge, even through another viewer.
        if (report.ProjectileId == 0 && state.Fleet?.TryGet(report.SourceGuid, out var vehicle) == true)
            ApplyVehicleDamage(connection, state, vehicle,
                VehicleCombatBalance.DestructibleImpactDamage(state.Fleet.Options.MaxHealth),
                "BA 01 breakable impact");
        if (damage.Prop.IsExplosive)
            ExplodeWorldObject(connection, state, damage.Prop.Position, playEffect: false);
    }
}
