using Cranberry.Transport;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private void SendVehicleInventory(SoeConnection connection, GatewaySessionState state, MatchVehicle vehicle)
    {
        var inventory = vehicle.Inventory;
        SendTunnel(connection, new BeginCharacterAccess(vehicle.Guid, state.Guid, Items: inventory.Items).WriteTo);
        foreach (var item in inventory.Items)
            SendTunnel(connection, new ItemAdd(vehicle.Guid, item).WriteTo);
        SendTunnel(connection, inventory.ToContainers().WriteTo);
        SendTunnel(connection, inventory.ToLoadout().WriteTo);
        SendTunnel(connection, writer => writer.WriteRaw(inventory.AbilityManager(vehicle.DriverGuid == state.Guid)));
        if (vehicle.DriverGuid == state.Guid)
        {
            SyncDriverEngineRuntime(connection, state, vehicle);
            if (vehicle.HeadlightsOn)
                SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(
                    VehicleDriverControls.HeadlightsAbility(vehicle.Definition.VehicleId), VehicleDriverControls.HeadlightsKey, true)));
            if (vehicle.HornOn)
                SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(
                    VehicleDriverControls.HornAbility, VehicleDriverControls.HornKey, true)));
            if (vehicle.SirenOn)
                SendTunnel(connection, writer => writer.WriteRaw(VehicleDriverControls.Ability(
                    VehicleDriverControls.SirenAbility, VehicleDriverControls.SirenKey, true)));
        }
    }

    private bool HandleVehicleItemUse(SoeConnection connection, GatewaySessionState state, RequestUseItem request)
    {
        if (state.Fleet is null || !state.Fleet.TryGetForOccupant(state.Guid, out var vehicle)) return false;
        if (request.CharacterGuid == state.Guid && request.ItemUseOptionId == 17
            && state.Inventory is { } player && vehicle.Inventory.TryGet(request.ItemGuid, out var fuel)
            && fuel is not null && AugustFuelFacts.IsRefuelItem(fuel.DefinitionId))
        {
            ItemUseOptionTable.TryGet(17, out var option);
            RunConsume(connection, state, player, new ItemActionResult(ItemActionKind.Consume,
                option.Kind, "refuel from vehicle cargo by 25 percent", ItemGuid: fuel.ItemGuid,
                DefinitionId: fuel.DefinitionId, Count: 1, BusyMilliseconds: option.BusyMsec,
                InteractionAnimationId: option.InteractionAnimationId, TargetCharacterGuid: vehicle.Guid));
            return true;
        }
        if (vehicle.Inventory.TryGet(request.ItemGuid, out var item) && item is not null
            && request.Kind is ItemUseOptionKind.LootItem or ItemUseOptionKind.RemoveItem)
        {
            return HandleVehicleContainerMove(connection, state, new MoveItemRequest(
                state.Inventory?.BaseBag?.Guid ?? 0, vehicle.Guid, request.ItemGuid, state.Guid,
                request.Count == 0 ? item.Count : request.Count, -1));
        }
        if (request.TargetCharacterGuid == vehicle.Guid && request.Kind == ItemUseOptionKind.EquipItem
            && state.Inventory?.Items.TryGetValue(request.ItemGuid, out var carried) == true)
        {
            var slot = LoadoutSlotTable.Slots(vehicle.Inventory.LoadoutId).FirstOrDefault(s =>
                VehicleInventory.IsComponentSlot(s.SlotId)
                && LoadoutSlotTable.ItemClasses(s.LoadoutId, s.SlotId).Contains(carried.Fact.ItemClass));
            return HandleVehicleContainerMove(connection, state, new MoveItemRequest(
                PlayerInventory.EquippedContainerGuid, state.Guid, request.ItemGuid, vehicle.Guid, 1, (int)slot.SlotId));
        }
        return false;
    }

    private bool HandleVehicleContainerMove(SoeConnection connection, GatewaySessionState state, MoveItemRequest move) =>
        MoveVehicleInventoryItem(connection, state, move, removalCompleted: false);

    private bool MoveVehicleInventoryItem(SoeConnection connection, GatewaySessionState state, MoveItemRequest move,
        bool removalCompleted)
    {
        if (state.Inventory is not PlayerInventory player || state.Fleet is null
            || !state.Fleet.TryGetForOccupant(state.Guid, out var vehicle)
            || (move.SourceCharacterGuid != vehicle.Guid && move.TargetCharacterGuid != vehicle.Guid)) return false;
        if (state.DeathSent || state.Hitpoints == 0 || RefuseInteractionDuringLogout(connection, state)) return true;
        if (!removalCompleted && state.PendingVehicleRemoval is not null)
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo);
            return true;
        }
        if (!removalCompleted && move.SourceCharacterGuid == vehicle.Guid
            && vehicle.Inventory.TryGet(move.ItemGuid, out var installed) && installed is not null
            && VehicleInventory.IsInstalledComponent(installed))
        {
            if (move.Count != installed.Count || move.Count == 0
                || !(move.TargetCharacterGuid == state.Guid
                    || move.TargetCharacterGuid == vehicle.Guid && move.ContainerGuid == vehicle.Inventory.CargoGuid))
                SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            else StartVehicleComponentRemoval(connection, state, player, vehicle, installed, move);
            return true;
        }
        bool success = false;
        if (move.SourceCharacterGuid == vehicle.Guid && move.TargetCharacterGuid == vehicle.Guid)
            success = vehicle.Inventory.MoveWithin(move.ItemGuid, move.Count, move.ContainerGuid, move.NewSlotId);
        else if (move.SourceCharacterGuid == vehicle.Guid && move.TargetCharacterGuid == state.Guid)
        {
            success = vehicle.Inventory.TakeTo(player, move.ItemGuid, move.Count, out var received);
            if (success && received is not null)
            {
                if (!vehicle.Inventory.TryGet(move.ItemGuid, out _))
                    SendTunnel(connection, new ItemDelete(vehicle.Guid, move.ItemGuid).WriteTo);
                SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, received.ToRecord(state.Guid)));
            }
        }
        else if (move.SourceCharacterGuid == state.Guid && move.TargetCharacterGuid == vehicle.Guid)
        {
            success = vehicle.Inventory.DepositFrom(player, move.ItemGuid, move.Count, move.ContainerGuid, move.NewSlotId);
            if (success)
            {
                if (player.Items.TryGetValue(move.ItemGuid, out var remaining))
                    SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, remaining.ToRecord(state.Guid)));
                else SendTunnel(connection, new ItemDelete(state.Guid, move.ItemGuid).WriteTo);
            }
        }
        if (!success)
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
            _log.Info($"{connection} vehicle inventory: refused item {move.ItemGuid}, count {move.Count}, slot {move.NewSlotId}");
            return true;
        }
        if (!vehicle.Inventory.HasEngineParts && vehicle.EngineOn)
        {
            vehicle.EngineOn = false;
            StopVehicleEngineForViewers(connection, state, vehicle);
        }
        if (!vehicle.Inventory.HasTurbo || !vehicle.EngineOn)
            ReleaseBoost(connection, state, vehicle, "component removed");
        if (!vehicle.Inventory.HasSlot(18)) SetVehicleHeadlights(connection, state, vehicle, false);
        if (!vehicle.Inventory.HasSlot(36)) SetVehicleHorn(connection, state, vehicle, false);
        if (!vehicle.Inventory.HasSlot(35)) SetVehicleSiren(connection, state, vehicle, false);
        // Restoring a component makes the motor available to movement input again;
        // inventory refresh must not start a parked vehicle.
        SendTunnel(connection, player.ToInitContainers().WriteTo);
        SendLoadoutSlots(connection, player);
        SendVehicleInventory(connection, state, vehicle);
        if (_sharedLootMembership.TryGetValue(state, out ulong matchId))
            foreach (var (viewer, link) in _sharedLootMatches[matchId].Members)
                if (viewer != state && link.State == ConnectionState.Open && vehicle.SeatOf(viewer.Guid) >= 0)
                {
                    if (move.SourceCharacterGuid == vehicle.Guid && !vehicle.Inventory.TryGet(move.ItemGuid, out _))
                        SendTunnel(link, new ItemDelete(vehicle.Guid, move.ItemGuid).WriteTo);
                    SendVehicleInventory(link, viewer, vehicle);
                }
        _log.Info($"{connection} vehicle inventory: moved item {move.ItemGuid}, count {move.Count}; engine parts={vehicle.Inventory.HasEngineParts}, turbo={vehicle.Inventory.HasTurbo}");
        return true;
    }

    private const int VehicleComponentRemovalMs = 10_000;
    private sealed record PendingVehicleRemoval(PlayerInventory Player, MatchVehicle Vehicle,
        InventoryItem Item, MoveItemRequest Move, int WorldGeneration, int InteractionGeneration, long DueMs);

    private void StartVehicleComponentRemoval(SoeConnection connection, GatewaySessionState state,
        PlayerInventory player, MatchVehicle vehicle, InventoryItem item, MoveItemRequest move)
    {
        long now = Environment.TickCount64;
        if (state.PendingMedicalCast is not null || state.ShredBusyUntil > now || state.CraftBusyUntil > now
            || state.ConsumeBusyUntil > now)
        {
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo);
            return;
        }
        var pending = new PendingVehicleRemoval(player, vehicle, item, move, state.WorldGeneration,
            state.InteractionGeneration, now + VehicleComponentRemovalMs);
        state.PendingVehicleRemoval = pending;
        if (!Later(connection, VehicleComponentRemovalMs, () => CompleteVehicleComponentRemoval(connection, state, pending,
                Environment.TickCount64)))
        {
            state.PendingVehicleRemoval = null;
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.ContainerInUse).WriteTo);
            return;
        }
        InventoryItemFacts.TryGet(item.DefinitionId, out var fact);
        SendTunnel(connection, new InteractionStart(state.Guid, VehicleComponentRemovalMs, fact.NameId, 2).WriteTo);
        _log.Info($"{connection} vehicle inventory: removing component {item.DefinitionId} slot {item.SlotId} in 10000 ms");
    }

    private void CompleteVehicleComponentRemoval(SoeConnection connection, GatewaySessionState state,
        PendingVehicleRemoval pending, long nowMs)
    {
        if (!ReferenceEquals(state.PendingVehicleRemoval, pending) || nowMs < pending.DueMs) return;
        state.PendingVehicleRemoval = null;
        bool valid = connection.State == ConnectionState.Open && !state.DeathSent && state.Hitpoints > 0
            && state.WorldGeneration == pending.WorldGeneration && state.InteractionGeneration == pending.InteractionGeneration
            && ReferenceEquals(state.Inventory, pending.Player)
            && state.Fleet?.TryGetForOccupant(state.Guid, out var current) == true
            && ReferenceEquals(current, pending.Vehicle) && current.Health > 0
            && current.Inventory.TryGet(pending.Item.ItemGuid, out var item) && item == pending.Item;
        if (valid) MoveVehicleInventoryItem(connection, state, pending.Move, removalCompleted: true);
        else if (connection.State == ConnectionState.Open)
            SendTunnel(connection, new ContainerError(state.Guid, ContainerErrorCode.InteractionValidationFailed).WriteTo);
        if (connection.State == ConnectionState.Open) SendInteractionStops(connection, state);
    }

    private void CancelVehicleComponentRemoval(SoeConnection connection, GatewaySessionState state, bool notify = true)
    {
        if (state.PendingVehicleRemoval is null) return;
        state.PendingVehicleRemoval = null;
        if (notify && connection.State == ConnectionState.Open) SendInteractionStops(connection, state);
    }
}
