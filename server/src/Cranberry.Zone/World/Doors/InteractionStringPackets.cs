using Cranberry.Protocol;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.World.Doors;

// Command.InteractionString (base 0x09, u16 sub 0x002d) — the packet that puts TEXT on the [F]
// prompt, and the one piece Cranberry has never answered (docs/47 §4c, §4d).
//
// It is not a door packet and it is not a loot packet: it is the prompt packet, and every
// interactable in the world needs it. The client's prompt driver FUN_14140bcd0 runs at most once a
// second; it reads the SINGLE current interaction target from FUN_1412b46e0(mgr+0x32260), drops it
// if EntityManager.Find fails, requires the distance from the local player to the target actor's
// bounding-box centre to be within the ClientInteractComponent's range (FUN_140c168f0 -> the
// component's vtable+0x38 — the 3.0 m Cranberry already sends in the ea 04), and only then sends
// this request. FUN_1412bae90 — the sender — also BLANKS the prompt string on every target change,
// so with no reply the label is permanently empty even though the target itself is bound.
//
// The client has a receive case for the same packet class: the Command-family dispatcher
// FUN_14129ad10 case 0x2d -> FUN_14129e7c0, which deserialises it, walks the entry array, and ends
//
//     if (replyGuid == ui->+0x1a8) { FUN_14140c430(ui, pos); FUN_14140c480(ui, stringId); }
//
// writing the prompt string id to ui+0x1b8. The guid check is why a reply must name the guid the
// request named, and why a late reply is dropped: the target may already have changed.
//
// The array is OPTIONAL. When it is empty — or when nothing in it qualifies — the packet's own
// default string id is used unchanged and FUN_14140c480 is still called. So the minimum useful
// reply is 19 bytes with entryCount = 0, which needs no bone names and no per-model data.
//
// docs/47 §7 q1 asked which of the two trailing u32s is the string id and which is the entry count.
// The derived order — string id first, matching the client object's field offsets (+0x28 before
// +0x40) and a SOE serialiser's declaration order — is now the ONLY order this writer has. The
// alternative was deleted with its switch in lane 0D: it wrote the locale id into the entry-count
// word of a 19-byte packet and made the client walk a 32-byte-stride array of string reads off the
// end of an 8-byte remainder, which is unknown and possibly fatal rather than a blank label.

/// <summary>
/// What the guid in a <see cref="InteractionStringRequest"/> turned out to name. The caller resolves
/// this — the doors, the loot world and the vehicle roster each know their own guids — and
/// <see cref="InteractionPromptStrings.For"/> turns it into a locale string id.
/// </summary>
public enum InteractionTargetKind
{
    /// <summary>Nothing this session recognises. Reply anyway, with id 0 — see <see cref="InteractionStringReply.For"/>.</summary>
    Unknown,

    /// <summary>A door the client is holding shut.</summary>
    ClosedDoor,

    /// <summary>A door the client is holding open.</summary>
    OpenDoor,

    /// <summary>A gate — a door whose kind name says gate rather than door.</summary>
    Gate,

    /// <summary>A ground-loot object.</summary>
    GroundLoot,

    /// <summary>A vehicle.</summary>
    Vehicle,
}

/// <summary>
/// The world-space prompt string ids, recovered from the August client's own locale (docs/47 §4e).
/// <para>
/// Every one is a template carrying a <c>[[*key*]]</c> keybind token, and some carry a
/// <c>[*target*]</c> token that the client fills from the entity's <c>NAME_ID</c>
/// (<c>AddLightweightNpc +0x38</c>). <b>A door's <c>NAME_ID</c> is 0</b>, so a door must use an id
/// with no <c>[*target*]</c> in it — <see cref="Open"/> or <see cref="CloseDoor"/> — while ground
/// loot, whose <c>0xd6</c> carries a real <c>NAME_ID</c> (12126 = <c>AK-47</c>), can use
/// <see cref="PickUpTarget"/>.
/// </para>
/// <para>
/// The ids were derived by inverting the locale key with <c>tools/locale/localedat.py</c>'s own
/// derivation (Bob Jenkins <c>lookup2</c> over the literal <c>Global.Text.%d</c> at VA
/// <c>0x143118638</c>) and each was verified round-trip. <b>[P-data]</b> — which text a given id
/// carries is proven; <i>which id to use for which object</i> is Cranberry's choice.
/// </para>
/// <para>
/// <b>Lane 2B (docs/104): that inversion is now the build's, not a one-off.</b> None of these ids
/// is typed here any more — each names the exact en_us TEXT it wants and
/// <c>tools/data/derive_strings.py</c> re-runs the locale-key inversion over every datasheet id
/// in <c>0..65,536</c> to find it, so a client change moves the constant instead of silently
/// leaving it pointing at the wrong sentence. Three of the eight texts are carried by two ids
/// each, which is why the derivation also records the occurrence it picked.
/// <see cref="AugustStrings.Prompts"/> holds the resolved id and the text side by side.
/// </para>
/// </summary>
public static class InteractionPromptStrings
{
    /// <summary>No text. What to send for an object the server cannot identify.</summary>
    public const uint None = 0;

    /// <summary><c>[[*key*]] Pick Up [*target*]</c> — ground loot.</summary>
    public const uint PickUpTarget = AugustStrings.Prompts.PickUpTarget;

    /// <summary><c>[[*key*]] Open</c> — a closed door. No <c>[*target*]</c>, which is why a door can use it.</summary>
    public const uint Open = AugustStrings.Prompts.Open;

    /// <summary><c>[[*key*]] Close Door</c> — an open door.</summary>
    public const uint CloseDoor = AugustStrings.Prompts.CloseDoor;

    /// <summary><c>&lt;[[*key*]] Use Door&gt;</c> — the alternative door wording.</summary>
    public const uint UseDoor = AugustStrings.Prompts.UseDoor;

    /// <summary><c>[[*key*]] Open [*target*]</c> — needs a <c>NAME_ID</c> on the target (docs/47 §7 q4).</summary>
    public const uint OpenTarget = AugustStrings.Prompts.OpenTarget;

    /// <summary><c>[[*key*]] Use Gate</c>.</summary>
    public const uint UseGate = AugustStrings.Prompts.UseGate;

    /// <summary><c>[[*key*]] Search [*target*]</c> — containers, later.</summary>
    public const uint SearchTarget = AugustStrings.Prompts.SearchTarget;

    /// <summary><c>[[*key*]] Take [*target*]</c>.</summary>
    public const uint TakeTarget = AugustStrings.Prompts.TakeTarget;

    /// <summary>The id Cranberry sends for each kind of target. <b>[design]</b> — any of them displays.</summary>
    public static uint For(InteractionTargetKind kind) => kind switch
    {
        InteractionTargetKind.ClosedDoor => Open,
        InteractionTargetKind.OpenDoor => CloseDoor,
        InteractionTargetKind.Gate => UseGate,
        InteractionTargetKind.GroundLoot => PickUpTarget,
        InteractionTargetKind.Vehicle => UseDoor,
        _ => None,
    };

    /// <summary>The prompt kind for a spawned door: gates say "Use Gate", everything else opens and closes.</summary>
    public static InteractionTargetKind KindOf(DoorInstance door)
    {
        ArgumentNullException.ThrowIfNull(door);
        if (door.KindName.Contains("Gate", StringComparison.OrdinalIgnoreCase))
        {
            return InteractionTargetKind.Gate;
        }

        return door.IsOpen ? InteractionTargetKind.OpenDoor : InteractionTargetKind.ClosedDoor;
    }
}

/// <summary>
/// One entry of the reply's interaction list — the 32-byte-stride struct the client walks
/// (docs/47 §4d): <c>{ IString attachPointName @+0x00; f32 range @+0x18; u32 stringId @+0x1c }</c>.
/// <para>
/// Per entry the client resolves <see cref="AttachPointName"/> to a node on the target's actor
/// (<c>FUN_141fddec0</c> returns the index, <c>FUN_141fde2f0</c> the node's world transform,
/// position at <c>+0x30</c>), measures the player's distance to it, and keeps the <b>nearest</b>
/// entry whose <see cref="Range"/> beats that distance. Entries with an empty name or a
/// non-positive range are skipped.
/// </para>
/// <para>
/// <b>Cranberry does not need this.</b> A reply with no entries falls back to the packet's own
/// default id, which is the whole fix. It is modelled because it is the same array
/// <c>09 0a InteractionList</c> / <c>09 0b InteractionSelect</c> / <c>09 0c InteractionStartWheel</c>
/// use, so it is the door to the radial wheel — and because writing it down is what stops the next
/// lane re-deriving it. <b>Untested: no entry has ever been on the wire.</b>
/// </para>
/// </summary>
public sealed record InteractionStringEntry(string AttachPointName, float Range, uint StringId)
{
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (string.IsNullOrEmpty(AttachPointName))
        {
            // The client skips an entry whose IString is empty, so writing one is a silent no-op
            // that still costs bytes on a 512-byte MTU. Refuse at the call site instead.
            throw new InvalidOperationException(
                "An interaction-list entry must name an attach point; the client skips empty ones.");
        }

        if (!float.IsFinite(Range) || Range <= 0f)
        {
            throw new InvalidOperationException(
                $"An interaction-list entry's range must be > 0 (got {Range}); the client skips it.");
        }

        writer.WriteString(AttachPointName);
        writer.WriteSingle(Range);
        writer.WriteUInt32(StringId);
    }
}

/// <summary>
/// c2s <c>Command.InteractionString</c> — 19 bytes, live in
/// <c>logs/host-20260829-220829.log:793,1676,1698</c> as
/// <c>09 2D 00 | 10 00 00 00 00 00 00 20 | 00 00 00 00 | 00 00 00 00</c>. <b>[P-live]</b>
/// <para>
/// The client asks at most once a second and only while the target is inside the interact range, so
/// the reply has to go out on the same tick. Both trailing words are zero in every observed request.
/// </para>
/// </summary>
public sealed record InteractionStringRequest(ulong TargetGuid, uint FirstWord = 0, uint SecondWord = 0)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;

    /// <summary><c>u8 0x09; u16 0x002d</c>.</summary>
    public const ushort SubOpcode = 0x002d;

    /// <summary><c>3 + u64 + u32 + u32</c> = 19, the observed length.</summary>
    public const int Length = 19;

    /// <summary>Header plus the guid — everything this handler actually needs.</summary>
    public const int MinimumLength = 11;

    /// <summary>True when the payload is this packet, without parsing it. Cheap enough for a <c>when</c> clause.</summary>
    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= MinimumLength
        && payload[0] == Opcode
        && (ushort)(payload[1] | (payload[2] << 8)) == SubOpcode;

    public static bool TryParse(ReadOnlySpan<byte> payload, out InteractionStringRequest? request)
    {
        request = null;
        if (!Matches(payload))
        {
            return false;
        }

        request = Parse(payload);
        return true;
    }

    public static InteractionStringRequest Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        ushort sub = reader.ReadUInt16();

        if (opcode != Opcode || sub != SubOpcode)
        {
            throw new PacketFormatException(
                $"Expected Command.InteractionString 09 2d 00, got {opcode:x2} {sub & 0xff:x2} {sub >> 8:x2}.");
        }

        ulong guid = reader.ReadUInt64();
        uint first = reader.Remaining >= sizeof(uint) ? reader.ReadUInt32() : 0u;
        uint second = reader.Remaining >= sizeof(uint) ? reader.ReadUInt32() : 0u;
        return new InteractionStringRequest(guid, first, second);
    }
}

/// <summary>
/// s2c <c>Command.InteractionString</c> — the reply that puts the label on the <c>[F]</c> prompt.
/// One packet class serves both directions (vtable <c>PTR_LAB_1431fd3f0</c>), so the reply has the
/// request's shape with the string id filled in.
/// <para>
/// <c>09 2d 00 | u64 targetGuid | u32 stringId | u32 entryCount | entryCount x entry</c> — 19 bytes
/// with no entries, which is the whole fix (docs/47 §4d).
/// </para>
/// <para>
/// <b>The guid must be the one the request named.</b> <c>FUN_14129e7c0</c> ends with
/// <c>if (replyGuid == ui-&gt;currentTarget)</c> and drops the reply otherwise, so echoing the
/// request's guid is not politeness — it is the only way the label is ever set.
/// </para>
/// <para><b>BUILT, not LIVE-VERIFIED.</b> Not one byte of this has been in front of the client.</para>
/// </summary>
public sealed record InteractionStringReply(
    ulong TargetGuid,
    uint StringId,
    IReadOnlyList<InteractionStringEntry>? Entries = null)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;
    public const ushort SubOpcode = InteractionStringRequest.SubOpcode;

    /// <summary>Length with no entries: <c>3 + u64 + u32 + u32</c>.</summary>
    public const int MinimalLength = InteractionStringRequest.Length;

    /// <summary>How many entries this reply carries.</summary>
    public int EntryCount => Entries?.Count ?? 0;

    /// <summary>
    /// The reply for one resolved target. <paramref name="kind"/> chooses the string id;
    /// <see cref="InteractionTargetKind.Unknown"/> yields id 0, which is still worth sending —
    /// <c>FUN_14140c480</c> is called either way, and an answered request is one the client is not
    /// re-asking every second for the rest of the match.
    /// </summary>
    public static InteractionStringReply For(ulong targetGuid, InteractionTargetKind kind) =>
        new(targetGuid, InteractionPromptStrings.For(kind));

    /// <summary>The reply for a spawned door, open or closed, gate-aware.</summary>
    public static InteractionStringReply ForDoor(DoorInstance door)
    {
        ArgumentNullException.ThrowIfNull(door);
        return For(door.WorldGuid, InteractionPromptStrings.KindOf(door));
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteUInt64(TargetGuid);

        // The derived order, and since lane 0D the only one: the client object's field offsets put
        // the default string id (+0x28) before the entry array (+0x40), and a SOE serialiser writes
        // in declaration order.
        writer.WriteUInt32(StringId);
        writer.WriteUInt32((uint)EntryCount);

        if (Entries is null)
        {
            return;
        }

        foreach (InteractionStringEntry entry in Entries)
        {
            entry.WriteTo(writer);
        }
    }
}
