using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// Lane 1F, the model half: <b>ammunition is carried, not conjured</b> (S3 row 6, D118). Every test
/// here is about the bag - what a reload takes out of it, what an unload puts back, and what happens
/// when it is empty.
/// </summary>
public sealed class AmmoStoreTests
{
    /// <summary><c>ClientItemDefinitions</c> 2425, "AR-15" - <c>WEAPON_ID</c> 6, <c>CLIP_SIZE</c> 30.</summary>
    private const uint ArFifteen = AugustHeldWeapon.ItemDefinitionId;

    /// <summary>Item 1429, ".223 Round" - what the AR-15's own description says it eats.</summary>
    private const uint TwoTwoThree = 1429;

    /// <summary>Item 1374, "12GA Pump Shotgun" - <c>CLIP_SIZE</c> 6, loaded shell by shell.</summary>
    private const uint PumpShotgun = 1374;

    /// <summary>Item 1511, "12 Gauge Buckshot Shell".</summary>
    private const uint BuckshotShell = 1511;

    private const ulong Rifle = 0x3100_0000_0000_0001;

    // ---------------------------------------------------------------- the client's own pairing

    [Theory]
    [InlineData(2425u, 1429u)]      // AR-15            -> .223 Round
    [InlineData(10u, 1429u)]        // AR-15 (loot row) -> .223 Round
    [InlineData(2229u, 2325u)]      // AK-47            -> 7.62x39 Round
    [InlineData(1997u, 1998u)]      // M9               -> 9mm Round
    [InlineData(2u, 1428u)]         // M1911A1          -> .45 Round
    [InlineData(1991u, 1992u)]      // R380             -> .380 Round
    [InlineData(1718u, 1719u)]      // .44 Magnum       -> .44 Round
    [InlineData(1374u, 1511u)]      // 12GA Pump        -> 12 Gauge Buckshot Shell
    [InlineData(1373u, 1469u)]      // .308             -> .308 Round
    public void EveryGunOnTheFloorKnowsWhatItEats(uint weaponItemId, uint expectedAmmoItemId) =>
        Assert.Equal(expectedAmmoItemId, AmmoTypes.AmmoItemFor(weaponItemId));

    [Fact]
    public void TheFistsAndABandageEatNothing()
    {
        Assert.Equal(0u, AmmoTypes.AmmoItemFor(PlayerInventory.SurvivorFistsItemDefinitionId));
        Assert.Equal(0u, AmmoTypes.AmmoItemFor(PlayerInventory.StarterBandageItemDefinitionId));
    }

    [Fact]
    public void OnlyTheTwelveGaugeIsLoadedShellByShell()
    {
        Assert.True(AmmoTypes.IsShellByShell(PumpShotgun));
        Assert.False(AmmoTypes.IsShellByShell(ArFifteen));
    }

    // ---------------------------------------------------------------- Count / Take / Grant

    [Fact]
    public void CountSeesEveryStackOfThatCalibre()
    {
        PlayerInventory inventory = Bag(30, 12);
        var store = new PlayerInventoryAmmoStore(inventory);

        Assert.Equal(42, store.Count(TwoTwoThree));
        Assert.Equal(0, store.Count(BuckshotShell));
    }

    [Fact]
    public void TakeSpendsTheSmallestStackFirstAndDeletesItWhenItEmpties()
    {
        PlayerInventory inventory = Bag(30, 5);
        var store = new PlayerInventoryAmmoStore(inventory);
        var changes = new List<AmmoStackChange>();

        Assert.Equal(8, store.Take(TwoTwoThree, 8, changes));

        // The 5-round stack went first and vanished; the 30 gave up 3 and survived.
        Assert.Equal(2, changes.Count);
        Assert.True(changes[0].Removed);
        Assert.False(changes[1].Removed);
        Assert.Equal(27u, changes[1].CountAfter);
        Assert.Equal(27, store.Count(TwoTwoThree));
    }

    [Fact]
    public void TakeReturnsLessThanAskedWhenTheBagRunsOut()
    {
        PlayerInventory inventory = Bag(4);
        var store = new PlayerInventoryAmmoStore(inventory);
        var changes = new List<AmmoStackChange>();

        Assert.Equal(4, store.Take(TwoTwoThree, 30, changes));
        Assert.Equal(0, store.Count(TwoTwoThree));
        Assert.True(Assert.Single(changes).Removed);
    }

    [Fact]
    public void GrantMergesIntoAnExistingStackAndCreatesOneWhenThereIsNone()
    {
        PlayerInventory inventory = Bag(10);
        var store = new PlayerInventoryAmmoStore(inventory);
        var changes = new List<AmmoStackChange>();

        Assert.True(store.Grant(TwoTwoThree, 7, changes));
        Assert.Equal(17, store.Count(TwoTwoThree));
        Assert.False(changes[0].Created);

        changes.Clear();
        Assert.True(store.Grant(BuckshotShell, 6, changes));
        Assert.True(changes[0].Created);
        Assert.Equal(6, store.Count(BuckshotShell));
    }

    // ---------------------------------------------------------------- the reload, end to end

    [Fact]
    public void AFullReloadEmptiesTheBoxAndAnswersEightyTwoOhEight()
    {
        (SessionCombat session, PlayerAmmoContext ammo, PlayerInventory inventory) = Session(30);
        var results = new List<WeaponArmResult>();

        Reload(session, ammo, results);

        WeaponArmResult only = Assert.Single(results);
        Assert.Contains("loaded 30 round(s)", only.Line, StringComparison.Ordinal);
        Assert.Equal(30, session.Shooter.AmmoOf(Rifle));
        Assert.Equal(0, new PlayerInventoryAmmoStore(inventory).Count(TwoTwoThree));

        // 11 04 for the emptied stack, then one 82 08.
        IReadOnlyList<byte[]> replies = Assert.IsAssignableFrom<IReadOnlyList<byte[]>>(only.Replies);
        Assert.Equal(2, replies.Count);
        Assert.Equal(ZoneOpcodes.ClientUpdateBase, replies[0][0]);
        Assert.Equal(ItemDelete.SubOpcode, BitConverter.ToUInt16(replies[0], 1));
        Assert.Equal(ZoneOpcodes.WeaponBase, replies[1][0]);
        Assert.Equal(WeaponReplyPackets.SubReload, replies[1][5]);

        // S5c §3.1: gameTime 0 means "apply now" in every s2c 0x82 this server writes.
        Assert.Equal(0u, BitConverter.ToUInt32(replies[1], 1));
    }

    [Fact]
    public void APartialReloadTakesWhatIsThereAndSaysSo()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) = Session(7);
        var results = new List<WeaponArmResult>();

        Reload(session, ammo, results);

        Assert.Equal(7, session.Shooter.AmmoOf(Rifle));
        Assert.Contains("PARTIAL", results[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBagIsRefusedWithEightyTwoZeroBAndTheClientsOwnHint()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) = Session(0);
        var results = new List<WeaponArmResult>();

        Reload(session, ammo, results);

        Assert.Contains("REFUSED", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("hint 15204", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(0, session.Shooter.AmmoOf(Rifle));

        byte[] reply = Assert.IsType<byte[]>(results[0].Reply);
        Assert.Equal(14, reply.Length);
        Assert.Equal(ZoneOpcodes.WeaponBase, reply[0]);
        Assert.Equal(WeaponReplyPackets.SubReloadRejected, reply[5]);
        Assert.Equal(Rifle, BitConverter.ToUInt64(reply, 6));
    }

    [Fact]
    public void AFullMagazineIsRefusedRatherThanTopped()
    {
        (SessionCombat session, PlayerAmmoContext ammo, PlayerInventory inventory) = Session(60);
        var results = new List<WeaponArmResult>();

        Reload(session, ammo, results);
        results.Clear();

        // This helper uses immediate reloads: the first answer has completed, so there is no
        // in-flight operation to retry. TimedReloadTests separately pins D342's pending retry.
        Reload(session, ammo, results);
        Assert.Contains("already full", results[0].Line, StringComparison.Ordinal);
        results.Clear();

        // The same full magazine remains refused later.
        Reload(session, ammo, results, nowMs: 60_000);
        Assert.Contains("already full", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(30, new PlayerInventoryAmmoStore(inventory).Count(TwoTwoThree));
    }

    [Fact]
    public void ThePumpShotgunIsLoadedOneShellAtATime()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) =
            Session(6, weapon: PumpShotgun, ammoItemId: BuckshotShell);
        var results = new List<WeaponArmResult>();

        Reload(session, ammo, results);

        Assert.Equal(6, session.Shooter.AmmoOf(Rifle));
        Assert.Contains("shell-by-shell", results[0].Line, StringComparison.Ordinal);

        // Six shells: six 82 08 rungs, each with a magazine one higher than the last, plus the one
        // 11 04 the box's last shell produced.
        IReadOnlyList<byte[]> replies = Assert.IsAssignableFrom<IReadOnlyList<byte[]>>(results[0].Replies);
        byte[][] reloads = [.. replies.Where(p => p[0] == ZoneOpcodes.WeaponBase)];
        Assert.Equal(6, reloads.Length);

        for (int shell = 0; shell < reloads.Length; shell++)
        {
            Assert.Equal(WeaponReplyPackets.SubReload, reloads[shell][5]);
            Assert.Equal((uint)(shell + 1), BitConverter.ToUInt32(reloads[shell], 18));
        }
    }

    [Fact]
    public void ADryGunRefusesToFireAndTheClientIsToldToUndrawTheShot()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) = Session(0);
        var results = new List<WeaponArmResult>();

        WeaponFireArm.Handle(
            session,
            ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [77]),
            CombatOptions.Default,
            ArFifteen,
            Rifle,
            Vector3.Zero,
            nowMs: 0,
            results,
            ammo);

        Assert.Contains("magazine empty", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("82 1e FireRejected", results[0].Line, StringComparison.Ordinal);

        // 82 1e: gameTime 0, the guid, flag/group/mode, then the ids the client drew.
        byte[] refusal = Assert.IsType<byte[]>(results[0].Reply);
        Assert.Equal(ZoneOpcodes.WeaponBase, refusal[0]);
        Assert.Equal(0u, BitConverter.ToUInt32(refusal, 1));
        Assert.Equal(WeaponReplyPackets.SubFireRejected, refusal[5]);
        Assert.Equal(Rifle, BitConverter.ToUInt64(refusal, 6));
        Assert.Equal(1u, BitConverter.ToUInt32(refusal, 17));
        Assert.Equal(77u, BitConverter.ToUInt32(refusal, 21));
        Assert.Equal(25, refusal.Length);
    }

    [Fact]
    public void ALootedGunIsEmptyAndOneReloadMakesItFire()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) = Session(30);
        var results = new List<WeaponArmResult>();

        // Retail hint 15204: "That gun isn't going to load itself."
        Fire(session, ammo, results, projectile: 1);
        Assert.Contains("REFUSED", results[0].Line, StringComparison.Ordinal);

        results.Clear();
        Reload(session, ammo, results);

        results.Clear();
        Fire(session, ammo, results, projectile: 2, nowMs: 5_000);
        Assert.Contains("ammo 29/30", results[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySoftHitTakesFiveOffTheGunAndSendsElevenOhThree()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) = Session(30);
        var results = new List<WeaponArmResult>();

        Reload(session, ammo, results);
        results.Clear();
        Fire(session, ammo, results, projectile: 1, nowMs: 5_000);

        int max = AmmoOptions.Default.MaxDurability;
        Assert.Equal(max - 5, session.Shooter.DurabilityOf(Rifle));
        Assert.Contains($"durability {max - 5}/{max}", results[0].Line, StringComparison.Ordinal);

        byte[] update = Assert.IsType<byte[]>(results[0].Reply);
        Assert.Equal(ItemUpdate.Length, update.Length);
        Assert.Equal(ItemUpdate.SubOpcode, BitConverter.ToUInt16(update, 1));
    }

    /// <summary>
    /// <b>Wave 13 (docs/107 §1, D197): the client's number in <c>82 28</c> is its PRE-refill count
    /// and the server does not adopt it.</b> The applier <c>FUN_1414887e0</c> reads its own count,
    /// compares it with our <c>ammoCount</c>, and only then writes our number into the ammo slot -
    /// so the field on the wire is the value the client has already thrown away. This test used to
    /// assert the opposite ("the client's count wins"), which would have rolled a reload back the
    /// moment the <c>ItemAdd</c> tail started carrying a magazine.
    /// </summary>
    [Fact]
    public void TheClientsPreRefillAmmoCountIsLoggedAndNotAdopted()
    {
        (SessionCombat session, PlayerAmmoContext ammo, _) = Session(30);
        var results = new List<WeaponArmResult>();
        Reload(session, ammo, results);
        results.Clear();

        // 82 28 AmmoCountAcknowledge: u64 guid; i32 clientAmmo; u32 serverAmmo.
        var packet = new List<byte> { ZoneOpcodes.WeaponBase, 0, 0, 0, 0, 0x28 };
        packet.AddRange(BitConverter.GetBytes(Rifle));
        packet.AddRange(BitConverter.GetBytes(28));
        packet.AddRange(BitConverter.GetBytes(30u));

        WeaponFireArm.Handle(
            session, [.. packet], CombatOptions.Default, ArFifteen, Rifle, Vector3.Zero, 0,
            results, ammo);

        Assert.Contains("the client counted 28", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("BEFORE applying the 30", results[0].Line, StringComparison.Ordinal);

        // The reload put 30 in, and it stays there: 28 is the number the client discarded.
        Assert.Equal(30, session.Shooter.AmmoOf(Rifle));
    }

    // ---------------------------------------------------------------- unload (D118 closed)

    [Fact]
    public void UnloadPutsTheMagazineBackInTheBagAsAStack()
    {
        (SessionCombat session, PlayerAmmoContext ammo, PlayerInventory inventory) = Session(30);
        var results = new List<WeaponArmResult>();
        Reload(session, ammo, results);
        results.Clear();
        Fire(session, ammo, results, projectile: 1, nowMs: 5_000);

        InventoryItemInstance gun = inventory.Items[Rifle];
        ItemActionResult plan = ItemVerbs.Unload(
            inventory, gun, ItemUseOptionKind.UnloadWeapon, session.Shooter);

        Assert.Equal(ItemActionKind.Unload, plan.Kind);
        Assert.Equal(TwoTwoThree, plan.DisplacedDefinitionId);
        Assert.Equal(29u, plan.DisplacedCount);
        Assert.Equal(0, session.Shooter.AmmoOf(Rifle));
        Assert.Equal(29, new PlayerInventoryAmmoStore(inventory).Count(TwoTwoThree));
    }

    [Fact]
    public void UnloadWithoutTheSessionsMagazinesIsStillDOneHundredAndEighteensRefusal()
    {
        PlayerInventory inventory = Bag(30);
        InventoryItemInstance gun = inventory.CreateInstance(ArFifteen, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);

        ItemActionResult refusal = InventoryActions.Resolve(
            inventory,
            new RequestUseItem(
                UnknownA: 1,
                ItemUseOptionId: 7,
                CharacterGuid: inventory.CharacterGuid,
                SourceCharacterGuid: inventory.CharacterGuid,
                TargetCharacterGuid: inventory.CharacterGuid,
                ItemGuid: gun.Guid,
                Simple: true,
                Count: 1,
                TrailingBytes: 0));

        Assert.Equal(ItemActionKind.Refused, refusal.Kind);
    }

    // ---------------------------------------------------------------- switches

    [Fact]
    public void TheSwitchesDefaultOnAndDescribeThemselves()
    {
        AmmoOptions shipped = AmmoOptions.Default;

        Assert.True(shipped.AmmoFromBag);
        Assert.True(shipped.GunsSpawnEmpty);
        Assert.True(shipped.SendItemUpdate);
        Assert.Equal(5, shipped.DurabilityLossPerShot);

        Assert.Contains("fromBag=ON", shipped.Describe(), StringComparison.Ordinal);
        Assert.Contains("gunsSpawnEmpty=ON", shipped.Describe(), StringComparison.Ordinal);
        Assert.Contains("itemUpdate 11 03=ON", shipped.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAnExactZeroTurnsOneOff()
    {
        var environment = new Dictionary<string, string?>
        {
            [AmmoOptions.FromBagVariable] = "0",
            [AmmoOptions.GunsSpawnEmptyVariable] = "false",     // a typo leaves the default
            [AmmoOptions.ItemUpdateVariable] = "0",
        };

        AmmoOptions options = AmmoOptions.FromEnvironment(
            name => environment.TryGetValue(name, out string? value) ? value : null);

        Assert.False(options.AmmoFromBag);
        Assert.True(options.GunsSpawnEmpty);
        Assert.False(options.SendItemUpdate);
    }

    [Fact]
    public void WithTheBagSwitchOffAReloadIsDOneHundredAndEighteensFreeRefill()
    {
        (SessionCombat session, PlayerAmmoContext ammo, PlayerInventory inventory) = Session(30);
        var results = new List<WeaponArmResult>();
        CombatOptions options = CombatOptions.Default with
        {
            Ammo = AmmoOptions.Default with { AmmoFromBag = false },
        };

        session.Shooter.DeclareWeapon(Rifle, ArFifteen, 0);
        WeaponFireArm.Handle(
            session, ShootingPacketBuilder.ReloadRequest(Rifle), options, ArFifteen, Rifle,
            Vector3.Zero, 0, results, ammo);

        Assert.Contains("magazine refilled to 30", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(30, new PlayerInventoryAmmoStore(inventory).Count(TwoTwoThree));
    }

    // ---------------------------------------------------------------- helpers

    private static void Reload(
        SessionCombat session, PlayerAmmoContext ammo, List<WeaponArmResult> results, long nowMs = 0) =>
        WeaponFireArm.Handle(
            session,
            ShootingPacketBuilder.ReloadRequest(Rifle),
            // These tests cover ammunition accounting; timed completion is tested separately.
            CombatOptions.Default with { TimedReload = false },
            session.Shooter.ItemDefinitionOf(Rifle) is var held && held != 0
                ? held
                : ammo.Inventory.Items[Rifle].DefinitionId,
            Rifle,
            Vector3.Zero,
            nowMs,
            results,
            ammo);

    private static void Fire(
        SessionCombat session,
        PlayerAmmoContext ammo,
        List<WeaponArmResult> results,
        uint projectile,
        long nowMs = 0) =>
        WeaponFireArm.Handle(
            session,
            ShootingPacketBuilder.Fire(Rifle, 0, 0, 0, [projectile]),
            CombatOptions.Default,
            ammo.Inventory.Items[Rifle].DefinitionId,
            Rifle,
            Vector3.Zero,
            nowMs,
            results,
            ammo);

    /// <summary>
    /// A session holding <paramref name="weapon"/> as instance <see cref="Rifle"/>, with
    /// <paramref name="rounds"/> of its calibre in the bag. Nothing here declares the magazine, so
    /// the gun is <b>empty</b> until something reloads it - which is the parity rule under test.
    /// </summary>
    private static (SessionCombat Session, PlayerAmmoContext Ammo, PlayerInventory Inventory) Session(
        int rounds, uint weapon = ArFifteen, uint ammoItemId = TwoTwoThree)
    {
        ulong next = 0x9000_0000_0000_0001;
        var inventory = new PlayerInventory(0x1001, () => next++);
        inventory.Bootstrap();

        // The gun takes the reserved guid so the wire, the model and the magazine agree, and it is
        // BOUND to a hotbar slot rather than stowed: a KOTK rifle is 1,500 bulk against a 200-bulk
        // carrier, so a gun in the bag would leave no room for a single round (docs/86 §3.4).
        next = Rifle;
        InventoryItemInstance gun = inventory.CreateInstance(weapon, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        next = 0x9100_0000_0000_0001;

        if (rounds > 0)
        {
            InventoryItemInstance box = inventory.CreateInstance(ammoItemId, (uint)rounds);
            inventory.TryStow(box);
        }

        return (
            new SessionCombat(),
            new PlayerAmmoContext(inventory, 0x1001, AmmoOptions.Default),
            inventory);
    }

    private static PlayerInventory Bag(params int[] stacks)
    {
        ulong next = 0x5000_0000_0000_0001;
        var inventory = new PlayerInventory(0x1001, () => next++);
        inventory.Bootstrap();

        foreach (int size in stacks)
        {
            InventoryItemInstance stack = inventory.CreateInstance(TwoTwoThree, (uint)size);
            inventory.TryStow(stack);
        }

        return inventory;
    }
}
