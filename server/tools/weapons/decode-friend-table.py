#!/usr/bin/env python3
r"""Decode the friend's captured ReferenceData "WeaponDefinitions" (0x17 0x04) packet and walk it
with CRANBERRY'S OWN [P] 1148 record layouts.

Source packet: C:\Z1\Server\Data\friendWeaponDefinitions.bin - 41,281 bytes, captured off the
friend's 1087 server (docs/122, D312).  Framing:

    17 04 | u32 bytesWithLength (= len-6) | u32 compressed (= len-14) | u32 uncompressed | lz4 block

The walk is the 1087 -> 1148 compatibility test (docs/122 lane A step 1): the layouts below are
read straight out of Cranberry's own sources so the two can never drift -

    src/Cranberry.Zone/Weapons/WeaponDefinitionsBlob.cs   lists 0 and 1 (docs/58 section 4)
    src/Cranberry.Zone/Weapons/WeaponListLayouts.cs       list 2 body + lists 3-7 lengths (docs/99)
    src/Cranberry.Zone/Weapons/WeaponListRecords.cs       the list 3-7 record shapes

If the walk consumes exactly the declared uncompressed length with all eight lists well formed, the
1087 body IS layout-compatible with the 1148 reader and may be shipped verbatim inside Cranberry's
own ReferenceData framing.

Usage:
    python tools/weapons/decode-friend-table.py [--input PATH] [--out out/weapons/friend-table.json]
                                                [--source-root .] [--quiet]

stdlib only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
from pathlib import Path

# --------------------------------------------------------------------------- LZ4 block decoder


def lz4_block_decompress(src: bytes, expected: int | None) -> bytes:
    """Standard LZ4 block format (no frame header): token / literals / 2-byte LE offset / match."""
    out = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                if i >= n:
                    raise ValueError("truncated literal length")
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        if lit:
            if i + lit > n:
                raise ValueError("truncated literals")
            out += src[i:i + lit]
            i += lit
        if i >= n:
            break  # last sequence is literals only
        if i + 2 > n:
            raise ValueError("truncated match offset")
        offset = src[i] | (src[i + 1] << 8)
        i += 2
        if offset == 0:
            raise ValueError("zero match offset")
        mlen = token & 0x0F
        if mlen == 15:
            while True:
                if i >= n:
                    raise ValueError("truncated match length")
                b = src[i]
                i += 1
                mlen += b
                if b != 255:
                    break
        mlen += 4
        start = len(out) - offset
        if start < 0:
            raise ValueError("match offset before start of output")
        for k in range(mlen):
            out.append(out[start + k])
    if expected is not None and len(out) != expected:
        raise ValueError("decompressed %d bytes, header declared %d" % (len(out), expected))
    return bytes(out)


# --------------------------------------------------------- Cranberry layouts, read from the source

_KIND_SIZE = {"U8": 1, "I8": 1, "B": 1, "I16": 2, "U32": 4, "F32": 4}

_LAYOUTS = "src/Cranberry.Zone/Weapons/WeaponListLayouts.cs"


def load_fire_mode_body(src_root: Path):
    """WeaponListLayouts.FireModeBody - (recordOffset, kind) in exact wire order."""
    text = (src_root / _LAYOUTS).read_text(encoding="utf-8")
    m = re.search(r"FireModeBody\s*\{\s*get;\s*\}\s*=\s*\[(.*?)\n\s*\];", text, re.S)
    if not m:
        raise SystemExit("could not find WeaponListLayouts.FireModeBody")
    fields = re.findall(r"new\(\s*(0x[0-9a-fA-F]+)\s*,\s*(U8|I8|I16|U32|F32|B)\s*\)", m.group(1))
    if not fields:
        raise SystemExit("FireModeBody parsed empty")
    return [(int(off, 16), kind) for off, kind in fields]


def load_fire_mode_columns(src_root: Path):
    """WeaponListLayouts.FireModeColumns - recordOffset -> [(name, kind, flagBit)]."""
    text = (src_root / _LAYOUTS).read_text(encoding="utf-8")
    m = re.search(r"FireModeColumns\s*\{\s*get;\s*\}\s*=\s*\[(.*?)\n\s*\];", text, re.S)
    if not m:
        raise SystemExit("could not find WeaponListLayouts.FireModeColumns")
    cols = {}
    pattern = re.compile(
        r'new\(\s*"([A-Z0-9_]+)"\s*,\s*(0x[0-9a-fA-F]+)\s*,\s*'
        r'FireModeColumnKind\.(\w+)\s*(?:,\s*(0x[0-9a-fA-F]+)\s*)?\)')
    for name, off, kind, bit in pattern.findall(m.group(1)):
        cols.setdefault(int(off, 16), []).append((name, kind, int(bit, 16) if bit else 0))
    return cols


# ----------------------------------------------------------------------------------- the cursor


class WalkError(Exception):
    pass


class Cursor:
    __slots__ = ("data", "pos")

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.pos = 0

    def need(self, count: int) -> None:
        if self.pos + count > len(self.data):
            raise WalkError("ran off the end: wanted %d B at %d, %d B left"
                            % (count, self.pos, len(self.data) - self.pos))

    def u8(self) -> int:
        self.need(1)
        v = self.data[self.pos]
        self.pos += 1
        return v

    def i8(self) -> int:
        v = self.u8()
        return v - 256 if v >= 128 else v

    def i16(self) -> int:
        self.need(2)
        v = struct.unpack_from("<h", self.data, self.pos)[0]
        self.pos += 2
        return v

    def u32(self) -> int:
        self.need(4)
        v = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return v

    def i32(self) -> int:
        self.need(4)
        v = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return v

    def f32(self) -> float:
        self.need(4)
        v = struct.unpack_from("<f", self.data, self.pos)[0]
        self.pos += 4
        return v

    def string(self) -> str:
        """SoeUtil::StringFixed<32> on the wire: u32 length + raw bytes (PacketWriter.WriteString)."""
        length = self.u32()
        if length > 4096:
            raise WalkError("implausible string length %d at %d" % (length, self.pos - 4))
        self.need(length)
        raw = self.data[self.pos:self.pos + length]
        self.pos += length
        return raw.decode("utf-8", "replace")

    def counted(self, what: str, limit: int = 1000000) -> int:
        n = self.i32()
        if n < 0 or n > limit:
            raise WalkError("implausible %s count %d at %d" % (what, n, self.pos - 4))
        return n


# ------------------------------------------------------------------------------------ the walk

# list 0 body, in WeaponDefinitionRecord.WriteTo order (docs/58 4e; WeaponDefinitionsBlob.cs).
LIST0_WORDS = [
    ("body_id", "u32"),        # def+0x18  the record's own ID (getter 0x1421e5f50)
    ("word_0x20", "u32"),
    ("byte_0x24", "u8"),
    ("word_0x28", "u32"), ("word_0x2c", "u32"), ("word_0x30", "u32"), ("word_0x34", "u32"),
    ("word_0x50", "u32"),
    ("TO_IRON_SIGHTS_TIME_MS", "u32"),    # def+0x38
    ("FROM_IRON_SIGHTS_TIME_MS", "u32"),  # def+0x3c
    ("word_0x40", "u32"), ("word_0x44", "u32"), ("word_0x48", "u32"), ("word_0x4c", "u32"),
    ("TURN_MODIFIER", "f32"),             # def+0x58  Weapon.TurnModifier
    ("MOVEMENT_MODIFIER", "f32"),         # def+0x5c  Weapon.MovementModifier
    ("word_0x70", "u32"), ("word_0x74", "u32"), ("word_0x78", "u32"),
    ("word_0x7c", "u32"), ("word_0x80", "u32"),
    ("word_0x84", "u32"), ("word_0x88", "u32"),   # thunk_FUN_140a51490 - a fixed pair
]

LIST0_TAIL = [
    ("word_0xa8", "u32"), ("word_0xac", "u32"), ("word_0xb8", "u32"), ("word_0xbc", "u32"),
    ("word_0xc0", "u32"), ("word_0xc4", "u32"), ("word_0xc8", "u32"),
]


def read_typed(cur, kind):
    if kind == "u32":
        return cur.u32()
    if kind == "f32":
        return round(cur.f32(), 6)
    if kind == "u8":
        return cur.u8()
    raise AssertionError(kind)


def walk_list0(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("WeaponDefinitions")):
        start = cur.pos
        rec = {"WEAPON_DEFINITION_ID": cur.u32()}
        for name, kind in LIST0_WORDS:
            rec[name] = read_typed(cur, kind)
        rec["string_0x90"] = cur.string()
        for name, kind in LIST0_TAIL:
            rec[name] = read_typed(cur, kind)
        slots = []
        for _s in range(cur.counted("AMMO_SLOTS", 4096)):
            slots.append({
                "AMMO_ID": cur.u32(),          # slot+0x00  AmmoSlot.AmmoId    [P]
                "CLIP_SIZE": cur.u32(),        # slot+0x04  AmmoSlot.ClipSize  [P]
                "word_0x08": cur.u32(),
                "byte_0x0c": cur.u8(),
                "word_0x10": cur.u32(), "word_0x14": cur.u32(), "word_0x18": cur.u32(),
                "string_0x20": cur.string(), "string_0x38": cur.string(),
                "string_0x50": cur.string(),
            })
        rec["AMMO_SLOTS"] = slots
        rec["FIRE_GROUP_IDS"] = [cur.u32() for _ in range(cur.counted("FIRE_GROUP_IDS", 4096))]
        rec["_bytes"] = cur.pos - start
        out.append(rec)
    return out


def walk_list1(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("FireGroups")):
        start = cur.pos
        rec = {"FIRE_GROUP_ID": cur.u32(), "word_0x18": cur.u32()}
        rec["FIRE_MODE_IDS"] = [cur.u32() for _ in range(cur.counted("FIRE_MODE_IDS", 4096))]
        rec["FLAGS"] = cur.u8()                       # rec+0x38  bit 0x40 = automatic branch
        words = [cur.u32() for _ in range(10)]        # rec+0x3c .. +0x60
        rec["trailing_words"] = words
        rec["SPIN_UP_MOVEMENT_MODIFIER"] = round(struct.unpack("<f", struct.pack("<I", words[6]))[0], 6)
        rec["SPIN_UP_TURN_RATE_MODIFIER"] = round(struct.unpack("<f", struct.pack("<I", words[7]))[0], 6)
        rec["_bytes"] = cur.pos - start
        out.append(rec)
    return out


def walk_list2(cur, body, columns, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("FireModes")):
        start = cur.pos
        rec = {"FIRE_MODE_ID": cur.u32(), "word_0x18": cur.u32()}
        raw = {}
        for off, kind in body:
            if kind in ("U8", "B"):
                v = cur.u8()
            elif kind == "I8":
                v = cur.i8()
            elif kind == "I16":
                v = cur.i16()
            elif kind == "U32":
                v = cur.u32()
            else:
                v = round(cur.f32(), 6)
            raw[off] = v
        # Name what Cranberry names (WeaponListLayouts.FireModeColumns); flag bytes expand to bits.
        named = {}
        for off, value in raw.items():
            cols = columns.get(off)
            if not cols:
                named["word_0x%03x" % off] = value
                continue
            for name, kind, bit in cols:
                if kind == "FlagBit":
                    named[name] = bool(int(value) & bit)
                else:
                    named[name] = value
        rec["fields"] = named
        rec["_bytes"] = cur.pos - start
        out.append(rec)
    return out


def walk_list3(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("ConeOfFire")):
        start = cur.pos
        rec = {"CONE_OF_FIRE_ID": cur.u32(), "word_0x00": cur.u32(), "STATES": []}
        for _s in range(cur.counted("ConeOfFire states", 4096)):
            state_id = cur.u32()
            word0 = cur.u32()                              # elem+0x18
            flags = cur.u8()                               # elem+0x20 - ONE byte (FUN_140a41f00)
            rec["STATES"].append({
                "STATE_ID": state_id,
                "FLAGS": flags,
                "words": [word0] + [cur.u32() for _ in range(16)],   # +0x24..+0x60
            })
        rec["_bytes"] = cur.pos - start
        out.append(rec)
    return out


def walk_list4(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("FireModeProjectiles")):
        out.append({
            "FIRE_MODE_DEFINITION_ID": cur.u32(),   # rec+0x20 (hash key)
            "word_0x00": cur.u32(),
            "AMMO_ITEM_ID": cur.u32(),              # rec+0x04
            "PROJECTILE_DEFINITION_ID": cur.u32(),  # rec+0x08
            "_bytes": 16,
        })
    return out


def walk_list5(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("AimAssist")):
        out.append({
            "AIM_ASSIST_ID": cur.u32(),
            "words": [cur.u32() for _ in range(23)],
            "_bytes": 96,
        })
    return out


def walk_list6(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("List6")):
        start = cur.pos
        rec = {"ID": cur.u32(), "word_0x18": cur.u32(), "ELEMENTS": []}
        for _s in range(cur.counted("List6 elements", 65536)):
            rec["ELEMENTS"].append({
                "KEY": cur.u32(),
                "words": [cur.u32(), cur.u32(), cur.u32()],
            })
        rec["_bytes"] = cur.pos - start
        out.append(rec)
    return out


def walk_list7(cur, out=None):
    out = [] if out is None else out
    for _ in range(cur.counted("List7")):
        start = cur.pos
        rec = {"ID": cur.u32(), "word_0x00": cur.u32()}
        rec["VALUES"] = [cur.u32() for _ in range(cur.counted("List7 values", 65536))]
        rec["_bytes"] = cur.pos - start
        out.append(rec)
    return out


LIST_NAMES = [
    "0 WeaponDefinitions", "1 FireGroups", "2 FireModes", "3 ConeOfFire",
    "4 FireModeProjectiles", "5 AimAssist", "6 List6", "7 List7",
]


def unwrap(raw: bytes):
    if raw[0:2] != b"\x17\x04":
        raise SystemExit("not a 17 04 ReferenceData packet (starts %s)" % raw[0:2].hex())
    with_length, compressed, uncompressed = struct.unpack_from("<III", raw, 2)
    if with_length != len(raw) - 6:
        raise SystemExit("bytesWithLength %d != len-6 %d" % (with_length, len(raw) - 6))
    if compressed != len(raw) - 14:
        raise SystemExit("compressed %d != len-14 %d" % (compressed, len(raw) - 14))
    body = lz4_block_decompress(raw[14:], uncompressed)
    header = {
        "wire_bytes": len(raw),
        "bytes_with_length": with_length,
        "compressed_length": compressed,
        "uncompressed_length": uncompressed,
        "body_md5": hashlib.md5(body).hexdigest(),  # noqa: S324 - provenance, not security
    }
    return body, header


# ---------------------------------------------------- the 1087 walk (the layout the packet IS)

# Z1's Data\weaponDefinitionSchema.json, the 1087 server's own schema for this packet. Walking the
# body with it is the CONTROL for the 1148 walk above: if the 1087 schema consumes the body to the
# last byte and the 1148 layouts do not, the difference is a real 1087 -> 1148 layout change and
# the captured bytes cannot be shipped verbatim to an August client.
_Z1_SCHEMA = r"C:\Z1\Server\Data\weaponDefinitionSchema.json"


def read_schema(body: bytes, pos: int, fields):
    obj = {}
    for f in fields:
        t = f["type"]
        n = f["name"]
        if t == "array":
            count = struct.unpack_from("<I", body, pos)[0]
            pos += 4
            rows = []
            for _ in range(count):
                row, pos = read_schema(body, pos, f["fields"])
                rows.append(row)
            obj[n] = rows
        elif t == "schema":
            obj[n], pos = read_schema(body, pos, f["fields"])
        elif t == "string":
            length = struct.unpack_from("<I", body, pos)[0]
            pos += 4
            obj[n] = body[pos:pos + length].decode("utf-8", "replace")
            pos += length
        elif t == "uint32":
            obj[n] = struct.unpack_from("<I", body, pos)[0]
            pos += 4
        elif t == "int32":
            obj[n] = struct.unpack_from("<i", body, pos)[0]
            pos += 4
        elif t == "uint16":
            obj[n] = struct.unpack_from("<H", body, pos)[0]
            pos += 2
        elif t == "uint8":
            obj[n] = body[pos]
            pos += 1
        elif t == "boolean":
            obj[n] = body[pos] != 0
            pos += 1
        elif t == "float":
            obj[n] = struct.unpack_from("<f", body, pos)[0]
            pos += 4
        else:
            raise WalkError("unknown schema type %s for %s" % (t, n))
    return obj, pos


def walk_1087(body: bytes, schema_path: str):
    """Walk the body with the 1087 schema. Returns (table, consumed, error)."""
    try:
        schema = json.loads(Path(schema_path).read_text(encoding="utf-8"))
    except OSError as exc:
        return None, 0, "the 1087 schema did not load (%s)" % exc
    try:
        table, pos = read_schema(body, 0, schema)
    except (WalkError, struct.error, IndexError) as exc:
        return None, 0, str(exc)
    return table, pos, None


def decode(raw: bytes, src_root: Path, schema_path: str = _Z1_SCHEMA):
    body, header = unwrap(raw)
    fire_mode_body = load_fire_mode_body(src_root)
    fire_mode_columns = load_fire_mode_columns(src_root)
    body_len = sum(_KIND_SIZE[k] for _o, k in fire_mode_body)

    cur = Cursor(body)
    lists = []
    offsets = []
    error = None
    walkers = [
        walk_list0, walk_list1,
        lambda c, out: walk_list2(c, fire_mode_body, fire_mode_columns, out),
        walk_list3, walk_list4, walk_list5, walk_list6, walk_list7,
    ]
    partial = []
    try:
        for walker in walkers:
            offsets.append(cur.pos)
            partial = []
            lists.append(partial)
            walker(cur, partial)
    except WalkError as exc:
        index = len(lists) - 1
        error = ("list %d (%s), record index %d, body offset %d: %s"
                 % (index, LIST_NAMES[index], len(partial), cur.pos, exc))

    consumed = cur.pos
    clean = error is None and consumed == len(body)

    table_1087, consumed_1087, error_1087 = walk_1087(body, schema_path)

    return {
        "header": header,
        "walk1087": {
            "schema": schema_path,
            "consumed": consumed_1087,
            "clean": error_1087 is None and consumed_1087 == len(body),
            "error": error_1087,
            "lists": [] if table_1087 is None else [
                {"list": name, "count": len(rows)} for name, rows in table_1087.items()
            ],
        },
        "table1087": table_1087,
        "fire_mode_body_bytes": body_len,
        "fire_mode_record_bytes": 8 + body_len,
        "walk": {
            "body_bytes": len(body),
            "consumed": consumed,
            "remaining": len(body) - consumed,
            "clean": clean,
            "error": error,
            "lists": [
                {"list": LIST_NAMES[i], "count": len(lists[i]),
                 "start_offset": offsets[i],
                 "bytes": (offsets[i + 1] if i + 1 < len(offsets) else consumed) - offsets[i]}
                for i in range(len(lists))
            ],
        },
        "lists": dict((LIST_NAMES[i], lists[i]) for i in range(len(lists))),
    }


def main() -> int:
    ap = argparse.ArgumentParser(description="decode the captured 17 04 weapon table")
    ap.add_argument("--input", default=r"C:\Z1\Server\Data\friendWeaponDefinitions.bin")
    ap.add_argument("--out", default="out/weapons/friend-table.json")
    ap.add_argument("--source-root", default=".")
    ap.add_argument("--schema", default=_Z1_SCHEMA)
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    raw = Path(args.input).read_bytes()
    report = decode(raw, Path(args.source_root).resolve(), args.schema)
    report["input"] = str(Path(args.input))

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(report, indent=1), encoding="utf-8")

    header = report["header"]
    walk = report["walk"]
    if not args.quiet:
        print("input                 %s" % args.input)
        print("wire bytes            %d" % header["wire_bytes"])
        print("declared uncompressed %d" % header["uncompressed_length"])
        print("decompressed          %d   md5 %s" % (walk["body_bytes"], header["body_md5"]))
        print("FireModes body bytes  %d (record %d)"
              % (report["fire_mode_body_bytes"], report["fire_mode_record_bytes"]))
        print("")
        for entry in walk["lists"]:
            print("  list %-24s count %6d   start %7d   bytes %7d"
                  % (entry["list"], entry["count"], entry["start_offset"], entry["bytes"]))
        print("")
        print("1148 walk: consumed %d / %d   remaining %d"
              % (walk["consumed"], walk["body_bytes"], walk["remaining"]))
        print("WALK CLEAN - the 1087 body is layout-compatible with the 1148 reader"
              if walk["clean"] else "WALK DIVERGED - %s" % walk["error"])
        print("")
        w87 = report["walk1087"]
        print("1087 control walk (%s):" % w87["schema"])
        for entry in w87["lists"]:
            print("  %-34s count %6d" % (entry["list"], entry["count"]))
        print("  consumed %d / %d   %s"
              % (w87["consumed"], walk["body_bytes"],
                 "CLEAN" if w87["clean"] else "FAILED: %s" % w87["error"]))
        print("wrote %s" % out_path)

    return 0 if walk["clean"] else 1


if __name__ == "__main__":
    sys.exit(main())
