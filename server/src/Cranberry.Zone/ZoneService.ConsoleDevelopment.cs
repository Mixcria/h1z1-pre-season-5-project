using System.Globalization;
using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Movement;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private ConsoleReply ConsoleGive(SoeConnection connection, GatewaySessionState state, uint id, uint count)
    {
        if (!InventoryItemFacts.TryGet(id, out var fact) || count is < 1 or > 1000)
            return ConsoleReply.Failed("unknown inventory item or invalid count (1..1000)");
        uint given = 0;
        while (given < count)
        {
            uint stack = Math.Min(count - given, (uint)Math.Min(100, Math.Max(1, fact.MaxStackSize)));
            var result = ConsoleSpawnLoot(connection, state, id, stack, pickup: true);
            if (!result.Ok)
                return new ConsoleReply([$"- gave {given}/{count} {ItemNames.Label(id)}", .. result.Lines], Ok: false);
            given += stack;
        }
        return ConsoleReply.Did($"gave {ItemNames.Label(id)} x{given}");
    }

    private ConsoleReply ConsoleKit(SoeConnection connection, GatewaySessionState state, string kit)
    {
        // C:\Z1\Server\Zone\ZoneAdminCommands.cs Kit: armour first, then weapons, ammo and meds.
        // IDs are August's own gameplay items. Every grant goes through real pickup and capacity.
        (uint Id, uint Count)[] supplies = kit switch
        {
            "pvp" => [(2124, 1), (2168, 1), (2271, 1), (2425, 1), (1374, 1), (1429, 100), (1511, 30), (2423, 10), (2424, 5)],
            "guns" => [(2124, 1), (2425, 1), (2229, 1), (1374, 1), (1899, 1), (1718, 1), (2, 1), (1997, 1), (1991, 1)],
            "ammo" => [(1429, 100), (2325, 100), (1511, 30), (1469, 30), (1719, 30), (1428, 60), (1998, 60), (1992, 60)],
            "meds" => [(2423, 10), (2424, 5)],
            "armour" => [(2124, 1), (2168, 1), (2271, 1)],
            "throwables" => [(65, 3), (14, 3), (2236, 3), (2237, 3), (2235, 3)],
            _ => [],
        };
        if (supplies.Length == 0) return ConsoleReply.Failed("unknown kit");
        List<string> lines = [];
        foreach (var (id, count) in supplies)
        {
            // Repeated /kit should not fill the bag with duplicate packs or discard a better pack.
            if (id == 2124 && state.Inventory?.LoadoutSlots.Values.Any(i => i.Fact.PassiveEquipSlotId == 10
                    && PlayerInventory.MaxBulkProvidedBy(i.Fact) >= (InventoryItemFacts.TryGet(2124, out var pack) ? PlayerInventory.MaxBulkProvidedBy(pack) : int.MaxValue)) == true)
                continue;
            var result = ConsoleGive(connection, state, id, count);
            lines.AddRange(result.Lines);
            if (!result.Ok) return new ConsoleReply([$"- {kit} kit stopped at {ItemNames.Label(id)}; earlier grants remain", .. lines], Ok: false);
        }
        return new ConsoleReply([$"+ {kit} kit granted; /ammo supplies the held gun and R reloads it", .. lines]);
    }

    private ConsoleReply ConsoleAmmo(SoeConnection connection, GatewaySessionState state, uint count)
    {
        if (state.Inventory is not PlayerInventory inventory
            || !inventory.Items.TryGetValue(state.HeldWeaponItemGuid, out var item))
            return ConsoleReply.Failed("hold a firearm first");
        uint ammo = AmmoTypes.AmmoItemFor(item.DefinitionId);
        return ammo == 0 ? ConsoleReply.Failed("the held item uses no ammunition") : ConsoleGive(connection, state, ammo, count);
    }

    private ConsoleReply ConsoleDrop(SoeConnection connection, GatewaySessionState state, string slot)
    {
        if (state.Inventory is not PlayerInventory inventory) return ConsoleReply.Failed("inventory unavailable");
        if (slot == "guns")
        {
            var guns = inventory.Items.Values.Where(i => AmmoTypes.AmmoItemFor(i.DefinitionId) != 0).ToArray();
            int dropped = 0;
            foreach (var gun in guns)
            {
                var result = ConsoleDropInstance(connection, state, inventory, gun);
                if (!result.Ok) return new ConsoleReply([$"- dropped {dropped}/{guns.Length} guns", .. result.Lines], Ok: false);
                dropped++;
            }
            return ConsoleReply.Did($"dropped {dropped} gun(s); /give <gun> starts a fresh weapon test");
        }
        InventoryItemInstance? item = null;
        if (slot == "hand") inventory.Items.TryGetValue(state.HeldWeaponItemGuid, out item);
        else if (uint.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            inventory.LoadoutSlots.TryGetValue(id, out item);
        else return ConsoleReply.Usage("/drop [hand|guns|loadout-slot]; /inv lists slot numbers");
        if (item is null) return ConsoleReply.Failed("that slot is empty");
        return ConsoleDropInstance(connection, state, inventory, item);
    }

    private ConsoleReply ConsoleDropInstance(SoeConnection connection, GatewaySessionState state, PlayerInventory inventory, InventoryItemInstance item)
    {
        var request = new RequestUseItem(0, 4, state.Guid, state.Guid, state.Guid, item.Guid, false, item.Count, 0);
        var plan = InventoryActions.Resolve(inventory, request);
        if (plan.Kind != ItemActionKind.Drop) return ConsoleReply.Failed(plan.Rule);
        ApplyInventoryAction(connection, state, inventory, plan);
        return inventory.Items.ContainsKey(item.Guid) ? ConsoleReply.Failed("drop was refused")
            : ConsoleReply.Did($"dropped {ItemNames.Label(item.DefinitionId)} x{plan.Count}");
    }

    private static MovementProfile ApplyConsoleSpeed(GatewaySessionState state, MovementProfile profile) =>
        profile with { MaxMovementSpeed = state.DevConsole.NativeRunMetresPerSecond
            ?? profile.MaxMovementSpeed * state.DevConsole.SpeedMultiplier };

    private ConsoleReply ConsoleSpeed(SoeConnection connection, GatewaySessionState state, float multiplier)
    {
        bool clearNative = state.DevConsole.NativeRunMetresPerSecond.HasValue;
        state.DevConsole.NativeRunMetresPerSecond = null;
        state.DevConsole.SpeedMultiplier = multiplier;
        PublishConsoleMovement(connection, state);
        if (clearNative) SendTunnel(connection, writer => NativeRunCommand.WriteReply(writer, 0f));
        return ConsoleReply.Did($"movement x{multiplier:0.##}; /speed 1 restores normal");
    }

    private void PublishConsoleMovement(SoeConnection connection, GatewaySessionState state)
    {
        // The same stat pair used at login and after a shoe change; the shared profile helper keeps
        // subsequent inventory refreshes from silently undoing this command.
        state.MovementStats.SetProfile(ApplyConsoleSpeed(state, Footwear.Apply(_options.Movement, Footwear.Equipped(state.Inventory))));
        var stats = state.MovementStats.BuildStatBurst(state.Guid);
        var speed = state.MovementStats.BuildBaseSpeedUpdate();
        SendTunnel(connection, stats.WriteTo);
        SendTunnel(connection, speed.WriteTo);
        state.MovementStats.MarkStatsDelivered();
    }

    private ConsoleReply ConsoleParachute(SoeConnection connection, GatewaySessionState state, float height)
    {
        if (state.Movement.Player?.Position is not Vector3 origin) return ConsoleReply.Failed("no player position");
        if (state.ChuteGuid != 0) return ConsoleReply.Failed("already under a parachute");
        TryExitVehicle(connection, state, "console /chute");
        state.Drop = new Vector4(origin, 1f);
        var air = new Vector4(origin.X, origin.Y + height, origin.Z, 1f);
        state.Match = MatchStep.Dropping;
        SendTunnel(connection, w => new SynchronizedTeleport(SynchronizedTeleport.StartingMatch).WriteTo(w));
        SendTunnel(connection, w => new UpdateLocation(air, new Vector4(0, 0, 0, 1), Apply: true, WaitForTeleport: true).WriteTo(w));
        ExpectClient(connection, state, ClientMilestone.TeleportClientReady, "console parachute teleport");
        SendParachute(connection, state, air);
        return ConsoleReply.Did($"parachute deployed {height:0} metres above your position");
    }

    private ConsoleReply ConsolePlayers() => ConsoleReply.Plain(
        _throwableSessions.Values.Where(s => s.Connection.State == ConnectionState.Open)
            .Select(s => $"* {s.State.CharacterName} guid={s.State.Guid} {s.State.Match} tier={s.State.DevConsole.Tier} "
                + (s.State.Movement.Player?.Position is Vector3 p ? Coordinates(p) : "no position")));

    private (SoeConnection Connection, GatewaySessionState State)? ConsoleFindPlayer(string name)
    {
        var matches = _throwableSessions.Values.Where(s => s.Connection.State == ConnectionState.Open
            && (s.State.CharacterName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || s.State.Guid.ToString(CultureInfo.InvariantCulture) == name)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private ConsoleReply ConsoleEvict(string name, string reason)
    {
        if (ConsoleFindPlayer(name) is not { } target) return ConsoleReply.Failed("player not found or name ambiguous; use /players then guid");
        SendTunnel(target.Connection, w => Gas.GasAlerts.Write(w, string.IsNullOrWhiteSpace(reason) ? "Disconnected by the test server owner" : reason));
        target.Connection.Disconnect();
        return ConsoleReply.Did($"disconnected {target.State.CharacterName}");
    }

    private ConsoleReply ConsoleTierChange(string name, string tier)
    {
        if (ConsoleFindPlayer(name) is not { } target) return ConsoleReply.Failed("player not found or name ambiguous; use /players then guid");
        ConsoleTier? value = null;
        if (!tier.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (!ConsolePermission.TryParseTier(tier, out var parsed))
                return ConsoleReply.Usage("/tier <name|guid> <player|client|tester|owner|admin|reset>");
            value = parsed;
        }
        target.State.DevConsole.TierOverride = value;
        var resolved = ResolveConsoleTier(target.Connection, target.State);
        return ConsoleReply.Did($"{target.State.CharacterName} console tier {resolved} for this connection");
    }

    private ConsoleReply ConsoleTeleportHere(GatewaySessionState caller, string name)
    {
        if (ConsoleFindPlayer(name) is not { } target) return ConsoleReply.Failed("player not found or name ambiguous; use /players then guid");
        if (target.State.Match != MatchStep.InMatch) return ConsoleReply.Failed("target must be in a match");
        if (caller.Movement.Player?.Position is not Vector3 here) return ConsoleReply.Failed("no player position");
        TryExitVehicle(target.Connection, target.State, "console /tphere");
        return ConsoleTeleport(target.Connection, target.State, here + new Vector3(2f, 0f, 0f));
    }

    private static ConsoleReply ConsoleCombatStatus(GatewaySessionState state)
    {
        var shooter = state.Combat.Shooter;
        List<string> lines = [$"* shots accepted={shooter.ShotsFired} refused={shooter.ShotsRefused} hits={shooter.HitsRegistered} damage={shooter.DamageDealt}"];
        if (state.Inventory is PlayerInventory inventory && inventory.Items.TryGetValue(state.HeldWeaponItemGuid, out var item))
        {
            uint ammo = AmmoTypes.AmmoItemFor(item.DefinitionId);
            long reserve = inventory.Items.Values.Where(i => i.DefinitionId == ammo).Sum(i => (long)i.Count);
            lines.Add($"  held {ItemNames.Label(item.DefinitionId)} base={item.DefinitionId} skin={item.DisplayDefinitionId} guid={item.Guid}");
            lines.Add($"  magazine={shooter.AmmoOf(item.Guid)}/{RetailBalance.ClipSize(item.DefinitionId)} reserve={reserve} ammo={ammo} mode={shooter.FireModeOf(item.Guid)}");
            if (WeaponItemProfiles.TryGet(item.DefinitionId, out var fact))
            {
                var blob = state.Weapons.Blob;
                var weapon = blob.WeaponDefinitions?.FirstOrDefault(w => w.WeaponDefinitionId == fact.WeaponId);
                int groupIndex = Math.Max(0, shooter.FireGroupOf(item.Guid));
                int modeIndex = Math.Max(0, shooter.FireModeOf(item.Guid));
                if (weapon is not null && groupIndex < weapon.FireGroupIds.Count)
                {
                    var group = blob.FireGroups?.FirstOrDefault(g => g.FireGroupId == weapon.FireGroupIds[groupIndex]);
                    if (group?.FireModeIds is { } ids && modeIndex < ids.Count
                        && blob.FireModes?.FirstOrDefault(m => m.FireModeId == ids[modeIndex]) is { } mode)
                    {
                        uint Word(short offset, uint fallback = 0) => mode.Overrides?.TryGetValue(offset, out uint word) == true ? word : fallback;
                        float Number(short offset, float fallback = 0f) => BitConverter.UInt32BitsToSingle(Word(offset, BitConverter.SingleToUInt32Bits(fallback)));
                        uint flags = Word(WeaponListLayouts.FireModeFlags2);
                        lines.Add($"  table={state.Weapons.Options.WeaponTable} group={group.FireGroupId} fireMode={mode.FireModeId} pellets={Math.Max(1, Word(WeaponListLayouts.FireModePelletsPerShot, (uint)mode.PelletsPerShot))} spread={Number(WeaponListLayouts.FireModePelletSpread, mode.PelletSpread):0.###}");
                        lines.Add($"  zoom={Number(WeaponListLayouts.FireModeDefaultZoom, mode.DefaultZoom):0.###} recoil={(flags & 0x40) != 0} horizontal={(flags & 0x20) != 0} magnitude={Number(WeaponListLayouts.FireModeRecoilMagnitudeMin):0.###}..{Number(WeaponListLayouts.FireModeRecoilMagnitudeMax):0.###}");
                    }
                }
            }
        }
        else lines.Add("  no weapon held");
        lines.Add($"  reload={(state.Combat.Reload is null ? "idle" : "active")} packets={state.Combat.PacketsSeen} undecodable={state.Combat.Undecodable} targets={state.Combat.Targets.Count}");
        return ConsoleReply.Plain(lines);
    }
}
