"""Create small public-definition slices and wholly synthetic movement fixtures."""
import json
import pathlib
import struct
import sys

sys.dont_write_bytecode = True
from audit_capture import definitions
from capture_source import load_capture, sha
from movement_1315 import FIELDS
from weapon_1315 import parse

FIXTURES = pathlib.Path(__file__).parent / "fixtures"


def packed(value, signed=False):
    shift = 3 if signed else 2
    raw = abs(value) << shift
    if signed and value < 0: raw |= 1
    for size in range(1, 5):
        if raw < 1 << (8 * size):
            return (raw | ((size - 1) << (1 if signed else 0))).to_bytes(size, "little")
    raise ValueError("packed magnitude out of range")


def synthetic_movement(mask, direction):
    data = bytes([0x46 if direction == "c2s" else 0x45])
    if direction == "s2c": data += bytes.fromhex("0100000000000000")
    data += struct.pack("<HIB", mask, 123456, 7)
    for bit, kind, count in FIELDS:
        if not mask & bit: continue
        for index in range(count):
            if kind == "u16": data += struct.pack("<H", 15360)
            elif kind == "packed_u": data += packed(8192)
            else: data += packed((index + 1) * (-123 if index % 2 else 321), True)
    return data


def main():
    FIXTURES.mkdir(parents=True, exist_ok=True)
    au, report, decoded = load_capture()
    source, body = next((p, b) for p, b in definitions(decoded, au.TABLE.lz4_block_decompress)
                        if p["name"] == "WeaponDefinitions")
    full = parse(body)
    selected = ([6], [6], [8, 9, 176, 405, 431], [1], [8, 9, 405, 431], [1], [1, 2], [1])
    mini = b""
    slices = []
    for number, ids in enumerate(selected):
        rows = [r for r in full["lists"][number] if r["id"] in ids]
        mini += struct.pack("<I", len(rows))
        for r in rows:
            chunk = body[r["offset"]:r["offset"] + r["bytes"]]
            slices.append({"list": number, "id": r["id"], "sourceOffset": r["offset"],
                           "fixtureOffset": len(mini), "bytes": len(chunk), "sha256": sha(chunk)})
            mini += chunk
    parse(mini)
    (FIXTURES / "weapon-mini.bin").write_bytes(mini)
    (FIXTURES / "weapon-mini.json").write_text(json.dumps({
        "kind": "public static definition slices; list counts rebuilt; not a replay table",
        "source": source, "sha256": sha(mini), "slices": slices}, indent=2) + "\n", encoding="utf-8", newline="\n")
    rows = []
    for direction in ("c2s", "s2c"):
        for mask in [bit for bit, _, _ in FIELDS] + [0x2222, 0x7fff]:
            rows.append({"kind": "synthetic; no captured identity/position/time", "direction": direction,
                         "mask": mask, "hex": synthetic_movement(mask, direction).hex()})
    (FIXTURES / "movement-synthetic.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8", newline="\n")
    events = []
    for stream, frame, time, direction, data, reliable in decoded:
        if frame == 22647 and data[:4] == b"\x06\xa9\x01\x00" and not any(r["schema"] == "dto-hit" for r in events):
            clean = bytearray(data); clean[4:12] = (1).to_bytes(8, "little")
            clean[-12:-8] = (123).to_bytes(4, "little"); clean[-8:] = (2).to_bytes(8, "little")
            events.append({"schema": "dto-hit", "sourceFrame": frame, "direction": direction, "hex": clean.hex()})
        if frame == 22655 and data[:4] == b"\x05\xa9\x02\x00":
            clean = bytearray(data); clean[4:12] = (1).to_bytes(8, "little")
            events.append({"schema": "dto-replacement", "sourceFrame": frame, "direction": direction, "hex": clean.hex()})
        if frame == 9993 and data[:3] == b"\x05\xde\x01":
            clean = bytearray(data); clean[3:11] = (1).to_bytes(8, "little"); clean[11:19] = (2).to_bytes(8, "little")
            events.append({"schema": "appearance", "sourceFrame": frame, "direction": direction, "hex": clean.hex()})
    events.append({"schema": "bounce", "hex": struct.pack("<IIQ", 123, 6683, 2).hex()})
    shot = struct.pack("<QfffIIfffI", 3, 1., 2., 3., 1, 123, 0., 0., 1., 0)
    events.append({"schema": "fire", "hex": shot.hex()})
    (FIXTURES / "event-synthetic.json").write_text(json.dumps({
        "sanitization": "Object/actor/projectile identifiers replaced with synthetic 1/2/3/123; fire pose and time synthetic; public model/effect/flag values retained.",
        "cases": events}, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(mini)} public definition bytes and {len(rows)} synthetic movement cases")


if __name__ == "__main__": main()
