using Cranberry.Zone.Combat;

namespace Cranberry.Zone.Inventory;

/// <summary>
/// One ammunition stack that moved, so the caller can send the packet that says so.
/// </summary>
/// <param name="ItemGuid">The stack's instance guid.</param>
/// <param name="ItemDefinitionId">The round.</param>
/// <param name="CountAfter">Units left in that stack; 0 when <paramref name="Removed"/>.</param>
/// <param name="Removed">The stack is gone - the caller sends <c>11 04 ItemDelete</c>, not
/// <c>11 03 ItemUpdate</c>.</param>
/// <param name="Created">The stack is new - the caller sends <c>11 02 ItemAdd</c>.</param>
public readonly record struct AmmoStackChange(
    ulong ItemGuid,
    uint ItemDefinitionId,
    uint CountAfter,
    bool Removed,
    bool Created);

/// <summary>
/// <b>The reserve is the bag.</b> Everything the reload path needs to know about carried
/// ammunition, and nothing else: how many rounds of a calibre the player has, how to spend them,
/// and how to put them back.
///
/// <para>
/// <b>Why this interface exists at all (S3 row 6, D118).</b> Before lane 1F Cranberry conjured
/// ammunition: <c>ShooterCombatState.Reload</c> set the magazine to
/// <c>RetailBalance.ClipSize(...)</c> out of nothing, so a reload could never fail and
/// <c>UnloadWeapon</c> had to be a named refusal, because unload → reload → unload would have
/// minted rounds. The owner's server feeds a reload from the carried stacks
/// (<c>ZoneInventoryActions.TryConsumeAmmo</c>, <c>ZoneCombat</c>'s <c>IAmmoStore.Count/Take</c>
/// over the session inventory, S6 §4.5) and that rule - not his code - is what crosses under D53.
/// </para>
///
/// <para>
/// <b>Smallest stacks first</b> is his rule too, and it is the one that matters for the wire: a
/// reload that empties a small stack completely produces one <c>ItemDelete</c> instead of leaving
/// a scatter of one- and two-round tiles behind in the panel.
/// </para>
/// </summary>
public interface IAmmoStore
{
    /// <summary>Rounds of this calibre the player is carrying, across every stack.</summary>
    int Count(uint ammoItemDefinitionId);

    /// <summary>
    /// Spend up to <paramref name="wanted"/> rounds, smallest stacks first. Returns how many were
    /// actually taken - fewer than asked is a <b>partial reload</b>, not a failure - and appends one
    /// entry per stack that moved.
    /// </summary>
    int Take(uint ammoItemDefinitionId, int wanted, IList<AmmoStackChange> changes);

    /// <summary>
    /// Put <paramref name="rounds"/> rounds back into the bag as a stack - the model half of
    /// <c>UnloadWeapon</c>. False when there is nowhere to put them, in which case nothing changed.
    /// </summary>
    bool Grant(uint ammoItemDefinitionId, int rounds, IList<AmmoStackChange> changes);
}

/// <summary>
/// <see cref="IAmmoStore"/> over one player's <see cref="PlayerInventory"/>. It touches the model
/// through <see cref="PlayerInventory.RemoveUnits"/> and <see cref="PlayerInventory.TryPickUp"/>
/// only, so a spent stack releases its container slot and a granted one obeys the same bulk and
/// stack-size rules a ground pickup does.
/// </summary>
public sealed class PlayerInventoryAmmoStore(PlayerInventory inventory) : IAmmoStore
{
    private readonly PlayerInventory _inventory =
        inventory ?? throw new ArgumentNullException(nameof(inventory));

    /// <summary>The inventory this store spends from.</summary>
    public PlayerInventory Inventory => _inventory;

    /// <inheritdoc />
    public int Count(uint ammoItemDefinitionId)
    {
        if (ammoItemDefinitionId == 0)
        {
            return 0;
        }

        int total = 0;
        foreach (InventoryItemInstance item in _inventory.Items.Values)
        {
            if (item.DefinitionId == ammoItemDefinitionId)
            {
                total += (int)item.Count;
            }
        }

        return total;
    }

    /// <inheritdoc />
    public int Take(uint ammoItemDefinitionId, int wanted, IList<AmmoStackChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (ammoItemDefinitionId == 0 || wanted <= 0)
        {
            return 0;
        }

        // Smallest first (his rule); the guid breaks ties so a reload is deterministic and a test
        // can freeze an expectation.
        List<InventoryItemInstance> stacks =
            [.. _inventory.Items.Values
                .Where(item => item.DefinitionId == ammoItemDefinitionId)
                .OrderBy(item => item.Count)
                .ThenBy(item => item.Guid)];

        int taken = 0;
        foreach (InventoryItemInstance stack in stacks)
        {
            if (taken >= wanted)
            {
                break;
            }

            uint want = (uint)Math.Min(wanted - taken, (int)stack.Count);
            ulong guid = stack.Guid;
            uint moved = _inventory.RemoveUnits(guid, want);
            if (moved == 0)
            {
                continue;
            }

            taken += (int)moved;
            bool gone = !_inventory.Items.ContainsKey(guid);
            changes.Add(new AmmoStackChange(
                guid,
                ammoItemDefinitionId,
                gone ? 0u : stack.Count,
                Removed: gone,
                Created: false));
        }

        return taken;
    }

    /// <inheritdoc />
    public bool Grant(uint ammoItemDefinitionId, int rounds, IList<AmmoStackChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (ammoItemDefinitionId == 0 || rounds <= 0)
        {
            return false;
        }

        InventoryPlacement plan = _inventory.TryPickUp(
            ammoItemDefinitionId, (uint)rounds, out InventoryItemInstance? instance);

        if (instance is null || plan.Kind == InventoryPlacementKind.Refused)
        {
            return false;
        }

        changes.Add(new AmmoStackChange(
            instance.Guid,
            ammoItemDefinitionId,
            instance.Count,
            Removed: false,
            Created: plan.Kind != InventoryPlacementKind.Stack));
        return true;
    }
}

/// <summary>
/// <b>The bridge between one live session's bag and its weapon loop</b> - the object
/// <c>WeaponFireArm</c> is handed so that it can spend rounds without knowing anything about
/// containers, and announce what it spent without knowing anything about item records.
/// <para>
/// It exists so that the arm stays a pure function of its inputs: every packet it wants sent comes
/// back in a list, and the caller drains that list exactly as it already drains the hit marker and
/// the <c>82 08</c> reply.
/// </para>
/// </summary>
public sealed class PlayerAmmoContext
{
    private readonly List<AmmoStackChange> _changes = [];

    public PlayerAmmoContext(PlayerInventory inventory, ulong ownerCharacterGuid, AmmoOptions options)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);
        Inventory = inventory;
        OwnerCharacterGuid = ownerCharacterGuid;
        Options = options;
        Store = new PlayerInventoryAmmoStore(inventory);
    }

    /// <summary>The bag these rounds live in.</summary>
    public PlayerInventory Inventory { get; }

    /// <summary>Whose inventory it is - the target guid of every <c>0x11</c> packet below.</summary>
    public ulong OwnerCharacterGuid { get; }

    /// <summary>The switches: whether the bag is used at all, and whether <c>11 03</c> goes out.</summary>
    public AmmoOptions Options { get; }

    /// <summary>The store itself, for callers that only want the model.</summary>
    public IAmmoStore Store { get; }

    /// <summary>Rounds of this calibre the player is carrying.</summary>
    public int Count(uint ammoItemDefinitionId) => Store.Count(ammoItemDefinitionId);

    /// <summary>
    /// Spend up to <paramref name="wanted"/> rounds and append the packets that tell the client so:
    /// <c>11 03 ItemUpdate</c> for a stack that shrank, <c>11 04 ItemDelete</c> for one that is
    /// gone. Returns how many were taken.
    /// </summary>
    public int Take(uint ammoItemDefinitionId, int wanted, List<byte[]> announcements)
    {
        ArgumentNullException.ThrowIfNull(announcements);
        _changes.Clear();
        int taken = Store.Take(ammoItemDefinitionId, wanted, _changes);
        Announce(announcements);
        return taken;
    }

    /// <summary>
    /// Put rounds back in the bag - the unload - and append <c>11 02 ItemAdd</c> for a new stack or
    /// <c>11 03 ItemUpdate</c> for one that grew.
    /// </summary>
    public bool Grant(uint ammoItemDefinitionId, int rounds, List<byte[]> announcements)
    {
        ArgumentNullException.ThrowIfNull(announcements);
        _changes.Clear();
        bool granted = Store.Grant(ammoItemDefinitionId, rounds, _changes);
        Announce(announcements);
        return granted;
    }

    /// <summary>
    /// The <c>11 03</c> that carries a weapon's new durability, or null when the switch is off or
    /// the guid is not an item this inventory holds.
    /// </summary>
    public byte[]? WeaponDurability(ulong itemGuid, int currentDurability)
    {
        if (!Options.SendItemUpdate
            || !Inventory.Items.TryGetValue(itemGuid, out InventoryItemInstance? weapon))
        {
            return null;
        }

        return ItemUpdate
            .ForDurability(OwnerCharacterGuid, weapon, currentDurability, Options.MaxDurability)
            .ToBytes();
    }

    private void Announce(List<byte[]> announcements)
    {
        foreach (AmmoStackChange change in _changes)
        {
            if (change.Removed)
            {
                // A stack that reached zero is DELETED, never updated to 0: FUN_141479ae0 would
                // happily write a zero count and leave an empty tile in the panel.
                using var writer = new Protocol.PacketWriter(ItemDelete.Length);
                new ItemDelete(OwnerCharacterGuid, change.ItemGuid).WriteTo(writer);
                announcements.Add(writer.Written.ToArray());
                continue;
            }

            if (!Inventory.Items.TryGetValue(change.ItemGuid, out InventoryItemInstance? stack))
            {
                continue;
            }

            if (change.Created)
            {
                // A stack the client has never been told about: an update for an unknown guid is a
                // no-op in FUN_140dbfcb0, so a genuinely new stack has to be an add.
                var add = new ItemAdd(OwnerCharacterGuid, stack.ToRecord(OwnerCharacterGuid));
                using var writer = new Protocol.PacketWriter(add.Length);
                add.WriteTo(writer);
                announcements.Add(writer.Written.ToArray());
                continue;
            }

            if (Options.SendItemUpdate)
            {
                announcements.Add(ItemUpdate.ForStack(OwnerCharacterGuid, stack).ToBytes());
            }
        }

        _changes.Clear();
    }
}

/// <summary>
/// The magazines one session is tracking, seen from the inventory side.
/// <para>
/// <c>ShooterCombatState</c> implements it. It exists so that <c>ItemVerbs.Unload</c> can be a real
/// unload without <c>Cranberry.Zone.Inventory</c> having to know what a fire hint is - and so that
/// a caller that has <b>no</b> magazines to offer gets D118's named refusal instead of silently
/// unloading nothing.
/// </para>
/// </summary>
public interface IWeaponMagazines
{
    /// <summary>Rounds in this instance's magazine; -1 when the session does not know the guid.</summary>
    int MagazineOf(ulong itemGuid);

    /// <summary>Empty it, and answer what came out; -1 when the guid is unknown.</summary>
    int UnloadMagazine(ulong itemGuid);
}
