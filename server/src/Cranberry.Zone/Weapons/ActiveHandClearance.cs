namespace Cranberry.Zone.Weapons;

/// <summary>
/// The narrowing predicate <c>ActiveHandRowGuard</c> consults before it will let an
/// <c>Equipment.SetCharacterEquipment</c> row name body slot 7 (RHand, the active hand).
/// <para>
/// <b>REGRESSION GUARD 5 is narrowed here, never removed.</b> With no clearance object the guard
/// behaves exactly as it has since docs/45: every slot-7 row is dropped, whatever it names. A
/// clearance object can only ever <em>permit</em> a row for a specific item guid it has positive
/// evidence for; it can never permit slot 7 in general, and it is consulted per row, not per packet.
/// </para>
/// <para>
/// <b>What "positive evidence" has to mean.</b> The crash instruction reads
/// <c>FUN_14228d840(item + 0xa8)-&gt;+0x38</c>, and <c>FUN_14228d840</c> returns
/// <c>comp-&gt;vtable[1](comp, group[comp+0x64].id)</c>. That is null - and the client dies - unless
/// <b>both</b> of these are true at once:
/// </para>
/// <list type="number">
/// <item>the weapon component owns a fire-group array with a valid current index, which only the
/// Weapon-class <c>ItemAdd</c> tail can deliver (<see cref="WeaponItemAddTail"/>); and</item>
/// <item>the id in that entry resolves inside the <c>ReferenceData "WeaponDefinitions"</c> list 1
/// this session actually sent (<see cref="WeaponDefinitionsBlob"/>).</item>
/// </list>
/// <para>
/// A clearance implementation that checks fewer than both is a crash waiting to happen, so the only
/// implementation in the tree - <see cref="WeaponFireGroupLedger"/> - records both halves and is
/// tested on both.
/// </para>
/// </summary>
public interface IActiveHandClearance
{
    /// <summary>
    /// True when this session has demonstrably delivered, for <paramref name="itemGuid"/>, a
    /// fire-group array whose current entry resolves in the weapon-definition table it sent.
    /// </summary>
    bool IsClearedForActiveHand(ulong itemGuid);
}

/// <summary>
/// The per-session record of what fire-group data actually went out, and the only
/// <see cref="IActiveHandClearance"/> in the tree.
/// <para>
/// It is a <b>ledger of sends, not of intentions</b>: <see cref="RecordFireGroupsDelivered"/> is
/// called after the <c>ItemAdd</c> has been written, and <see cref="DeclareFireGroupsAvailable"/>
/// after the <c>ReferenceData</c> has been written. Nothing is cleared on the strength of an option
/// being set - which is the docs/32 evidence rule applied to the one code path that can crash the
/// client.
/// </para>
/// <para>
/// <b>Not thread-safe by itself.</b> One instance belongs to one connection and is touched only from
/// that connection's send path, the same ownership every other per-connection state in the zone has.
/// </para>
/// </summary>
public sealed class WeaponFireGroupLedger : IActiveHandClearance
{
    private readonly HashSet<uint> _available = [];
    private readonly Dictionary<ulong, uint> _delivered = [];

    /// <summary>
    /// True once a <c>ReferenceData "WeaponDefinitions"</c> carrying at least one list-1 record has
    /// been written for this session. Until then nothing can be cleared, because
    /// <c>comp-&gt;vtable[1](id)</c> would return null for every id.
    /// </summary>
    public bool WeaponDefinitionsSent { get; private set; }

    /// <summary>Fire-group ids this session's list 1 carries.</summary>
    public IReadOnlyCollection<uint> AvailableFireGroupIds => _available;

    /// <summary>Item guids that have had a real Weapon <c>ItemAdd</c> tail written for them.</summary>
    public IReadOnlyCollection<ulong> ItemsWithFireGroups => _delivered.Keys;

    /// <summary>
    /// Called <b>after</b> the <c>ReferenceData "WeaponDefinitions"</c> packet has been written, with
    /// the ids its list 1 carries.
    /// </summary>
    public void DeclareFireGroupsAvailable(IEnumerable<uint> fireGroupIds)
    {
        ArgumentNullException.ThrowIfNull(fireGroupIds);
        foreach (uint fireGroupId in fireGroupIds)
        {
            _available.Add(fireGroupId);
        }

        WeaponDefinitionsSent = _available.Count > 0;
    }

    /// <summary>
    /// Called <b>after</b> a <see cref="WeaponItemAdd"/> has been written, with the tail that went
    /// with it. Records the item only when the tail is one the crash site can survive
    /// (<see cref="WeaponItemAddTail.IsSafeForActiveHand"/>) <em>and</em> its current group's id is
    /// one this session declared.
    /// </summary>
    /// <returns>True when the item is now cleared for the active hand.</returns>
    public bool RecordFireGroupsDelivered(ulong itemGuid, WeaponItemAddTail tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (itemGuid == 0 || !tail.IsSafeForActiveHand)
        {
            return false;
        }

        uint currentGroupId = tail.CurrentGroup!.FireGroupId;
        if (!_available.Contains(currentGroupId))
        {
            // The tail would build an array whose current entry names a group the client cannot
            // resolve - FUN_14228d840 still returns 0 and the row would still crash.
            return false;
        }

        _delivered[itemGuid] = currentGroupId;
        return true;
    }

    /// <summary>Forget an item - a grant that was rolled back, or an item that left the inventory.</summary>
    public void Forget(ulong itemGuid) => _delivered.Remove(itemGuid);

    /// <summary>The fire group an item's current index selects, or null when the item is not cleared.</summary>
    public uint? DeliveredFireGroupId(ulong itemGuid) =>
        _delivered.TryGetValue(itemGuid, out uint fireGroupId) ? fireGroupId : null;

    /// <inheritdoc />
    public bool IsClearedForActiveHand(ulong itemGuid) =>
        WeaponDefinitionsSent && itemGuid != 0 && _delivered.ContainsKey(itemGuid);
}
