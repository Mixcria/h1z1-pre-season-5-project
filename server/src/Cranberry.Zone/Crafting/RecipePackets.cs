using Cranberry.Protocol;

namespace Cranberry.Zone.Crafting;

// The 0x26 RecipeBase family (s2c) and 09 1a Command.RecipeStart (c2s).
//
// docs/59 §1.5.2 corrects a decompiler lie that would otherwise have hidden this family: Ghidra
// types the dispatcher's forwarder FUN_140dc02d0 as `void FUN_140dc02d0(void)`, which reads as "the
// payload is discarded". Its three instructions are
//     mov rcx,[rip+0x031a9ed9]  ; DAT_143f6a1b0
//     mov rcx,[rcx+0x138]       ; the RecipeManager
//     jmp -> 0x141518250
// with rdx (payload) and r8 (length) untouched, so it is a tail call to
// FUN_141518250(recipeManager, payload, length) and the family is fully live.
//
// The sub-dispatcher's five arms were re-read this pass from
// out/wave6-prep/ghidra-recipes/_141518250_.../FUN_141518250_141518250.c and each arm's reader from
// out/wave6-prep/ghidra-recipes2. Every one of them REJECTS TRAILING BYTES: each arm ends with a
// variant of `if (0 < end - cursor) return;`, so a packet one byte too long is silently dropped.
// The lengths below are therefore exact, not minimums.

/// <summary>The <c>0x26 cPacketIdRecipeBase</c> family. Sub is a <b>u8</b>, not a u16.</summary>
public static class RecipeOpcodes
{
    /// <summary><c>cPacketIdRecipeBase</c>, registrations-1148 id <c>0x2600</c>.</summary>
    public const byte RecipeBase = ZoneOpcodes.RecipeBase;

    /// <summary>Learn one recipe (<c>FUN_141518250</c> arm 1). Body: one bare
    /// <see cref="RecipeRecord"/>. Raises the <c>"RecipeLearned"</c> Scaleform toast.</summary>
    public const byte AddSub = 0x01;

    /// <summary>Refresh one ingredient's live counts (arm 2, reader <c>FUN_141514120</c>).</summary>
    public const byte ComponentUpdateSub = 0x02;

    /// <summary>Forget one recipe (arm 3, reader <c>FUN_141514320</c>).</summary>
    public const byte RemoveSub = 0x03;

    /// <summary>Replace the whole recipe table (arm 9, reader <c>FUN_1415145e0</c>).</summary>
    public const byte ListSub = 0x09;

    /// <summary>Drive the "Crafting..." / "Ready to Craft" label (arm 0x0a, reader
    /// <c>FUN_141514240</c>) by dispatching <c>EVENT_RECIPE_CRAFTING_STATUS</c>.</summary>
    public const byte CraftingStatusSub = 0x0a;
}

/// <summary>
/// <c>0x26 01 Recipe.Add</c> - one recipe, mid-match.
/// <para>
/// <c>FUN_141518250</c> arm 1 allocates a node from the manager's pool, copies the parsed record in
/// with <c>FUN_141514f50</c>, stores the recipe id at <c>node+0x288</c>, links the node into the
/// manager's list and its 64-bucket hash at <c>manager+0xf0 + (recipeId &amp; 0x3f) * 8</c>, and
/// raises the Scaleform toast <c>"RecipeLearned"</c> whose text is
/// <see cref="RecipeRecord.ItemNameStringId"/> resolved through the localisation manager.
/// </para>
/// <para>The arm rejects any trailing byte, so the packet is exactly 2 + the record.</para>
/// </summary>
public sealed record RecipeAdd(RecipeRecord Recipe)
{
    public int Length => 2 + Recipe.Length;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(Recipe);
        w.WriteByte(RecipeOpcodes.RecipeBase);
        w.WriteByte(RecipeOpcodes.AddSub);
        Recipe.WriteTo(w);
    }
}

/// <summary>
/// <c>0x26 09 Recipe.List</c> - replace the whole recipe table.
/// <para>
/// <c>FUN_141518250</c> arm 9 clears the manager (<c>FUN_141519b20</c> / <c>FUN_141519a60</c>),
/// reads the list with <c>FUN_1415145e0</c> (which delegates to the same <c>FUN_140a54bc0</c> the
/// self record uses), then walks the array at its 0x278 stride calling
/// <c>FUN_141516280(manager, recipeId, record, 0)</c> per element - the identical entry point the
/// <c>SendSelfToClient</c> sink <c>FUN_140db5680</c> calls. That equivalence is why this packet is
/// Cranberry's <b>default</b> delivery path: it carries byte-for-byte the same records as the self
/// record's <c>0x11a</c> field but arrives on its own, after <c>ClientIsReady</c>, where a mistake
/// costs a missing crafting tab instead of a failed login (see <see cref="CraftingOptions"/>).
/// </para>
/// <para>
/// This also answers docs/59 §1.7 needle 5 ("does <c>0x26 09</c> work post-login?") by making the
/// post-login send the thing that ships first rather than the thing that ships last.
/// </para>
/// </summary>
public sealed record RecipeList(IReadOnlyList<RecipeRecord> Recipes)
{
    public int Length => 2 + RecipeRecord.ListLength(Recipes);

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(Recipes);
        w.WriteByte(RecipeOpcodes.RecipeBase);
        w.WriteByte(RecipeOpcodes.ListSub);
        RecipeRecord.WriteList(w, Recipes);
    }
}

/// <summary>
/// <c>0x26 02 Recipe.ComponentUpdate</c> - 18 bytes exactly
/// (<c>u8 0x26; u8 0x02; u32 recipeId; u32 componentIndex; u64 value</c>, reader
/// <c>FUN_141514120</c>).
/// <para>
/// Arm 2 finds the recipe by id, walks its component chain to <paramref name="ComponentIndex"/>
/// and writes the u64 into the component's <c>+0x28</c> - the field
/// <see cref="RecipeComponentRecord.LiveCounts"/> describes. It is the panel's
/// <c>countInInventory</c> / <c>quantityInProximity</c> refresh and nothing else; the panel is
/// perfectly usable without it, which is why docs/59 §1.6 put it out of the first cut and why
/// Cranberry sends it only when <see cref="CraftingOptions.SendComponentCounts"/> is on.
/// </para>
/// </summary>
public sealed record RecipeComponentUpdate(uint RecipeId, uint ComponentIndex, ulong Value)
{
    public const int Length = 18;

    /// <summary>Convenience over <see cref="RecipeComponentRecord.PackLiveCounts"/>.</summary>
    public static RecipeComponentUpdate Counts(
        uint recipeId,
        uint componentIndex,
        uint countInInventory,
        uint quantityInProximity = 0) =>
        new(recipeId, componentIndex, RecipeComponentRecord.PackLiveCounts(countInInventory, quantityInProximity));

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(RecipeOpcodes.RecipeBase);
        w.WriteByte(RecipeOpcodes.ComponentUpdateSub);
        w.WriteUInt32(RecipeId);
        w.WriteUInt32(ComponentIndex);
        w.WriteUInt64(Value);
    }
}

/// <summary>
/// <c>0x26 03 Recipe.Remove</c> - 7 bytes exactly
/// (<c>u8 0x26; u8 0x03; u32 recipeId; u8 flag</c>, reader <c>FUN_141514320</c>).
/// <para>
/// Arm 3 walks the hash bucket <c>manager+0xf0 + (recipeId &amp; 0x3f) * 8</c> comparing
/// <c>node+0x288</c> and unlinks the match. It returns early when <c>recipeId &lt; 1</c>, so recipe
/// id 0 is not addressable - one more reason Cranberry keys recipes by their output item id, which
/// is never zero.
/// </para>
/// </summary>
public sealed record RecipeRemove(uint RecipeId, bool Flag = false)
{
    public const int Length = 7;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(RecipeOpcodes.RecipeBase);
        w.WriteByte(RecipeOpcodes.RemoveSub);
        w.WriteUInt32(RecipeId);
        w.WriteBool(Flag);
    }
}

/// <summary>
/// The <c>status</c> byte of <see cref="RecipeCraftingStatus"/>.
/// <para>
/// Retail RecipeManager.as reads EVENT_RECIPE_CRAFTING_STATUS and sets isCrafting when the
/// second argument equals zero. A nonzero value ends the animation and enables the recipe again.
/// </summary>
public enum RecipeCraftingState : byte
{
    /// <summary>Idle - "Ready to Craft".</summary>
    Ready = 1,

    /// <summary>The busy timer is running - "Crafting...".</summary>
    Crafting = 0,

    /// <summary>Another craft holds the timer - "Currently crafting another item."</summary>
    Busy = 2,

    /// <summary>Refused - "You cannot craft that right now."</summary>
    Refused = 3,
}

/// <summary>
/// <c>0x26 0a Recipe.CraftingStatus</c> - 7 bytes exactly
/// (<c>u8 0x26; u8 0x0a; u32 recipeId; u8 status</c>, reader <c>FUN_141514240</c>).
/// <para>
/// Arm 0x0a dispatches the Scaleform event <c>"EVENT_RECIPE_CRAFTING_STATUS"</c> with the recipe id
/// and the status byte as its two arguments (<c>FUN_141513680(DAT_1451d2650, &amp;event,
/// &amp;value, &amp;value + 4)</c>). This is the packet that drives the crafting window's busy
/// label, and it is the only feedback a refused craft can produce inside the crafting window
/// itself - <c>Container.Error</c> prints to the console instead.
/// </para>
/// </summary>
public sealed record RecipeCraftingStatus(uint RecipeId, RecipeCraftingState Status)
{
    public const int Length = 7;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(RecipeOpcodes.RecipeBase);
        w.WriteByte(RecipeOpcodes.CraftingStatusSub);
        w.WriteUInt32(RecipeId);
        w.WriteByte((byte)Status);
    }
}

/// <summary>
/// <c>09 1b Command.ShowRecipeWindow</c> - the server opening the crafting tab.
/// <para>
/// Registered as <c>cCommandPacketIdShowRecipeWindow</c> (<c>registrations-1148</c> id
/// <c>0x1b000900</c>). Its body is <b>[BLOCKED]</b> for the same reason <c>09 1a</c>'s is - the
/// Command family has no per-packet serializer to decompile - so Cranberry writes the header only
/// and nothing sends this today. It exists so the opcode is named in one place rather than
/// rediscovered.
/// </para>
/// </summary>
public sealed record ShowRecipeWindow
{
    public const byte Opcode = ZoneOpcodes.CommandBase;
    public const ushort SubOpcode = 0x001b;
    public const int Length = 3;

    public void WriteTo(PacketWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte(Opcode);
        w.WriteUInt16(SubOpcode);
    }
}
