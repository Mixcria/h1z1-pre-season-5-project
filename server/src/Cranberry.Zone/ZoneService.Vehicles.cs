using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Match;
using Cranberry.Zone.World;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

/// <summary>
/// <b>The vehicle lane's orchestration — damage, boost and the seat change</b> (docs/117,
/// <c>out\overhaul-20260901\AUDIT-vehicles.md</c>).
///
/// <para>
/// It sits in its own partial for the same reason lane 1D-lite's does: <c>ZoneService.cs</c> gains
/// only its call sites, so a lane working on doors or weapons in the same tree never has to merge
/// against this one. The writers it uses all shipped tested in <c>Vehicles/</c>; this file is the
/// half that calls them.
/// </para>
/// <para>
/// <b>What was true before it.</b> <c>VehicleFleet.ApplyDamage</c> had <i>zero callers</i> in the
/// whole source tree — a car in Cranberry could not be hurt by a bullet, a crash or a flip — the
/// combat hit path had no vehicle branch at all, <c>8e 01</c> was logged and dropped, no boost byte
/// existed anywhere, and <c>70 0a</c> was unhandled. Every one of those is closed here, each behind
/// its own switch.
/// </para>
/// </summary>
public sealed partial class ZoneService
{
    /// <summary>
    /// <b>The one place a vehicle's condition moves.</b> Applies the damage, then sends what the
    /// client needs to see it:
    ///
    /// <list type="number">
    /// <item><c>8d ResourceEvent</c> type 3 on the vehicle's own guid with resource
    /// <c>561</c> / type <c>1</c> — the client's own condition row
    /// (<c>Resources.txt</c>, <c>MAX_VALUE</c> 100,000).</item>
    /// <item><c>88 1e Vehicle.HealthUpdateOwner</c> to the driver. This writer has existed and been
    /// tested since wave 5 and <b>nothing had ever sent it</b>, so even once damage existed the
    /// driver could not have seen it (AUDIT-vehicles gap 8).</item>
    /// <item><c>09 1d Command.PlayDialogEffect</c> with the crossed-into
    /// <c>VEH_Damage_&lt;family&gt;_Stage0n</c> composite effect — the smoke, and at stage 4 the
    /// fire. Only on a crossing, never on every hit.</item>
    /// </list>
    ///
    /// <para>
    /// At zero, occupants are cleared and the August death composite plays once. The blast
    /// damages nearby players in this match through normal death handling. Its radius and damage
    /// are explicit server tuning; the burnt model remains for the owner's seven-second lifetime.
    /// </para>
    /// </summary>
    private VehicleDamageOutcome? ApplyVehicleDamage(
        SoeConnection connection,
        GatewaySessionState state,
        MatchVehicle vehicle,
        uint amount,
        string source,
        bool collision = false,
        GatewaySessionState? attacker = null)
    {
        if (state.Fleet is not VehicleFleet fleet || !_options.VehicleDamage.Enabled || amount == 0)
        {
            return null;
        }

        VehicleDamageOutcome outcome = fleet.Damage(vehicle, amount, collision);
        if (!outcome.Moved && outcome.StageEffectId == 0)
        {
            return outcome;
        }

        SendVehicleCondition(connection, state, outcome);

        _log.Info($"{connection} vehicles: {source} −{outcome.Charged} on {vehicle} — condition "
            + $"{outcome.Before} → {outcome.After}/{_options.VehicleFleet.MaxHealth} "
            + $"({outcome.Condition}, stage {vehicle.DamageStage}"
            + (outcome.StageEffectId == 0 ? string.Empty : $", effect {outcome.StageEffectId}")
            + ")");

        if (outcome.Destroyed)
        {
            WreckVehicle(connection, state, outcome, attacker);
        }

        return outcome;
    }

    /// <summary>The three s2c rows a condition change owes, in the order the client wants them.</summary>
    private void SendVehicleCondition(
        SoeConnection connection, GatewaySessionState state, in VehicleDamageOutcome outcome)
    {
        MatchVehicle vehicle = outcome.Vehicle;
        uint before = outcome.Before;
        uint after = outcome.After;

        KeyValuePair<GatewaySessionState, SoeConnection>[] viewers = _sharedLootMembership.TryGetValue(state, out ulong matchId)
            ? _sharedLootMatches[matchId].Members.ToArray() : [new(state, connection)];
        foreach (var (viewer, link) in viewers)
        {
            bool evicted = outcome.EvictedOccupants.Contains(viewer.Guid);
            if (link.State != ConnectionState.Open || (viewer != state && !evicted
                && viewer.Guid != vehicle.DriverGuid && !viewer.StreamedVehicles.IsSpawned(vehicle.Guid))) continue;
            SendTunnel(link, writer => new CharacterResourceUpdate(
                vehicle.Guid, AugustVehicleDamageFacts.ConditionResourceId,
                AugustVehicleDamageFacts.ConditionResourceType, after, before).WriteTo(writer));

            // 88/1e names the receiving character. Include the final zero after fleet eviction.
            if (vehicle.DriverGuid == viewer.Guid || evicted)
                SendTunnel(link, new VehicleHealthUpdateOwner(viewer.Guid, after).WriteTo);
            if (outcome.StageEffectId != 0)
            {
                uint effect = outcome.StageEffectId;
                SendTunnel(link, writer => new PlayDialogEffect(vehicle.Guid, effect).WriteTo(writer));
            }
        }
    }

    /// <summary>
    /// A car reached zero: everyone in it comes out, and each of them gets the clearing burst that
    /// a normal dismount sends. The burnt model is retired by the shared fleet's world pump.
    /// </summary>
    private void WreckVehicle(
        SoeConnection connection, GatewaySessionState state, in VehicleDamageOutcome outcome,
        GatewaySessionState? attacker = null)
    {
        ExplodeVehicle(connection, state, outcome, attacker);
    }

    /// <summary>
    /// The burst that takes a rider out of a car, shared by a normal dismount and a wreck. Unlike
    /// the parachute there is <b>no <c>0f 01 RemovePlayer</c></b> — the car stays in the world.
    /// </summary>
    private void SendVehicleClearingBurst(
        SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        StopDriverEngineRuntime(connection, state, vehicle);
        SendTunnel(connection, writer => new DismountResponse(state.Guid, vehicle.Guid).WriteTo(writer));
        SendTunnel(connection, writer => new VehicleOccupyCleared(state.Guid).WriteTo(writer));
        SendTunnel(connection, writer =>
            new VehicleOwner(vehicle.Guid, OwnerGuid: 0, VehicleId: 0).WriteTo(writer));
        SendTunnel(connection, writer =>
            new ManagedObjectResponseControl(Control: false, ObjectGuid: vehicle.Guid).WriteTo(writer));
        SendTunnel(connection, writer =>
            CharacterManagedObject.Release(vehicle.Guid).WriteTo(writer));
        SendTunnel(connection, VehicleManagedLocation.At(vehicle).WriteTo);
        SendTunnel(connection, VehiclePoseRelay.Parked(vehicle).WriteTo);
        PublishTeamHudStatus(state); // Clear driving immediately on dismount or wreck, without a foot packet.
    }

    /// <summary>
    /// <b>The bullet branch the hit path never had</b> (AUDIT-vehicles gap 3). A
    /// <c>82 06 ProjectileHitReport</c> names its target by guid; before this lane the arbitration
    /// resolved that guid against the practice targets and the player set only, so a guid that
    /// named a car fell through with "no damage model for it yet" and shooting a vehicle did
    /// nothing at all.
    ///
    /// <para>
    /// The vehicle is resolved <b>after</b> the practice targets and the players, so nothing that
    /// already worked can be shadowed by a car; guid ranges do not overlap in any case
    /// (<c>VehicleWorldGuidBase</c>), and the ordering is defence against a future one that does.
    /// </para>
    /// <para>
    /// VehicleCombatBalance applies the owner's one-point AR-15 reference on a 100,000-unit
    /// condition bar. Other weapons retain their previous baseline pending period-specific
    /// evidence. Accepted fire identity, range and the reported impact are checked here.
    /// </para>
    /// </summary>
    private bool TryDamageVehicleWithBullet(
        SoeConnection connection, GatewaySessionState state, WeaponArmResult shot)
    {
        if (!_options.VehicleDamage.Enabled
            || !_options.VehicleDamage.Bullets
            || state.Fleet is not VehicleFleet fleet
            || !fleet.TryGet(shot.TargetGuid, out MatchVehicle? vehicle))
        {
            return false;
        }

        if (vehicle.Health == 0)
        {
            _log.Info($"{connection} vehicles: bullet on {vehicle} — already a wreck");
            return true;
        }

        if (!_options.Combat.EnableCombatDamage || state.DeathSent || state.Hitpoints == 0
            || state.Match != MatchStep.InMatch || shot.UnresolvedHit is not { } hit)
            return true;
        float distance = Vector3.Distance(new(shot.AcceptedFire.X, shot.AcceptedFire.Y, shot.AcceptedFire.Z), vehicle.Position);
        float impactDistance = Vector3.Distance(new(hit.X, hit.Y, hit.Z), vehicle.Position);
        if (!float.IsFinite(distance) || distance > _options.Combat.MaxHitDistance
            || !float.IsFinite(impactDistance) || impactDistance > 10f) return true;
        uint amount = VehicleCombatBalance.BulletDamage(
            RetailBalance.WeaponDefinitionIdFor(shot.AcceptedFire.ItemDefinitionId), shot.TargetDamageUnits);
        var outcome = ApplyVehicleDamage(connection, state, vehicle, amount, "82 06 bullet", attacker: state);
        if (_options.Combat.SendHitMarker && outcome is { Charged: > 0 })
            SendHitFeedback(connection, new WeaponHitFeedback(outcome.Value.Charged, IsVehicle: true));
        return true;
    }

    /// <summary>
    /// Records the attitude of a car from the pose its owner just streamed, and logs a flip the
    /// moment it happens. The pulse itself is charged by <see cref="PumpVehicleDamage"/> on the
    /// world pump, not here — a 20 Hz stream must not be a 20 Hz damage source.
    /// </summary>
    private void NoteVehicleAttitude(
        SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, Quaternion rotation)
    {
        if (!_options.VehicleDamage.Enabled
            || !_options.VehicleDamage.Flip
            || state.Fleet is not VehicleFleet fleet)
        {
            return;
        }

        if (!fleet.NoteAttitude(vehicle, rotation, _options.VehicleDamage.UpsideDownDotThreshold))
        {
            return;
        }

        _log.Info($"{connection} vehicles: {vehicle} is now "
            + $"{(vehicle.UpsideDown ? "UPSIDE DOWN" : "back on its wheels")} "
            + $"(up·world-up {VehicleFlipDetector.UpDot(rotation):0.00} against "
            + $"{_options.VehicleDamage.UpsideDownDotThreshold:0.00})"
            + (vehicle.UpsideDown
                ? $", pulsing {AugustVehicleDamageFacts.UpsideDownDamagePulse(vehicle.Definition.VehicleId)} "
                    + $"every {_options.VehicleDamage.FlipPulseIntervalMs} ms"
                : string.Empty));
    }

    /// <summary>
    /// The world pump's vehicle-damage arm: one <c>UPSIDE_DOWN_DAMAGE_PULSE</c> for every car on
    /// its roof that is due one, and the extra fuel a held boost costs.
    /// </summary>
    private void PumpVehicleDamage(
        SoeConnection connection, GatewaySessionState state, long nowMs, float billedSeconds)
    {
        if (state.Fleet is not VehicleFleet fleet)
        {
            return;
        }

        ReapVehicleWrecks(connection, state, fleet, nowMs);

        foreach (VehicleDamageOutcome outcome in fleet.PulseUpsideDown(nowMs, _options.VehicleDamage))
        {
            SendVehicleCondition(connection, state, outcome);
            _log.Info($"{connection} vehicles: UPSIDE_DOWN_DAMAGE_PULSE −{outcome.Charged} on "
                + $"{outcome.Vehicle} — condition {outcome.Before} → {outcome.After} "
                + $"({outcome.Condition})");
            if (outcome.Destroyed)
            {
                WreckVehicle(connection, state, outcome);
            }
        }

        // A held boost spends the fuel tank faster. The pump has already billed everyone the
        // cruising rate, so this is only the DIFFERENCE, and it is charged to the boosting cars
        // alone. The client's own AbilityEx rows say the tank is what a boost spends
        // (RESOURCE_TYPE 50 = ResourceTypeFuel); how fast is Cranberry's ruling.
        float extraPerSecond =
            _options.VehicleFuel.BurnPerSecond * (_options.VehicleBoost.FuelMultiplier - 1f);
        if (!_options.VehicleFuel.Enabled || billedSeconds <= 0f || extraPerSecond <= 0f)
        {
            return;
        }

        float extra = extraPerSecond * billedSeconds;
        foreach (MatchVehicle vehicle in fleet.Vehicles)
        {
            if (!state.Boost.IsBoosting(vehicle.Guid) || vehicle.Fuel <= 0f)
            {
                continue;
            }

            vehicle.Fuel = MathF.Max(0f, vehicle.Fuel - extra);
            if (vehicle.Fuel > 0f)
            {
                continue;
            }

            // The tank ran dry under the boost. Take the boost off before the engine, so the client
            // is never left holding a resident turbo effect on a car that has stopped.
            ReleaseBoost(connection, state, vehicle, "the tank ran dry");
        }
    }

    // ------------------------------------------------------------------------------ the boost

    /// <summary>
    /// <b><c>0x9e</c> Effects — the boost press and release</b> (AUDIT-vehicles gap 6, docs/117 §3).
    ///
    /// <para>
    /// The whole path, and it must be the whole path. The client's own <c>ClientEffects</c> rows for
    /// all four turbo ids carry <c>EXPIRE_MSEC = 0</c> and <c>FLAG_CAN_STACK = 0</c>, so the effect
    /// can never age out and may not be added twice — only the server's own
    /// <c>Effect.RemoveEffect</c> frees the client's slot. Answering the press and not the release
    /// is <i>worse</i> than answering neither: press #1 works and every later press is refused by
    /// the client itself with <c>failed to add busy effectId=90000</c>.
    /// </para>
    /// </summary>
    private void HandleEffects(
        SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload, string hex)
    {
        if (!EffectRequest.TryParse(payload, out EffectRequest? parsed)
            || parsed is null)
        {
            _log.Info($"{connection} zone 0x9e effects {payload.Length} bytes: {hex} "
                + $"({(_options.VehicleBoost.Enabled ? "unreadable" : VehicleBoostOptions.EnabledVariable + "=0")}"
                + ", unanswered)");
            return;
        }

        EffectRequest request = parsed;
        if (HandlePunchAnimation(connection, state, request)) return;
        uint effectId = request.Head.EffectId1;

        if (AugustVehicleBoostFacts.IsMotorRunClientEffect(effectId))
        {
            // A server-forced stop can produce this after dismount/seat loss. Free the
            // sender's local effect even though that sender can no longer control the car.
            if (request.IsRemove && request.SourceCharacterId == state.Guid)
                SendTunnel(connection, writer => writer.WriteRaw(request.RemoveEcho()));
            if (state.Fleet?.TryGet(request.TargetCharacterId, out var motorVehicle) == true
                && motorVehicle.DriverGuid == state.Guid && motorVehicle.OwnerGuid == state.Guid
                && motorVehicle.Health > 0 && request.SourceCharacterId == state.Guid
                && (request.IsAdd || request.IsRemove))
            {
                SetDriverEngine(connection, state, motorVehicle, request.IsAdd, clientRuntime: true);
                if (request.IsAdd && !motorVehicle.EngineOn)
                    SendTunnel(connection, writer => writer.WriteRaw(request.RemoveEcho()));
            }
            else if (request.IsAdd && request.SourceCharacterId == state.Guid)
            {
                // A queued client-run activation can arrive after dismount/seat loss.
                // It has already created a local effect: ignoring it leaves that loop alive.
                // Remove only this sender's effect; never stop a subsequent driver's car.
                SendTunnel(connection, writer => writer.WriteRaw(request.RemoveEcho()));
                if (state.Fleet?.TryGet(request.TargetCharacterId, out var abandoned) == true
                    && abandoned.DriverGuid == 0 && abandoned.CoastingOwnerGuid == state.Guid)
                    StopVehicleEngineForViewers(connection, state, abandoned);
            }
            return;
        }

        if (!AugustVehicleBoostFacts.IsTurboClientEffect(effectId))
        {
            _log.Info($"{connection} zone 0x9e effects sub=0x{request.Sub:x2} effect {effectId}/"
                + $"{request.Head.EffectId2} ({payload.Length} bytes) — not a vehicle effect: {hex}");
            return;
        }

        if (!_options.VehicleBoost.Enabled) return;

        if (state.Fleet is not VehicleFleet fleet
            || !fleet.TryGet(request.TargetCharacterId, out MatchVehicle? vehicle))
        {
            _log.Info($"{connection} vehicles: boost names 0x{request.TargetCharacterId:x16}, which "
                + "is not a car this session holds — ignored");
            return;
        }

        if (request.IsRemove)
        {
            // The echo goes out even when this server did not think the boost was on: the client's
            // slot is the client's, and refusing to free it is the one failure mode that cannot be
            // recovered from without a reconnect.
            byte[] echo = request.RemoveEcho();
            SendTunnel(connection, writer => writer.WriteRaw(echo));
            bool wasOn = state.Boost.Release(vehicle.Guid);
            SendBoostTags(connection, state, vehicle, on: false);
            _log.Info($"{connection} vehicles: BOOST RELEASED on {vehicle} — 9e 03 echoed "
                + $"({echo.Length} B){(wasOn ? string.Empty : " (the server did not think it was on)")}; "
                + $"{state.Boost}");
            return;
        }

        if (!request.IsAdd)
        {
            _log.Info($"{connection} zone 0x9e effects sub=0x{request.Sub:x2} turbo {effectId} "
                + $"({payload.Length} bytes) — no handler: {hex}");
            return;
        }

        // Validate the installed components and driver before granting the effect.
        string? refusal =
            vehicle.DriverGuid != state.Guid ? "passengers do not drive"
            : vehicle.Health == 0 ? "the car is a wreck"
            : !vehicle.Inventory.HasTurbo ? "turbo is missing"
            : !vehicle.Inventory.HasEngineParts ? "engine components are missing"
            : _options.VehicleFuel.Enabled && vehicle.Fuel <= 0f ? "the tank is empty"
            : null;

        if (refusal is not null)
        {
            state.Boost.Refused();
            SendTunnel(connection, writer => new VehicleActivateBoostFailed(vehicle.Guid).WriteTo(writer));
            _log.Info($"{connection} vehicles: boost REFUSED on {vehicle} — {refusal} "
                + $"(88 2b VehicleActivateBoostFailed, {VehicleActivateBoostFailed.Length} B)");
            return;
        }

        bool first = state.Boost.Press(vehicle.Guid);
        SendTunnel(connection, writer => new CharacterTurbo(
            state.Guid, _options.VehicleBoost.TurboOnValue).WriteTo(writer));
        SendBoostTags(connection, state, vehicle, on: true);
        _log.Info($"{connection} vehicles: BOOST ON in {vehicle} — client effect {effectId}, "
            + $"0f 33 Character.Turbo value {_options.VehicleBoost.TurboOnValue} "
            + $"(polarity INFERRED, {VehicleBoostOptions.TurboByteVariable} flips it), "
            + $"fuel {vehicle.Fuel:F0} burning at x{_options.VehicleBoost.FuelMultiplier:0.#}"
            + $"{(first ? string.Empty : " (already boosting)")}; {state.Boost}");
    }

    /// <summary>
    /// The composite tag other players' clients draw, and the <c>0f 33</c> that comes off with it.
    /// <c>VEH_Engine_Boost_&lt;family&gt;</c> — 5016 / 319 / 279 / 354 — is the client's own
    /// <c>ACTIVE_COMP_EFFECT_ID</c> for the turbo row, and all four are in the August catalogue.
    /// </summary>
    private void SendBoostTags(
        SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, bool on)
    {
        uint tag = AugustVehicleBoostFacts.TurboCompositeEffect(vehicle.Definition.VehicleId);
        if (tag == 0)
        {
            return;
        }

        ulong guid = vehicle.Guid;
        if (on)
        {
            SendTunnel(connection, writer => new AddEffectTagCompositeEffect(guid, tag).WriteTo(writer));
            return;
        }

        SendTunnel(connection, writer => new RemoveEffectTagCompositeEffect(guid, tag).WriteTo(writer));
        SendTunnel(connection, writer => new CharacterTurbo(
            state.Guid, _options.VehicleBoost.TurboOffValue).WriteTo(writer));
    }

    /// <summary>Takes a boost off without a client packet having asked — a dry tank, a wreck, a dismount.</summary>
    private void ReleaseBoost(
        SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle, string why)
    {
        if (!state.Boost.Release(vehicle.Guid))
        {
            return;
        }

        SendBoostTags(connection, state, vehicle, on: false);
        SendTunnel(connection, writer => new VehicleActivateBoostFailed(vehicle.Guid).WriteTo(writer));
        _log.Info($"{connection} vehicles: boost ended on {vehicle} — {why}");
    }

    /// <summary>
    /// <c>a0 0d VehicleActivateAbility</c> / <c>a0 0f VehicleDeactivateAbility</c>. <b>Logged, not
    /// acted on.</b>
    ///
    /// <para>
    /// Native 140cb5f40 handles subs 1-17 before 140cc44b0 handles 0x12-0x2b.
    /// The vehicle manager is a0/06. Activation traffic is logged here; effect requests
    /// on 0x9e drive the server's boost state and release echo.
    /// </para>
    /// </summary>
    private void LogVehicleAbility(
        SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload, string hex)
    {
        uint turbo = 0;
        for (int offset = 2; offset + 4 <= payload.Length; offset++)
        {
            uint candidate = BitConverter.ToUInt32(payload[offset..(offset + 4)]);
            if (AugustVehicleBoostFacts.IsTurboAbility(candidate))
            {
                turbo = candidate;
                break;
            }
        }

        _log.Info($"{connection} zone Abilities sub=0x{payload[1]:x2} "
            + $"{(payload[1] == 0x0d ? "VehicleActivateAbility" : "VehicleDeactivateAbility")} "
            + $"({payload.Length} bytes), native receive arm 140cb5f40; "
            + (turbo == 0
                ? "No VehicleTurbo ability id found in the body."
                : $"Carries VehicleTurbo ability {turbo} — the boost runs on 0x9e, which is answered.")
            + $" hex={hex}");
    }

    // ------------------------------------------------------------------------ the seat change

    /// <summary>
    /// <c>70 0a Mount.SeatChangeRequest</c> (AUDIT-vehicles gap 14). Unhandled until this lane.
    ///
    /// <para>
    /// The body is a candidate rather than a derivation — see <see cref="SeatChangeRequest"/> — so
    /// <b>every request is logged with its full hex whether it is acted on or not</b>, and the
    /// plausibility gate is what makes acting on a guess safe: a seat index that does not resolve on
    /// the car the player is actually in is refused, so a wrong reading costs a log line rather than
    /// a teleport into a seat that does not exist.
    /// </para>
    /// <para>
    /// The client refuses a seat change while the vehicle is moving ("You cannot switch seats while
    /// the vehicle is moving.") and inside its own <c>VehicleSeatSwapCooldownMs</c> of 250, and
    /// <see cref="VehicleFleet.TryChangeSeat"/> applies both, so the two stay in step.
    /// </para>
    /// </summary>
    private void HandleSeatChangeRequest(
        SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload, string hex)
    {
        if (!SeatChangeRequest.TryParse(payload, out SeatChangeRequest? parsed) || parsed is null)
        {
            _log.Info($"{connection} zone Mount.SeatChangeRequest ({payload.Length} bytes, under the "
                + $"{SeatChangeRequest.MinimumLength}-byte candidate form): {hex} — unanswered, and "
                + "this line IS the derivation (docs/43 §5.3 blocker 2)");
            return;
        }

        SeatChangeRequest request = parsed;
        string seen = $"{connection} zone Mount.SeatChangeRequest guid=0x{request.Guid:x16} "
            + $"seat={request.Seat} ({payload.Length} bytes) hex={hex}";

        if (state.Fleet is not VehicleFleet fleet
            || !fleet.TryGetForOccupant(state.Guid, out MatchVehicle? seated))
        {
            _log.Info($"{seen} — the sender is not in a vehicle, unanswered");
            return;
        }

        // The plausibility gate. A candidate field that reads as a seat this car does not have is
        // evidence the candidate is wrong, not an instruction.
        if (request.Seat > int.MaxValue
            || !seated.Definition.TryGetSeat((int)request.Seat, out _))
        {
            _log.Warn($"{seen} — {request.Seat} is not a seat of {seated} "
                + $"(it has {seated.Definition.SeatCount}); the candidate body is probably WRONG, "
                + "nothing sent");
            return;
        }

        bool wasDriver = seated.DriverGuid == state.Guid;
        bool alreadySimulating = seated.OwnerGuid == state.Guid || seated.CoastingOwnerGuid == state.Guid;
        ulong formerSimulator = seated.CoastingOwnerGuid;
        VehicleActionResult result = fleet.TryChangeSeat(
            state.Guid,
            (int)request.Seat,
            Environment.TickCount64,
            seated.LastSpeed,
            out MatchVehicle? vehicle);

        if (result != VehicleActionResult.Ok || vehicle is null)
        {
            _log.Info($"{seen} — refused: {result}");
            return;
        }

        bool driver = vehicle.Definition.Seats[(int)request.Seat].IsDriver;
        if (driver)
        {
            if (formerSimulator != 0 && formerSimulator != state.Guid)
                TransferVehicleSimulation(state, vehicle, formerSimulator);
            ApplyDriverVehicleSkin(connection, state, vehicle);
            state.Movement.RegisterManagedEntity(vehicle.TransientId, vehicle.Guid);
            if (!alreadySimulating)
            {
                SendTunnel(connection, writer =>
                    CharacterManagedObject.Grant(vehicle.Guid, state.Guid).WriteTo(writer));
            }
        }

        IReadOnlyList<VehicleOccupantSlot> occupants = vehicle.Occupants();
        SendTunnel(connection, writer => new SeatChangeResponse(
            Rider: state.Guid,
            Mount: vehicle.Guid,
            Seat: request.Seat,
            IsDriver: driver ? 1u : 0u).WriteTo(writer));
        if (driver)
            SendTunnel(connection, writer => new VehicleOwnerState(
                vehicle.Guid, vehicle.OwnerGuid, vehicle.Definition.VehicleId, occupants).WriteTo(writer));
        else if (wasDriver)
            SendTunnel(connection, new VehicleOwner(vehicle.Guid, 0, 0).WriteTo);
        SendTunnel(connection, writer => new VehicleOccupantState(
            vehicle.Guid,
            state.Guid,
            vehicle.Definition.VehicleId,
            vehicle.Definition.SeatCount,
            occupants).WriteTo(writer));

        SendVehicleInventory(connection, state, vehicle);
        if (!driver && wasDriver)
        {
            ReleaseBoost(connection, state, vehicle, "driver seat vacated");
            StopVehicleEngineForViewers(connection, state, vehicle);
            SetVehicleHorn(connection, state, vehicle, false);
        }

        PublishVehicleOccupants(connection, state, vehicle);

        _log.Info($"{seen} → seat {request.Seat} of {vehicle} "
            + $"({(driver ? "now the driver — managed object granted" : "passenger")})");
    }

    private void ReleaseSettledVehicles(SoeConnection connection, GatewaySessionState state, long nowMs)
    {
        if (state.Fleet is not VehicleFleet fleet) return;
        foreach (MatchVehicle vehicle in fleet.Vehicles)
        {
            if (vehicle.CoastingOwnerGuid != state.Guid || !vehicle.CoastCanRelease(nowMs)) continue;
            ReleaseVehicleSimulation(connection, state, vehicle);
        }
    }

    private void ReleaseVehicleSimulation(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle,
        bool broadcastParked = true)
    {
        if (vehicle.CoastingOwnerGuid == state.Guid) vehicle.EndCoast();
        state.Movement.RemoveManagedEntity(vehicle.TransientId);
        if (connection.State != ConnectionState.Open)
        {
            if (broadcastParked)
                SendVehicleControlToViewers(connection, state, vehicle, VehiclePoseRelay.Parked(vehicle).WriteTo);
            return;
        }
        // Keep the current body transform through release. The reported movement position
        // includes a physics-local offset: native 142337f30 accounts for it, whereas 11/23
        // assigns that position directly to the body origin and visibly lifts the car.
        SendTunnel(connection, new ManagedObjectResponseControl(false, vehicle.Guid).WriteTo);
        SendTunnel(connection, CharacterManagedObject.Release(vehicle.Guid).WriteTo);
        var parked = VehiclePoseRelay.Parked(vehicle);
        if (broadcastParked)
            SendVehicleControlToViewers(connection, state, vehicle, parked.WriteTo);
        else SendTunnel(connection, parked.WriteTo);
        // Taking control emptied this client's interpolation queue. In native 140c0e400,
        // adding ONE motionless record does not wake the entity: 140c10cb0 returns false
        // for count == 1 with zero linear/angular velocity. A second record makes it call
        // 140c14610 -> 140eb87b0 and schedule the final pose/audio update. Keep the same
        // timestamp/pose so this cannot introduce movement or a clock regression.
        // Other viewers retain their queues and already receive the ordinary stop relay.
        SendTunnel(connection, parked.WriteTo);
        _log.Info($"{connection} vehicles: simulation released for {vehicle.Guid} at {vehicle.Position}");
    }

    private void TransferVehicleSimulation(GatewaySessionState state, MatchVehicle vehicle, ulong previousSimulator)
    {
        if (!_sharedLootMembership.TryGetValue(state, out ulong matchId)) return;
        foreach (var (viewer, link) in _sharedLootMatches[matchId].Members)
        {
            if (viewer.Guid != previousSimulator) continue;
            ReleaseVehicleSimulation(link, viewer, vehicle);
            // Ownership and simulation are separate from the now-vacant former driver's seat.
            if (link.State == ConnectionState.Open)
                SendTunnel(link, new CharacterManagedObject(vehicle.Guid, 0, state.Guid).WriteTo);
            return;
        }
    }

    // ----------------------------------------------------------------------- the test seam

    /// <summary>
    /// The seam <c>Cranberry.Tests</c> uses to drive this file's arms, and the reason it exists:
    /// the honest route to a car crash is a 15 s lobby timer, a 20 s countdown, a drop, a parachute
    /// landing, a walk to a parked car and then a wall — all on <c>Task.Delay</c> against the wall
    /// clock, because <c>Later</c> has no injectable clock.
    ///
    /// <para>
    /// Everything here is a <i>shortcut into</i> the production path, never a reimplementation of
    /// it: <see cref="Deliver"/> hands raw client bytes to the real gateway dispatcher, and
    /// <see cref="PumpDamage"/> is the production <see cref="ZoneService.PumpVehicleDamage"/>. Only
    /// the entry — the fleet, the seat and the release stamp — is faked.
    /// </para>
    /// </summary>
    internal sealed class VehicleTestSession(ZoneService service, SoeConnection connection)
    {
        private readonly GatewaySessionState _state = (GatewaySessionState)connection.Tag!;

        /// <summary>The match car park this session holds.</summary>
        public VehicleFleet Fleet => _state.Fleet!;

        public Inventory.PlayerInventory? Inventory => _state.Inventory;
        public void OpenInventory() => service.HandleInventoryWindow(connection, _state, "open");

        /// <summary>The local character guid.</summary>
        public ulong Guid => _state.Guid;

        /// <summary>Server-side player health.</summary>
        public uint Hitpoints => _state.Hitpoints;

        public long ExitProtectedUntilMs => _state.VehicleExitProtectedUntilMs;

        /// <summary>Production collision handler at an explicit monotonic time, without sleeps.</summary>
        public void CollisionAt(ReadOnlySpan<byte> payload, long nowMs) =>
            service.HandleCollisionReport(connection, _state, payload, nowMs);

        /// <summary>This session's boost book-keeping.</summary>
        public VehicleBoostState Boost => _state.Boost;

        /// <summary>How many <c>8e 01</c> reports carried <c>causeOfDamage 2 ToxicGas</c>.</summary>
        public long GasReports => _state.CollisionGasReports;

        /// <summary>
        /// Puts the session in a live match past the post-arrival grace, with a one-car fleet the
        /// player is optionally driving.
        /// </summary>
        public MatchVehicle EnterMatchWithCar(
            uint vehicleId = 1, bool asDriver = true, uint hitpoints = 10_000, float fuel = 5_000f)
        {
            _state.Match = MatchStep.InMatch;
            _state.Hitpoints = hitpoints;
            _state.DeathSent = false;
            _state.VictorySent = false;
            _state.AliveSent = null;
            _state.EndedAtMs = 0;
            _state.Released = true;
            // Far enough back that gate 2's post-arrival grace has expired; the grace itself is
            // pinned separately by a test that does NOT do this.
            _state.ReleasedAtMs = Environment.TickCount64 - 3_600_000;
            _state.ChuteGuid = 0;
            _state.MountRequested = false;
            _state.PlayerCollision.Reset();
            _state.VehicleExitProtectedUntilMs = 0;

            var fleet = new VehicleFleet(VehicleRosterData.Value, service._options.VehicleFleet);
            var vehicle = new MatchVehicle(
                guid: VehicleWorldGuidBase,
                transientId: VehicleTransientIdBase,
                definition: VehicleRosterData.Value.Require(vehicleId),
                position: Vector3.Zero,
                yaw: 0f,
                health: service._options.VehicleFleet.MaxHealth,
                fuel: fuel);
            fleet.Add(vehicle);
            _state.Fleet = fleet;
            _state.StreamedVehicles.NoteSpawned(vehicle.Guid, vehicle.Position);

            if (asDriver)
            {
                fleet.TryEnter(vehicle.Guid, _state.Guid, 0, Environment.TickCount64, out _, out _);
                vehicle.EngineOn = true;
                _state.Movement.RegisterManagedEntity(vehicle.TransientId, vehicle.Guid);
            }
            else
            {
                fleet.TryEnter(vehicle.Guid, _state.Guid, 1, Environment.TickCount64, out _, out _);
            }

            // The seat was taken a microsecond ago, so the client's own 1,000 ms interaction
            // cooldown would refuse the very next dismount. A test cannot wait out a wall clock, and
            // the cooldown itself is pinned directly in VehicleFleetTests - so the stamp is cleared
            // rather than the guard being weakened.
            vehicle.LastInteractionMs = long.MinValue;
            return vehicle;
        }

        /// <summary>Hands one raw zone packet to the real gateway dispatcher, on channel 0.</summary>
        public void Deliver(ReadOnlySpan<byte> zonePacket, byte channel = 0)
        {
            byte[] framed = new byte[zonePacket.Length + 1];
            framed[0] = new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: channel).ToByte();
            zonePacket.CopyTo(framed.AsSpan(1));
            service.OnMessage(connection, framed);
        }

        /// <summary>Runs the production flip/boost arm of the world pump at a chosen instant.</summary>
        public void PumpDamage(long nowMs, float billedSeconds = 0f) =>
            service.PumpVehicleDamage(connection, _state, nowMs, billedSeconds);

        /// <summary>The production <c>TryExitVehicle</c>, so the dismount speed guard is testable.</summary>
        public bool Exit(string source = "test") =>
            service.TryExitVehicle(connection, _state, source);

        public void PumpCoasting(long nowMs) => service.ReleaseSettledVehicles(connection, _state, nowMs);

        public void RestreamVehiclesAt(Vector3 position)
        {
            foreach (var action in PlanRestreamVehiclesAt(position)) action();
        }

        public IReadOnlyList<Action> PlanRestreamVehiclesAt(Vector3 position)
        {
            using var writer = new Cranberry.Protocol.PacketWriter();
            PositionUpdateBlock.AtRest(position).WriteTo(writer);
            _state.Movement.ApplyPlayer(ClientMovementUpdate.Parse(writer.Written));
            var burst = new List<Action>();
            service.PlanVehicleRestream(connection, _state, burst);
            return burst;
        }

        public bool IsVehicleStreamed(ulong guid) => _state.StreamedVehicles.IsStreamed(guid);

        public bool Enter(ulong vehicleGuid) => service.TryEnterVehicle(connection, _state,
            vehicleGuid, 0, "test reentry", VehicleEntrySource.PlayerSelect);

        public void RefreshInventory(MatchVehicle vehicle) => service.SendVehicleInventory(connection, _state, vehicle);
        public long? ComponentRemovalDueMs => _state.PendingVehicleRemoval?.DueMs;
        public void CompleteComponentRemoval(long nowMs)
        {
            if (_state.PendingVehicleRemoval is { } pending)
                service.CompleteVehicleComponentRemoval(connection, _state, pending, nowMs);
        }

        /// <summary>Pretends the player is still under the canopy, for gate 3.</summary>
        public void UnderCanopy(ulong chuteGuid = 0xC0FFEE)
        {
            _state.ChuteGuid = chuteGuid;
            _state.MountRequested = true;
        }

        /// <summary>Pretends the release happened just now, for gate 2.</summary>
        public void JustArrived() => _state.ReleasedAtMs = Environment.TickCount64;

        /// <summary>Pretends the world release has not happened, for gate 1.</summary>
        public void BeforeRelease() => _state.Released = false;
    }

    /// <summary>Opens the <see cref="VehicleTestSession"/> seam for <c>Cranberry.Tests</c>.</summary>
    internal VehicleTestSession ForVehicleTest(SoeConnection connection) => new(this, connection);
}
