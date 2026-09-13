using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// The three stages of docs/58 §11 and the narrowed REGRESSION GUARD 5 - docs/60 §2 and §4.
/// <para>
/// <b>These are the safety tests.</b> The owner is away and cannot play-test; the single worst
/// outcome available to this wave is a build that hard-crashes their client on return. Every test
/// here exists to make that outcome fail the suite instead.
/// </para>
/// </summary>
public sealed class WeaponStageTests
{
    private const uint RHand = ActiveHandRowGuard.ActiveHandSlotId;
    private const ulong SelfGuid = 0x3100_0000_0000_0001;
    private const ulong ItemGuid = 0x3100_0000_0000_0004;
    private const uint Ar15 = 2425;

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    private static InventoryItem Record(uint definitionId, ulong itemGuid = ItemGuid) =>
        new(definitionId, itemGuid, Count: 1, OwnerGuid: SelfGuid, ContainerGuid: 0,
            ContainerDefinitionId: 0, SlotId: 1);

    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(entry => entry.Name, entry => entry.Value);
        return name => map.GetValueOrDefault(name);
    }

    // ------------------------------------------------------------------ the defaults

    /// <summary>
    /// <b>EVERY stage is ON by default from wave 9</b>, and <c>AllOff</c> is still all off.
    /// <para>
    /// The staged rollout was correct engineering while the owner was away and could not attribute
    /// a crash. It is the wrong default now that he play-tests: with every stage off he cannot hold,
    /// fire or swing anything, and the August client said so itself - two
    /// <c>CreateItem - weapon definition not found for weapon ID 1374 / 1405</c> lines in
    /// <c>Client\Logs\WeaponErrors.log</c> at the two instants he picked a gun up, and
    /// <c>player does not have anything equipped in the primary weapon slot!</c> in
    /// <c>FirstPersonArms.log</c>. docs/89 §1.
    /// </para>
    /// <para>
    /// The safety properties are unchanged and are pinned by the rest of this class:
    /// <c>Effective</c> still refuses stage 3 without stages 1+2, and guard 5 is narrowed rather
    /// than removed. What changed is only which way the switches point.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryStageIsOnByDefaultAndAllOffIsStillAllOff()
    {
        var options = new WeaponStageOptions();

        Assert.True(options.SendWeaponDefinitions);          // stage 1
        Assert.True(options.PopulateWeaponDefinitions);      // stage 1b - the list CreateItem hashes
        Assert.True(options.PopulateFireGroups);             // list 1
        Assert.True(options.WriteWeaponItemAddTail);         // stage 2
        Assert.True(options.AllowWielding);                  // stage 3
        Assert.True(options.Effective.AllowWielding);        // ...and nothing refuses it

        // The default is no longer the rollback, so AllOff has to spell every field out or it
        // silently stops being the wave-5 control.
        Assert.NotEqual(WeaponStageOptions.AllOff, WeaponStageOptions.Default);
        Assert.False(WeaponStageOptions.AllOff.SendWeaponDefinitions);
        Assert.False(WeaponStageOptions.AllOff.PopulateWeaponDefinitions);
        Assert.False(WeaponStageOptions.AllOff.PopulateFireGroups);
        Assert.False(WeaponStageOptions.AllOff.WriteWeaponItemAddTail);
        Assert.False(WeaponStageOptions.AllOff.AllowWielding);

        // The default session now builds the table the client asks for, and a real weapon tail.
        var shipped = new WeaponSession();
        Assert.True(shipped.TryCreateWeaponDefinitions(out _));
        Assert.NotNull(shipped.CreateTail(Ar15));
        Assert.NotNull(shipped.Clearance);

        // And AllOff is still, exactly, wave 5.
        var control = new WeaponSession(WeaponStageOptions.AllOff);
        Assert.Null(control.Clearance);
        Assert.Null(control.CreateTail(Ar15));
        Assert.False(control.TryCreateWeaponDefinitions(out _));
    }

    /// <summary>
    /// <b>The two weapon ids the August client named by number are in the table Cranberry now
    /// sends.</b> This is the test that would have failed the wave-8 build, had it been written:
    /// item 1374 (the pump shotgun he picked up at 20:38:08) resolves to <c>WEAPON_ID</c> 1374 and
    /// item 2229 (20:38:19) to <c>WEAPON_ID</c> 1405 - the exact two numbers in
    /// <c>WeaponErrors.log</c> - and both are list-0 rows of the default blob.
    /// </summary>
    [Fact]
    public void TheDefaultBlobCarriesTheTwoWeaponIdsTheClientAskedForByNumber()
    {
        var session = new WeaponSession();
        Assert.True(session.TryCreateWeaponDefinitions(out _));

        uint[] carried = (session.Blob.WeaponDefinitions ?? [])
            .Select(definition => definition.WeaponDefinitionId)
            .ToArray();

        Assert.Contains(1374u, carried);   // WeaponErrors.log 20:38:08
        Assert.Contains(1405u, carried);   // WeaponErrors.log 20:38:19
        Assert.Contains(12u, carried);     // item 85 "Fists"
    }

    /// <summary>
    /// <b>Stage 1 has a zero-layout-exposure first run.</b>
    /// <c>CRANBERRY_WEAPON_DEFINITIONS=1 CRANBERRY_WEAPON_DEFS_LIST1=0</c> sends the 32-byte empty
    /// envelope - eight <c>i32 0</c>s, a well-formed table - which proves the type name, the
    /// dispatch and the envelope without putting one byte of the DERIVED 53-byte
    /// <c>FireGroupRecord</c> on a wire. Before this switch existed, list 1 was populated
    /// unconditionally and the only thing stage 1 could send was 3,212 bytes of that layout.
    /// </summary>
    [Fact]
    public void StageOneCanShipTheEmptyEnvelopeAsItsFirstRun()
    {
        var control = new WeaponStageOptions
        {
            SendWeaponDefinitions = true,
            PopulateFireGroups = false,
            PopulateWeaponDefinitions = false,
        };
        var session = new WeaponSession(control);

        Assert.True(session.TryCreateWeaponDefinitions(out ReferenceData packet));
        Assert.Equal(WeaponDefinitionsBlob.EmptyLength, session.Blob.Length);
        Assert.Equal(32, session.Blob.Length);
        Assert.Equal(new byte[WeaponDefinitionsBlob.EmptyLength], session.Blob.ToArray());
        Assert.Equal(WeaponDefinitionsBlob.TypeName, packet.TypeName);

        // And it leaves the ledger honest: nothing was declared, so nothing can ever be cleared.
        session.MarkWeaponDefinitionsSent();
        Assert.False(session.Ledger.WeaponDefinitionsSent);
        Assert.Empty(session.Ledger.AvailableFireGroupIds);
    }

    /// <summary>
    /// With list 1 on - the default once stage 1 is enabled - the ledger declares the ids the blob
    /// actually carried, not the static table.
    /// </summary>
    [Fact]
    public void WithListOneOnTheLedgerDeclaresWhatTheBlobCarried()
    {
        var session = new WeaponSession(new WeaponStageOptions { SendWeaponDefinitions = true });

        Assert.True(session.TryCreateWeaponDefinitions(out _));
        session.MarkWeaponDefinitionsSent();

        Assert.True(session.Ledger.WeaponDefinitionsSent);
        Assert.Equal(
            (session.Blob.FireGroups ?? []).Select(g => g.FireGroupId).ToHashSet(),
            session.Ledger.AvailableFireGroupIds.ToHashSet());
    }

    /// <summary>
    /// <b>The rollback is still exact.</b> Under <c>AllOff</c> - i.e.
    /// <c>CRANBERRY_WEAPON_DEFINITIONS=0 CRANBERRY_WEAPON_TAIL=0 CRANBERRY_WIELD=0</c> - a weapon's
    /// <c>ItemAdd</c> is byte-identical to what wave 5 sent, and the shipped default is not, because
    /// it now carries the 68-byte Weapon tail. Both halves matter: one is the revert, the other is
    /// the feature.
    /// </summary>
    [Fact]
    public void TheAllOffRollbackIsStillByteIdenticalToWaveFive()
    {
        InventoryItem item = Record(Ar15);

        Assert.Equal(
            Bytes(new ItemAdd(SelfGuid, item).WriteTo),
            Bytes(new WeaponSession(WeaponStageOptions.AllOff).CreateItemAdd(SelfGuid, item)));

        Assert.NotEqual(
            Bytes(new ItemAdd(SelfGuid, item).WriteTo),
            Bytes(new WeaponSession().CreateItemAdd(SelfGuid, item)));
    }

    /// <summary>
    /// <b>Stage 3 cannot be enabled without stage 2.</b> A slot-7 row without the tail that builds
    /// the fire-group array is precisely the docs/45 minidump, so the options object refuses the
    /// combination rather than trusting a call site - and says so, so the boot log can too.
    /// </summary>
    [Fact]
    public void WieldingWithoutTheTailIsRefused()
    {
        var reckless = new WeaponStageOptions { AllowWielding = true, WriteWeaponItemAddTail = false };

        Assert.True(reckless.WieldingRefusedForMissingTail);
        Assert.False(reckless.Effective.AllowWielding);
        Assert.Null(new WeaponSession(reckless).Clearance);
        Assert.Contains("IGNORED", reckless.Describe(), StringComparison.Ordinal);

        var proper = new WeaponStageOptions
        {
            AllowWielding = true,
            WriteWeaponItemAddTail = true,
            SendWeaponDefinitions = true,
        };
        Assert.False(proper.WieldingRefusedForMissingTail);
        Assert.True(proper.Effective.AllowWielding);
    }

    /// <summary>
    /// <b>Stage 3 cannot be enabled without a stage 1 that actually carried fire groups.</b> A tail's
    /// group id is resolved against list 1; for an id the client was never sent
    /// <c>FUN_14228d840</c> returns 0 and the slot-7 row is the same docs/45 null deref.
    /// <para>
    /// The ledger has always closed this hole - <c>IsClearedForActiveHand</c> short-circuits on
    /// <c>WeaponDefinitionsSent</c> - but the rule was implicit, and it became load-bearing the
    /// moment stage 1 stopped defaulting on: <c>CRANBERRY_WEAPON_TAIL=1 CRANBERRY_WIELD=1</c> alone
    /// is now the reachable combination, not an abuse of the switches.
    /// </para>
    /// </summary>
    [Fact]
    public void WieldingWithoutTheTableIsRefused()
    {
        var noTable = new WeaponStageOptions
        {
            AllowWielding = true,
            WriteWeaponItemAddTail = true,
            SendWeaponDefinitions = false,
        };

        Assert.False(noTable.WieldingRefusedForMissingTail);
        Assert.True(noTable.WieldingRefusedForMissingTable);
        Assert.False(noTable.Effective.AllowWielding);
        Assert.Null(new WeaponSession(noTable).Clearance);
        Assert.Contains("IGNORED", noTable.Describe(), StringComparison.Ordinal);

        // An EMPTY list 1 is refused for exactly the same reason - the envelope resolves nothing.
        var emptyList = noTable with { SendWeaponDefinitions = true, PopulateFireGroups = false };
        Assert.True(emptyList.WieldingRefusedForMissingTable);
        Assert.False(emptyList.Effective.AllowWielding);

        var proper = noTable with { SendWeaponDefinitions = true };
        Assert.False(proper.WieldingRefusedForMissingTable);
        Assert.True(proper.Effective.AllowWielding);
    }

    /// <summary>
    /// The environment switches, and the deliberate strictness of the parse: only the exact strings
    /// <c>"1"</c> and <c>"0"</c> move a switch, so a typo leaves the default rather than silently
    /// changing how a match plays. From wave 9 every stage defaults ON, so <c>"0"</c> is the
    /// interesting direction and it is what the owner is given as a revert.
    /// </summary>
    [Fact]
    public void OnlyTheExactStringZeroRevertsAStage()
    {
        // A typo leaves the shipped default, whichever way it points.
        Assert.True(WeaponStageOptions.FromEnvironment(Env()).WriteWeaponItemAddTail);
        Assert.True(WeaponStageOptions.FromEnvironment(Env((WeaponStageOptions.TailVariable, "false"))).WriteWeaponItemAddTail);
        Assert.True(WeaponStageOptions.FromEnvironment(Env((WeaponStageOptions.WieldVariable, "no"))).AllowWielding);

        // Every stage is live with nothing set at all - the whole point of the wave.
        WeaponStageOptions shipped = WeaponStageOptions.FromEnvironment(Env());
        Assert.True(shipped.SendWeaponDefinitions);
        Assert.True(shipped.PopulateWeaponDefinitions);
        Assert.True(shipped.PopulateFireGroups);
        Assert.True(shipped.Effective.AllowWielding);

        // CRANBERRY_WEAPON_DEFINITIONS=0 is the whole-feature revert: no table, so stage 3 is
        // refused rather than left silently half-armed.
        WeaponStageOptions noTable = WeaponStageOptions.FromEnvironment(
            Env((WeaponStageOptions.SendDefinitionsVariable, "0")));
        Assert.False(noTable.SendWeaponDefinitions);
        Assert.True(noTable.WieldingRefusedForMissingTable);
        Assert.False(noTable.Effective.AllowWielding);

        // CRANBERRY_WEAPON_TAIL=0 refuses stage 3 for the other reason.
        WeaponStageOptions noTail = WeaponStageOptions.FromEnvironment(
            Env((WeaponStageOptions.TailVariable, "0")));
        Assert.True(noTail.WieldingRefusedForMissingTail);
        Assert.False(noTail.Effective.AllowWielding);

        // CRANBERRY_WIELD=0 leaves the table and the tail on and the hand empty.
        WeaponStageOptions noWield = WeaponStageOptions.FromEnvironment(
            Env((WeaponStageOptions.WieldVariable, "0")));
        Assert.True(noWield.SendWeaponDefinitions);
        Assert.True(noWield.WriteWeaponItemAddTail);
        Assert.False(noWield.AllowWielding);

        // ...and the two list switches still move independently.
        Assert.False(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.PopulateFireGroupsVariable, "0")))
            .PopulateFireGroups);
        Assert.False(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.PopulateDefinitionsVariable, "0")))
            .PopulateWeaponDefinitions);

        // Wave 13 (docs/107 s1): the tail's magazine is ON by default and CRANBERRY_WEAPON_TAIL_AMMO=0
        // is its revert - the one tail switch that changes the wire's LENGTH, so it is pinned here
        // rather than left to the byte tests alone.
        Assert.True(shipped.WriteWeaponTailMagazine);
        Assert.False(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.TailAmmoVariable, "0")))
            .WriteWeaponTailMagazine);
        Assert.True(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.TailAmmoVariable, "false")))
            .WriteWeaponTailMagazine);
        Assert.False(WeaponStageOptions.AllOff.WriteWeaponTailMagazine);
        Assert.Contains("tailMagazine=ON", shipped.Describe(), System.StringComparison.Ordinal);

        // Wave 14 (docs/107 addendum): the projectile table is ON by default, because list 4 now
        // gives a fire mode a way to reach it. So are the two new list switches that feed it.
        Assert.True(shipped.SendProjectileDefinitions);
        Assert.True(shipped.PopulateAmmoSlots);
        Assert.True(shipped.PopulateFireModeProjectiles);
        Assert.False(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.ProjectileDefinitionsVariable, "0")))
            .SendProjectileDefinitions);
        Assert.False(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.PopulateAmmoSlotsVariable, "0")))
            .PopulateAmmoSlots);
        Assert.False(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.PopulateFireModeProjectilesVariable, "0")))
            .PopulateFireModeProjectiles);
        Assert.True(WeaponStageOptions
            .FromEnvironment(Env((WeaponStageOptions.PopulateAmmoSlotsVariable, "false")))
            .PopulateAmmoSlots);
        Assert.False(WeaponStageOptions.AllOff.PopulateAmmoSlots);
        Assert.False(WeaponStageOptions.AllOff.PopulateFireModeProjectiles);
        Assert.Contains("projectiles=ON", shipped.Describe(), System.StringComparison.Ordinal);
        Assert.Contains("list4=ON", shipped.Describe(), System.StringComparison.Ordinal);
        Assert.Contains("ammoSlots=ON", shipped.Describe(), System.StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ REGRESSION GUARD 5

    /// <summary>
    /// <b>GUARD 5, unnarrowed.</b> With no clearance - the shipped default - a slot-7 row for an
    /// item with no fire-group data is dropped, and the packet is byte-identical to one that never
    /// carried the row. This is the wave-4 fix and it must survive every future wave.
    /// </summary>
    [Fact]
    public void GuardFiveStillBlocksASlotSevenRowForAnItemWithNoFireGroupData()
    {
        var withRow = new SetCharacterEquipmentWithSlots(
            4099, Slots: [new EquipmentSlotRow(1, 0x3100000000000003), new EquipmentSlotRow(RHand, ItemGuid)]);
        var withoutRow = new SetCharacterEquipmentWithSlots(
            4099, Slots: [new EquipmentSlotRow(1, 0x3100000000000003)]);

        Assert.Equal(Bytes(withoutRow.WriteTo), Bytes(withRow.WriteTo));
        Assert.True(ActiveHandRowGuard.IsCrashingRow(new EquipmentSlotRow(RHand, ItemGuid), clearance: null));

        // A ledger that has been told nothing is exactly as strict as no ledger at all.
        var empty = new WeaponFireGroupLedger();
        Assert.True(ActiveHandRowGuard.IsCrashingRow(new EquipmentSlotRow(RHand, ItemGuid), empty));
        Assert.Equal(Bytes(withoutRow.WriteTo), Bytes((withRow with { Clearance = empty }).WriteTo));
    }

    /// <summary>
    /// <b>The clearance has to have BOTH halves.</b> The crash site reads
    /// <c>comp-&gt;vtable[1](group[comp+0x64].id)</c>, so a fire-group array with an id the
    /// <c>WeaponDefinitions</c> table does not carry is still a null dereference. The ledger refuses
    /// to clear an item until the table has been declared <em>and</em> a safe tail was written for
    /// that specific guid.
    /// </summary>
    [Fact]
    public void ClearanceNeedsBothTheTableAndTheTail()
    {
        var ledger = new WeaponFireGroupLedger();
        WeaponItemAddTail tail = AugustWeaponTable.CreateTail(Ar15)!;

        // Tail first, no table: refused, because comp->vtable[1](6) would still return null.
        Assert.False(ledger.RecordFireGroupsDelivered(ItemGuid, tail));
        Assert.False(ledger.IsClearedForActiveHand(ItemGuid));

        ledger.DeclareFireGroupsAvailable(AugustWeaponTable.FireGroupIds);
        Assert.True(ledger.WeaponDefinitionsSent);

        // Table but no tail: still refused - the array would be empty.
        Assert.False(ledger.IsClearedForActiveHand(ItemGuid));

        // Both: cleared, and only for this guid.
        Assert.True(ledger.RecordFireGroupsDelivered(ItemGuid, tail));
        Assert.True(ledger.IsClearedForActiveHand(ItemGuid));
        Assert.False(ledger.IsClearedForActiveHand(ItemGuid + 1));
        Assert.False(ledger.IsClearedForActiveHand(0));
        Assert.Equal(6u, ledger.DeliveredFireGroupId(ItemGuid));

        // A tail the crash site could not survive is never recorded, table or no table.
        Assert.False(ledger.RecordFireGroupsDelivered(ItemGuid + 2, new WeaponItemAddTail([])));
        Assert.False(ledger.IsClearedForActiveHand(ItemGuid + 2));

        ledger.Forget(ItemGuid);
        Assert.False(ledger.IsClearedForActiveHand(ItemGuid));
    }

    /// <summary>
    /// A fire group whose id is <em>not</em> in the table this session sent can never be cleared,
    /// even though its tail is well formed. This is the case a hand-written call site would get
    /// wrong.
    /// </summary>
    [Fact]
    public void AFireGroupOutsideTheDeliveredTableIsNeverCleared()
    {
        var ledger = new WeaponFireGroupLedger();
        ledger.DeclareFireGroupsAvailable([6u]);

        WeaponItemAddTail elsewhere = new(
            [AugustWeaponTable.CreateGroup(fireGroupId: 999, clipSize: 30)]);

        Assert.True(elsewhere.IsSafeForActiveHand);                     // shape is fine
        Assert.False(ledger.RecordFireGroupsDelivered(ItemGuid, elsewhere));  // id is not
        Assert.False(ledger.IsClearedForActiveHand(ItemGuid));
    }

    // ------------------------------------------------------------------ stage 3, end to end

    /// <summary>
    /// The full staged sequence of docs/60 §5 run 3: table, then a weapon <c>ItemAdd</c> with the
    /// real tail, then - and only then - the slot-7 row survives the guard. One item, by guid.
    /// </summary>
    [Fact]
    public void WithAllThreeStagesOnTheClearedItemReachesSlotSeven()
    {
        var session = new WeaponSession(new WeaponStageOptions
        {
            SendWeaponDefinitions = true,
            WriteWeaponItemAddTail = true,
            AllowWielding = true,
        });

        Assert.True(session.TryCreateWeaponDefinitions(out ReferenceData table));
        Assert.Equal(WeaponDefinitionsBlob.TypeName, table.TypeName);
        session.MarkWeaponDefinitionsSent();

        InventoryItem gun = Record(Ar15);
        byte[] grant = Bytes(session.CreateItemAdd(SelfGuid, gun));
        Assert.Equal(149, grant.Length);           // the weapon tail went out, magazine and all
        Assert.True(session.Ledger.IsClearedForActiveHand(ItemGuid));

        var wielded = new SetCharacterEquipmentWithSlots(
            4099,
            Slots: [new EquipmentSlotRow(RHand, ItemGuid)],
            Clearance: session.Clearance);
        var other = new SetCharacterEquipmentWithSlots(
            4099,
            Slots: [new EquipmentSlotRow(RHand, ItemGuid + 1)],
            Clearance: session.Clearance);

        Assert.True(Bytes(wielded.WriteTo).Length > Bytes(other.WriteTo).Length);
        Assert.Equal(EquipmentSlotRow.MinimalLength, Bytes(wielded.WriteTo).Length - Bytes(other.WriteTo).Length);
    }

    /// <summary>
    /// Stage 2 alone changes the <c>ItemAdd</c> and <b>nothing else</b>: the guard is untouched, so
    /// a slot-7 row is still dropped even for the item whose tail just went out. This is docs/60
    /// §5 run 2 - the shape test that cannot crash.
    /// </summary>
    [Fact]
    public void StageTwoAloneNeverLetsAnythingReachSlotSeven()
    {
        var session = new WeaponSession(WeaponStageOptions.AllOff with { WriteWeaponItemAddTail = true, PopulateFireGroups = true });
        session.MarkWeaponDefinitionsSent();

        InventoryItem gun = Record(Ar15);
        Assert.Equal(145, Bytes(session.CreateItemAdd(SelfGuid, gun)).Length);
        Assert.True(session.Ledger.IsClearedForActiveHand(ItemGuid));    // the ledger knows...
        Assert.Null(session.Clearance);                                  // ...but nothing asks it

        var packet = new SetCharacterEquipmentWithSlots(
            4099, Slots: [new EquipmentSlotRow(RHand, ItemGuid)], Clearance: session.Clearance);
        var bare = new SetCharacterEquipmentWithSlots(4099, Slots: []);
        Assert.Equal(Bytes(bare.WriteTo), Bytes(packet.WriteTo));
    }

    /// <summary>
    /// A non-weapon grant is unaffected by every stage: same bytes, and it never enters the ledger.
    /// </summary>
    [Fact]
    public void NonWeaponGrantsAreUntouchedAtEveryStage()
    {
        var session = new WeaponSession(new WeaponStageOptions
        {
            WriteWeaponItemAddTail = true,
            AllowWielding = true,
        });
        session.MarkWeaponDefinitionsSent();

        InventoryItem bandage = Record(2423);
        Assert.Equal(
            Bytes(new ItemAdd(SelfGuid, bandage).WriteTo),
            Bytes(session.CreateItemAdd(SelfGuid, bandage)));
        Assert.False(session.Ledger.IsClearedForActiveHand(ItemGuid));
    }

    /// <summary>The boot line names every stage, so a play-test can never guess which ones ran.</summary>
    [Fact]
    public void TheDescriptionNamesEveryStage()
    {
        // The line docs/89 §6 tells the owner to read before he clicks anything.
        string line = new WeaponSession().Describe();

        Assert.Contains("definitions=ON", line, StringComparison.Ordinal);
        Assert.Contains("list0=ON", line, StringComparison.Ordinal);
        Assert.Contains("list1=ON", line, StringComparison.Ordinal);
        Assert.Contains("itemAddTail=ON", line, StringComparison.Ordinal);
        Assert.Contains("wielding=ON", line, StringComparison.Ordinal);

        // The wave-5 rollback still describes itself as such.
        string off = new WeaponSession(WeaponStageOptions.AllOff).Describe();
        Assert.Contains("definitions=off", off, StringComparison.Ordinal);
        Assert.Contains("itemAddTail=off", off, StringComparison.Ordinal);
        Assert.Contains("wielding=off", off, StringComparison.Ordinal);

        // And the empty-envelope control says so, in bytes as well as in words.
        string control = new WeaponSession(new WeaponStageOptions
        {
            SendWeaponDefinitions = true,
            PopulateFireGroups = false,
            PopulateWeaponDefinitions = false,
        }).Describe();
        Assert.Contains("definitions=ON", control, StringComparison.Ordinal);
        Assert.Contains("list1=off", control, StringComparison.Ordinal);
        Assert.Contains("32 blob bytes", control, StringComparison.Ordinal);
    }
}
