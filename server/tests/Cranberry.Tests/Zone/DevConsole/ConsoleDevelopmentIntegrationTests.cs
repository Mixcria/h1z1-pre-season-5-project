using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Gas;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Loot;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder) DevelopmentMatch()
    {
        var setup = Admit(new ZoneOptions
        {
            Console = TestConsole with { ModMenuEnabled = false },
            Combat = CombatOptions.Default with { PracticeTarget = false },
        });
        ArmZoningReady(setup.Connection);
        SendClientIsReady(setup.Service, setup.Connection);
        EnterStep(setup.Connection, "InMatch");
        var movement = Member<SessionMovementState>(setup.Connection, "Movement");
        movement.ApplyPlayer(ClientMovementUpdate.Parse(Convert.FromHexString("020018F6B21C00000000")));
        movement.PinPlayer(new Vector3(100, 20, 100));
        return setup;
    }

    private static T Member<T>(SoeConnection connection, string name) =>
        (T)connection.Tag!.GetType().GetProperty(name)!.GetValue(connection.Tag)!;

    [Theory]
    [InlineData("startmatch", "")]
    [InlineData("kotkdrop", "")]
    [InlineData("match", "drop")]
    public void StartingFromPregameSendsTheRealStartAndParachuteOnce(string command, string arguments)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        EnterStep(connection, "Lobby");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, command, arguments);
        Assert.Equal("Dropping", Member<object>(connection, "Match").ToString());
        Assert.Single(Sent(recorder, before), p => p.Length > 3 && p[1] == 0xce && p[2] == 0x16);
        Assert.Contains(Sent(recorder, before), p => p.Length > 2 && p[1] == 0xd7);
        SendExecuteCommand(service, connection, command, arguments);
        Assert.Single(Sent(recorder, before), p => p.Length > 3 && p[1] == 0xce && p[2] == 0x16);
    }

    [Fact]
    public void PvpKitAndAmmoGrantRealInventoryAndDropUsesTheGroundPath()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "kit", "pvp");
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        Assert.Contains(inventory.Items.Values, i => i.DefinitionId == 2124 && i.LoadoutSlotId != 0);
        var rifle = Assert.Single(inventory.Items.Values, i => i.DefinitionId == 2425);
        Assert.Equal(5L, inventory.Items.Values.Where(i => i.DefinitionId == 2424).Sum(i => (long)i.Count));
        Assert.DoesNotContain(ConsoleLines(recorder, before), l => l.StartsWith('-'));
        SetSessionMember(connection, "HeldWeaponItemGuid", rifle.Guid);
        long rounds = inventory.Items.Values.Where(i => i.DefinitionId == 1429).Sum(i => (long)i.Count);
        SendExecuteCommand(service, connection, "ammo", "120");
        Assert.Equal(rounds + 120, inventory.Items.Values.Where(i => i.DefinitionId == 1429).Sum(i => (long)i.Count));
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "combat");
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("base=2425"));
        var shooter = Member<SessionCombat>(connection, "Combat").Shooter;
        shooter.DeclareWeapon(rifle.Guid, rifle.DefinitionId, 17);
        SendExecuteCommand(service, connection, "drop", "hand");
        Assert.False(inventory.Items.ContainsKey(rifle.Guid));
        Assert.False(shooter.Knows(rifle.Guid));
        Assert.Contains(Sent(recorder, before), p => p.Length > 2 && p[1] == 0xd6);
    }

    [Fact]
    public void GunsKitContainsAllEightFirearmsAndDropGunsFreesEveryWeaponSlot()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "kit", "guns");
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        Assert.DoesNotContain(ConsoleLines(recorder, before), l => l.StartsWith('-'));
        Assert.Equal(new uint[] { 2, 1374, 1718, 1899, 1991, 1997, 2229, 2425 },
            inventory.Items.Values.Where(i => AmmoTypes.AmmoItemFor(i.DefinitionId) != 0).Select(i => i.DefinitionId).OrderBy(i => i));
        SendExecuteCommand(service, connection, "drop", "guns");
        Assert.DoesNotContain(inventory.Items.Values, i => AmmoTypes.AmmoItemFor(i.DefinitionId) != 0);
        SendExecuteCommand(service, connection, "give", "ak47");
        Assert.Single(inventory.Items.Values, i => i.DefinitionId == 2229);
    }

    [Fact]
    public void LootRingPublishesNearbyItemsAndAdoptsThemForStreamingCleanup()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var loot = Member<LootWorld>(connection, "Loot");
        var stream = Member<MatchLoot>(connection, "StreamedLoot");
        var original = loot.Items.Select(i => i.WorldGuid).ToHashSet();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "loot", "ring");
        var spawned = loot.Items.Where(i => !original.Contains(i.WorldGuid)).ToArray();
        Assert.Equal(3, spawned.Length);
        Assert.All(spawned, i => Assert.True(stream.IsStreamed(LootStreamKey.ForDropped(i.WorldGuid))));
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0xf8 && p[2] == 1);
    }

    [Fact]
    public void ExplicitTargetsWorkWithAutomaticTargetsDisabledAndRespawnDoesNotAccumulate()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var combat = Member<SessionCombat>(connection, "Combat");
        SendExecuteCommand(service, connection, "target", "spawn");
        var first = Assert.Single(combat.Targets.All);
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "target", "spawn");
        var replacement = Assert.Single(combat.Targets.All);
        Assert.NotEqual(first.WorldGuid, replacement.WorldGuid);
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0x0f && p[2] == 1);
        SendExecuteCommand(service, connection, "target", "clear");
        Assert.Empty(combat.Targets.All);
    }

    [Fact]
    public void SpeedChangesBothTrackerAndStatPacketsAndRestores()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var tracker = Member<PlayerMovementTracker>(connection, "MovementStats");
        float normal = tracker.Profile.MaxMovementSpeed;
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "speed", "2");
        Assert.Equal(normal * 2, tracker.Profile.MaxMovementSpeed);
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0x0f && p[2] == 0x40);
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0x11 && p[2] == 5);
        SendExecuteCommand(service, connection, "speed", "1");
        Assert.Equal(normal, tracker.Profile.MaxMovementSpeed);
    }

    [Theory]
    [InlineData("speed", "NaN")]
    [InlineData("speed", "fast")]
    [InlineData("give", "bandage nonsense")]
    [InlineData("ammo", "oops")]
    [InlineData("tp", "NaN 10 20")]
    [InlineData("tp", "10 Infinity 20")]
    [InlineData("up", "Infinity")]
    [InlineData("hurt", "oops")]
    [InlineData("doors", "open nonsense")]
    [InlineData("loot", "spawn bandage nonsense")]
    [InlineData("loot", "find ar15 nonsense")]
    public void MalformedArgumentsDoNotPerformAMutation(string command, string arguments)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, command, arguments);
        Assert.All(Sent(recorder, before), p => Assert.True(IsConsolePrint(p)));
        Assert.StartsWith("?", Assert.Single(ConsoleLines(recorder, before)));
    }

    [Fact]
    public void ParachuteUsesTeleportHandshakeAndPreservesInventory()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "chute", "500");
        Assert.Equal("Dropping", Member<object>(connection, "Match").ToString());
        Assert.Same(inventory, Member<PlayerInventory>(connection, "Inventory"));
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0x11 && p[2] == 0x0a);
        Assert.Contains(Sent(recorder, before), p => p.Length > 2 && p[1] == 0xd7);
    }

    [Fact]
    public void LobbyActuallyTransfersAndEndActuallyShowsVictory()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "endmatch");
        Assert.Equal("Ended", Member<object>(connection, "Match").ToString());
        Assert.Contains(Sent(recorder, before), p => p.Length > 3 && p[1] == 0xce && p[2] == 0x18);
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "lobby");
        Assert.Equal("Transferring", Member<object>(connection, "Match").ToString());
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("fresh pregame lobby"));
    }

    [Fact]
    public void PlayersAndTiersUseConnectedSessionAndPermissionChangesSurviveNextCommand()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "players");
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("Cranberry guid=4097 InMatch"));
        SendExecuteCommand(service, connection, "tier", "4097 tester");
        Assert.Equal(ConsoleTier.Tester, Session(connection).Tier);
        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "startmatch");
        Assert.Contains(ConsoleLines(recorder, before), l => l.Contains("Owner only"));
        Assert.Equal(ConsoleTier.Tester, Session(connection).Tier);
    }

    [Fact]
    public void AdvancingGasEmitsNextPhaseAndNeverReplaysSkippedDamage()
    {
        var gas = new GasController(new GasSettings());
        var schedule = gas.Start(1000);
        Assert.True(gas.AdvanceToNextEvent(2000));
        var tick = gas.Tick(2000, [new PlayerSample(0, new Vector3(100000, 0, 100000), true)]);
        Assert.True(tick.Has(GasTickEvents.RevealSafeZone));
        Assert.Empty(tick.Damage.ToArray());
        Assert.Equal(schedule.Phases[0].RevealAtMs, gas.MatchClockAt(2000));
        gas.Stop();
        Assert.False(gas.AdvanceToNextEvent(3000));
    }
}


