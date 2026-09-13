using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone.Emotes;

namespace Cranberry.Zone;

/// <summary>
/// The player record carried by <c>SendSelfToClient</c> (ClientProtocol_1148 base opcode 3), in
/// the exact order the August client's loader <c>FUN_140a31140</c> and its sub-loaders consume it.
/// The loader ends with <c>if (streamError || cursor - base &lt; length) FUN_1409e1080()</c>, where
/// <c>FUN_1409e1080</c> writes <c>0xbadbeef</c> to address zero: the record must be consumed
/// completely and exactly, so every field below is written even when it is empty.
///
/// Field names are this project's own unless the client's code fixed a role; offsets in comments
/// are the player-object offsets the loader stores into (dumped under
/// <c>out\ghidra-aug\fable-self-bootstrap\_140a31140</c>, read sequence extracted with
/// <c>tools\selfschema\selfschema.py</c>).
/// </summary>
public sealed record SelfRecord
{
    /// <summary>u64 at player+0xd0; no consumer found.</summary>
    public ulong Field0 { get; init; }

    /// <summary>u64 at player+0xd8: the character guid, logged as <c>ClientSent - %llu</c> and used
    /// as the local actor's guid. Must equal the gateway guid.</summary>
    public ulong Guid { get; init; }

    /// <summary>Client varint at player+0xe0 (copied to client+0x649f and into the actor descriptor).</summary>
    public uint TransientId { get; init; }

    /// <summary>u64 server clock; the client keeps <c>now - value</c> as a delta. Zero is accepted.</summary>
    public ulong ServerTime { get; init; }

    /// <summary>u32 at player+0xb9dc; the actor creator looks it up with <c>FUN_14220c720</c>
    /// (a definition table) and falls back to defaults when it is unknown.</summary>
    public uint ActorDefinitionId { get; init; }

    /// <summary>Strings at player+0xba00 and +0xba18. The actor creator equips items from them
    /// when +0xba00 is non-empty.</summary>
    public string TextA { get; init; } = string.Empty;
    public string TextB { get; init; } = string.Empty;

    /// <summary>Hair-tint and eye-tint ids at player+0xb9e0 and +0xb9e4.</summary>
    public uint ValueA { get; init; }
    public uint ValueB { get; init; }

    /// <summary>Strings at player+0xba30 (passed to the actor's slot +0xa0 by the actor creator),
    /// +0xba48 and +0xba60.</summary>
    public string TextC { get; init; } = string.Empty;
    public string TextD { get; init; } = string.Empty;
    public string TextE { get; init; } = string.Empty;

    /// <summary>u32 values at player+0xb9e8 and +0xb9ec, followed by the skin-tone shader group
    /// at +0xb9f0 (copied onto the actor), then u32 values at +0xb9f4 and +0xb9f8.</summary>
    public uint ValueC { get; init; }
    public uint ValueD { get; init; }
    public uint ValueE { get; init; }
    public uint ValueF { get; init; }
    public uint ValueG { get; init; }

    /// <summary>Four floats at player+0x3b0: the spawn position read by the actor creator.</summary>
    public Vector4 Position { get; init; }

    /// <summary>Four floats at player+0x3c0: orientation; the actor creator normalises it and
    /// substitutes a default when the result is not positive.</summary>
    public Vector4 Orientation { get; init; } = new(0, 0, 0, 1);

    /// <summary>Sub-record loaded by <c>FUN_140a40000</c> into player+0xe8.</summary>
    public SelfIdentity Identity { get; init; } = new();

    /// <summary>u32 at player+0xa638.</summary>
    public uint Field1 { get; init; }

    /// <summary>u64 at player+0xa6f8; u32 at +0xa700 and +0xa704.</summary>
    public ulong Field2 { get; init; }
    public uint Field3 { get; init; }
    public uint Field4 { get; init; }

    /// <summary>Bools at player+0x23c and +0x23d (the second selects a mode on the actor).</summary>
    public bool FlagA { get; init; }
    public bool FlagB { get; init; }

    /// <summary>u32 at player+0x240 (non-zero passed to <c>FUN_140c76080</c>) and +0x244.</summary>
    public uint Field5 { get; init; }
    public uint Field6 { get; init; }

    /// <summary>Bool at player+0x24c; u32 at +0x254, +0x250, +0xa734, +0xa738, +0x238.</summary>
    public bool FlagC { get; init; }
    public uint Field7 { get; init; }
    public uint Field8 { get; init; }
    public uint Field9 { get; init; }
    public uint Field10 { get; init; }
    public uint Field11 { get; init; }

    /// <summary>u64 timestamps at player+0x3e8 and +0x3f0; the loader adds the clock delta unless
    /// the value equals the client's "no time" sentinel.</summary>
    public ulong TimeA { get; init; }
    public ulong TimeB { get; init; }

    /// <summary>u32 at player+0x3d0, bool at +0x3d4, u32 at +0x3d8.</summary>
    public uint Field12 { get; init; }
    public bool FlagD { get; init; }
    public uint Field13 { get; init; }

    /// <summary>u32 handed to <c>FUN_140b79870(player+0x2b8, value)</c> after the first list.</summary>
    public uint Field14 { get; init; }

    /// <summary>u32 at player+0x200 (copied onto the actor by <c>FUN_140c75950</c>).</summary>
    public uint Field15 { get; init; }

    /// <summary>Fields of the <c>FUN_140a33b50</c> sub-record at player+0x3480: u32, u32, u8, u32, u32 after its list.</summary>
    public uint Field16 { get; init; }
    public uint Field17 { get; init; }
    public bool FlagE { get; init; }
    public uint Field18 { get; init; }
    public uint Field19 { get; init; }

    /// <summary>
    /// The crafting recipes the player knows, carried by the list at blob offset <c>0x11a</c>
    /// (<c>FUN_140a54bc0</c>, elements <c>FUN_140a44390</c>, sink <c>FUN_140db5680</c> - the same
    /// manager entry point <c>0x26 09 Recipe.List</c> uses). Empty by default, which writes the
    /// same <c>00 00 00 00</c> this field has always carried, so the record is byte-identical to
    /// wave 5 unless a caller fills it. See <c>Crafting/RecipeRecord.cs</c> and docs/62.
    /// </summary>
    public IReadOnlyList<Crafting.RecipeRecord> Recipes { get; init; } = [];

    /// <summary>Bool at player+0xad80.</summary>
    public bool FlagF { get; init; }

    /// <summary>Trailing u32 of the <c>FUN_140a331a0</c> sub-record (player+0xbdb8), read after its list.</summary>
    public uint Field20A { get; init; }

    /// <summary>u32 that follows the <c>FUN_140a33390</c> list (player+0xa428 sub-record).</summary>
    public uint Field20 { get; init; }

    /// <summary>u32 at player+0x258.</summary>
    public uint Field21 { get; init; }

    /// <summary>u32 at player+0x2a8 and +0x2ac.</summary>
    public uint Field22 { get; init; }
    public uint Field23 { get; init; }

    /// <summary>u32 pair inside the <c>FUN_140a2b650</c> sub-record (player+0xcff8: +0xc0, +0xc4).</summary>
    public uint Field24 { get; init; }
    public uint Field25 { get; init; }

    /// <summary>Bool at the end of the <c>FUN_140a2c000</c> sub-record (player+0xd1a0+0x158).</summary>
    public bool FlagG { get; init; }

    /// <summary>The <c>FUN_140a2eb50</c> sub-record at player+0xd310: u32, <c>FUN_140a2ec10</c>
    /// {u32, u32, u32} (the stream reaches it in RDX although Ghidra shows one argument),
    /// <c>FUN_140a2ecc0</c> {u32, u32, u32}, u32.</summary>
    public uint Field26 { get; init; }
    public uint Field26A { get; init; }
    public uint Field26B { get; init; }
    public uint Field26C { get; init; }
    public uint Field27 { get; init; }
    public uint Field28 { get; init; }
    public uint Field29 { get; init; }
    public uint Field30 { get; init; }

    /// <summary>u32 at player+0xd338.</summary>
    public uint Field31 { get; init; }

    /// <summary>The <c>FUN_140a30570</c> sub-record at player+0xd340: u64, then <c>FUN_140a30370</c>
    /// {<c>FUN_140a3b9a0</c> {u32, u32, u64}, u64, u32, u32, u32, <c>FUN_140a30490</c> {u32 x4}, str}
    /// (all three callees reached by register pass-through; the first was invisible to the
    /// static derivations and cost the live client 16 bytes — found by the cursor trace of
    /// 2026-08-28 20:05), then u8.</summary>
    public ulong Field32 { get; init; }
    public uint Field32Pre0 { get; init; }
    public uint Field32Pre1 { get; init; }
    public ulong Field32Pre2 { get; init; }
    public ulong Field32A { get; init; }
    public uint Field32B { get; init; }
    public uint Field32C { get; init; }
    public uint Field32D { get; init; }
    public uint Field32E { get; init; }
    public uint Field32F { get; init; }
    public uint Field32G { get; init; }
    public uint Field32H { get; init; }
    public string TextG { get; init; } = string.Empty;
    public byte Field33 { get; init; }

    /// <summary>The <c>FUN_140a37670</c> sub-record at player+0xd428: u32, <c>FUN_140a37720</c> {u32 x3}, u32.</summary>
    public uint Field34 { get; init; }
    public uint Field34A { get; init; }
    public uint Field34B { get; init; }
    public uint Field34C { get; init; }
    public uint Field35 { get; init; }

    /// <summary>The <c>FUN_140a395e0</c> sub-record at player+0xddc8: u32, <c>FUN_140a39690</c> {u32 x3}, u32.</summary>
    public uint Field36 { get; init; }
    public uint Field36A { get; init; }
    public uint Field36B { get; init; }
    public uint Field36C { get; init; }
    public uint Field37 { get; init; }

    /// <summary>The <c>FUN_140a39f30</c> sub-record at player+0xe738: u32, u32, u32.</summary>
    public uint Field38 { get; init; }
    public uint Field39 { get; init; }
    public uint Field40 { get; init; }

    /// <summary>Head of the <c>FUN_140a3af50</c> sub-record (player+0xf140): <c>FUN_140a3fa20</c>
    /// {u32, u32, u64, u64} called before any visible argument setup (RCX/RDX still hold the
    /// object and the stream; confirmed in the machine code at 0x140a3af60).</summary>
    public uint Field40A { get; init; }
    public uint Field40B { get; init; }
    public ulong Field40C { get; init; }
    public ulong Field40D { get; init; }

    /// <summary>Inside the <c>FUN_140a3af50</c> sub-record (player+0xf140): <c>FUN_140a3fb00</c> {u32, u32, u64} and a trailing u8.</summary>
    public uint Field41 { get; init; }
    public uint Field42 { get; init; }
    public ulong Field43 { get; init; }
    public byte Field44 { get; init; }

    /// <summary>Head of the <c>FUN_140a461b0</c> sub-record (player+0xf2f8): u32, u32, str.</summary>
    public uint Field45 { get; init; }
    public uint Field46 { get; init; }
    public string TextF { get; init; } = string.Empty;

    /// <summary>
    /// Current emote assignments in the skin manager at player+0xf2f8. The native self loader
    /// clears this manager before reading it, so every actor rebuild needs these rows again.
    /// FUN_140a55700 consumes the same keyed entries as Items.SetSkinItemManager.
    /// </summary>
    public IReadOnlyList<AugustEmote> Emotes { get; init; } = [];

    /// <summary>The <c>FUN_140a3bb00</c> sub-record (player+0xf548): u32, list, u32.</summary>
    public uint Field47 { get; init; }
    public uint Field48 { get; init; }

    /// <summary>Health, stamina, and other character resources loaded by
    /// <c>FUN_140a56700</c> into player+0xfde0.</summary>
    public IReadOnlyList<CharacterResource> Resources { get; init; } = [];

    /// <summary>The <c>FUN_140a45f80</c> sub-record (player+0x10008): five u64 and one u32.</summary>
    public ulong Field49 { get; init; }
    public ulong Field50 { get; init; }
    public ulong Field51 { get; init; }
    public ulong Field52 { get; init; }
    public ulong Field53 { get; init; }
    public uint Field54 { get; init; }

    /// <summary>Tail of the record: bool at player+0x106b8; u64 + u32 handed to <c>FUN_140c96b20</c>;
    /// u64, u64, u32, u32 handed to <c>FUN_140c96a90</c>; bools at +0x106b9, +0x106ba, +0x106bc (u8),
    /// +0x106bb; u32 at +0x106c0 and +0x106c4.</summary>
    public bool FlagH { get; init; }
    public ulong Field55 { get; init; }
    public uint Field56 { get; init; }
    public ulong Field57 { get; init; }
    public ulong Field58 { get; init; }
    public uint Field59 { get; init; }
    public uint Field60 { get; init; }
    public bool FlagI { get; init; }
    public bool FlagJ { get; init; }
    public byte Field61 { get; init; }
    public bool FlagK { get; init; }
    public uint Field62 { get; init; }
    public uint Field63 { get; init; }
}

/// <summary>Sub-record loaded by <c>FUN_140a40000</c> into player+0xe8: three u32, the character
/// name (the <c>%s</c> in <c>received our character: %s</c>), three more strings and a u64.</summary>
public sealed record SelfIdentity
{
    public uint Value0 { get; init; }
    public uint Value1 { get; init; }
    public uint Value2 { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Text1 { get; init; } = string.Empty;
    public string Text2 { get; init; } = string.Empty;
    public string Text3 { get; init; } = string.Empty;
    public ulong Value3 { get; init; }
}

/// <summary>
/// One 94-byte entry consumed by <c>FUN_140a56700</c>. The August client defines resource 1 as
/// health and resource 6 as stamina; fields whose purpose is not yet needed remain zero.
/// </summary>
public sealed record CharacterResource(
    uint OuterKey,
    uint ResourceId,
    uint ResourceType,
    uint CurrentValue,
    uint PreviousValue)
{
    public const int WireLength = 94;

    public static IReadOnlyList<CharacterResource> Starter { get; } =
    [
        new(OuterKey: 1, ResourceId: 1, ResourceType: 1, CurrentValue: 10_000, PreviousValue: 10_000),
        new(OuterKey: 6, ResourceId: 6, ResourceType: 6, CurrentValue: 600, PreviousValue: 600),
    ];
}

/// <summary>
/// Serialises <see cref="SelfRecord"/> exactly as <c>FUN_140a31140</c> reads it. Every list the
/// client can load (inventory, loadouts, stats, effects, …) is written with a zero count until its
/// element layout is carried over from the extracted read trees; the count prefixes are what the
/// loader's final length assertion requires.
/// </summary>
public static class SelfRecordCodec
{
    /// <summary>Byte length of a record whose strings are empty and whose lists are all empty:
    /// 823 from the static derivations (tools/selfschema, docs/10) plus the 16 bytes of
    /// <c>FUN_140a3b9a0</c> that only the live cursor trace revealed.</summary>
    public const int MinimalLength = 839;

    public static byte[] ToArray(SelfRecord record)
    {
        using var writer = new PacketWriter(1024);
        Write(writer, record);
        return writer.Written.ToArray();
    }

    public static void Write(PacketWriter w, SelfRecord r)
    {
        // --- FUN_140a31140 head (blob 0..109) ---------------------------------------------
        w.WriteUInt64(r.Field0);                    // +0xd0
        w.WriteUInt64(r.Guid);                      // +0xd8
        // +0xe0 (FUN_140a190f0). The client copies this to world+0x324f8 and uses it as "me" for
        // the rest of the session (docs/100 §7, S5c §2). It must be non-zero and must never collide
        // with a peer's id: TransientIdTable reserves 0-15 and allocates from 16.
        ClientVarInt.Write(w, r.TransientId);
        w.WriteUInt64(r.ServerTime);                // clock delta source
        w.WriteUInt32(r.ActorDefinitionId);         // +0xb9dc
        w.WriteString(r.TextA);                     // +0xba00
        w.WriteString(r.TextB);                     // +0xba18
        w.WriteUInt32(r.ValueA);                    // +0xb9e0
        w.WriteUInt32(r.ValueB);                    // +0xb9e4
        w.WriteString(r.TextC);                     // +0xba30
        w.WriteString(r.TextD);                     // +0xba48
        w.WriteString(r.TextE);                     // +0xba60
        w.WriteUInt32(r.ValueC);                    // +0xb9e8
        w.WriteUInt32(r.ValueD);                    // +0xb9ec
        w.WriteUInt32(r.ValueE);                    // +0xb9f0
        w.WriteUInt32(r.ValueF);                    // +0xb9f4
        w.WriteUInt32(r.ValueG);                    // +0xb9f8
        WriteVector4(w, r.Position);                // +0x3b0..+0x3bc
        WriteVector4(w, r.Orientation);             // +0x3c0..+0x3cc

        // --- FUN_140a40000 -> player+0xe8 (blob 109..145) ---------------------------------
        w.WriteUInt32(r.Identity.Value0);
        w.WriteUInt32(r.Identity.Value1);
        w.WriteUInt32(r.Identity.Value2);
        w.WriteString(r.Identity.Name);
        w.WriteString(r.Identity.Text1);
        w.WriteString(r.Identity.Text2);
        w.WriteString(r.Identity.Text3);
        w.WriteUInt64(r.Identity.Value3);

        w.WriteUInt32(r.Field1);                    // +0xa638
        WriteEmptyList(w);                          // FUN_140a4d190 -> +0xa640: {u32, u32} entries
        w.WriteUInt64(r.Field2);                    // +0xa6f8
        w.WriteUInt32(r.Field3);                    // +0xa700
        w.WriteUInt32(r.Field4);                    // +0xa704
        w.WriteBool(r.FlagA);                       // +0x23c
        w.WriteBool(r.FlagB);                       // +0x23d
        w.WriteUInt32(r.Field5);                    // +0x240
        w.WriteUInt32(r.Field6);                    // +0x244
        w.WriteBool(r.FlagC);                       // +0x24c
        w.WriteUInt32(r.Field7);                    // +0x254
        w.WriteUInt32(r.Field8);                    // +0x250
        w.WriteUInt32(r.Field9);                    // +0xa734
        w.WriteUInt32(r.Field10);                   // +0xa738
        w.WriteUInt32(r.Field11);                   // +0x238
        w.WriteUInt64(r.TimeA);                     // +0x3e8
        w.WriteUInt64(r.TimeB);                     // +0x3f0
        w.WriteUInt32(r.Field12);                   // +0x3d0
        w.WriteBool(r.FlagD);                       // +0x3d4
        w.WriteUInt32(r.Field13);                   // +0x3d8

        WriteEmptyList(w);                          // count of FUN_140a1fc10 entries (FUN_140a32c70 loader)
        w.WriteUInt32(r.Field14);                   // FUN_140b79870(player+0x2b8, value)
        WriteEmptyList(w);                          // count of {u32, u32} pairs -> FUN_140b7dde0(player+0x2b8)
        WriteEmptyList(w);                          // count of FUN_140a30a70 entries (0x3f0-byte objects)

        // FUN_140a331a0 -> player+0xbdb8
        WriteEmptyList(w);                          //   entries (FUN_140a3aa60 loader)
        w.WriteUInt32(r.Field20A);                  //   trailing u32 (player+0xbdb8 + 0xb)

        w.WriteUInt32(r.Field15);                   // +0x200

        // FUN_140a33b50 -> player+0x3480
        WriteEmptyList(w);                          //   entries
        w.WriteUInt32(r.Field16);
        w.WriteUInt32(r.Field17);
        w.WriteBool(r.FlagE);
        w.WriteUInt32(r.Field18);
        w.WriteUInt32(r.Field19);

        WriteEmptyList(w);                          // FUN_140a30790 -> player+0x5cf8: entries (FUN_140a2bb60 loader)
        WriteEmptyList(w);                          // count of FUN_140a44620 entries -> FUN_140dc0f00
        // FUN_140a54bc0: entries (FUN_140a44390 loader, 0x278 each) -> FUN_140db5680.
        // THE RECIPE LIST (docs/62 §3). Writes an i32 zero when Recipes is empty, exactly as
        // WriteEmptyList always did, so this line is byte-identical to wave 5 by default.
        Crafting.RecipeRecord.WriteList(w, r.Recipes);
        WriteEmptyList(w);                          // count of FUN_140a3e6d0 entries -> FUN_140ca0ed0(player+0xa568)
        w.WriteBool(r.FlagF);                       // +0xad80
        WriteEmptyList(w);                          // FUN_140a4c970 -> player+0xad88: u32 entries
        WriteEmptyList(w);                          // count of u32 values that are read and discarded
        WriteEmptyList(w);                          // count of {u32 id; FUN_140db5280 body} entries
        WriteEmptyList(w);                          // count of {u32 key; FUN_140a30280 body} entries (player+0xc328 table)

        // FUN_140a33390 -> player+0xa428
        WriteEmptyList(w);                          //   entries
        w.WriteUInt32(r.Field20);                   //   trailing u32 (loader field +0x25*8)

        WriteEmptyList(w);                          // FUN_140a4d020 -> player+0xe60: entries
        WriteEmptyList(w);                          // count of u32 entries -> FUN_140b0f270(player+0xc788)
        w.WriteUInt32(r.Field21);                   // +0x258
        WriteEmptyList(w);                          // FUN_140a4caf0: u32 array (player+0x268)
        WriteEmptyList(w);                          // FUN_140a4caf0: u32 array (player+0x280)
        WriteEmptyList(w);                          // FUN_140a5d190: array (player+0x298)
        w.WriteUInt32(r.Field22);                   // +0x2a8
        w.WriteUInt32(r.Field23);                   // +0x2ac

        // FUN_140a2b590 -> player+0xc8c0
        WriteEmptyList(w);                          //   FUN_140a55420 entries (FUN_140a2c090 loader)
        WriteEmptyList(w);                          //   FUN_140a55420 entries (second list at +0xa8)
        WriteEmptyList(w);                          //   FUN_140a4ce90 entries (+0x150)

        // FUN_140a2b650 -> player+0xcff8
        WriteEmptyList(w);                          //   FUN_140a552e0 entries x4
        WriteEmptyList(w);
        WriteEmptyList(w);
        WriteEmptyList(w);
        w.WriteUInt32(r.Field24);                   //   +0xc0
        w.WriteUInt32(r.Field25);                   //   +0xc4
        WriteEmptyList(w);                          //   FUN_140a56110 entries
        WriteEmptyList(w);                          //   FUN_140a55c80 entries
        WriteEmptyList(w);                          //   FUN_140a4d020 entries x2
        WriteEmptyList(w);

        // FUN_140a2c000 -> player+0xd1a0
        WriteEmptyList(w);                          //   FUN_140a56530 entries
        WriteEmptyList(w);                          //   FUN_140a5cac0 entries
        w.WriteBool(r.FlagG);                       //   +0x158

        // FUN_140a2eb50 -> player+0xd310
        w.WriteUInt32(r.Field26);
        w.WriteUInt32(r.Field26A);                  //   FUN_140a2ec10 {u32, u32, u32} (hidden hand-off)
        w.WriteUInt32(r.Field26B);
        w.WriteUInt32(r.Field26C);
        w.WriteUInt32(r.Field27);                   //   FUN_140a2ecc0 {u32, u32, u32}
        w.WriteUInt32(r.Field28);
        w.WriteUInt32(r.Field29);
        w.WriteUInt32(r.Field30);

        w.WriteUInt32(r.Field31);                   // +0xd338

        // FUN_140a30570 -> player+0xd340
        w.WriteUInt64(r.Field32);
        w.WriteUInt32(r.Field32Pre0);               //   FUN_140a30370 -> FUN_140a3b9a0 {u32, u32, u64} (hidden hand-off, live-proven)
        w.WriteUInt32(r.Field32Pre1);
        w.WriteUInt64(r.Field32Pre2);
        w.WriteUInt64(r.Field32A);                  //   FUN_140a30370 {u64, u32, u32, u32, ...} (hidden hand-off)
        w.WriteUInt32(r.Field32B);
        w.WriteUInt32(r.Field32C);
        w.WriteUInt32(r.Field32D);
        w.WriteUInt32(r.Field32E);                  //     FUN_140a30490 {u32 x4}
        w.WriteUInt32(r.Field32F);
        w.WriteUInt32(r.Field32G);
        w.WriteUInt32(r.Field32H);
        w.WriteString(r.TextG);                     //     str (FUN_140a30370 +0x38)
        w.WriteByte(r.Field33);

        WriteEmptyList(w);                          // FUN_140a4d330 -> player+0xd3c0: entries

        // FUN_140a37670 -> player+0xd428
        w.WriteUInt32(r.Field34);
        w.WriteUInt32(r.Field34A);                  //   FUN_140a37720 {u32 x3} (hidden hand-off)
        w.WriteUInt32(r.Field34B);
        w.WriteUInt32(r.Field34C);
        w.WriteUInt32(r.Field35);

        WriteEmptyList(w);                          // FUN_140a55530 -> player+0xd440: entries
        WriteEmptyList(w);                          // FUN_140a5a510 -> player+0xd4a8: entries
        WriteEmptyList(w);                          // FUN_140a5a320 -> player+0xd4d8: entries
        WriteEmptyList(w);                          // FUN_140a57220 -> player+0xd888: entries (u64 key + FUN_140a38b00 body)
        WriteEmptyList(w);                          // FUN_140a57090 -> player+0xd9b8: entries (u64 key + FUN_140a38070 body)
        WriteEmptyList(w);                          // FUN_140a563e0 -> player+0xda98: entries
        WriteEmptyList(w);                          // FUN_140a4edb0 -> player+0xdd20: entries

        // FUN_140a395e0 -> player+0xddc8
        w.WriteUInt32(r.Field36);
        w.WriteUInt32(r.Field36A);                  //   FUN_140a39690 {u32 x3} (hidden hand-off)
        w.WriteUInt32(r.Field36B);
        w.WriteUInt32(r.Field36C);
        w.WriteUInt32(r.Field37);

        // FUN_140a39f30 -> player+0xe738
        w.WriteUInt32(r.Field38);
        w.WriteUInt32(r.Field39);
        w.WriteUInt32(r.Field40);

        WriteEmptyList(w);                          // FUN_140a55ae0 -> player+0xe7f8: entries

        // FUN_140a2b910 -> player+0xe890
        WriteEmptyList(w);                          //   FUN_140a57340 entries
        WriteEmptyList(w);                          //   FUN_140a57530 entries
        WriteEmptyList(w);                          //   FUN_140a57910 entries

        // FUN_140a393b0 -> player+0xed28
        WriteEmptyList(w);                          //   FUN_140a57760 entries

        // FUN_140a3af50 -> player+0xf140
        w.WriteUInt32(r.Field40A);                  //   FUN_140a3fa20 {u32, u32, u64, u64} (hidden hand-off)
        w.WriteUInt32(r.Field40B);
        w.WriteUInt64(r.Field40C);
        w.WriteUInt64(r.Field40D);
        WriteEmptyList(w);                          //   FUN_140a4f6f0 entries
        w.WriteUInt32(r.Field41);                   //   FUN_140a3fb00 {u32, u32, u64}
        w.WriteUInt32(r.Field42);
        w.WriteUInt64(r.Field43);
        WriteEmptyList(w);                          //   FUN_140a4f8c0 entries
        WriteEmptyList(w);                          //   FUN_140a4f560 entries
        w.WriteByte(r.Field44);                     //   +0x168

        // FUN_140a461b0 -> player+0xf2f8
        w.WriteUInt32(r.Field45);
        w.WriteUInt32(r.Field46);
        w.WriteString(r.TextF);
        WriteEmptyList(w);                          //   FUN_140a507a0 entries
        EmotePackets.WriteItems(w, r.Emotes);       //   FUN_140a55700 current emote assignments
        WriteEmptyList(w);                          //   FUN_140a56b50 entries

        // FUN_140a3bb00 -> player+0xf548
        w.WriteUInt32(r.Field47);
        WriteEmptyList(w);                          //   FUN_140a55fa0 entries
        w.WriteUInt32(r.Field48);

        WriteEmptyList(w);                          // FUN_140a56280 -> player+0xf680: entries

        // FUN_140a495b0 -> player+0xfc20
        WriteEmptyList(w);                          //   FUN_140a5b8e0 entries
        WriteEmptyList(w);                          //   FUN_140a5baa0 entries
        WriteEmptyList(w);                          //   FUN_140a5bc60 entries
        WriteEmptyList(w);                          //   FUN_140a5bfd0 entries x2
        WriteEmptyList(w);

        WriteResources(w, r.Resources);             // FUN_140a56700 -> player+0xfde0: entries

        // FUN_140a45f80 -> player+0x10008
        w.WriteUInt64(r.Field49);
        w.WriteUInt64(r.Field50);
        w.WriteUInt64(r.Field51);
        w.WriteUInt64(r.Field52);
        w.WriteUInt64(r.Field53);
        w.WriteUInt32(r.Field54);

        WriteEmptyList(w);                          // FUN_140a569c0 -> player+0x10040: entries
        WriteEmptyList(w);                          // FUN_140a55df0 -> player+0x10320: entries
        WriteEmptyList(w);                          // FUN_140a4eb30 -> player+0x103e8: entries
        WriteEmptyList(w);                          // FUN_140a56cd0 -> player+0x105e0: entries

        // --- tail (blob 674..723) ----------------------------------------------------------
        w.WriteBool(r.FlagH);                       // +0x106b8
        w.WriteUInt64(r.Field55);                   // FUN_140c96b20(player+0xe90, ...)
        w.WriteUInt32(r.Field56);
        w.WriteUInt64(r.Field57);                   // FUN_140c96a90(player+0xe90, ...)
        w.WriteUInt64(r.Field58);
        w.WriteUInt32(r.Field59);
        w.WriteUInt32(r.Field60);
        w.WriteBool(r.FlagI);                       // +0x106b9
        w.WriteBool(r.FlagJ);                       // +0x106ba
        w.WriteByte(r.Field61);                     // +0x106bc
        w.WriteBool(r.FlagK);                       // +0x106bb
        w.WriteUInt32(r.Field62);                   // +0x106c0
        w.WriteUInt32(r.Field63);                   // +0x106c4
    }

    private static void WriteEmptyList(PacketWriter w) => w.WriteInt32(0);

    internal static void WriteResources(PacketWriter w, IReadOnlyList<CharacterResource> resources)
    {
        w.WriteInt32(resources.Count);
        foreach (CharacterResource resource in resources)
        {
            w.WriteUInt32(resource.OuterKey);
            w.WriteUInt32(resource.ResourceId);
            w.WriteUInt32(resource.ResourceType);
            w.WriteInt32(0);                        // nested entry count
            w.WriteUInt32(resource.CurrentValue);
            w.WriteUInt32(resource.PreviousValue);
            for (int index = 0; index < 7; index++)
            {
                w.WriteUInt32(0);
            }

            for (int index = 0; index < 5; index++)
            {
                w.WriteUInt64(0);
            }

            w.WriteByte(0);
            w.WriteByte(0);
        }
    }

    private static void WriteVector4(PacketWriter w, Vector4 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
        w.WriteSingle(v.W);
    }
}

/// <summary>
/// The client's compact unsigned integer (<c>FUN_140a190f0</c>): the low two bits of the first
/// byte give the number of extra little-endian bytes (0-3); the value is the whole little-endian
/// quantity shifted right by two.
/// </summary>
public static class ClientVarInt
{
    public static void Write(PacketWriter w, uint value)
    {
        int extra = value < (1u << 6) ? 0 : value < (1u << 14) ? 1 : value < (1u << 22) ? 2 : 3;
        if (extra == 3 && value >= (1u << 30))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Client varints carry at most 30 bits.");
        }

        uint packed = (value << 2) | (uint)extra;
        for (int i = 0; i <= extra; i++)
        {
            w.WriteByte((byte)(packed >> (8 * i)));
        }
    }

    public static int Length(uint value) =>
        value < (1u << 6) ? 1 : value < (1u << 14) ? 2 : value < (1u << 22) ? 3 : 4;
}
