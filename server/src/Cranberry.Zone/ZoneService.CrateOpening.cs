using System.Numerics;
using System.Text.Json;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private sealed class CrateOpeningRun(EconomyCrate crate, int count,
        InventoryItemInstance weapon, InventoryItemInstance? previousHand)
    {
        public EconomyCrate Crate { get; } = crate;
        public int Remaining { get; set; } = count;
        public InventoryItemInstance Weapon { get; } = weapon;
        public InventoryItemInstance? PreviousHand { get; } = previousHand;
        public uint PreviousSlot { get; } = previousHand?.LoadoutSlotId ?? 7;
        public ulong ActorGuid { get; set; }
        public const uint TransientId = 3_000_001; // beside the isolated menu vehicle preview
        public int Hits { get; set; }
        public bool Pending { get; set; }
        public bool Finished { get; set; }
        public bool AccountInventoryRefreshPending { get; set; }
        public SessionCombat Combat { get; } = new();
        public List<OwnedAccountItem> Awards { get; } = [];
        public int RevealedAwardEntries { get; set; }
    }

    private bool HandleCrateOpening(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != StartCrateOpening.BaseOpcode) return false;
        if (StartCrateOpening.IsCompletion(payload))
        {
            // Stock Done stays on the results page; Back sends Stop and leaves it.
            if (payload[1] == 5 && state.CrateOpening is { } stopped)
            {
                // The economic award is already saved even if its animation is still pending.
                RevealCrateAwards(connection, stopped);
                EndCrateOpening(connection, state, preserveResults: true);
                SendCrateOpeningSummary(connection, state, stopped);
            }
            else if (payload[1] == 4)
                EndCrateOpening(connection, state);
            return true;
        }
        if (payload[1] != 3) return false;
        var request = StartCrateOpening.Parse(payload);
        if (state.CrateOpening is { Finished: false }) return true;
        if (state.CrateOpening is not null) EndCrateOpening(connection, state);
        EconomyCatalog.Default.TryGetCrate(request.CrateItemId, out var crate);
        long count = 0;
        if (_economy is not null && state.Match == MatchStep.Menu && crate is { CrownsCost: 0 }
            && state.Weapons.SupportsCrateOpeningWeapon)
        {
            var account = ReadAccountEconomy(state);
            long held = Copies(account, crate.ItemId);
            if (crate.KeyItemId != 0) held = Math.Min(held, Copies(account, crate.KeyItemId));
            // The native list holds at most 100 rows; larger owned stacks continue through Again.
            count = Math.Min(held, request.OpenOne ? 1 : 100);
        }
        if (count is <= 0 or > 100 || !EnsureInventory(connection, state, "crate shooting gallery"))
        {
            foreach (byte[] packet in CrateOpeningPresentation.Result(crate, [], false))
                SendTunnel(connection, writer => writer.WriteRaw(packet));
            _log.Warn($"{connection} refused crate opening {request.CrateItemId}: no usable copies or gallery weapon unavailable");
            return true;
        }
        var inventory = state.Inventory!;
        inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out var previousHand);
        var weapon = inventory.CreateInstance(CrateOpeningWeapon.ItemId, 1);
        inventory.BindLoadout(weapon, CrateOpeningWeapon.LoadoutSlotId, BodySlots.RightHand);
        state.Combat.Shooter.EnsureDeclared(weapon.Guid, weapon.DefinitionId, CrateOpeningWeapon.Profile.ClipSize);
        var run = new CrateOpeningRun(crate!, (int)count, weapon, previousHand);
        run.Combat.Shooter.EnsureDeclared(weapon.Guid, weapon.DefinitionId, CrateOpeningWeapon.Profile.ClipSize);
        state.CrateOpening = run;
        SendTunnel(connection, new CrateOpeningScreen(ListCrateImageSetId: crate!.ImageSetId, ResetTables: true).WriteTo);
        SendTunnel(connection, new UpdateLocation(CrateSceneLayout.PlayerPosition, new Vector4(0, 0, 0, 1)).WriteTo);
        SendTunnel(connection, CrateSceneLayout.Camera.WriteTo);
        SendTunnel(connection, state.Weapons.CreateItemAdd(state.Guid, weapon.ToRecord(state.Guid)));
        if (!TrySendWieldSequence(connection, state, inventory, weapon, previousAbilityId: 0))
        {
            EndCrateOpening(connection, state);
            foreach (byte[] packet in CrateOpeningPresentation.Result(crate, [], false))
                SendTunnel(connection, writer => writer.WriteRaw(packet));
            return true;
        }
        PrepareNextCrate(connection, state, run);
        _log.Info($"{connection} crate gallery ready: {count} x {crate.ItemId}; rewards wait for validated hits");
        return true;
    }

    private static long Copies(AccountEconomySnapshot account, uint itemId) =>
        account.Items.Where(item => item.AccountItemId == itemId).Sum(item => (long)item.Count);

    private void SendUnopenedCrates(SoeConnection connection, CrateOpeningRun run) =>
        SendTunnel(connection, new CrateOpeningUnopened(Enumerable.Repeat(
            new UnopenedCrateRow(run.Crate.NameLocaleId, run.Crate.ImageSetId, 255, 255, 255), run.Remaining).ToArray()).WriteTo);

    private void PrepareNextCrate(SoeConnection connection, GatewaySessionState state, CrateOpeningRun run)
    {
        if (state.CrateOpening != run || run.Finished) return;
        if (run.ActorGuid != 0) SendTunnel(connection, new RemovePlayer(run.ActorGuid).WriteTo);
        run.ActorGuid = 0x4800_0000_0000_0000ul + (ulong)++state.EconomyRequestSequence;
        run.Hits = 0;
        run.Pending = false;
        SendUnopenedCrates(connection, run);
        SendTunnel(connection, new CrateOpeningControl(9).WriteTo);
        SendTunnel(connection, new LightweightEntityBody(ZoneOpcodes.AddLightweightNpc, run.ActorGuid,
            CrateOpeningRun.TransientId, 10029, CrateSceneLayout.CratePosition, CrateSceneLayout.CrateRotation,
            SpawnFlags1: LightweightEntityBody.CollidableFlag).WriteTo);
        SendTunnel(connection, new LightweightToFullNpc(CrateOpeningRun.TransientId, run.ActorGuid).WriteTo);
        SendTunnel(connection, new CrateOpeningTarget(run.ActorGuid).WriteTo);
        SendTunnel(connection, new CrateOpeningScreen(ShowInfoMessage: true,
            InfoMessageCode: "UI.CrateOpening.ShootTheCrate", ListCrateImageSetId: run.Crate.ImageSetId).WriteTo);
        SendTunnel(connection, new CrateOpeningControl(11).WriteTo);
    }

    private bool HandleCrateOpeningWeapon(SoeConnection connection, GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (state.CrateOpening is not { } run) return false;
        if (run.Finished || run.Pending || state.Match != MatchStep.Menu) return true;
        WeaponFireArm.Handle(run.Combat, payload, _options.Combat with { TimedReload = false },
            CrateOpeningWeapon.ItemId, run.Weapon.Guid,
            new Vector3(CrateSceneLayout.PlayerPosition.X, CrateSceneLayout.PlayerPosition.Y, CrateSceneLayout.PlayerPosition.Z),
            Environment.TickCount64, state.WeaponArmResults);
        foreach (var result in state.WeaponArmResults)
        {
            if (result.Reply is { } reply) SendTunnel(connection, writer => writer.WriteRaw(reply));
            if (result.TargetGuid != run.ActorGuid || run.Pending) continue;
            // An unresolved target is offered only after consuming a corroborating fire hint.
            // Duplicate/unmatched projectiles cannot advance the native clasp stages.
            if (++run.Hits < StartCrateOpening.RequiredHits)
                SendTunnel(connection, new CrateSceneAnimation(run.ActorGuid, "Flinch").WriteTo);
            else CompleteShotCrate(connection, state, run);
        }
        return true;
    }

    private void CompleteShotCrate(SoeConnection connection, GatewaySessionState state, CrateOpeningRun run)
    {
        run.Pending = true;
        SendTunnel(connection, new CrateOpeningControl(12).WriteTo);
        var result = new AccountEconomyOperations(_economy!).OpenCrates(state.AccountId,
            $"crate-opening:{state.EconomyLinkId}:{++state.EconomyRequestSequence}", [new(run.Crate.ItemId, 1)]);
        if (!result.Succeeded)
        {
            run.Finished = true;
            SendCrateOpeningSummary(connection, state, run);
            _log.Warn($"{connection} crate opening refused on break: {result.Error}");
            return;
        }
        var awards = JsonSerializer.Deserialize<EconomyAwardReceipt>(result.Receipt!.ResultJson!)!.Granted;
        run.Awards.AddRange(awards);
        run.Remaining--;
        PublishAccountEconomy(state.AccountId, inventoryChanged: true);
        SendTunnel(connection, new CrateSceneAnimation(run.ActorGuid, "Open").WriteTo);
        SendTunnel(connection, new CrateOpeningControl(8).WriteTo);
        SendTunnel(connection, new CrateOpeningScreen(CrateIsOpen: true, ListCrateImageSetId: run.Crate.ImageSetId).WriteTo);
        // Delay presentation only. The saved award survives disconnects during the animation.
        void Reveal()
        {
            if (state.CrateOpening != run) return;
            RevealCrateAwards(connection, run);
            SendUnopenedCrates(connection, run);
            if (run.Remaining == 0)
            {
                run.Finished = true;
                SendCrateOpeningSummary(connection, state, run);
            }
            else if (!Later(connection, 500, () => PrepareNextCrate(connection, state, run)))
                PrepareNextCrate(connection, state, run);
        }
        if (!Later(connection, 850, Reveal)) Reveal();
        _log.Info($"{connection} shot crate {run.Crate.ItemId}: awarded {string.Join(",", awards.Select(a => a.AccountItemId))}; {run.Remaining} left");
    }

    private void RevealCrateAwards(SoeConnection connection, CrateOpeningRun run)
    {
        while (run.RevealedAwardEntries < run.Awards.Count)
        {
            var award = run.Awards[run.RevealedAwardEntries];
            var skin = EconomyCatalog.Default.Skins[award.AccountItemId];
            for (uint i = 0; i < award.Count; i++)
            {
                SendTunnel(connection, new CrateOpeningOpened(skin.NameLocaleId, skin.ImageSetId, skin.RarityId).WriteTo);
                SendTunnel(connection, new PlayWorldCompositeEffect(run.ActorGuid,
                    CrateRewardEffects.ForRarity(skin.RarityId), CrateSceneLayout.CratePosition).WriteTo);
            }
            run.RevealedAwardEntries++;
        }
    }

    private void SendCrateOpeningSummary(SoeConnection connection, GatewaySessionState state, CrateOpeningRun run)
    {
        var account = ReadAccountEconomy(state);
        bool again = Copies(account, run.Crate.ItemId) > 0
            && (run.Crate.KeyItemId == 0 || Copies(account, run.Crate.KeyItemId) > 0);
        uint count = checked((uint)run.Awards.Sum(a => (long)a.Count));
        SendTunnel(connection, new CrateOpeningControl(12).WriteTo);
        SendTunnel(connection, new CrateOpeningUnopened([]).WriteTo);
        SendTunnel(connection, new CrateOpeningScreen(ShowResultsMessage: true, CrateIsOpen: count > 0,
            ResultsTitleCode: count > 0 ? "UI.CrateOpening.FinishedCongrats" : "UI.CrateOpening.FinishedNoCratesOpened",
            ResultsSubtitleCode: count > 0 ? "UI.CrateOpening.FinishedItemCount" : "",
            ListCrateImageSetId: run.Crate.ImageSetId, ShowAgain: again, SubtitleCount: count).WriteTo);
    }

    private void EndCrateOpening(SoeConnection connection, GatewaySessionState state, bool preserveResults = false)
    {
        if (state.CrateOpening is not { } run)
        {
            // Back can arrive after Done already removed the scene but retained its results.
            if (!preserveResults) ResetCrateOpening(connection);
            return;
        }
        state.CrateOpening = null; // invalidate delayed callbacks
        SendTunnel(connection, new CrateOpeningControl(12).WriteTo);
        if (run.ActorGuid != 0) SendTunnel(connection, new RemovePlayer(run.ActorGuid).WriteTo);
        var inventory = state.Inventory!;
        // Remove the active binding while its temporary item still exists in the client.
        SendTunnel(connection, new UnsetCharacterEquipmentSlot(state.Guid, BodySlots.RightHand).WriteTo);
        inventory.RemoveUnits(run.Weapon.Guid, 1);
        state.Combat.Shooter.ForgetWeapon(run.Weapon.Guid);
        state.Weapons.Ledger.Forget(run.Weapon.Guid);
        SendTunnel(connection, new ItemDelete(state.Guid, run.Weapon.Guid).WriteTo);
        if (run.PreviousHand is { } previous && inventory.Items.ContainsKey(previous.Guid))
            inventory.BindLoadout(previous, run.PreviousSlot, BodySlots.RightHand);
        // Publish the latest ownership once, after removing the gallery hand but before the
        // final normal dress/binding. A refresh after that binding would replace it again.
        if (run.AccountInventoryRefreshPending && _economy is not null)
            SendAccountInventory(connection, state, ReadAccountEconomy(state));
        SendLoadoutSlots(connection, inventory);
        SendCharacterAppearance(connection, state, "left crate shooting gallery");
        if (run.PreviousHand is { DefinitionId: not PlayerInventory.SurvivorFistsItemDefinitionId } oldWeapon)
            TrySendWieldSequence(connection, state, inventory, oldWeapon, previousAbilityId: 0);
        BindSelectedFists(connection, state, force: true);
        if (MenuViewTable.TryResolve(state.MenuView, _options.MenuViews.Coverage, out var camera, out var mark))
        {
            SendTunnel(connection, new UpdateLocation(mark, MenuViewTable.SubjectRotation(state.MenuView)).WriteTo);
            SendTunnel(connection, camera.WriteTo);
        }
        if (!preserveResults) ResetCrateOpening(connection);
    }

    // f50c stops firing but leaves the native gallery state set. Reset must be last: even an
    // empty f506 list activates gallery mode again (August 140da5ec0/140da25d0).
    private void ResetCrateOpening(SoeConnection connection) =>
        SendTunnel(connection, new CrateOpeningScreen(ResetTables: true).WriteTo);
}
