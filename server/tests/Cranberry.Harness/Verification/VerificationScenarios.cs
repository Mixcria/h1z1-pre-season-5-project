using System.Numerics;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Scenarios;

namespace Cranberry.Harness.Verification;

/// <summary>
/// <b>The run lane — the first reading of everything waves 3 to 6 built and never live-verified.</b>
///
/// <para>Waves 3, 4, 5 and 6 shipped eleven features that were "BUILT + TESTED" and had never had a
/// single byte in front of a client: loot streaming, gas pacing, doors, item tints, worn visuals,
/// the random drop, movement stats on a second match, vehicles, crafting, the inventory, and the
/// staged weapon rollout. The owner found out about the previous four regressions by playing, one
/// session each. These scenarios drive the live server with the modelled client and take a reading
/// of every one of them in three sessions.</para>
///
/// <list type="table">
/// <item><term>V1</term><description>the in-match sweep: land, walk, press F on everything, and
///   read the world the server built — loot, streaming, doors, tints, worn items, inventory,
///   crafting, vehicles, movement stats, the weapon ItemAdd</description></item>
/// <item><term>V2</term><description>gas pacing, measured off the wire over five and a half minutes
///   of a real match clock</description></item>
/// <item><term>V3</term><description>a SECOND match on one link: the wave-3 stat-burst bug, and a
///   second independent drop point</description></item>
/// </list>
///
/// <para><b>What a probe can and cannot say.</b> Nothing here can see a colour, a mesh on a body, or
/// whether a door blocks a player: those are the client's renderer and this is a wire harness. What
/// it can do is check the three things upstream of every one of them — that the packet went out,
/// that its fields name something the client's own data can actually load, and that the client-side
/// consequence the server owes (a grant, a removal, a door-state swing) followed. Where that is
/// not enough the probe says NOT-TESTABLE and names what the owner would have to look at.</para>
/// </summary>
public static class VerificationScenarios
{
    private const byte Ce = VerificationPackets.GameModeHud;

    /// <summary>Every ammunition-box model the shipped loot tables can name (docs/54 §2, docs/39 §5).</summary>
    public static IReadOnlyList<uint> KnownAmmunitionBoxModels { get; } = [10, 8023, 10132, 10133, 10134, 10135, 9210];

    /// <summary>The model docs/54 A3 convicted: <c>Common_Props_AmmoBoxes_Shotgun.adr</c> ships in no pack.</summary>
    public const uint MissingShotgunBoxModel = 10137;

    /// <summary>
    /// The sprint speed the shipped movement profile promises (host banner). <b>5.74 m/s since the
    /// wave-8 Z1 port</b> — the owner's own 4.10 m/s base × his 1.40 sprint modifier (docs/76 §0).
    /// <para>
    /// Anything that has to be outrunnable is sized against this, and it fell 13 % in wave 8, so a
    /// gas probe that passed at 6.60 does not necessarily pass now. That is the point: the wall was
    /// always meant to be escapable by a sprinting player, and this is what a sprinting player is.
    /// </para>
    /// </summary>
    public const float SprintSpeed = 5.74f;

    /// <summary>The gas wall speed the ladder is paced to — the RADIUS rate (docs/77 §4.3).</summary>
    public const float GasWallSpeed = 4.05f;

    /// <summary>
    /// What a player actually has to outrun: the drawn ring's leading edge, which is the radius rate
    /// plus the walking centre. <c>GasSettings.LeadingEdgeSpeedCeiling()</c> for the shipped
    /// defaults, and the bound this probe grades against (docs/77 §4).
    /// </summary>
    public const float GasEdgeCeiling = 4.88f;

    /// <summary>Match clock at which phase 1's ring starts moving, and the first moment a ce 01 may be drawn.</summary>
    public static TimeSpan FirstMovementDelay { get; } = TimeSpan.FromSeconds(270);

    /// <summary>docs/53 §5.4 / the host's own schedule: the first phase is revealed two minutes in.</summary>
    public static TimeSpan FirstRevealDelay { get; } = TimeSpan.FromSeconds(120);

    /// <summary>The play area, 4 400 m at (-250, 0, 100) since wave 8 (docs/66 §3, docs/77 §4.2).</summary>
    public const float InitialRadius = 4400f;

    // ---- V1: the in-match sweep ---------------------------------------------------------------

    /// <summary>
    /// <b>V1 — land, walk, and press F on the world.</b> Lane A's S4 gets the modelled client onto
    /// the ground with a real recorded pose; everything after that is this lane's.
    /// </summary>
    public static Scenario V1InMatchSweep(ProbeSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        int landingMark = 0;
        Vector3 landingCentroid = Vector3.Zero;
        var pressed = new List<string>();
        var grants = new List<ItemGrant>();
        ulong doorPressed = 0;
        bool doorRePressed = false;
        ulong vehiclePressed = 0;
        int mountAfterPress = 0;
        int mountAfterRequest = 0;

        return Scenario.Named("V1 in-match sweep — loot, doors, tints, worn items, inventory, crafting, vehicles, weapons")
            .Then(LaneAScenarios.S4Drop())
            .Do("let the landing burst finish (slices are 16 objects every 40 ms)",
                (c, token) => c.Clock.DelayAsync(TimeSpan.FromSeconds(6), token))
            .Do("note the landing burst", c =>
            {
                landingMark = c.Ledger.Mark();
                landingCentroid = ServerLedger.Centroid(c.Ledger.Npcs);
                c.Notes.Add($"landing: {c.Ledger.Npcs.Count} world object(s) around {landingCentroid}");
            })

            // ---- the world the landing built ----
            .Probe(sheet, "L1", "ground loot at the landing point",
                "docs/13 §9, docs/33 — the touchdown is the first moment the player stands on terrain with a known position",
                c =>
                {
                    IReadOnlyList<WorldObject> objects = WorldObjects(c);
                    int loot = objects.Count(o => o.DoorId == 0);
                    return loot > 0
                        ? ProbeOutcome.Pass($"{loot} ground item(s) spawned, centroid {landingCentroid}")
                        : ProbeOutcome.Fail($"0 ground items in {objects.Count} world object(s) — the landing armed an empty world");
                })
            .Probe(sheet, "T1", "every spawned model resolves to an actor the client ships",
                "docs/54 A3 — Models.txt row 10137 names Common_Props_AmmoBoxes_Shotgun.adr, which is in no pack; "
                + "the owner's own client logged the load failure ten times",
                c =>
                {
                    ClientAssets assets = ClientAssets.Current;
                    if (!assets.Available)
                    {
                        return ProbeOutcome.NotTestable($"the extracted client corpus is not at {assets.Root}");
                    }

                    uint[] models = [.. WorldObjects(c).Select(o => o.Entity.ModelId).Distinct().Order()];
                    uint[] broken = [.. models.Where(m => !assets.ModelResolves(m))];
                    return broken.Length == 0
                        ? ProbeOutcome.Pass(
                            $"{models.Length} distinct model id(s) spawned, all resolve through Models.txt to a "
                            + $"shipped .adr ({assets.ActorCount} actors on disk); 10137 appeared "
                            + $"{WorldObjects(c).Count(o => o.Entity.ModelId == MissingShotgunBoxModel)} time(s)")
                        : ProbeOutcome.Fail("unloadable model(s): " + string.Join("; ", broken.Select(assets.Explain)));
                })
            .Probe(sheet, "T2", "ammunition-box colour (the AR-15 brown / shotgun white report)",
                "docs/54 A1/A2/A3 — no value on any wire can change a gun's hue; the coloured objects are the boxes, "
                + "and the fix was a model id, not a tint",
                c =>
                {
                    ClientAssets assets = ClientAssets.Current;
                    uint[] boxes =
                    [
                        .. WorldObjects(c)
                            .Select(o => o.Entity.ModelId)
                            .Where(m => KnownAmmunitionBoxModels.Contains(m) || m == MissingShotgunBoxModel)
                            .Distinct()
                            .Order(),
                    ];
                    string named = string.Join(", ", boxes.Select(m => $"{m}={assets.ModelFile(m) ?? "?"}"));
                    return boxes.Contains(MissingShotgunBoxModel)
                        ? ProbeOutcome.Fail($"the missing shotgun box model 10137 is still on the wire; boxes seen: {named}")
                        : ProbeOutcome.NotTestable(
                            $"the wire is correct as far as it can be checked — boxes seen: [{named}], none of them the "
                            + "unloadable 10137 — but whether a box renders green, brown or white is the client's "
                            + "shader and only the owner's eyes can confirm it");
                })
            .Probe(sheet, "D1", "the map's own doors are streamed around the player",
                "docs/42 §10.3 / docs/47 §I3 — a door spawn is a loot spawn with the +0x19c door-id field set",
                c =>
                {
                    IReadOnlyList<WorldObject> doors = [.. WorldObjects(c).Where(o => o.DoorId != 0)];
                    if (doors.Count == 0)
                    {
                        return ProbeOutcome.Fail(
                            $"no 0xd6 body in {WorldObjects(c).Count} carried a non-zero door id, so no door was spawned "
                            + "within the 60 m disc of the landing point");
                    }

                    ClientAssets assets = ClientAssets.Current;
                    string models = string.Join(", ", doors
                        .Select(d => d.Entity.ModelId).Distinct().Order()
                        .Select(m => $"{m}={assets.ModelFile(m) ?? "?"}"));
                    return ProbeOutcome.Pass($"{doors.Count} door proxy/proxies spawned; models [{models}]");
                })
            .Probe(sheet, "D2", "door meshes are the createAsKinematic collision twins (docs/55's fix)",
                "docs/55 §0 finding 6 — 33 of the client's 3,406 actors are authored createAsKinematic=\"1\", 29 of them "
                + "doors; naming the twin is the ONLY lever the 1148 protocol gives a server over whether a door blocks",
                c =>
                {
                    ClientAssets assets = ClientAssets.Current;
                    uint[] doorModels = [.. WorldObjects(c).Where(o => o.DoorId != 0).Select(o => o.Entity.ModelId).Distinct()];
                    if (doorModels.Length == 0)
                    {
                        return ProbeOutcome.Unknown("no door spawned, so no mesh to check");
                    }

                    var kinematic = new List<string>();
                    var notKinematic = new List<string>();
                    foreach (uint model in doorModels)
                    {
                        string? file = assets.ModelFile(model);
                        string? text = file is null ? null : ActorText(assets, file);
                        if (text is null)
                        {
                            notKinematic.Add($"{model}={file ?? "?"} (no .adr on disk)");
                        }
                        else if (text.Contains("createAsKinematic=\"1\"", StringComparison.Ordinal))
                        {
                            kinematic.Add($"{model}={file}");
                        }
                        else
                        {
                            notKinematic.Add($"{model}={file}"
                                + (text.Contains("<CollisionData", StringComparison.Ordinal) ? " (collides, not kinematic)" : " (NO CollisionData)"));
                        }
                    }

                    return notKinematic.Count == 0
                        ? ProbeOutcome.Pass($"all {kinematic.Count} door mesh(es) are createAsKinematic=\"1\": {string.Join(", ", kinematic)}")
                        : ProbeOutcome.Fail($"door mesh(es) that are not the kinematic twin: {string.Join("; ", notKinematic)}"
                            + (kinematic.Count > 0 ? $" (kinematic: {string.Join(", ", kinematic)})" : string.Empty));
                })

            // ---- the player acts on that world ----
            .Try("press F on a door", async (c, token) =>
            {
                WorldObject? door = WorldObjects(c).FirstOrDefault(o => o.DoorId != 0);
                if (door is null)
                {
                    return;
                }

                doorPressed = door.Entity.Guid;
                c.RequestInteractionString(door.Entity.Guid);
                await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                await c.PressInteractAsync(door.Entity.Guid, door.Entity.Position, token).ConfigureAwait(false);

                // docs/79 §4 E14 D8: a HELD [F] re-fires the whole press, and the August client's own
                // interaction re-evaluation runs at a median 171 ms. This second press stands in for
                // one of those re-fires. The 800 ms window must absorb it — a second 0f 0a here is
                // the door slamming shut inside its own 785 ms swing.
                await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                await c.PressInteractAsync(door.Entity.Guid, door.Entity.Position, token).ConfigureAwait(false);
                doorRePressed = true;

                await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(600), token).ConfigureAwait(false);
                pressed.Add($"door {door.Entity.Guid} (door id {door.DoorId}), pressed twice 200 ms apart");
            })
            .Try("press F on up to three firearms, three wearables and three other items", async (c, token) =>
            {
                ClientAssets assets = ClientAssets.Current;
                IReadOnlyList<WorldObject> loot = [.. WorldObjects(c).Where(o => o.DoorId == 0)];
                var chosen = new List<WorldObject>();
                chosen.AddRange(loot.Where(o => Named(assets, o).StartsWith("Weapon", StringComparison.OrdinalIgnoreCase)).Take(3));
                chosen.AddRange(loot.Where(o => IsWearable(Named(assets, o))).Take(3));
                chosen.AddRange(loot.Where(o => !chosen.Contains(o)).Take(3));

                foreach (WorldObject target in chosen.Distinct())
                {
                    c.RequestInteractionString(target.Entity.Guid);
                    await c.PressInteractAsync(target.Entity.Guid, target.Entity.Position, token).ConfigureAwait(false);
                    await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                    pressed.Add($"{Named(assets, target)} ({target.Entity.Guid})");
                }
            })
            .Try("press F on a parked vehicle, then ask for a mount the recorded way and the synthesised way",
                async (c, token) =>
                {
                    // Vehicles[0] is the parachute; anything after it is a parked car (docs/43).
                    IReadOnlyList<LightweightEntity> parked = [.. c.Ledger.Vehicles.Skip(1)];
                    if (parked.Count == 0)
                    {
                        return;
                    }

                    LightweightEntity car = parked[0];
                    vehiclePressed = car.Guid;
                    c.RequestInteractionString(car.Guid);

                    // The two arms are separated in time so the answer can be attributed: the
                    // recorded [F] pair first, two full seconds to answer, and only then the
                    // synthesised 70 01 that no client has ever been recorded sending.
                    int beforePress = c.Ledger.Count(VerificationPackets.MountBase, 0x02);
                    await c.PressInteractAsync(car.Guid, car.Position, token).ConfigureAwait(false);
                    await c.Clock.DelayAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    mountAfterPress = c.Ledger.Count(VerificationPackets.MountBase, 0x02) - beforePress;

                    int beforeRequest = c.Ledger.Count(VerificationPackets.MountBase, 0x02);
                    c.SendMountRequest(car.Guid);
                    await c.Clock.DelayAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    mountAfterRequest = c.Ledger.Count(VerificationPackets.MountBase, 0x02) - beforeRequest;
                })
            .Try("send a craft request for the first recipe the server listed", async (c, token) =>
            {
                if (c.Ledger.Count(VerificationPackets.RecipeBase, VerificationPackets.RecipeListSub) == 0)
                {
                    return;
                }

                c.SendRecipeStart(FirstRecipeId(c) ?? 0);
                await c.Clock.DelayAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            })
            .Do("collect the grants", c => grants.AddRange(ItemGrants(c)))

            // ---- what the world did back ----
            .Probe(sheet, "L2", "a pickup is granted and the world object removed",
                "docs/13 §5 — the grant goes out before the removal, so a refused pickup can never vanish the object",
                c => c.Ledger.ItemAdds > 0 && c.Ledger.RemovedObjects.Count > 0
                    ? ProbeOutcome.Pass(
                        $"{pressed.Count} press(es) → {c.Ledger.ItemAdds} ClientUpdate.ItemAdd and "
                        + $"{c.Ledger.RemovedObjects.Count} Character.RemovePlayer; pressed: {string.Join("; ", pressed)}")
                    : ProbeOutcome.Fail(
                        $"{pressed.Count} press(es) → {c.Ledger.ItemAdds} ItemAdd(s), "
                        + $"{c.Ledger.RemovedObjects.Count} removal(s)"))
            .Probe(sheet, "D3", "the [F] prompt's text is answered (Command.InteractionString 09 2d)",
                "docs/47 §7 q1 / §4d — the 09 2d reply is the packet that carries the prompt's TEXT: "
                + "u64 targetGuid | u32 stringId | u32 entryCount, and FUN_14129e7c0 drops a reply whose guid is not "
                + "the client's current target, so both fields have to be right",
                c =>
                {
                    var replies = new List<(ulong Guid, uint StringId)>();
                    foreach (byte[] payload in c.Ledger.Payloads(
                        VerificationPackets.CommandBase, sub16: VerificationPackets.InteractionStringSub))
                    {
                        if (payload.Length >= 15)
                        {
                            replies.Add((BitConverter.ToUInt64(payload.AsSpan(3, 8)), BitConverter.ToUInt32(payload.AsSpan(11, 4))));
                        }
                    }

                    if (replies.Count == 0)
                    {
                        return ProbeOutcome.Fail("the server answered no 09 2d at all, so every [F] prompt is blank");
                    }

                    (ulong Guid, uint StringId)[] labelled = [.. replies.Where(r => r.StringId != 0)];
                    string ids = string.Join(", ", replies.Select(r => $"{r.Guid}:{r.StringId}").Distinct().Take(8));
                    bool doorLabelled = doorPressed != 0 && labelled.Any(r => r.Guid == doorPressed);
                    return labelled.Length == 0
                        ? ProbeOutcome.Fail($"{replies.Count} reply/replies, every one with string id 0 — the prompt "
                            + $"renders blank. Replies (guid:stringId): {ids}")
                        : ProbeOutcome.Pass($"{labelled.Length} of {replies.Count} replies carry a non-zero string id "
                            + $"and echo the guid that was asked about{(doorLabelled ? ", the pressed door among them" : string.Empty)}; "
                            + $"(guid:stringId) {ids}");
                })
            .Probe(sheet, "D4", "a door actually swings (Character.UpdateCharacterState 0f 0a)",
                "docs/42 §5c — the only packet that swings a door; open yaw, pivot and the swing itself are the client's",
                c =>
                {
                    var states = new List<(ulong Guid, bool Open)>();
                    foreach (byte[] payload in c.Ledger.Payloads(VerificationPackets.CharacterBase, sub8: VerificationPackets.DoorStateUpdateSub))
                    {
                        if (VerificationPackets.TryReadDoorState(payload) is { } state)
                        {
                            states.Add(state);
                        }
                    }

                    if (doorPressed == 0)
                    {
                        return ProbeOutcome.Unknown("no door was in reach to press");
                    }

                    return states.Any(s => s.Guid == doorPressed && s.Open)
                        ? ProbeOutcome.Pass($"door {doorPressed} answered with 0f 0a open=true ({states.Count} door state update(s) in the session)")
                        : ProbeOutcome.Fail($"pressed door {doorPressed}; door states seen: "
                            + (states.Count == 0 ? "none" : string.Join(", ", states.Select(s => $"{s.Guid}:{(s.Open ? "open" : "closed")}"))));
                })
            .Probe(sheet, "D7", "every door record carries the physics flag +0x1b1 & 0x20, and nothing else does",
                "docs/68 §2d / §3 R1 — the ONE wire-reachable switch that reaches the client's physics-body creator "
                + "(FUN_140c51c90 → entity+0x37e5 |= 0x40 → FUN_141fea440 → FUN_141fed340). docs/68 §1 read 00 here on "
                + "all 823 records of the owner's capture, which is why nothing this server spawns has ever been solid. "
                + "docs/79 §4 E5–E8 put it on the wire; this is docs/68 F2 done in the harness rather than by hand",
                c =>
                {
                    var doorFlags = new List<byte>();
                    var itemFlags = new List<byte>();
                    foreach (byte[] payload in c.Ledger.Payloads(VerificationPackets.AddLightweightNpc))
                    {
                        byte flags = VerificationPackets.SpawnFlagsOf(payload);
                        if (VerificationPackets.DoorIdOf(payload) != 0)
                        {
                            doorFlags.Add(flags);
                        }
                        else
                        {
                            itemFlags.Add(flags);
                        }
                    }

                    if (doorFlags.Count == 0)
                    {
                        return ProbeOutcome.Unknown("no door was streamed in, so there is no flag byte to read");
                    }

                    int doorsWithout = doorFlags.Count(f => (f & 0x20) == 0);
                    int itemsWith = itemFlags.Count(f => f != 0);
                    string seen = $"{doorFlags.Count} door record(s) flags {{{string.Join(", ", doorFlags.Select(f => $"0x{f:x2}").Distinct())}}}, "
                        + $"{itemFlags.Count} non-door record(s) flags {{{string.Join(", ", itemFlags.Select(f => $"0x{f:x2}").Distinct())}}}";

                    // A ground item with a physics body is an obstacle you cannot step over
                    // (docs/68 §5 F1 open question 4), so "loot is still 0x00" is half the assertion.
                    return doorsWithout == 0 && itemsWith == 0
                        ? ProbeOutcome.Pass($"every door asks for a physics body and no ground object does — {seen}")
                        : ProbeOutcome.Fail($"{doorsWithout} door(s) without 0x20 and {itemsWith} non-door(s) with a "
                            + $"non-zero flag byte — {seen}");
                })
            .Probe(sheet, "D8", "a held [F] does not slam the door shut inside its own swing",
                "docs/79 §2 — the August client re-evaluates its interaction target at a median 171 ms "
                + "(logs/host-20260830-163007.log) and the owner's Z1 server measured the same ≈165 ms re-fire of a "
                + "HELD key at 1087. The press window is 800 ms, the client's own 785 ms swing (docs/42 §6b) rounded "
                + "up, so the second press 200 ms later must be ABSORBED — no second 0f 0a",
                c =>
                {
                    if (doorPressed == 0 || !doorRePressed)
                    {
                        return ProbeOutcome.Unknown("no door was in reach to press twice");
                    }

                    var forThisDoor = new List<bool>();
                    foreach (byte[] payload in c.Ledger.Payloads(
                        VerificationPackets.CharacterBase, sub8: VerificationPackets.DoorStateUpdateSub))
                    {
                        if (VerificationPackets.TryReadDoorState(payload) is { } state && state.DoorGuid == doorPressed)
                        {
                            forThisDoor.Add(state.Open);
                        }
                    }

                    return forThisDoor.Count switch
                    {
                        0 => ProbeOutcome.Fail($"door {doorPressed} was pressed twice and answered with no 0f 0a at all"),
                        1 => ProbeOutcome.Pass($"two presses 200 ms apart on door {doorPressed} → exactly one 0f 0a "
                            + $"(open={forThisDoor[0]}); the re-fire was absorbed"),
                        _ => ProbeOutcome.Fail($"door {doorPressed} answered {forThisDoor.Count} times to two presses "
                            + $"200 ms apart ({string.Join(", ", forThisDoor.Select(o => o ? "open" : "closed"))}) — the "
                            + "press window is letting a held key toggle the door inside its own swing"),
                    };
                })
            .Probe(sheet, "D9", "the [F] caption is refreshed on the tick the door toggles",
                "docs/79 §4 E10 — the client's prompt driver FUN_14140bcd0 polls at most once a second, so without a "
                + "push the label still reads 'Open' on a door that is now open. The owner's Z1 proximity sweep is NOT "
                + "portable (FUN_14129e7c0 drops a reply whose guid is not the UI's current target, docs/47 §4d); the "
                + "moment of a toggle is the one instant the target is provably bound",
                c =>
                {
                    if (doorPressed == 0)
                    {
                        return ProbeOutcome.Unknown("no door was in reach to press");
                    }

                    var ids = new List<uint>();
                    foreach (byte[] payload in c.Ledger.Payloads(
                        VerificationPackets.CommandBase, sub16: VerificationPackets.InteractionStringSub))
                    {
                        if (payload.Length >= 15 && BitConverter.ToUInt64(payload.AsSpan(3, 8)) == doorPressed)
                        {
                            ids.Add(BitConverter.ToUInt32(payload.AsSpan(11, 4)));
                        }
                    }

                    // 12416 = "[F] Open", 8922 = "[F] Close Door" — both re-derived from the AUGUST
                    // locale by inverting the Global.Text.%d hash (docs/47 §4e), never Z1's id 78.
                    return ids.Contains(8922u)
                        ? ProbeOutcome.Pass($"door {doorPressed} prompt ids in order: "
                            + $"{string.Join(" → ", ids)} — the toggle pushed the 'Close Door' caption")
                        : ProbeOutcome.Fail($"door {doorPressed} was toggled but no 09 2d carrying 8922 followed; "
                            + $"ids seen: {(ids.Count == 0 ? "none" : string.Join(" → ", ids))}");
                })
            .Probe(sheet, "D5", "door collision — does the door block the player?",
                "docs/55 §0 finding 1 — there is NO collision field, flag or component anywhere in ClientProtocol_1148; "
                + "collision comes from the actor definition and from nothing else",
                _ => ProbeOutcome.NotTestable(
                    "collision is client-side physics. A wire harness can prove the server names a colliding, kinematic "
                    + "actor (probe D2) and sends positionUpdateType 0 (docs/55 §2, the collidable value); whether the "
                    + "August client then acquires collision depends on the model-bind ordering hazard of docs/55 §2c, "
                    + "which is observable only by walking into a door"))
            .Probe(sheet, "W1", "worn items: a picked-up wearable is attached with a real mesh",
                "docs/54 B1 — every SetCharacterEquipmentWithSlots in the owner's play-test carried '5 meshes', the "
                + "unchanged starter outfit, after 1, 2, 3, 4 and 5 pickups, because MeshFor returned an empty string. "
                + "The mesh lives in the ATTACHMENT list, not in the equipment-slot row",
                c =>
                {
                    IReadOnlyList<byte[]> equipment = c.Ledger.Payloads(VerificationPackets.EquipmentBase, sub8: 0x01);
                    if (equipment.Count == 0)
                    {
                        return ProbeOutcome.Fail("no SetCharacterEquipment (94 01) was sent at all");
                    }

                    EquipmentPacket? first = VerificationPackets.TryReadEquipment(equipment[0]);
                    EquipmentPacket? last = VerificationPackets.TryReadEquipment(equipment[^1]);
                    if (first is null || last is null)
                    {
                        return ProbeOutcome.Unknown("the 94 01 body did not decode");
                    }

                    if (grants.Count == 0 || last.Slots.Count == 0)
                    {
                        return ProbeOutcome.Unknown(
                            $"nothing was equipped; the dress is {last.Attachments.Count} mesh(es), "
                            + $"{last.Slots.Count} slot row(s)");
                    }

                    uint[] bound = [.. last.Slots.Select(r => r.SlotId).Distinct().Order()];
                    uint[] withMesh =
                    [
                        .. bound.Where(slot => last.Attachments.Any(a => a.SlotId == slot && a.ModelName.Length > 0)),
                    ];
                    uint[] without = [.. bound.Except(withMesh)];
                    string meshes = string.Join(", ", last.Attachments.Select(a => $"slot {a.SlotId}='{a.ModelName}'"
                        + (a.ShaderParameterGroupId == 0 ? string.Empty : $" shader {a.ShaderParameterGroupId}")));
                    string summary = $"{grants.Count} grant(s) → body slot(s) [{string.Join(",", bound)}]; the dress went "
                        + $"from {first.Attachments.Count} to {last.Attachments.Count} mesh(es): {meshes}";

                    return without.Length == 0
                        ? ProbeOutcome.Pass(summary)
                        : ProbeOutcome.Fail($"{summary} — body slot(s) [{string.Join(",", without)}] carry an equipment "
                            + "row with NO attachment mesh, which is the docs/54 B1 symptom for those slots");
                })
            .Probe(sheet, "W2", "worn items: does the attachment render?",
                "docs/54 B3 — worn colour lives only in the shader parameter group, and the mesh is resolved from the "
                + "appearance table's ModelId column",
                _ => ProbeOutcome.NotTestable(
                    "whether a named mesh appears on the character model, and in the right colourway, is the client's "
                    + "renderer. The harness can prove the row names a mesh that ships (probe W1 + T1); it cannot see "
                    + "the character"))
            .Probe(sheet, "I1", "inventory: the container model reaches the client",
                "docs/41 §I1(b) / docs/63 — InitContainers (c8 0002) once per match, the loadout (86 03/04) and the "
                + "bag it carries; without them a granted item is 'in the count but in no panel'",
                c =>
                {
                    int init = c.Ledger.Count(VerificationPackets.ContainerBase, 0x02);
                    int loadout3 = c.Ledger.Count(VerificationPackets.LoadoutsBase, 0x03);
                    int loadout4 = c.Ledger.Count(VerificationPackets.LoadoutsBase, 0x04);
                    return init > 0 && (loadout3 + loadout4) > 0
                        ? ProbeOutcome.Pass($"InitContainers x{init}, SetCurrentLoadout x{loadout3}, SetLoadoutSlots x{loadout4}")
                        : ProbeOutcome.Fail($"InitContainers x{init}, SetCurrentLoadout x{loadout3}, SetLoadoutSlots x{loadout4}");
                })
            .Probe(sheet, "I2", "inventory: a grant is auto-assigned to a real slot of a real container",
                "docs/41 §0 — a container guid that names no container is exactly the 'in the count, in no panel' bug; "
                + "docs/63 §2 is the slot rule, checked here against the client's own LoadoutSlotItemClasses shape",
                c =>
                {
                    if (grants.Count == 0)
                    {
                        return ProbeOutcome.Unknown("nothing was granted in this session");
                    }

                    const ulong Equipped = 0xFFFF_FFFF_FFFF_FFFFUL;
                    ItemGrant[] homeless = [.. grants.Where(g => g.ContainerGuid == 0 || g.SlotId == 0)];
                    ItemGrant[] equipped = [.. grants.Where(g => g.ContainerGuid == Equipped)];
                    ulong[] bags = [.. grants.Select(g => g.ContainerGuid).Where(g => g is not (0 or Equipped)).Distinct()];
                    string sample = string.Join("; ", grants.Take(4).Select(g =>
                        $"def {g.DefinitionId} → container {(g.ContainerGuid == Equipped ? "EQUIPPED" : g.ContainerGuid.ToString())} "
                        + $"slot {g.SlotId}"));
                    string shape = $"{grants.Count} grant(s): {equipped.Length} carry the equipped sentinel "
                        + "0xFFFFFFFFFFFFFFFF (docs/46 §3 — the client's own 'worn or wielded' key), "
                        + $"{grants.Count - equipped.Length} carry a bag guid ({string.Join(",", bags)}); {sample}";
                    return homeless.Length == 0
                        ? ProbeOutcome.Pass(shape)
                        : ProbeOutcome.Fail($"{homeless.Length} of {grants.Count} grant(s) landed in container 0 or slot 0. {shape}");
                })
            .Probe(sheet, "I3", "inventory: the panel and the loadout are repainted after a pickup",
                "docs/41 §I1 — the loadout binding (86 05) and the container repaint (c8 0006) are what move the row "
                + "into the panel the player is looking at",
                c =>
                {
                    int bind = c.Ledger.Count(VerificationPackets.LoadoutsBase, 0x05);
                    int repaint = c.Ledger.Count(VerificationPackets.ContainerBase, 0x06);
                    int panels = c.Ledger.ProximateItemLists;
                    return grants.Count == 0
                        ? ProbeOutcome.Unknown("nothing was granted, so nothing had to be repainted")
                        : bind + repaint > 0
                            ? ProbeOutcome.Pass($"SetLoadoutSlot x{bind}, UpdateContainer x{repaint}, ProximateItems republished x{panels}")
                            : ProbeOutcome.Fail($"after {grants.Count} grant(s): SetLoadoutSlot x{bind}, UpdateContainer x{repaint}");
                })
            .Probe(sheet, "I4", "inventory: bulk, stacking and the drag verbs",
                "docs/63 §0 — Items.RequestUseItem (ac 2c) has been arriving unanswered since wave 2; §5 names the gaps",
                _ => ProbeOutcome.NotTestable(
                    "bulk and stacking are read out of the container repaint's own fields, and the drag verbs are "
                    + "answered only when a player drags. The harness has no recorded ac 2c payload that names an item "
                    + "guid from THIS session (the 53 recorded ones name items that no longer exist), so the verb "
                    + "cannot be replayed honestly and the panel arithmetic cannot be seen from outside the client"))
            .Probe(sheet, "C1", "crafting: the recipe list reaches the client in the match",
                "docs/62 §1, D34 — 0x26 09 Recipe.List after the in-match ClientIsReady, never inside the zoning burst "
                + "(regression guard 4 pins that opcode order)",
                c =>
                {
                    IReadOnlyList<byte[]> lists = c.Ledger.Payloads(
                        VerificationPackets.RecipeBase, sub8: VerificationPackets.RecipeListSub);
                    if (lists.Count == 0)
                    {
                        return ProbeOutcome.Fail("no 0x26 09 Recipe.List arrived; the crafting tab is empty");
                    }

                    TimeSpan? ready = c.Milestones.At(HarnessMilestone.ZoningClientIsReadySent);
                    TimeSpan? at = c.Ledger.FirstAt(VerificationPackets.RecipeBase);
                    return ProbeOutcome.Pass(
                        $"{lists.Count} Recipe.List, {lists[0].Length + 1} B, first at {at?.TotalSeconds:F3}s "
                        + $"(ClientIsReady (Zoning) was {ready?.TotalSeconds:F3}s)");
                })
            .Probe(sheet, "C2", "crafting: the recipes inside the self record (offset 0x11a)",
                "docs/62 §3 / D289 — the six records now ride in the self record's 0x11a list by default; "
                + "DIAG-recipes-vehicles §A proved this is the ONLY delivery that populates the crafting WINDOW",
                c => ProbeOutcome.NotTestable(
                    "the running host now reports 'crafting: recipeList=ON, selfRecord=ON' (D289), so the 0x11a list "
                    + "IS on the wire — but whether the client's Scaleform panel rendered its rows is a datasource "
                    + "rebuild inside FUN_141513580 that no replay can see from outside the client. "
                    + $"(The self record this session received was {SelfRecordLength(c)} B; empty it is 839.)"))
            .Probe(sheet, "C3", "crafting: the server answers a craft request",
                "docs/62 §6 — 09 1a Command.RecipeStart, consume then grant then the packet plan",
                c =>
                {
                    int status = c.Ledger.Count(VerificationPackets.RecipeBase, 0x0A);
                    int add = c.Ledger.Count(VerificationPackets.RecipeBase, 0x01);
                    int componentUpdate = c.Ledger.Count(VerificationPackets.RecipeBase, 0x02);
                    return status + add + componentUpdate > 0
                        ? ProbeOutcome.Pass($"the craft drew RecipeBase replies: status x{status}, add x{add}, "
                            + $"componentUpdate x{componentUpdate} — the ANSWER path works; whether a craft that actually "
                            + "holds its ingredients grants the output was NOT exercised, because the character carried "
                            + "only what it had picked up "
                            + "— NOTE the request was SYNTHESISED; no capture contains a 09 1a, so this proves the "
                            + "server answers that shape, not that the client sends it")
                        : ProbeOutcome.Unknown(
                            $"the synthesised 09 1a drew no new RecipeBase traffic (status x{status}, add x{add}); the "
                            + "server refuses a craft whose ingredients the character does not hold (docs/62 §5), which "
                            + "is the expected outcome for a character carrying only the starter outfit");
                })
            .Probe(sheet, "V1", "vehicles: the map's parked fleet is streamed in",
                "docs/43 / docs/61 §2 — the fleet is 300 cars on the map's own parking anchors, streamed on the shared "
                + "world pump within 250 m of the player",
                c =>
                {
                    IReadOnlyList<LightweightEntity> parked = [.. c.Ledger.Vehicles.Skip(1)];
                    if (parked.Count == 0)
                    {
                        return ProbeOutcome.Fail(
                            $"only {c.Ledger.Vehicles.Count} AddLightweightVehicle (the parachute) arrived; no car was streamed");
                    }

                    ClientAssets assets = ClientAssets.Current;
                    string models = string.Join(", ", parked.Select(v => v.ModelId).Distinct().Order()
                        .Select(m => $"{m}={assets.ModelFile(m) ?? "?"}"));
                    float furthest = parked.Max(v => ServerLedger.HorizontalDistance(landingCentroid, v.Position));
                    return ProbeOutcome.Pass($"{parked.Count} car(s) spawned within {furthest:F0} m of the landing; models [{models}]");
                })
            .Probe(sheet, "V2", "vehicles: an E-press produces a mount",
                "docs/43 blocker 1 / docs/61 §5 — which packet an E-press produces was never settled; both arms are wired",
                c =>
                {
                    if (vehiclePressed == 0)
                    {
                        return ProbeOutcome.Unknown("no parked car was in reach to press");
                    }

                    int mountResponses = c.Ledger.Count(VerificationPackets.MountBase, 0x02);
                    int owner = c.Ledger.Count(VerificationPackets.VehicleBase, 0x01);
                    int occupy = c.Ledger.Count(VerificationPackets.VehicleBase, 0x02);
                    string arms = $"the recorded [F] pair drew {mountAfterPress} MountResponse(s); the synthesised 70 01 "
                        + $"two seconds later drew {mountAfterRequest}";
                    return mountAfterPress > 0
                        ? ProbeOutcome.Pass($"car {vehiclePressed}: {arms} — so docs/43 blocker 1 is answered from RECORDED "
                            + "client bytes alone. Session totals: MountResponse x" + mountResponses
                            + $", Vehicle.Occupy x{occupy}, Vehicle.Owner x{owner} (the parachute accounts for one of each)")
                        : mountAfterRequest > 0
                            ? ProbeOutcome.Fail($"car {vehiclePressed}: {arms} — only the SYNTHESISED 70 01 was answered, and "
                                + "no client has ever been recorded sending one, so an E-press would do nothing")
                            : ProbeOutcome.Fail($"car {vehiclePressed}: {arms} — neither arm produced a mount");
                })
            .Probe(sheet, "V3", "vehicles: the 0x78 bystander relay",
                "docs/61 §1 — the relay is the driver's own bytes with byte 0 changed, fanned out to everyone else in range",
                c => ProbeOutcome.NotTestable(
                    $"the relay only ever sends to OTHER players, and this lane runs one client against a one-character "
                    + $"roster: {c.Ledger.Count(VerificationPackets.PlayerUpdatePosition)} 0x78 packets arrived, which is "
                    + "the correct answer for a solo match and no evidence either way. Two simultaneous clients in one "
                    + "match are what would test it, and the server's roster has one character"))
            .Probe(sheet, "V4", "vehicles: fuel",
                "docs/61 §3 — burn rate, gauge policy and refuel",
                _ => ProbeOutcome.NotTestable(
                    "the running host reports 'fuel gauge on, fuel burn off' in its own startup banner: with burning off "
                    + "there is no drain to measure, and turning it on needs a host restart (CRANBERRY_VEHICLE_FUEL_BURN=1). "
                    + "The gauge itself is a resource row that only goes out to a seated driver, which probe V2 gates on"))
            .Probe(sheet, "M1", "movement: the stat burst reaches the client in the match",
                "docs/40 §3 — 18 speed/blend entries plus the base-speed 11 05; without them the six modes all move at "
                + "the client's built-in default, which is the owner's complaint",
                c =>
                {
                    IReadOnlyList<byte[]> bursts = c.Ledger.Payloads(
                        VerificationPackets.CharacterBase, sub8: VerificationPackets.UpdateStatSub);
                    int baseSpeed = c.Ledger.Count(VerificationPackets.ClientUpdateBase, 0x05);
                    if (bursts.Count == 0)
                    {
                        return ProbeOutcome.Fail("no Character.UpdateStat (0f 40) arrived at all");
                    }

                    (ulong Guid, uint Entries)? decoded = VerificationPackets.TryReadStatBurst(bursts[0]);
                    return decoded is { Entries: >= 18 } && baseSpeed > 0
                        ? ProbeOutcome.Pass($"{bursts.Count} stat burst(s), {decoded.Value.Entries} entries for guid "
                            + $"{decoded.Value.Guid}, ClientUpdate 11 05 x{baseSpeed}")
                        : ProbeOutcome.Fail($"{bursts.Count} burst(s) with {decoded?.Entries.ToString() ?? "?"} entries, 11 05 x{baseSpeed}");
                })
            .Probe(sheet, "M2", "movement: are the speeds the ones the client obeys?",
                "docs/40 §4.1 — the client ships no speed table at all, so the server chooses every value; "
                + "since docs/76 those values are the owner's own Z1 numbers",
                _ => ProbeOutcome.NotTestable(
                    "nothing client-originated ever acknowledges a stat. The only client evidence is the speed the "
                    + "client's own channel-2 stream reports, and the harness cannot generate one: every channel-2 byte "
                    + "it sends is a real client's, recorded in another session at another speed. The host's own "
                    + "observer prints the comparison when the OWNER moves (host-20260830-131819.log 13:20:41.634, "
                    + "'observed 6.6 m/s in stance Sprinting — profile predicts 6.60 — MATCH' — that is the wave-5 "
                    + "profile; after docs/76 the same line should read 5.7 against a predicted 5.74)"))
            .Probe(sheet, "G1", "weapons: with the stages off, an ItemAdd is the wave-5 packet",
                "docs/60 — the shipped wire is wave 5's byte for byte; the 68-byte Weapon tail and the narrowed slot-7 "
                + "guard are opt-in behind CRANBERRY_WEAPON_TAIL / CRANBERRY_WIELD",
                c =>
                {
                    if (grants.Count == 0)
                    {
                        return ProbeOutcome.Unknown("nothing was granted in this session");
                    }

                    ClientAssets assets = ClientAssets.Current;
                    ItemGrant[] weapons = [.. grants.Where(g => assets.Item(g.DefinitionId)?.IsWeaponClass == true)];
                    ItemGrant[] wrong = [.. grants.Where(g => g.BlobLength != ItemGrant.Wave5BlobLength)];
                    string shape = $"{grants.Count} grant(s), blob lengths "
                        + $"{string.Join(",", grants.Select(g => g.BlobLength).Distinct().Order())} B; "
                        + $"{weapons.Length} of them Weapon-class by the client's own CODE_FACTORY_NAME";
                    return wrong.Length == 0
                        ? weapons.Length > 0
                            ? ProbeOutcome.Pass($"{shape} — every one is the 63-byte wave-5 blob (62 B record + the "
                                + "1-byte Generic tail), weapons included")
                            : ProbeOutcome.Pass($"{shape} — every blob is the 63-byte wave-5 shape, but NO weapon was "
                                + "granted, so the weapon-specific half of the claim is untested this run")
                        : ProbeOutcome.Fail($"{shape}; {wrong.Length} grant(s) are not 63 B: "
                            + string.Join(", ", wrong.Select(g => $"def {g.DefinitionId}={g.BlobLength} B")));
                })
            .Probe(sheet, "G2", "weapons: no WeaponDefinitions ReferenceData is on the wire",
                "docs/60's own correction — a parse failure in FUN_140b055c0 is a store to address 0, and the send site "
                + "is the login bootstrap; the stage ships off",
                c =>
                {
                    string[] tables = [.. c.Ledger.ReferenceData.Keys.Order()];
                    return tables.Contains("WeaponDefinitions")
                        ? ProbeOutcome.Fail("a WeaponDefinitions table went out although the host reports definitions=off")
                        : ProbeOutcome.Pass($"ReferenceData tables sent: [{string.Join(", ", tables)}] — no WeaponDefinitions, "
                            + "as the host's banner says");
                })
            .Probe(sheet, "G3", "weapons: fire groups, recoil and a shot",
                "docs/58 / docs/60 — the c2s fire layout has never been derived because slot 7 has always carried "
                + "Weapon_Empty.adr in all 96 captured sessions",
                _ => ProbeOutcome.NotTestable(
                    "no capture contains a single 0x82 WeaponBase packet, so the harness has no recorded client bytes to "
                    + "fire with, and the host runs wielding=off so nothing would be in hand to fire. This is the one "
                    + "feature whose derivation still needs the owner: one rifle in hand, one trigger press, one capture"))
            .Probe(sheet, "R1", "the random drop: where did this match drop?",
                "docs/48 §5.1 — every match draws one of the 92 named places from the client's own z2-loot-spawns.bin, "
                + "weighted by marker count, from the KotK.SkySpawn altitude of 850 m",
                c => c.Ledger.Vehicles.Count == 0
                    ? ProbeOutcome.Unknown("no parachute, so no air spawn to read")
                    : ProbeOutcome.Pass($"air spawn {c.Ledger.Vehicles[0].Position} (the parachute's own 0xd7 position); "
                        + "variety is a cross-match question — see the R2 row"))

            // ---- the walk, and what the streamer did about it ----
            .Do("walk the recorded route for 100 s", (c, token) => c.Clock.DelayAsync(TimeSpan.FromSeconds(100), token))
            .Probe(sheet, "L3", "loot streams into the buildings the player walks to",
                "docs/52 — RealGroundLootArmed was a one-way latch, so the landing burst was the only loot a match ever "
                + "saw; the owner's report was 'no matter what building I went into there was no loot'",
                c =>
                {
                    IReadOnlyList<LightweightEntity> fresh = c.Ledger.NpcsSince(landingMark);
                    if (fresh.Count == 0)
                    {
                        return ProbeOutcome.Fail(
                            $"no AddLightweightNpc at all in the {c.Ledger.Since(landingMark).Count} message(s) sent "
                            + "during the walk — this is the empty-buildings regression");
                    }

                    float furthest = fresh.Max(e => ServerLedger.HorizontalDistance(landingCentroid, e.Position));
                    return ProbeOutcome.Pass(
                        $"{fresh.Count} more object(s) during the walk, furthest {furthest:F1} m from the landing "
                        + $"centroid; {c.Ledger.RemovedObjects.Count} eviction(s), {c.Ledger.ProximateItemLists} panel republish(es)");
                })
            .Probe(sheet, "L4", "the streamer evicts what the player left behind",
                "docs/52 — eviction past 90 m with a 13-byte 0f 01 is what keeps the live set under MaxLive (128)",
                c => c.Ledger.RemovedObjects.Count > 1
                    ? ProbeOutcome.Pass($"{c.Ledger.RemovedObjects.Count} Character.RemovePlayer in the session")
                    : ProbeOutcome.Fail($"only {c.Ledger.RemovedObjects.Count} removal(s); the live set can only grow"))
            .Probe(sheet, "D6", "every door the walk streamed in is a kinematic collision twin",
                "docs/55 §I1 — the host's own door pump counts 'N live, M kinematic'; the run lane's question is whether "
                + "M ever falls short of N across a whole walk, not just at the landing",
                c =>
                {
                    ClientAssets assets = ClientAssets.Current;
                    uint[] doorModels = [.. WorldObjects(c).Where(o => o.DoorId != 0).Select(o => o.Entity.ModelId).Distinct().Order()];
                    var bad = new List<string>();
                    var good = new List<string>();
                    foreach (uint model in doorModels)
                    {
                        string? file = assets.ModelFile(model);
                        string? text = file is null ? null : ActorText(assets, file);
                        if (text is not null && text.Contains("createAsKinematic=\"1\"", StringComparison.Ordinal))
                        {
                            good.Add($"{model}={file}");
                        }
                        else
                        {
                            bad.Add($"{model}={file ?? "?"}"
                                + (text is null ? " (no .adr on disk)"
                                    : text.Contains("<CollisionData", StringComparison.Ordinal) ? " (collides, NOT kinematic)"
                                    : " (NO CollisionData at all)"));
                        }
                    }

                    return bad.Count == 0
                        ? ProbeOutcome.Pass($"{doorModels.Length} door mesh(es) over the whole walk, all kinematic: {string.Join(", ", good)}")
                        : ProbeOutcome.Fail($"{bad.Count} of {doorModels.Length} door mesh(es) are not the kinematic twin: "
                            + $"{string.Join("; ", bad)}"
                            + (good.Count == 0 ? string.Empty : $" (kinematic: {string.Join(", ", good)})"));
                })
            .Read("sweep", c =>
                $"{c.Ledger.Zone.Count} server message(s); {c.Ledger.Npcs.Count} world objects, "
                + $"{c.Ledger.Vehicles.Count} vehicles, {c.Ledger.ItemAdds} grants, "
                + $"{c.Ledger.RemovedObjects.Count} removals");
    }

    // ---- V2: gas pacing -----------------------------------------------------------------------

    /// <summary>
    /// <b>V2 — the gas, measured rather than read out of a settings file.</b>
    ///
    /// <para>The wall the player sees is the <c>ce 01</c> ring, re-sent every 500 ms with the
    /// server's own interpolated radius (docs/18 §1b). So the speed the owner complained about is
    /// directly measurable from the wire: sample the rings, difference them, and add the drift of
    /// the centre, because a wall closing on a stationary player moves at both.</para>
    ///
    /// <para>The scenario stays in the match for five and a half minutes of match clock, which
    /// covers the phase-1 reveal at 2:00 and a minute of the shrink that starts at 4:30. It cannot
    /// cover the whole 24:50 ladder, and says so.</para>
    /// </summary>
    public static Scenario V2GasPacing(ProbeSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var rings = new List<(TimeSpan At, GasRing Ring)>();

        return Scenario.Named("V2 gas pacing — measured off the ce 01 stream for five and a half minutes")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E2")
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(30), after: "the second transfer request")
            .Expect("F1")
            .Expect("F3")
            .Expect(HarnessMilestone.TeleportStartReceived, TimeSpan.FromSeconds(120), after: "entering the world")
            .Expect("G1")
            .Expect("G3")
            .RequireEventually(
                "the server sent StartMatch (ce 0016), which is when the gas clock starts",
                c => c.Ledger.FirstAt(Ce, VerificationPackets.StartMatchSub) is not null,
                TimeSpan.FromSeconds(90),
                "docs/53 — the match clock is measured from the ce 16 StartMatch send")
            .Do("ride the chute and watch the ring for 5 min 30 s of match clock",
                (c, token) => c.Clock.DelayAsync(TimeSpan.FromSeconds(330), token))
            .Do("decode the ring stream", c =>
            {
                rings.Clear();
                IReadOnlyList<ServerRecord> records = c.Ledger.Zone;
                var payloads = new Queue<byte[]>(c.Ledger.Payloads(Ce, sub16: VerificationPackets.GasRingSub));
                foreach (ServerRecord record in records)
                {
                    if (record.Opcode != Ce || record.Sub16 != VerificationPackets.GasRingSub || payloads.Count == 0)
                    {
                        continue;
                    }

                    byte[] payload = payloads.Dequeue();
                    if (VerificationPackets.TryReadGasRing(payload) is { } ring)
                    {
                        rings.Add((record.At, ring));
                    }
                }

                c.Notes.Add($"gas: {rings.Count} ce 01 ring(s) decoded");
            })
            .Probe(sheet, "S1", "no gas is drawn until the gas starts moving",
                "docs/77 §6 — GasPreMoveRing.None, the owner's own click-test ruling: no ce 01 at all until phase 1's "
                + "ring starts travelling at 4:30, and the first one that does arrive is the play-area boundary. This "
                + "probe INVERTS the wave-5 rule it replaces, which expected a boundary ring from StartMatch",
                c =>
                {
                    TimeSpan? start = c.Ledger.FirstAt(Ce, VerificationPackets.StartMatchSub);
                    if (start is null)
                    {
                        return ProbeOutcome.Unknown("StartMatch never arrived");
                    }

                    if (rings.Count == 0)
                    {
                        return ProbeOutcome.Fail(
                            "no ce 01 ring was ever sent, not even after the first movement; the client has no gas to draw");
                    }

                    (TimeSpan At, GasRing Ring) first = rings[0];
                    double at = (first.At - start.Value).TotalSeconds;
                    if (at < FirstMovementDelay.TotalSeconds - 5)
                    {
                        return ProbeOutcome.Fail(
                            $"a ce 01 arrived at StartMatch +{at:F1} s, r={first.Ring.Radius:F1} m — the gas is on the map "
                            + $"{FirstMovementDelay.TotalSeconds - at:F0} s before it starts moving, which is the owner's own report");
                    }

                    return Math.Abs(first.Ring.Radius - InitialRadius) < 25f
                        ? ProbeOutcome.Pass($"first ring at StartMatch +{at:F1} s, r={first.Ring.Radius:F1} m at "
                            + $"{first.Ring.Centre}, blend {first.Ring.BlendMs} ms, {rings.Count} rings in the window")
                        : ProbeOutcome.Fail($"the first ring is r={first.Ring.Radius:F1} m, not the {InitialRadius:F0} m play area");
                })
            .Probe(sheet, "S2", "the first phase is revealed two minutes into the match",
                "docs/53 §5.4 / the host's own schedule — reveal 2:00, first move 4:30, match 24:50",
                c =>
                {
                    TimeSpan? start = c.Ledger.FirstAt(Ce, VerificationPackets.StartMatchSub);
                    TimeSpan? reveal = c.Ledger.FirstAt(Ce, VerificationPackets.GasSafeZoneSub);
                    if (start is null)
                    {
                        return ProbeOutcome.Unknown("StartMatch never arrived");
                    }

                    if (reveal is null)
                    {
                        return ProbeOutcome.Fail("no ce 02 safe-zone reveal in 5 min 30 s of match clock");
                    }

                    double delta = (reveal.Value - start.Value).TotalSeconds;
                    IReadOnlyList<byte[]> reveals = c.Ledger.Payloads(Ce, sub16: VerificationPackets.GasSafeZoneSub);
                    GasSafeZone? zone = reveals.Count == 0 ? null : VerificationPackets.TryReadGasSafeZone(reveals[0]);
                    string detail = $"ce 02 at StartMatch +{delta:F1} s, target r={zone?.Radius:F0} m at {zone?.Centre}";
                    return Math.Abs(delta - FirstRevealDelay.TotalSeconds) <= 5
                        ? ProbeOutcome.Pass(detail)
                        : ProbeOutcome.Fail($"{detail} — docs/53 promises {FirstRevealDelay.TotalSeconds:F0} s");
                })
            .Probe(sheet, "S3", "the wall is slower than a sprint",
                "docs/77 §4 — the ladder paces the RADIUS at 4.05 m/s and the centre walk is capped at 0.20 of each "
                + "radius drop, so the leading edge cannot exceed 4.88 m/s. This probe failed before wave 8 at 7.48 and "
                + "10.91 m/s in two consecutive matches",
                _ =>
                {
                    if (rings.Count < 3)
                    {
                        return ProbeOutcome.Unknown($"only {rings.Count} ring(s) decoded");
                    }

                    // The drawn circle both shrinks and walks, and a player standing on the side the
                    // centre is walking away from meets an edge closing at the SUM of the two rates.
                    // Reporting only the radius rate is what makes a 4.05 m/s ladder feel faster than
                    // it reads.
                    double worst = 0;
                    double worstAt = 0;
                    for (int i = 1; i < rings.Count; i++)
                    {
                        double seconds = (rings[i].At - rings[i - 1].At).TotalSeconds;
                        if (seconds <= 0.05)
                        {
                            continue;
                        }

                        float shrink = rings[i - 1].Ring.Radius - rings[i].Ring.Radius;
                        float drift = Vector3.Distance(rings[i - 1].Ring.Centre, rings[i].Ring.Centre);
                        double speed = (shrink + drift) / seconds;
                        if (speed > worst)
                        {
                            worst = speed;
                            worstAt = rings[i].At.TotalSeconds;
                        }
                    }

                    // The sustained rates over the moving part of the window, which is the number a
                    // player experiences; the per-sample maximum above is noise around it.
                    (TimeSpan At, GasRing Ring) firstMoving = rings.FirstOrDefault(r => r.Ring.Radius < InitialRadius - 0.5f);
                    (TimeSpan At, GasRing Ring) last = rings[^1];
                    double movingSeconds = firstMoving.Ring is null ? 0 : (last.At - firstMoving.At).TotalSeconds;
                    double shrinkRate = movingSeconds <= 0 ? 0 : (firstMoving.Ring!.Radius - last.Ring.Radius) / movingSeconds;
                    double driftRate = movingSeconds <= 0
                        ? 0
                        : Vector3.Distance(firstMoving.Ring!.Centre, last.Ring.Centre) / movingSeconds;

                    string detail = $"sustained {shrinkRate:F2} m/s of radius + {driftRate:F2} m/s of centre travel = "
                        + $"{shrinkRate + driftRate:F2} m/s at the leading edge over {movingSeconds:F0} s of movement "
                        + $"(per-sample peak {worst:F2} m/s at {worstAt:F0}s); the sprint is {SprintSpeed:F2} m/s. "
                        + $"Radius {rings[0].Ring.Radius:F0} → {last.Ring.Radius:F0} m, centre {rings[0].Ring.Centre} → "
                        + $"{last.Ring.Centre}";
                    // Graded against the analytic ceiling, not against the sprint: a wall a player
                    // can only just match is a wall that kills them on the first hill. A little
                    // slack for sampling noise on a 2 Hz stream.
                    return shrinkRate + driftRate <= GasEdgeCeiling + 0.2
                        ? ProbeOutcome.Pass(detail)
                        : ProbeOutcome.Fail($"{detail} — the leading edge is above the {GasEdgeCeiling:F2} m/s ceiling "
                            + "GasSettings.CentreDriftFraction is supposed to bound it to (docs/77 §4)");
                })
            .Probe(sheet, "S4", "the whole 24:50 ladder",
                "docs/77 §4.3 — ten phases, 4400 → 40 m, last circle closed at 24:50",
                _ => ProbeOutcome.NotTestable(
                    "a scenario cannot sit in one match for twenty-five minutes and still be a test; this run measures "
                    + "the opening boundary, the phase-1 reveal and the phase-1 shrink rate, which is the phase the "
                    + "docs/53 complaint was about. Phases 2-10 are the same code path with different constants and "
                    + "remain arithmetic, not observation"))
            .Read("gas", _ => rings.Count == 0
                ? "(no rings)"
                : $"{rings.Count} rings, r {rings[0].Ring.Radius:F0} → {rings[^1].Ring.Radius:F0} m, "
                    + $"centre {rings[0].Ring.Centre} → {rings[^1].Ring.Centre}");
    }

    // ---- V3: the second match -----------------------------------------------------------------

    /// <summary>
    /// <b>V3 — a second match on one link.</b> Wave 3 shipped a stat burst that was delivered once
    /// per <i>session</i> rather than once per match, so a player's second match silently reverted
    /// to the client's built-in speeds. Nothing in the repository could see that: the packet is
    /// correct, the log line is absent, and no test drives two matches.
    /// </summary>
    public static Scenario V3SecondMatch(ProbeSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        int firstMatchMark = 0;
        Vector3 firstDrop = Vector3.Zero;
        bool secondZoning = false;
        bool secondReady = false;

        return Scenario.Named("V3 a second match on one link — the wave-3 stat-burst reset, and a second drop point")
            .Connect()
            .Expect("C4")
            .Expect("C6")
            .Expect("D1")
            .Do("PLAY", c => c.ClickPlay())
            .Expect("E2")
            .Expect(HarnessMilestone.ZoningBegun, TimeSpan.FromSeconds(30), after: "the second transfer request")
            .Expect("F1")
            .Expect("F3")
            .Expect(HarnessMilestone.TeleportStartReceived, TimeSpan.FromSeconds(120), after: "entering the world")
            .Expect("G1")
            .Expect("G3")
            .RequireEventually(
                "the first match reached StartMatch and a parachute",
                c => c.Ledger.Vehicles.Count > 0,
                TimeSpan.FromSeconds(60),
                "docs/48 — the drop is what names the place this match landed on")
            // The drop handshake has to be COMPLETE before the BACK, not merely started. The first
            // live reading of this scenario pressed it while SynchronizedTeleport.ClientReady was
            // still in flight, and the finding that fell out is written up in the return of this
            // wave: the release path sets Match = InMatch unconditionally, so an ack that lands
            // after AbandonMatch resurrects the match and every later PLAY is dropped on the floor.
            .Do("let the drop settle", (c, token) => c.Clock.DelayAsync(TimeSpan.FromSeconds(6), token))
            .Do("note the first match", c =>
            {
                firstDrop = c.Ledger.Vehicles[0].Position;
                firstMatchMark = c.Ledger.Mark();
                c.Notes.Add($"match 1 air spawn {firstDrop}");
            })
            .Do("BACK out of the match", c => c.PressBack())
            .Do("the pause a player takes at the menu", (c, token) => c.Clock.DelayAsync(TimeSpan.FromSeconds(5), token))
            .Probe(sheet, "X1", "BACK tears the match down",
                "ZoneService.AbandonMatch — the gateway link outlives the BACK button, so every armed timer and the gas "
                + "pump have to be torn down by hand or they keep pushing StartMatch at a client in character select",
                c =>
                {
                    int logout = c.Ledger.Payloads(VerificationPackets.ClientUpdateBase, sub16: 0x0030).Count;
                    return logout > 0
                        ? ProbeOutcome.Pass($"CompleteLogoutProcess x{logout}; "
                            + $"{c.Ledger.Since(firstMatchMark).Count} message(s) since the first match's drop")
                        : ProbeOutcome.Fail("the BACK drew no ClientUpdate.CompleteLogoutProcess (11 0030)");
                })
            .Do("PLAY again", c => c.ClickPlay())
            .Try("wait up to 40 s for a second ClientBeginZoning", async (c, token) =>
            {
                TimeSpan deadline = c.Clock.Now + TimeSpan.FromSeconds(40);
                while (c.Clock.Now < deadline)
                {
                    if (c.Milestones.Count(HarnessMilestone.ZoningBegun) >= 2)
                    {
                        secondZoning = true;
                        break;
                    }

                    await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                }

                if (!secondZoning)
                {
                    return;
                }

                TimeSpan readyBy = c.Clock.Now + TimeSpan.FromSeconds(10);
                while (c.Clock.Now < readyBy)
                {
                    if (c.Milestones.Count(HarnessMilestone.ZoningClientIsReadySent) >= 2)
                    {
                        secondReady = true;
                        break;
                    }

                    await c.Clock.DelayAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                }

                await c.Clock.DelayAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            })
            .Probe(sheet, "X2", "a second PLAY starts a second match on the same link",
                "ZoneService's PlayerWorldTransferRequest handler answers Menu and Queued and silently drops every "
                + "other state; AbandonMatch is what is supposed to put the session back in Menu",
                c => secondZoning
                    ? ProbeOutcome.Pass($"the second PLAY drew a second ClientBeginZoning and the client answered "
                        + $"ClientIsReady {(secondReady ? "again" : "NOT again")}")
                    : ProbeOutcome.Fail(
                        "the second PLAY was never answered: no second ClientBeginZoning in 40 s. The client sent its "
                        + "PlayerWorldTransferRequest pair and the server dropped both, so the player is stuck at the "
                        + "menu with a PLAY button that does nothing"))
            .Probe(sheet, "M3", "the movement stat burst is re-sent for the SECOND match",
                "docs/40 / PlayerMovementTracker.Reset — wave 3 never cleared StatsDelivered, so the second match "
                + "silently reverted to the client's built-in speeds and nothing in any log said so",
                c =>
                {
                    IReadOnlyList<byte[]> bursts = c.Ledger.Payloads(
                        VerificationPackets.CharacterBase, sub8: VerificationPackets.UpdateStatSub);
                    int second = c.Ledger.Payloads(
                        VerificationPackets.CharacterBase, sub8: VerificationPackets.UpdateStatSub, since: firstMatchMark).Count;
                    if (!secondZoning)
                    {
                        return ProbeOutcome.Unknown(
                            $"there was no second match to measure ({bursts.Count} stat burst(s) in the session)");
                    }

                    return second > 0
                        ? ProbeOutcome.Pass($"{bursts.Count} stat burst(s) in the session, {second} of them after the BACK "
                            + "— the second match gets its speeds")
                        : ProbeOutcome.Fail($"{bursts.Count} stat burst(s) in the session and NONE after the BACK: the "
                            + "second match runs at the client's built-in speeds");
                })
            .Probe(sheet, "I5", "the inventory and the crafting tab are rebuilt for the second match",
                "docs/41 §I1(a) — the match actor is gone and with it its containers; leaving them behind would make the "
                + "next match's InitContainers the second one this client has seen for the same character",
                c =>
                {
                    int init = c.Ledger.Payloads(VerificationPackets.ContainerBase, sub16: VerificationPackets.InitContainersSub, since: firstMatchMark).Count;
                    int recipes = c.Ledger.Payloads(VerificationPackets.RecipeBase, sub8: VerificationPackets.RecipeListSub, since: firstMatchMark).Count;
                    if (!secondZoning)
                    {
                        return ProbeOutcome.Unknown("there was no second match to rebuild anything for");
                    }

                    return init > 0 && recipes > 0
                        ? ProbeOutcome.Pass($"after the BACK: InitContainers x{init}, Recipe.List x{recipes}")
                        : ProbeOutcome.Fail($"after the BACK: InitContainers x{init}, Recipe.List x{recipes}");
                })
            .Try("wait up to 150 s for the second match's parachute", async (c, token) =>
            {
                TimeSpan deadline = c.Clock.Now + TimeSpan.FromSeconds(150);
                while (c.Clock.Now < deadline && c.Ledger.Vehicles.Count < 2)
                {
                    await c.Clock.DelayAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
            })
            .Probe(sheet, "R2", "the random drop varies between matches",
                "docs/48 §5.1 — the seed is drawn per match from the session guid and the wall clock, and the place is "
                + "drawn from the 92 named ones weighted by marker count",
                c =>
                {
                    IReadOnlyList<LightweightEntity> chutes = c.Ledger.Vehicles;
                    if (chutes.Count < 2)
                    {
                        return ProbeOutcome.Unknown($"only {chutes.Count} parachute(s) in the session");
                    }

                    Vector3 second = chutes[^1].Position;
                    float distance = ServerLedger.HorizontalDistance(firstDrop, second);
                    return distance > 50f
                        ? ProbeOutcome.Pass($"match 1 dropped at {firstDrop}, match 2 at {second} — {distance:F0} m apart")
                        : ProbeOutcome.Fail($"both matches dropped within {distance:F0} m: {firstDrop} then {second}");
                })
            .Read("second match", c =>
                $"{c.Ledger.Vehicles.Count} parachute(s); "
                + string.Join(" | ", c.Ledger.Vehicles.Select(v => v.Position.ToString())));
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>One spawned world object, with the door id that says whether it is a door.</summary>
    public sealed record WorldObject(LightweightEntity Entity, uint DoorId);

    /// <summary>
    /// Every <c>0xd6</c> the server has sent, decoded from the retained payloads rather than from
    /// the ledger's npc list, so the door-id discriminator and the entity come from the same bytes.
    /// </summary>
    public static IReadOnlyList<WorldObject> WorldObjects(HarnessClient client, int since = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        var found = new List<WorldObject>();
        foreach (byte[] payload in client.Ledger.Payloads(VerificationPackets.AddLightweightNpc, since: since))
        {
            if (ServerPackets.TryReadLightweightEntity(payload) is { } entity)
            {
                found.Add(new WorldObject(entity, VerificationPackets.DoorIdOf(payload)));
            }
        }

        return found;
    }

    /// <summary>Every decoded <c>ClientUpdate.ItemAdd</c> of the session.</summary>
    public static IReadOnlyList<ItemGrant> ItemGrants(HarnessClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var found = new List<ItemGrant>();
        foreach (byte[] payload in client.Ledger.Payloads(
            VerificationPackets.ClientUpdateBase, sub16: VerificationPackets.ItemAddSub))
        {
            if (VerificationPackets.TryReadItemAdd(payload) is { } grant)
            {
                found.Add(grant);
            }
        }

        return found;
    }

    private static string Named(ClientAssets assets, WorldObject o) =>
        assets.ModelFile(o.Entity.ModelId) ?? $"model {o.Entity.ModelId}";

    private static bool IsWearable(string modelFile) =>
        modelFile.Contains("Clothes", StringComparison.OrdinalIgnoreCase)
        || modelFile.Contains("Backpack", StringComparison.OrdinalIgnoreCase)
        || modelFile.Contains("Helmet", StringComparison.OrdinalIgnoreCase)
        || modelFile.Contains("Kevlar", StringComparison.OrdinalIgnoreCase);

    private static int SelfRecordLength(HarnessClient client) =>
        client.Ledger.Zone.FirstOrDefault(r => r.Opcode == 0x03)?.Length ?? 0;

    /// <summary>
    /// The first recipe id of the server's own <c>Recipe.List</c>: <c>26 09 | u32 count | rows</c>,
    /// each row starting with its <c>RecipeId</c> (docs/62 §2, wire column 1 = record <c>+0x00</c>).
    /// </summary>
    private static uint? FirstRecipeId(HarnessClient client)
    {
        foreach (byte[] payload in client.Ledger.Payloads(
            VerificationPackets.RecipeBase, sub8: VerificationPackets.RecipeListSub))
        {
            if (payload.Length >= 10)
            {
                return BitConverter.ToUInt32(payload.AsSpan(6, 4));
            }
        }

        return null;
    }

    private static string? ActorText(ClientAssets assets, string actorFile)
    {
        try
        {
            string path = Path.Combine(assets.Root, "adr", actorFile);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
