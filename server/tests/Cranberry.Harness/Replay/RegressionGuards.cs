using System.Text;
using Cranberry.Harness.Wire;
using Cranberry.Zone;

namespace Cranberry.Harness.Replay;

/// <summary>How seriously to take a guard finding.</summary>
public enum GuardSeverity
{
    /// <summary>A packet shape a capture proves the August client cannot survive.</summary>
    Fatal,

    /// <summary>A shape correlated with failure, or a layout the tool could not verify.</summary>
    Suspect,

    /// <summary>An observation worth printing that is not by itself a fault.</summary>
    Info,
}

public sealed record GuardFinding(
    string Id,
    GuardSeverity Severity,
    string Title,
    string Detail,
    string Evidence,
    IReadOnlyList<TimeSpan> At)
{
    public override string ToString()
    {
        string when = At.Count == 0
            ? string.Empty
            : "  at " + string.Join(", ", At.Take(6).Select(t => $"{t.TotalSeconds:F3}s")) + (At.Count > 6 ? ", …" : string.Empty);
        return $"[{Id}] {Severity}: {Title}\n    {Detail}{when}\n    evidence: {Evidence}";
    }
}

/// <summary>One message as a guard sees it, whichever stream it came from.</summary>
public sealed record GuardMessage(TimeSpan At, PacketSignature Signature, byte[] Bytes, int Length, bool Truncated)
{
    public static GuardMessage From(CaptureMessage message) =>
        new(message.At, message.Signature, message.Bytes, message.Length, message.Truncated);
}

/// <summary>
/// The four packet shapes docs/32 and docs/45 identified as fatal, checked against any
/// server-to-client stream — a recorded one or a live one. They are the whole reason this repo
/// keeps captures: each was diagnosed once from the wire at the cost of a play-test session, and
/// each can now be re-checked in milliseconds against every session ever recorded.
///
/// <para>Sweeping the archive with these gives the history — 109 capture files, 146 gateway
/// sessions at the time of writing: nine sessions carry an
/// UnsetCharacterEquipmentSlot burst (2026-08-29 15:16 to 18:32, the docs/32 window) and six carry
/// a slot-7 equipment row (2026-08-29 22:08 and 2026-08-30 08:54, the docs/45 window). Both windows
/// are closed in every session recorded since.</para>
///
/// <para><b>The one number that is hard-coded rather than looked up.</b> Slot 1 (Head) is named
/// here as the crash-on-clear slot with a citation, instead of calling
/// <c>Cranberry.Zone.Inventory.BodySlots.ClearingCrashesClient</c>. That would be a second piece of
/// server behaviour shared with the harness, and docs/72 §2 keeps that list at exactly one entry.
/// Nothing in G1 depends on it: the rule is that no clear may reach the DRESS path, and the slot
/// id only enriches the message.</para>
/// </summary>
public static class RegressionGuards
{
    /// <summary>Body slot 1, Head — the one IS_REQUIRED row with an empty DEFAULT_ADR (docs/41 §1d).</summary>
    public const uint CrashOnClearSlot = 1;

    /// <summary>Body slot 7, RHand — the active-equip slot (docs/45 §1).</summary>
    public const uint ActiveHandSlot = 7;

    /// <summary>
    /// The self record's size. It moved eight times while the record was being derived (6, 124,
    /// 156, 734, 834, 848, 850, 1096) and settled at 1,093 in <c>wire-20260829-131127</c>, which
    /// carries both values. Every session recorded since — sixty-odd captures, both known-good
    /// references and everything today's build has served — sends exactly 1,093.
    /// </summary>
    public const int SendSelfToClientLength = 1093;

    public static IReadOnlyList<GuardFinding> Run(IEnumerable<GuardMessage> serverToClient)
    {
        List<GuardMessage> messages = [.. serverToClient];
        List<GuardFinding> findings = [];

        findings.AddRange(NoUnsetCharacterEquipmentSlot(messages));
        findings.AddRange(NoActiveHandEquipmentRow(messages));
        findings.AddRange(NoWornSkinBeforeEquipment(messages));
        findings.AddRange(AppearancePayloadSizes(messages));

        return findings;
    }

    /// <summary>Convenience: run the guards over the server side of a recorded session.</summary>
    public static IReadOnlyList<GuardFinding> Run(CaptureSession session) =>
        Run(session.ServerToClient.Select(GuardMessage.From));

    // ---- G1 ------------------------------------------------------------------------------------

    /// <summary>
    /// How long after a <c>94 03</c> a <c>94 02</c> may arrive and still belong to the same draw.
    /// It is <see cref="ReplayScript.DefaultAnchorWindow"/> - the replay script's own causal
    /// window - rather than a number invented here, so "the same burst" means one thing in this
    /// repository. The observed distance is milliseconds (the whole six-packet draw is written by
    /// one call in <c>WieldSequence</c>), so the window is three orders of magnitude wider than it
    /// needs to be and still never spans two separate hotbar draws.
    /// </summary>
    public static TimeSpan DrawBurstWindow => ReplayScript.DefaultAnchorWindow;

    /// <summary>
    /// <b>G1 - no UnsetCharacterEquipmentSlot on the DRESS path.</b> The burst appears in zero of
    /// the sessions the client accepted and in every session that crashed with G10 or hung on the
    /// loading screen (docs/32; SurvivorSlots' standing rule). Clearing slot 1 leaves a required
    /// slot with no mesh and no default, which is the crash half of the crash-vs-hang split.
    ///
    /// <para><b>Narrowed 2026-09-02 (wave 11, lane 0A).</b> docs/95 §2 states the distinction this
    /// guard did not make: <i>"An unset burst on the DRESS path is what hung the client after
    /// ClientBeginZoning. One unset, for one slot, at the moment an item leaves it, is a different
    /// use."</i> Wave 10's <c>WieldSequence</c> step 2 reached a live wire on 2026-09-02 in
    /// <c>wire-20260902-212215.txt</c> - one <c>94 03</c> clearing slot 76, followed by
    /// <c>11 04, 11 02, 86 04, a0 05, 94 02</c> - and the client survived it: the session reached
    /// the landing, walked 35 m, wrote no <c>WeaponErrors.log</c> and left no minidump
    /// (docs/98 §7.4). So the rule is now: a <c>94 03</c> closed by a
    /// <c>94 02 SetCharacterEquipmentSlot</c> within <see cref="DrawBurstWindow"/> is a draw and is
    /// reported as <see cref="GuardSeverity.Info"/>; a <c>94 03</c> that closes nothing is a
    /// dress-path clear and stays <see cref="GuardSeverity.Fatal"/>. The four docs/32 fixtures are
    /// unaffected - none of them carries a <c>94 02</c> at all, because this project did not send
    /// one until wave 10.</para>
    /// </summary>
    private static IEnumerable<GuardFinding> NoUnsetCharacterEquipmentSlot(List<GuardMessage> messages)
    {
        List<GuardMessage> unset = [.. messages.Where(m => EquipmentPackets.IsUnsetCharacterEquipmentSlot(m.Signature))];
        if (unset.Count == 0)
        {
            yield break;
        }

        List<TimeSpan> bindings =
        [
            .. messages
                .Where(m => EquipmentPackets.IsSetCharacterEquipmentSlot(m.Signature))
                .Select(m => m.At)
        ];

        List<GuardMessage> dress = [];
        List<GuardMessage> draw = [];
        foreach (GuardMessage message in unset)
        {
            (ClosedByABinding(message, bindings) ? draw : dress).Add(message);
        }

        if (dress.Count > 0)
        {
            SortedSet<uint> slots = ClearedSlots(dress);
            bool fatalSlot = slots.Contains(CrashOnClearSlot);
            yield return new GuardFinding(
                "G1",
                GuardSeverity.Fatal,
                "the server sent UnsetCharacterEquipmentSlot outside a draw",
                $"{dress.Count} packet(s), clearing slot(s) [{string.Join(", ", slots)}], none of them "
                + $"closed by a 94 02 SetCharacterEquipmentSlot within {DrawBurstWindow.TotalSeconds:F0}s"
                + (fatalSlot
                    ? $". Slot {CrashOnClearSlot} (Head) is the crash-on-clear slot: required, no default mesh - the G10 half of docs/32"
                    : ". No slot 1, which is the hang-not-crash half of docs/32"),
                "docs/32 §The two differences, §Why the two failure modes differ; docs/41 §1d; "
                + "docs/95 §2 (one unset, at the moment an item leaves a slot, is a different use)",
                [.. dress.Select(m => m.At)]);
        }

        if (draw.Count > 0)
        {
            yield return new GuardFinding(
                "G1i",
                GuardSeverity.Info,
                "UnsetCharacterEquipmentSlot inside a draw burst",
                $"{draw.Count} packet(s), vacating slot(s) [{string.Join(", ", ClearedSlots(draw))}], each "
                + "closed by a 94 02 SetCharacterEquipmentSlot in the same burst. That is WieldSequence "
                + "step 2 (docs/95 §2), not the docs/32 dress-path clear.",
                "docs/95 §2; docs/98 §7.4 (wire-20260902-212215.txt: slot 76, the client survived it)",
                [.. draw.Select(m => m.At)]);
        }
    }

    private static SortedSet<uint> ClearedSlots(IEnumerable<GuardMessage> unset)
    {
        var slots = new SortedSet<uint>();
        foreach (GuardMessage message in unset)
        {
            if (TryReadUnsetSlot(message.Bytes, out uint slot))
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    /// <summary>
    /// True when a <c>94 02</c> binding follows this clear inside <see cref="DrawBurstWindow"/> -
    /// the signature of a hotbar draw taking an item off its peg and putting it in the hand.
    /// </summary>
    private static bool ClosedByABinding(GuardMessage unset, List<TimeSpan> bindings)
    {
        foreach (TimeSpan at in bindings)
        {
            if (at >= unset.At && at - unset.At <= DrawBurstWindow)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadUnsetSlot(ReadOnlySpan<byte> gatewayMessage, out uint slotId)
    {
        slotId = 0;
        if (gatewayMessage.Length < 23)
        {
            return false;
        }

        try
        {
            var reader = new WireReader(gatewayMessage);
            reader.U8();
            reader.U8();
            reader.U8();
            reader.LeU32();   // profile
            reader.LeU64();   // character
            reader.LeU32();   // unknown
            slotId = reader.LeU32();
            return true;
        }
        catch (WireFormatException)
        {
            return false;
        }
    }

    // ---- G2 ------------------------------------------------------------------------------------

    /// <summary>
    /// <b>G2 — no equipment-slot row for body slot 7 on the whole-character <c>94 01</c>.</b>
    /// <para>
    /// <b>The cause changed twice; the verdict did not.</b> docs/45 proved with a minidump that a
    /// slot-7 row made the client resolve a fire-group descriptor it did not have and dereference
    /// NULL at +0x38. Wave 9 shipped the two suppliers of that descriptor
    /// (<c>ReferenceData "WeaponDefinitions"</c> and the Weapon <c>ItemAdd</c> tail), and on
    /// 2026-08-31 such a row went out deliberately: <b>the client survived</b> — two full sessions,
    /// both closed by the owner, no new minidump. <b>It froze his input instead</b>, until he
    /// dropped the gun, and answered with <c>82 27 AimBlockedNotify</c> 19 ms after the wield.
    /// </para>
    /// <para>
    /// <b>The stated cause, from 2026-09-02 (wave 11):</b> the freeze is <b>a slot-7 binding while
    /// the definition's <c>+0x18</c> is 0 / <c>+0x5c</c> is 0</b>. The August client multiplies the
    /// local player's movement speed by <c>Weapon.MovementModifier</c> at <c>def+0x5c</c> and its
    /// turn rate by <c>Weapon.TurnModifier</c> at <c>def+0x58</c> (<c>FUN_14228fa20</c> →
    /// <c>FUN_1411acfa0</c>; <c>FUN_140c4c440</c>), and it resolves that record by the body id at
    /// <c>def+0x18</c> (<c>FUN_1421e5f50</c> is <c>mov eax,[rcx+0x18]; ret</c>). Cranberry shipped
    /// all three as <c>0</c> in all 61 records until wave 11, and 0 metres per second is exactly
    /// what the owner saw. It is the <i>binding</i> that makes the zeros reachable — six controls
    /// without a slot-7 row moved 5–30 m — which is why this guard is the one that states it.
    /// The fixtures below do not change: the packet shapes they catch are the same shapes.
    /// </para>
    /// <para>
    /// So this guard stays <see cref="GuardSeverity.Fatal"/> — a player who cannot move is a dead
    /// session — but it now enforces docs/95 rather than docs/45: a slot-7 <b>binding</b> belongs on
    /// <c>94 02 SetCharacterEquipmentSlot</c>, sent LAST, after the ability manager. The owner's Z1
    /// server never puts one on the whole-character packet, in 60 draws.
    /// </para>
    /// <para>
    /// A slot-7 <i>attachment</i> (the Weapon_Empty.adr mesh) is harmless and is present in both
    /// known-good reference sessions, so the two are separated here; the attachment set is reported
    /// as Info only.
    /// </para>
    /// </summary>
    private static IEnumerable<GuardFinding> NoActiveHandEquipmentRow(List<GuardMessage> messages)
    {
        List<(GuardMessage Message, SetCharacterEquipmentPacket? Packet)> parsed =
        [
            .. messages
                .Where(m => EquipmentPackets.IsSetCharacterEquipment(m.Signature))
                .Select(m => (m, EquipmentPackets.TryParseSetCharacterEquipment(m.Bytes)))
        ];

        if (parsed.Count == 0)
        {
            yield break;
        }

        List<(GuardMessage Message, SetCharacterEquipmentPacket Packet)> good =
            [.. parsed.Where(p => p.Packet is not null).Select(p => (p.Message, p.Packet!))];

        List<GuardMessage> unparsed = [.. parsed.Where(p => p.Packet is null).Select(p => p.Message)];
        if (unparsed.Count > 0)
        {
            yield return new GuardFinding(
                "G2?",
                GuardSeverity.Suspect,
                "a SetCharacterEquipment packet did not match the recorded layout",
                $"{unparsed.Count} of {parsed.Count} packet(s) could not be decoded, so their slot rows were not checked. "
                + "Either the layout changed or the message was too long to materialise.",
                "Cranberry.Zone.CharacterPackets (client parser FUN_140cd4d40); docs/45 §2",
                [.. unparsed.Select(m => m.At)]);
        }

        List<(GuardMessage Message, SetCharacterEquipmentPacket Packet)> offenders =
            [.. good.Where(p => p.Packet.SlotRowIds.Contains(ActiveHandSlot))];

        if (offenders.Count > 0)
        {
            yield return new GuardFinding(
                "G2",
                GuardSeverity.Fatal,
                $"SetCharacterEquipment carried an equipment-slot row for body slot {ActiveHandSlot} (RHand)",
                $"{offenders.Count} packet(s); row sets "
                + string.Join(" | ", offenders.Select(o => $"[{string.Join(", ", o.Packet.SlotRowIds)}]"))
                + ". docs/95: a slot-7 binding belongs on 94 02 SetCharacterEquipmentSlot, sent "
                + "last, after the ability manager - not on the whole-character 94 01. The cause "
                + "of the freeze, as of 2026-09-02, is a slot-7 binding while the definition's "
                + "+0x18 is 0 / +0x5c is 0: the client resolves the held weapon's record by the "
                + "body id at def+0x18 and multiplies movement speed by def+0x5c and turn rate by "
                + "def+0x58, and Cranberry shipped all three as 0. On 2026-08-31 this shape did "
                + "NOT crash the client (docs/45's NULL fire-group descriptor has been supplied "
                + "since wave 9) but froze the player until he dropped the item.",
                "docs/98 / OVERHAUL-PLAN §1.4 (FUN_14228fa20 -> FUN_1411acfa0 for +0x5c, "
                + "FUN_140c4c440 for +0x58, FUN_1421e5f50 for +0x18; the client's own constructor "
                + "FUN_1421e5b70 presets 1.0f); docs/95 §0 (the 18:35 session; 82 27 "
                + "AimBlockedNotify 19 ms after the wield); docs/45 §1-§2a for the superseded "
                + "crash cause",
                [.. offenders.Select(o => o.Message.At)]);
        }

        List<(GuardMessage Message, SetCharacterEquipmentPacket Packet)> notConsumed =
            [.. good.Where(p => !p.Packet.FullyConsumed)];
        if (notConsumed.Count > 0)
        {
            yield return new GuardFinding(
                "G2!",
                GuardSeverity.Suspect,
                "SetCharacterEquipment decoded but left bytes over",
                $"{notConsumed.Count} packet(s) parsed without consuming the whole message "
                + $"({string.Join(", ", notConsumed.Select(p => $"{p.Packet.BytesConsumed}/{p.Packet.Length}"))}). "
                + "The guard's view of the slot rows may be wrong.",
                "Cranberry.Zone.CharacterPackets",
                [.. notConsumed.Select(p => p.Message.At)]);
        }

        var attachmentSlots = new SortedSet<uint>(good.SelectMany(p => p.Packet.AttachmentSlotIds));
        var rowSlots = new SortedSet<uint>(good.SelectMany(p => p.Packet.SlotRowIds));
        yield return new GuardFinding(
            "G2i",
            GuardSeverity.Info,
            "equipment composition",
            $"{good.Count} SetCharacterEquipment packet(s); attachment slots [{string.Join(", ", attachmentSlots)}]; "
            + $"equipment-slot rows [{(rowSlots.Count == 0 ? "none" : string.Join(", ", rowSlots))}]",
            "docs/45 §2 — the row list is the wave-3 addition; the attachment list is not",
            []);
    }

    // ---- G3 ------------------------------------------------------------------------------------

    /// <summary>
    /// <b>G3 — worn-skin announcements must not precede SetCharacterEquipment on the zoning path.</b>
    /// docs/32's first difference: the skin re-announce and the equipment packet used to be sent in
    /// response to the client's ClientIsReady (Zoning) and were moved to fire proactively at match
    /// zoning, into a client that was not listening. The wire shape of that mistake is a
    /// <c>SetSkinItem</c> (ac 24) arriving after ClientBeginZoning with no SetCharacterEquipment
    /// between them.
    /// </summary>
    private static IEnumerable<GuardFinding> NoWornSkinBeforeEquipment(List<GuardMessage> messages)
    {
        List<TimeSpan> zoningOffenders = [];
        List<TimeSpan> menuOffenders = [];
        bool zoning = false;
        bool equipmentSeenInPhase = false;

        foreach (GuardMessage message in messages)
        {
            if (message.Signature.Kind == SignatureKind.Zone
                && message.Signature.Opcode == ZoneOpcodes.ClientBeginZoning)
            {
                zoning = true;
                equipmentSeenInPhase = false;
                continue;
            }

            if (EquipmentPackets.IsSetCharacterEquipment(message.Signature))
            {
                equipmentSeenInPhase = true;
                continue;
            }

            if (!EquipmentPackets.IsSetSkinItem(message.Signature) || equipmentSeenInPhase)
            {
                continue;
            }

            (zoning ? zoningOffenders : menuOffenders).Add(message.At);
        }

        if (zoningOffenders.Count > 0)
        {
            yield return new GuardFinding(
                "G3",
                GuardSeverity.Fatal,
                "worn-skin rows were announced after ClientBeginZoning with no SetCharacterEquipment first",
                $"{zoningOffenders.Count} SetSkinItem packet(s) on the zoning path. docs/32's first difference: "
                + "the wardrobe re-announce belongs on the client's own ClientIsReady (Zoning), not on the match-zoning path.",
                "docs/32 §The two differences (1), §Fix (1)",
                zoningOffenders);
        }

        if (menuOffenders.Count > 0)
        {
            yield return new GuardFinding(
                "G3i",
                GuardSeverity.Info,
                "worn-skin rows preceded the first SetCharacterEquipment of the menu bootstrap",
                $"{menuOffenders.Count} SetSkinItem packet(s) before the bootstrap's equipment packet. "
                + "Both known-good reference sessions have none; the four docs/32 sessions have 0-6.",
                "docs/32 §Fix (1); captures wire-20260829-150206 / -184346 (0) vs -151627 (0, 2, 5, 6)",
                menuOffenders);
        }
    }

    // ---- G4 ------------------------------------------------------------------------------------

    /// <summary>
    /// <b>G4 — the appearance payloads keep their recorded sizes.</b> The self record
    /// (SendSelfToClient) has been 1,093 bytes in every session since 2026-08-29 13:11 — see
    /// <see cref="SendSelfToClientLength"/> — which makes any change to it a deliberate act that
    /// should be seen; and the size of
    /// SetCharacterEquipment is the number that moved by 24 bytes when docs/45's slot-7 row went on
    /// the wire.
    /// </summary>
    private static IEnumerable<GuardFinding> AppearancePayloadSizes(List<GuardMessage> messages)
    {
        var selfLengths = new SortedSet<int>(messages
            .Where(m => m.Signature.Kind == SignatureKind.Zone && m.Signature.Opcode == ZoneOpcodes.SendSelfToClient)
            .Select(m => m.Length));

        if (selfLengths.Count > 0 && (selfLengths.Count > 1 || selfLengths.Min != SendSelfToClientLength))
        {
            yield return new GuardFinding(
                "G4",
                GuardSeverity.Suspect,
                "the self record changed size",
                $"SendSelfToClient is [{string.Join(", ", selfLengths)}] byte(s); every session since 2026-08-29 13:11 sends {SendSelfToClientLength}.",
                "docs/09/10 (the self record); every capture from wire-20260829-131127 onwards",
                []);
        }

        var equipmentLengths = new SortedSet<int>(messages
            .Where(m => EquipmentPackets.IsSetCharacterEquipment(m.Signature))
            .Select(m => m.Length));

        if (equipmentLengths.Count > 0)
        {
            yield return new GuardFinding(
                "G4i",
                GuardSeverity.Info,
                "SetCharacterEquipment payload sizes",
                $"[{string.Join(", ", equipmentLengths)}] byte(s). The known-good reference sessions send 476 "
                + "(and 726 once the wardrobe is dressed); docs/45's fatal packet was its predecessor plus exactly 24 bytes.",
                "docs/45 §2",
                []);
        }
    }

    /// <summary>Renders findings, worst first, for a report.</summary>
    public static string Render(string label, IReadOnlyList<GuardFinding> findings)
    {
        var text = new StringBuilder();
        int fatal = findings.Count(f => f.Severity == GuardSeverity.Fatal);
        int suspect = findings.Count(f => f.Severity == GuardSeverity.Suspect);
        text.AppendLine($"{label}: {fatal} fatal, {suspect} suspect, {findings.Count - fatal - suspect} informational");
        foreach (GuardFinding finding in findings.OrderBy(f => f.Severity))
        {
            text.AppendLine("  " + finding.ToString().Replace("\n", "\n  ", StringComparison.Ordinal));
        }

        return text.ToString();
    }
}
