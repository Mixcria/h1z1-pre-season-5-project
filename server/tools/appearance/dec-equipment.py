"""Decode every ``94 01 SetCharacterEquipment`` / ``94 02 SetCharacterEquipmentSlot`` in a Cranberry
wire capture, attachment by attachment, and say whose colourway each attachment carries.

This is the decode docs/106 has used since its section 1.2 (the lost ``out\\skinlane\\dec2.py``),
kept in the tree this time. Layouts are Cranberry's own writers:
``src/Cranberry.Zone/CharacterPackets.cs`` (``SetCharacterEquipment``,
``CharacterEquipmentAttachment``) and ``SetCharacterEquipmentWithSlots``.

    python tools/appearance/dec-equipment.py C:\\Aug2017\\captures\\wire-20260903-213229.txt \
        --after 21:35:38 [--table C:\\Aug2017\\out\\appearance-tints\\z1-appearance-parsed.json]

The appearance-row -> item map is read from the parsed table so each attachment's ``app`` list can
be labelled with the item those rows belong to (the base item, or the wardrobe reward whose rows
replaced it). Cranberry-authored rows (``0x4352xxxx``) are labelled ``authored``.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path


class Reader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.pos = 0

    def u8(self) -> int:
        value = self.data[self.pos]
        self.pos += 1
        return value

    def u32(self) -> int:
        (value,) = struct.unpack_from("<I", self.data, self.pos)
        self.pos += 4
        return value

    def i32(self) -> int:
        (value,) = struct.unpack_from("<i", self.data, self.pos)
        self.pos += 4
        return value

    def u64(self) -> int:
        (value,) = struct.unpack_from("<Q", self.data, self.pos)
        self.pos += 8
        return value

    def string(self) -> str:
        count = self.u32()
        value = self.data[self.pos : self.pos + count].decode("utf-8", "replace")
        self.pos += count
        return value

    @property
    def remaining(self) -> int:
        return len(self.data) - self.pos


def read_attachment(reader: Reader) -> dict:
    model = reader.string()
    texture = reader.string()
    tint = reader.string()
    decal = reader.string()
    tint_id = reader.u32()
    composite = reader.u32()
    effect = reader.u32()
    slot = reader.u32()
    group = reader.u32()
    count = reader.i32()
    ids = [reader.u32() for _ in range(count)]
    flag = reader.u8()
    return {
        "model": model,
        "texture": texture,
        "tint": tint,
        "decal": decal,
        "tintId": tint_id,
        "composite": composite,
        "effect": effect,
        "slot": slot,
        "grp": group,
        "app": ids,
        "flag": flag,
    }


def decode_94_01(payload: bytes) -> dict:
    reader = Reader(payload)
    assert reader.u8() == 0x94 and reader.u8() == 0x01
    profile = reader.u32()
    guid = reader.u64()
    unknown = reader.u32()
    tint_alias = reader.string()
    decal_alias = reader.string()
    slot_count = reader.i32()
    slots = []
    for _ in range(slot_count):
        slot_id = reader.u32()
        slot_id2 = reader.u32()
        item_guid = reader.u64()
        s1 = reader.string()
        s2 = reader.string()
        slots.append((slot_id, slot_id2, item_guid, s1, s2))
    attachment_count = reader.i32()
    attachments = [read_attachment(reader) for _ in range(attachment_count)]
    trailing = reader.u8() if reader.remaining else None
    return {
        "profile": profile,
        "guid": guid,
        "unknown": unknown,
        "tintAlias": tint_alias,
        "decalAlias": decal_alias,
        "slots": slots,
        "attachments": attachments,
        "trailing": trailing,
        "leftover": reader.remaining,
    }


def decode_94_02(payload: bytes) -> dict:
    reader = Reader(payload)
    assert reader.u8() == 0x94 and reader.u8() == 0x02
    profile = reader.u32()
    guid = reader.u64()
    unknown = reader.u32()
    slot_id = reader.u32()
    slot_id2 = reader.u32()
    item_guid = reader.u64()
    s1 = reader.string()
    s2 = reader.string()
    attachment = read_attachment(reader)
    return {
        "profile": profile,
        "guid": guid,
        "unknown": unknown,
        "slot": (slot_id, slot_id2, item_guid, s1, s2),
        "attachment": attachment,
        "leftover": reader.remaining,
    }


def load_rows(path: Path) -> dict[int, tuple[int, int, int]]:
    """row id -> (item id, gender id, shader group)."""
    table = json.loads(path.read_text(encoding="utf-8"))
    return {row[0]: (row[2], row[4], row[6]) for row in table["app"]}


def label(ids: list[int], rows: dict[int, tuple[int, int, int]]) -> str:
    if not ids:
        return "NO ROWS"
    parts = []
    for rid in ids:
        if rid >= 0x43520000:
            parts.append(f"{rid:#x}=authored")
        elif rid in rows:
            item, gender, group = rows[rid]
            parts.append(f"{rid}=item {item} g{gender} grp{group}")
        else:
            parts.append(f"{rid}=?")
    return ", ".join(parts)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("capture")
    parser.add_argument("--after", default="00:00:00")
    parser.add_argument("--before", default="23:59:59.999")
    parser.add_argument(
        "--table", default=r"C:\Aug2017\out\appearance-tints\z1-appearance-parsed.json"
    )
    parser.add_argument("--slots", default="", help="comma list of body slots to print; empty = all")
    args = parser.parse_args()

    rows = load_rows(Path(args.table))
    wanted = {int(s) for s in args.slots.split(",") if s.strip()}

    with open(args.capture, encoding="utf-8", errors="replace") as handle:
        for number, line in enumerate(handle, start=1):
            parts = [p.strip() for p in line.rstrip("\n").split("|")]
            if len(parts) < 6 or parts[3] != "s2c":
                continue
            stamp = parts[0]
            if stamp < args.after or stamp > args.before:
                continue
            hexbytes = parts[5]
            if not hexbytes.startswith("059401") and not hexbytes.startswith("059402"):
                continue
            payload = bytes.fromhex(hexbytes)[1:]
            try:
                if payload[1] == 0x01:
                    packet = decode_94_01(payload)
                    print(
                        f":{number} {stamp} 94 01 guid={packet['guid']} profile={packet['profile']} "
                        f"rows={[s[0] for s in packet['slots']]} "
                        f"attachments={len(packet['attachments'])} leftover={packet['leftover']}"
                    )
                    for att in packet["attachments"]:
                        if wanted and att["slot"] not in wanted:
                            continue
                        print(
                            f"    slot {att['slot']:>3}  grp {att['grp']:>5}  app {att['app']}  "
                            f"{att['model']}  -> {label(att['app'], rows)}"
                        )
                else:
                    packet = decode_94_02(payload)
                    att = packet["attachment"]
                    print(
                        f":{number} {stamp} 94 02 guid={packet['guid']} row={packet['slot'][0]} "
                        f"itemGuid={packet['slot'][2]} leftover={packet['leftover']}"
                    )
                    print(
                        f"    slot {att['slot']:>3}  grp {att['grp']:>5}  app {att['app']}  "
                        f"{att['model']}  -> {label(att['app'], rows)}"
                    )
            except Exception as error:  # noqa: BLE001 - a decode failure is the finding
                print(f":{number} {stamp} DECODE FAILED: {error!r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
