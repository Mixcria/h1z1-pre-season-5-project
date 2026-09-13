"""Grades one Cranberry wire capture for the wave-11 owner-free loop (live-run.ps1).

Everything here is read from the capture file the host wrote: the client's own channel-2
movement stream (decoded with the same field order as src/Cranberry.Zone/MovementPackets.cs,
FUN_140a3ca40), the server's ReferenceData "WeaponDefinitions" table, and the handful of
server-to-client and client-to-server messages a draw is made of.

    python grade-capture.py <capture.txt> [--from HH:MM:SS] [--to HH:MM:SS] [--json out.json]

Nothing is written except the JSON the caller asks for; the capture is opened read-only.
"""

import argparse
import json
import math
import struct
import sys

# ---------------------------------------------------------------- capture file


def to_ms(stamp):
    hh, mm, rest = stamp.split(":")
    ss, frac = rest.split(".")
    return ((int(hh) * 60 + int(mm)) * 60 + int(ss)) * 1000 + int(frac)


def read_lines(path):
    """(line number, milliseconds, stamp, direction, payload bytes) per application message."""
    out = []
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        for number, line in enumerate(handle, 1):
            if line.startswith("#"):
                continue
            parts = [p.strip() for p in line.rstrip("\n").split("|")]
            if len(parts) < 6 or parts[3] not in ("c2s", "s2c"):
                continue
            body = parts[5]
            if len(body) < 2:
                continue
            try:
                raw = bytes.fromhex(body)
            except ValueError:
                continue
            out.append((number, to_ms(parts[0]), parts[0], parts[3], raw))
    return out


# ------------------------------------------------------- channel-2 record decode


class Reader:
    def __init__(self, data):
        self.data = data
        self.at = 0

    def u8(self):
        value = self.data[self.at]
        self.at += 1
        return value

    def u16(self):
        value = struct.unpack_from("<H", self.data, self.at)[0]
        self.at += 2
        return value

    def u32(self):
        value = struct.unpack_from("<I", self.data, self.at)[0]
        self.at += 4
        return value

    def f32(self):
        value = struct.unpack_from("<f", self.data, self.at)[0]
        self.at += 4
        return value

    def packed_unsigned(self):
        first = self.u8()
        packed = first
        for i in range(1, (first & 3) + 1):
            packed |= self.u8() << (i * 8)
        return packed >> 2

    def packed_signed(self):
        first = self.u8()
        negative = first & 1
        packed = first
        for i in range(1, ((first >> 1) & 3) + 1):
            packed |= self.u8() << (i * 8)
        magnitude = packed >> 3
        return -magnitude if negative else magnitude

    def scaled(self, scale):
        return self.packed_signed() / scale

    def vector(self, scale):
        return (self.scaled(scale), self.scaled(scale), self.scaled(scale))

    def quaternion(self, scale):
        return (self.scaled(scale), self.scaled(scale), self.scaled(scale), self.scaled(scale))


def decode_movement(payload):
    """One channel-2 ClientMovementUpdate; raises on anything that does not walk cleanly."""
    r = Reader(payload)
    mask = r.u16()
    if mask & ~0x1FFF:
        raise ValueError("unknown movement flag bits 0x%04x" % mask)
    record = {"mask": mask, "clientTime": r.u32(), "state": r.u8()}
    record["posture"] = r.packed_unsigned() if mask & 0x0001 else None
    record["position"] = r.vector(100.0) if mask & 0x0002 else None
    record["orientation"] = r.f32() if mask & 0x0020 else None
    record["s14c"] = r.scaled(100.0) if mask & 0x0040 else None
    record["s150"] = r.scaled(100.0) if mask & 0x0080 else None
    record["s154"] = r.scaled(100.0) if mask & 0x0004 else None
    record["verticalSpeed"] = r.scaled(100.0) if mask & 0x0008 else None
    record["horizontalSpeed"] = r.scaled(10.0) if mask & 0x0010 else None
    record["auxiliary"] = r.vector(100.0) if mask & 0x0100 else None
    record["rotation"] = r.quaternion(100.0) if mask & 0x0200 else None
    record["s140"] = r.scaled(10.0) if mask & 0x0400 else None
    record["s144"] = r.scaled(10.0) if mask & 0x0800 else None
    if mask & 0x1000:
        record["precisePosition"] = r.vector(100.0)
        r.quaternion(100.0)
    else:
        record["precisePosition"] = None
    if r.at != len(payload):
        raise ValueError("%d trailing byte(s)" % (len(payload) - r.at))
    return record


# --------------------------------------------------- ReferenceData WeaponDefinitions


def decode_weapon_definitions(payload):
    """List-0 of the WeaponDefinitions table. `payload` starts at the 0x17 base opcode."""
    if not payload or payload[0] != 0x17:
        return None
    at = 1
    tag = struct.unpack_from("<H", payload, at)[0]
    at += 2
    name_length = tag & 0x1FFF
    name = payload[at:at + name_length].decode("ascii", "replace")
    at += name_length
    if name != "WeaponDefinitions":
        return None
    at += 1                      # the NUL the 13-bit length does not count
    at += 8                      # the declared length, twice
    blob_start = at
    count = struct.unpack_from("<I", payload, at)[0]
    at += 4
    records = []
    for _ in range(count):
        start = at
        key = struct.unpack_from("<I", payload, at)[0]
        at += 4
        body_id = struct.unpack_from("<I", payload, at)[0]
        at += 4
        at += 4                  # +0x20
        at += 1                  # +0x24
        at += 72                 # +0x28 .. +0x80 (18 u32; 0x58 and 0x5c are inside)
        at += 8                  # the sub-structure pair
        string_length = struct.unpack_from("<I", payload, at)[0]
        at += 4 + string_length
        at += 28                 # 7 u32 (+0xa8 +0xac +0xb8 +0xbc +0xc0 +0xc4 +0xc8)
        # def+0xd0 THE AMMO-SLOT ARRAY (thunk_FUN_140a515d0 -> FUN_140a2c690 bodies).
        # i32 n, then n slots of: u32 ammoId, u32 clipSize, u32, u8, u32 x3, then three
        # SoeUtil::StringFixed<32> (u32 length + bytes) - 37 bytes with empty strings.
        # Cranberry shipped this array EMPTY until wave 14; the old reader here assumed it
        # was a bare u32 array, which walked off the end of the 89,830-byte wave-14 blob.
        slot_count = struct.unpack_from("<I", payload, at)[0]
        at += 4
        for _ in range(slot_count):
            at += 12             # +0x00 ammoId, +0x04 clipSize, +0x08
            at += 1              # +0x0c
            at += 12             # +0x10 +0x14 +0x18
            for _ in range(3):   # +0x20 +0x38 +0x50 StringFixed<32>
                at += 4 + struct.unpack_from("<I", payload, at)[0]
        fire_groups = struct.unpack_from("<I", payload, at)[0]
        at += 4
        groups = list(struct.unpack_from("<%dI" % fire_groups, payload, at)) if fire_groups else []
        at += 4 * fire_groups
        body = payload[start:at]
        records.append({
            "id": key,
            "bodyId": body_id,
            "offset": start - blob_start,
            "length": len(body),
            "fireGroups": groups,
            "bytes4to7": body[4:8].hex(),
            "bytes57to60": body[57:61].hex(),
            "bytes61to64": body[61:65].hex(),
        })
    return records


# ------------------------------------------------------------------- the grade


def grade(path, window_from=None, window_to=None):
    lines = read_lines(path)
    low = to_ms(window_from) if window_from else None
    high = to_ms(window_to) if window_to else None

    movement = []
    malformed = 0
    weapon_definitions = None
    hand_bindings = []
    item_deletes = []
    aim_blocked = []
    stance = 0
    loadout_slots = 0

    for number, at, stamp, direction, raw in lines:
        header = raw[0]
        payload = raw[1:]
        if direction == "c2s" and (header >> 5) == 2 and (header & 0x1F) == 6:
            try:
                record = decode_movement(payload)
            except Exception:                                     # noqa: BLE001 - a census, not a parser
                malformed += 1
                continue
            record["at"] = at
            record["stamp"] = stamp
            record["line"] = number
            movement.append(record)
            continue

        if direction == "c2s" and len(payload) >= 15 and payload[0] == 0x82 and payload[5] == 0x27:
            aim_blocked.append({
                "stamp": stamp,
                "line": number,
                "itemGuid": "%016x" % struct.unpack_from("<Q", payload, 6)[0],
                "aimBlocked": payload[14],
            })
            continue

        if direction != "s2c" or not payload:
            continue
        if payload[0] == 0x17 and weapon_definitions is None:
            weapon_definitions = decode_weapon_definitions(payload)
        elif payload[0] == 0x94 and len(payload) > 1 and payload[1] == 0x02 and len(payload) >= 30:
            hand_bindings.append({
                "stamp": stamp,
                "line": number,
                "slot": struct.unpack_from("<I", payload, 18)[0],
                "itemGuid": "%016x" % struct.unpack_from("<Q", payload, 22)[0],
            })
        elif payload[0] == 0x11 and len(payload) >= 3 and struct.unpack_from("<H", payload, 1)[0] == 0x0004:
            item_deletes.append({"stamp": stamp, "line": number})
        elif payload[0] == 0x0F and len(payload) > 1 and payload[1] == 0x20:
            stance += 1
        elif payload[0] == 0x86 and len(payload) > 1 and payload[1] == 0x04:
            loadout_slots += 1

    window = [m for m in movement
              if (low is None or m["at"] >= low) and (high is None or m["at"] <= high)]

    positions = [m for m in window if m["position"] or m["precisePosition"]]
    coordinates = [(m["position"] or m["precisePosition"]) for m in positions]
    displacement = 0.0
    if coordinates:
        displacement = math.hypot(
            max(c[0] for c in coordinates) - min(c[0] for c in coordinates),
            max(c[2] for c in coordinates) - min(c[2] for c in coordinates))

    # The best five-second bucket, so a pause at either end of the window cannot mask a
    # healthy stream in the middle of it.
    best_five_seconds = 0
    for index, sample in enumerate(positions):
        limit = sample["at"] + 5000
        best_five_seconds = max(
            best_five_seconds,
            sum(1 for other in positions[index:] if other["at"] <= limit))

    yaws = set()
    for m in window:
        if m["orientation"] is not None:
            yaws.add(round(m["orientation"], 2))
        elif m["rotation"] is not None:
            yaws.add(round(m["rotation"][0], 2))

    input_records = sum(
        1 for m in window
        if (m["posture"] is not None and (m["posture"] >> 16) & 1) or (m["s144"] or 0) > 0)

    hand = [b for b in hand_bindings if b["slot"] == 7]
    offenders = []
    if weapon_definitions:
        offenders = [r for r in weapon_definitions
                     if r["bodyId"] != r["id"]
                     or r["bytes57to60"] != "0000803f"
                     or r["bytes61to64"] != "0000803f"]

    return {
        "capture": path,
        "window": {"from": window_from, "to": window_to},
        "channel2": {
            "records": len(movement),
            "recordsInWindow": len(window),
            "malformed": malformed,
            "positionRecordsInWindow": len(positions),
            "bestPositionRecordsPerFiveSeconds": best_five_seconds,
            "displacementMetres": round(displacement, 3),
            "distinctYaw": len(yaws),
            "inputRecordsInWindow": input_records,
        },
        "draw": {
            "slot7Bindings": hand,
            "allEquipmentSlotBindings": hand_bindings,
            "itemDeletes": item_deletes,
            "weaponStance0f20": stance,
            "setLoadoutSlots8604": loadout_slots,
            "aimBlockedNotify8227": aim_blocked,
        },
        "weaponDefinitions": {
            "records": len(weapon_definitions) if weapon_definitions else 0,
            "offenders": len(offenders),
            "firstOffenders": offenders[:3],
            "sample": (weapon_definitions or [])[:1],
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture")
    parser.add_argument("--from", dest="window_from")
    parser.add_argument("--to", dest="window_to")
    parser.add_argument("--id", dest="definition_id", type=int,
                        help="also print the list-0 record with this id")
    parser.add_argument("--json", dest="json_path")
    arguments = parser.parse_args()

    result = grade(arguments.capture, arguments.window_from, arguments.window_to)
    if arguments.definition_id is not None:
        table = decode_weapon_definitions_for(arguments.capture, arguments.definition_id)
        result["weaponDefinitions"]["requested"] = table

    text = json.dumps(result, indent=2)
    if arguments.json_path:
        with open(arguments.json_path, "w", encoding="utf-8") as handle:
            handle.write(text)
    print(text)
    return 0


def decode_weapon_definitions_for(path, definition_id):
    for _, _, _, direction, raw in read_lines(path):
        if direction != "s2c" or len(raw) < 2 or raw[1] != 0x17:
            continue
        records = decode_weapon_definitions(raw[1:])
        if records:
            return [r for r in records if r["id"] == definition_id]
    return []


if __name__ == "__main__":
    sys.exit(main())
