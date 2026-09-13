#!/usr/bin/env python3
"""Generate docs/10-self-record-layout.md from a checked schema.

The schema below is a transcription of the permitted Ghidra decompiles for
H1Z1.exe build 0.0.118.208059.  It deliberately models calls and loop bodies,
not merely the zero-count minimal path.  Running this file also assembles the
minimal blob and checks its distinguished offsets.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import struct


@dataclass
class Read:
    wire: str
    dest: int | str | None
    label: str = ""
    note: str = ""
    confidence: str = "PROVEN"
    default: int = 0


@dataclass
class Call:
    addr: str
    base: int | str = 0
    note: str = ""
    confidence: str = "PROVEN"
    exports: tuple[str, ...] = ()


@dataclass
class Loop:
    count: str
    body: list[object]
    note: str = ""


@dataclass
class Branch:
    control: str
    test: str
    body: list[object]
    active_for_default: bool = False
    note: str = ""


@dataclass
class Alternative:
    test: str
    body: list[object]
    active_for_default: bool = False
    note: str = ""


@dataclass
class Schema:
    addr: str
    name: str
    ops: list[object] = field(default_factory=list)


def R(wire, dest, label="", note="", confidence="PROVEN", default=0):
    return Read(wire, dest, label, note, confidence, default)


def C(addr, base=0, note="", confidence="PROVEN", exports=()):
    return Call(addr.lower(), base, note, confidence, tuple(exports))


def L(count, *body, note=""):
    return Loop(count, list(body), note)


def B(control, test, *body, active=False, note=""):
    return Branch(control, test, list(body), active, note)


def A(test, *body, active=False, note=""):
    return Alternative(test, list(body), active, note)


SCHEMAS: dict[str, Schema] = {}


def S(addr: str, name: str, *ops: object) -> None:
    addr = addr.lower()
    SCHEMAS[addr] = Schema(addr, name, list(ops))


# Primitive helpers.
S("140b78f60", "length-prefixed string", R("str (4+N)", 0, note="i32 N followed by N bytes; no terminator"))
S("140a190f0", "low-two-bit varint", R("varint (1-4)", 0, note="extra=(first&3); value=little-endian word >> 2"))


# Head and early collections.
S("140a40000", "identity/name block",
  R("u32 (4)", 0x108), R("u32 (4)", 0x10c), R("u32 (4)", 0x110),
  C("140b78f60", 0x00, "name"), C("140b78f60", 0x30),
  C("140b78f60", 0xa0), C("140b78f60", 0xd0), R("u64 (8)", 0x100))

def map_u32_u32(addr: str, name: str) -> None:
    S(addr, name,
      R("i32 list-count (4)", None, "n", "allocation/loop bound"),
      L("n", R("u32 (4)", "local element key"), R("u32 (4)", "local element value")))


map_u32_u32("140a4d190", "u32-to-u32 map")
S("140a4d8b0", "u32-to-pair map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"),
    R("u32 (4)", "local element value+0x00"), R("u32 (4)", "local element value+0x04")))
map_u32_u32("140a4d020", "u32-to-u32 map")
map_u32_u32("140a4d330", "u32-to-u32 map")
map_u32_u32("140a4ce90", "u32-to-u32 map")

S("140a4cbf0", "u32 vector",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element")))
S("140a4caf0", "u32 vector",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element")))
S("140a4c970", "u32 set",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element/hash key")))
S("140a5d190", "boolean vector",
  R("i32 list-count (4)", None, "n", "validated against remaining byte count"),
  L("n", R("u8 (1)", "local element", note="normalized to bool")))

S("140a32c70", "early list element",
  R("u32 (4)", 0x04), R("u32 (4)", 0x08), R("u32 (4)", 0x0c),
  R("u8 (1)", 0x10, note="normalized to bool"), R("u32 (4)", 0x18),
  R("u32 (4)", 0x24), R("u32 (4)", 0x1c), R("u32 (4)", 0x20),
  R("u8 (1)", 0x00, note="fixed loop iteration 1/2"),
  R("u8 (1)", 0x01, note="fixed loop iteration 2/2"),
  R("u32 (4)", 0x14), C("140a4d8b0", 0x38),
  R("u32 (4)", 0x28), R("u32 (4)", 0x2c), R("i8 (1)", 0x30),
  R("u32 (4)", 0x68), R("u32 (4)", 0x6c), R("u32 (4)", 0x70),
  R("u32 (4)", 0x74), R("u32 (4)", 0x78), R("u32 (4)", 0x7c),
  R("u32 (4)", 0x80), R("u32 (4)", 0x84), R("u32 (4)", 0x88),
  R("u32 (4)", 0x8c))
S("140a1fc10", "early-element allocation wrapper", C("140a32c70", 0))

S("140a30c50", "nested early-map value",
  R("u32 (4)", 0x00), R("u32 (4)", 0x20), R("u32 (4)", 0x1c),
  R("u32 (4)", 0x18), R("u32 (4)", 0x10), R("u32 (4)", 0x14),
  R("u32 (4)", 0x24), R("u8 (1)", 0x28, note="normalized to bool"))
S("140a59900", "nested early map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a30c50", "local element value")))


# Polymorphic modifier base and selector-specific tails.
S("1421dc9b0", "modifier base",
  R("u8 (1)", 0x18, note="normalized to bool"), R("u32 (4)", 0x28),
  R("u32 (4)", 0x2c), R("u32 (4)", 0x1c), R("u32 (4)", 0x30),
  R("u32 (4)", 0x34), R("u32 (4)", 0x38), C("140b78f60", 0x40),
  R("u32 (4)", 0x58), R("u8 (1)", 0x5c, note="normalized to bool"))

for addr, name in [
    ("1421e32c0", "modifier selector 22"), ("1421e33c0", "modifier selector 8"),
    ("1421e3520", "modifier selector 10"), ("1421e36a0", "modifier selector 24"),
    ("1421e37a0", "modifier selector 7"), ("1421e38a0", "modifier selector 6"),
    ("1421e39a0", "modifier selector 14"), ("1421e3aa0", "modifier selector 27"),
    ("1421e3be0", "modifier selector 25")]:
    S(addr, name, C("1421dc9b0", 0, confidence="INFERRED", note="decompiler omits base-loader arguments; RCX/RDX are preserved"), R("u32 (4)", 0x60))
S("1421dcd60", "modifier selector 11", C("1421dc9b0", 0, confidence="INFERRED", note="decompiler omits base-loader arguments; RCX/RDX are preserved"),
  R("u32 (4)", 0x60), R("u32 (4)", 0x64), R("u32 (4)", 0x68), R("u32 (4)", 0x6c))
S("1421dcf80", "modifier selector 13", C("1421dc9b0", 0, confidence="INFERRED", note="decompiler omits base-loader arguments; RCX/RDX are preserved"), R("u32 (4)", 0x60), R("u32 (4)", 0x64))
S("1421dce70", "modifier selector 1", C("1421dc9b0", 0, confidence="INFERRED", note="decompiler omits base-loader arguments; RCX/RDX are preserved"),
  B("outer_flag", "outer flag != 0", R("u64 (8)", "local"), R("u32 (4)", 0x68), R("u32 (4)", 0x6c),
    active=False, note="object+0x5d is assigned from FUN_140a45090's first flag"))
S("1421dd120", "modifier selector 15", C("1421dc9b0", 0, confidence="INFERRED", note="decompiler omits base-loader arguments; RCX/RDX are preserved"),
  C("140b78f60", 0x60, confidence="INFERRED", note="destination recovered from object layout; read call is direct"),
  R("u32 (4)", 0x78), R("u32 (4)", 0x7c))
S("1421dcbe0", "modifier selector 17", C("1421dc9b0", 0, confidence="INFERRED", note="decompiler omits base-loader arguments; RCX/RDX are preserved"),
  C("140b78f60", 0x60, confidence="INFERRED", note="destination recovered from object layout; read call is direct"),
  R("u32 (4)", 0xa0), R("u32 (4)", 0xa4), R("u8 (1)", 0xa8, note="normalized to bool"),
  R("u32 (4)", 0xac), R("u8 (1)", 0xb0, note="normalized to bool"))

MODIFIER_SELECTORS = {
    1:"1421dce70", 3:"1421dc9b0", 6:"1421e38a0", 7:"1421e37a0", 8:"1421e33c0",
    10:"1421e3520", 11:"1421dcd60", 12:"1421dc9b0", 13:"1421dcf80", 14:"1421e39a0",
    15:"1421dd120", 17:"1421dcbe0", 18:"1421dc9b0", 19:"1421dc9b0", 20:"1421dc9b0",
    22:"1421e32c0", 23:"1421dc9b0", 24:"1421e36a0", 25:"1421e3be0", 26:"1421dc9b0",
    27:"1421e3aa0", 28:"1421dc9b0",
}

modifier_alts = [B("selector", f"selector == {sel}",
                   C(addr, "local modifier object", confidence="INFERRED",
                     note="loader target recovered from factory/vtable mapping"),
                   note=f"FUN_1421e0600 vtable dispatch for selector {sel}")
                 for sel, addr in MODIFIER_SELECTORS.items()]
S("140a45090", "modifier-bearing block",
  R("u8 (1)", 0x170, "outer_flag", "normalized to bool"), C("140a4d190", 0x78),
  R("u32 (4)", 0x74), R("u32 (4)", 0x128), R("u32 (4)", 0x164),
  R("u32 (4)", 0x168), R("u32 (4)", 0x150), R("u32 (4)", 0x154),
  R("u64 (8)", "local"), R("u64 (8)", "local"), R("u32 (4)", 0x15c),
  R("u32 (4)", 0x160),
  R("i32 list-count (4)", None, "mods", "allocation/loop bound"),
  L("mods", R("u32 (4)", "local selector", "selector", "vtable/type selector"), *modifier_alts,
    note="invalid selector makes FUN_1421e0600 return null, then caller dereferences it"),
  C("140a4cbf0", 0x130))

S("140a30a70", "large early element",
  R("u32 (4)", 0x138), R("u32 (4)", 0x13c), R("u32 (4)", 0x140),
  R("u32 (4)", 0x144), R("u32 (4)", 0x150), R("u32 (4)", 0x154),
  R("u32 (4)", 0x158), C("140a45090", 0x160), C("140a59900", 0))


# Inventory, including the client-data-dependent generic/weapon branch.
S("140a3aa60", "inventory item common header",
  R("u32 (4)", 0x08, "item_key", "item-definition lookup key"), R("u32 (4)", 0x0c),
  R("u64 (8)", 0x10), R("u32 (4)", 0x18), R("u64 (8)", 0x20),
  R("u32 (4)", 0x28), R("u32 (4)", 0x2c), R("u32 (4)", 0x30),
  R("u32 (4)", 0x34), R("u32 (4)", 0x38),
  R("u8 (1)", 0x3c, note="normalized to bool"), R("u64 (8)", 0x40), R("u32 (4)", 0x48))
S("140bcdc70", "generic inventory item tail", R("u8 (1)", 0x59, note="normalized to bool"))

S("141484a20", "weapon u32 vector",
  R("u32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element")))
S("141483ec0", "weapon compact sub-element",
  R("u8 (1)", 0x10), R("u32 (4)", 0x14), R("i32 (4)", 0x18), R("i32 (4)", 0x18,
    note="second value overwrites destination when positive"))
S("141484300", "weapon byte-counted group",
  R("u32 (4)", "local group id"),
  R("i8 list-count (1)", None, "n", "signed allocation/loop bound"),
  L("n", C("141483ec0", "local group sub-element")))

S("140a12af0", "compact weapon map key",
  R("u16 (2)", "local compact header", "hdr",
    "low 13 bits=N; bit 14 adds u32; bit 15 selects inline numeric form"),
  B("hdr", "bit15 == 0", R("bytes (N+1)", "local interned string bytes",
      note="N=header&0x1fff; includes trailing byte skipped by cursor (normally NUL)"),
    note="FUN_140a12af0 advances cursor by N+1 after the u16"),
  B("hdr", "bit14 != 0", R("u32 (4)", "local key part 2")))
S("141484b70", "weapon compact-key map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a12af0", "local element key"), C("140a30280", "local element value")))
S("1414848b0", "weapon nested compact map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("141484b70", "local element value")))
S("141484740", "weapon outer nested map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("1414848b0", "local element value")))
S("141483fa0", "weapon primary tail",
  C("141484a20", 0x10),
  R("i8 list-count (1)", None, "groups", "signed allocation/loop bound"),
  L("groups", C("141484300", "local group")),
  R("i8 (1)", 0x40), R("u8 (1)", 0x44), R("u32 (4)", 0x48),
  R("i8 (1)", 0x64), R("i8 (1)", 0x6c), R("i8 (1)", 0x70),
  R("u32 (4)", 0x74), R("u8 (1)", 0xa8), R("u32 (4)", 0xac),
  R("i8 (1)", 0x7c, note="converted to f32 after read"), R("u32 (4)", 0xe8))
S("14148c3c0", "weapon inventory item tail",
  C("140bcdc70", 0), C("141483fa0", 0xa8), C("141484b70", 0x198), C("141484740", 0x1c8))
S("140a331a0", "inventory container",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a3aa60", "local item", exports=("item_key",)),
    B("item_key", "client item-definition type != 0x14",
      C("140bcdc70", "local item", confidence="INFERRED", note="loader target recovered from factory/vtable mapping"),
      note="generic vtable 0x14324dbd8"),
    B("item_key", "client item-definition type == 0x14",
      C("14148c3c0", "local item", confidence="INFERRED", note="loader target recovered from factory/vtable mapping"),
      note="weapon vtable 0x143254c98"),
    note="branch uses client item-definition lookup keyed by header field at +0x08"),
  R("u32 (4)", 0x58))


# Loadouts and adjacent records.
S("140a3d430", "four-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08), R("u32 (4)", 0x0c))
S("140a3d510", "nested loadout value",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08),
  R("u8 (1)", 0x10, note="normalized to bool"),
  C("140a45090", 0x20, confidence="INFERRED",
    note="decompiler omits RDX argument; stream remains live across thunk"),
  R("u32 (4)", 0x280), R("u32 (4)", 0x284), R("u32 (4)", 0x288),
  R("u32 (4)", 0x28c), R("u8 (1)", 0x290, note="normalized to bool"),
  R("u32 (4)", 0x0c), R("u32 (4)", 0x2a4), C("140a3d430", 0x294))
S("140a4d700", "nested loadout map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3d510", "local element value")))
S("140a43b90", "loadout element",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08), R("u32 (4)", 0x0c),
  R("u8 (1)", 0x10, note="normalized to bool"), R("u64 (8)", 0x18), R("u32 (4)", 0x20),
  R("u8 (1)", 0x24, note="normalized to bool"), R("u32 (4)", 0x28),
  C("140a45090", 0x30, confidence="INFERRED",
    note="decompiler omits RDX argument; stream remains live across thunk"),
  C("140a4d700", 0x290), R("u32 (4)", 0x370),
  R("u8 (1)", 0x374, note="normalized to bool"),
  R("u8 (1)", 0x11, note="normalized to bool; unusual adjacent destination"))
S("140a33b50", "loadout container",
  R("u32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a43b90", "local element")),
  R("u32 (4)", 0x2840), R("u32 (4)", 0x2838),
  R("u8 (1)", 0x283c, note="normalized to bool"), R("u32 (4)", 0x2844), R("u32 (4)", 0x2848))
S("140a2bb60", "secondary loadout element",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08),
  R("u8 (1)", 0x0c, note="normalized to bool"), R("u64 (8)", 0x10), R("u64 (8)", 0x18),
  R("u32 (4)", 0x20), C("140a4d700", 0x290),
  R("u32 (4)", 0x378), R("u32 (4)", 0x37c), R("u32 (4)", 0x380),
  R("u32 (4)", 0x384), R("u32 (4)", 0x388),
  R("u8 (1)", 0x390, note="normalized to bool"), R("u32 (4)", 0x38c),
  R("u32 (4)", 0x394), R("u32 (4)", 0x398),
  R("u8 (1)", 0x39c, note="normalized to bool"), R("u8 (1)", 0x39d, note="normalized to bool"),
  R("u32 (4)", 0x3a0))
S("140a30790", "secondary loadout vector",
  R("u32 list-count (4)", None, "n", "allocation/loop bound"), L("n", C("140a2bb60", "local element")))

S("140a44620", "local notification element",
  R("u64 (8)", 0x00), C("140b78f60", 0x08), R("u32 (4)", 0x20),
  R("u64 (8)", 0x28), R("u8 (1)", 0x30, note="normalized to bool"))
S("140a441a0", "local state-map value",
  R("u32 (4)", 0x00), R("u32 (4)", 0x10), R("u32 (4)", 0x14),
  R("u32 (4)", 0x18), R("u32 (4)", 0x1c), R("u32 (4)", 0x20),
  R("u64 (8)", 0x28), R("u32 (4)", 0x30), R("u32 (4)", 0x04))
S("140a4da70", "local state map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a441a0", "local element value")))
S("140a44390", "local state element",
  R("u32 (4)", 0x00), R("u32 (4)", 0x08), R("u32 (4)", 0x18),
  R("u32 (4)", 0x1c), R("u32 (4)", 0x20), R("u32 (4)", 0x24),
  R("u32 (4)", 0x28), R("u32 (4)", 0x2c), R("u8 (1)", 0x30, note="normalized to bool"),
  R("u32 (4)", 0x34), C("140a4da70", 0x50), R("u32 (4)", 0x04))
S("140a54bc0", "local state vector",
  R("i32 list-count (4)", None, "n", "validated against remaining byte count; allocation/loop bound"),
  L("n", C("140a44390", "local element")))
S("140a3e6d0", "small string element",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08),
  R("u64 (8)", 0x28), R("u8 (1)", 0x30, note="normalized to bool"),
  R("u32 (4)", 0x0c), C("140b78f60", 0x10))

S("140da94b0", "key-selected root payload",
  R("u32 (4)", 0xd0), R("u8 (1)", 0xd4, note="normalized to bool"),
  R("u32 (4)", 0xd8), R("u32 (4)", 0xdc), C("140a4cbf0", 0xe0))
S("140db5280", "key-selected root payload lookup wrapper", C("140da94b0", 0))
S("140a30280", "selector-dependent pair",
  R("u32 (4)", 0x00), R("u8 (1)", 0x04, "kind", "branch/type selector"),
  B("kind", "kind is 0 or 1", R("u32 (4)", 0x08), R("u32 (4)", 0x0c), active=True,
    note="all other byte values consume no tail"))
S("140a33390", "triple map plus scalar",
  R("u32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element+0x00"), R("u32 (4)", "local element+0x04"),
    R("u32 (4)", "local element+0x08")), R("u32 (4)", 0x128))


# Mid/tail aggregate loaders.
S("140a4e3b0", "u32-to-pair map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"),
    R("u32 (4)", "local element value+0x00"), R("u32 (4)", "local element value+0x04")))
S("140a2c090", "aggregate value",
  R("u32 (4)", 0x00), C("140a4e3b0", 0x08), R("u32 (4)", 0x70),
  R("u32 (4)", 0x74), R("u8 (1)", 0x78, note="normalized to bool"))
S("140a55420", "aggregate map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a2c090", "local element value")))
S("140a2b590", "three-map aggregate",
  C("140a55420", 0x00), C("140a55420", 0xa8), C("140a4ce90", 0x150))

S("140a2b780", "three-u32 value", R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08))
S("140a552e0", "three-u32-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a2b780", "local element value")))
S("140a3bc90", "3xu32+2xu64 value",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08),
  R("u64 (8)", 0x10), R("u64 (8)", 0x18))
S("140a56110", "3xu32+2xu64 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3bc90", "local element value")))
S("140a3a2e0", "3xu32+u64 value",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08), R("u64 (8)", 0x10))
S("140a55c80", "3xu32+u64 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3a2e0", "local element value")))
S("140a2b650", "eight-map aggregate",
  C("140a552e0", 0x00), C("140a552e0", 0x30), C("140a552e0", 0x60), C("140a552e0", 0x90),
  R("u32 (4)", 0xc0), R("u32 (4)", 0xc4), C("140a56110", 0x168), C("140a55c80", 0xc8),
  C("140a4d020", 0xf8), C("140a4d020", 0x128))

S("140a3fb00", "two-u32-and-u64", R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u64 (8)", 0x08))
S("140a56530", "fixed-value map with trailing u32",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3fb00", "local element value"),
    R("u32 (4)", "local element tail")))
S("140a46c50", "base record plus u32/i8 value",
  C("140a3fb00", 0, confidence="INFERRED", note="decompiler omits arguments; entry RCX/RDX remain destination/stream"),
  R("u32 (4)", 0x10), R("i8 (1)", 0x14))
S("140a5cac0", "i32/i8 keyed map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("i32 (4)", "local element key part 1"), R("i8 (1)", "local element key part 2"),
    C("140a46c50", "local element value")))
S("140a2c000", "two-map aggregate plus byte",
  C("140a56530", 0x00), C("140a5cac0", 0xa8), R("u8 (1)", 0x158))

S("140a2ec10", "three-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08))
S("140a2ecc0", "three-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08))
S("140a2eb50", "eight-u32 fixed record",
  R("u32 (4)", 0x00),
  C("140a2ec10", 0x04, confidence="INFERRED", note="RDX stream omitted in decompile; preserved across thunk"),
  C("140a2ecc0", 0x10), R("u32 (4)", 0x1c))

S("140a30490", "four-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08), R("u32 (4)", 0x0c))
S("140a30370", "timestamp/string subrecord",
  R("u64 (8)", 0x10), R("u32 (4)", 0x18), R("u32 (4)", 0x1c), R("u32 (4)", 0x20),
  C("140a30490", 0x24), C("140b78f60", 0x38))
S("140a30570", "timestamp wrapper",
  R("u64 (8)", 0x00),
  C("140a30370", 0x08, confidence="INFERRED", note="RDX stream omitted in decompile; preserved across thunk"),
  R("u8 (1)", 0x60))

S("140a37720", "three-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08))
S("140a37670", "five-u32 fixed record",
  R("u32 (4)", 0x00),
  C("140a37720", 0x04, confidence="INFERRED", note="RDX stream omitted in decompile; preserved across thunk"),
  R("u32 (4)", 0x10))
S("140a55530", "u32-to-pair map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"),
    R("u32 (4)", "local element value+0x00"), R("u32 (4)", "local element value+0x04")))
S("140a5a510", "compound-key map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local key part 1"), R("u64 (8)", "local key part 2"),
    R("u32 (4)", "local value+0x00"), R("u32 (4)", "local value+0x04")))
S("140a5a320", "compound-key nested map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local key part 1"), R("u64 (8)", "local key part 2"),
    C("140a4d020", "local value")))

S("140a37f10", "three-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08))
S("140a38310", "u64/u32/u64 fixed record",
  R("u64 (8)", 0x00), R("u32 (4)", 0x08), R("u64 (8)", 0x10))
S("140a38c60", "2xu64+4xu32 fixed record",
  R("u64 (8)", 0x00), R("u64 (8)", 0x08), R("u32 (4)", 0x10),
  R("u32 (4)", 0x14), R("u32 (4)", 0x18), R("u32 (4)", 0x1c))
S("140a37e60", "three-u32 fixed record",
  R("u32 (4)", 0x00), R("u32 (4)", 0x04), R("u32 (4)", 0x08))
S("140a38b00", "large u64-keyed value",
  R("u64 (8)", 0x00), R("u32 (4)", 0x08), C("140a37f10", 0x18),
  C("140a38310", 0x28), C("140a38c60", 0x40), C("140a37e60", 0x60),
  R("u32 (4)", 0x6c), R("u8 (1)", 0x70, note="normalized to bool"))
S("140a57220", "u64-keyed large-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u64 (8)", "local element key/hash key"), C("140a38b00", "local element value")))
S("140a38070", "u64-keyed six-field value",
  R("u64 (8)", 0x00), R("u32 (4)", 0x08), R("u32 (4)", 0x0c),
  R("u32 (4)", 0x10), R("u64 (8)", 0x18), R("u32 (4)", 0x20))
S("140a57090", "u64-keyed six-field map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u64 (8)", "local element key/hash key"), C("140a38070", "local element value")))

S("140a39250", "string-rich nested value",
  R("u32 (4)", 0x20), R("u64 (8)", 0x28), C("140b78f60", 0x30), C("140b78f60", 0x70))
S("140a558a0", "string-rich nested map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a39250", "local element value")))
S("140a2ef60", "two-string value",
  R("u32 (4)", 0x00), C("140b78f60", 0x08), C("140b78f60", 0x48), C("140a558a0", 0x88))
S("140a563e0", "two-string-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a2ef60", "local element value")))
S("140a39740", "seven-u32 value",
  R("u32 (4)", 0), R("u32 (4)", 4), R("u32 (4)", 8), R("u32 (4)", 0x0c),
  R("u32 (4)", 0x10), R("u32 (4)", 0x18), R("u32 (4)", 0x14))
S("140a4edb0", "seven-u32-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a39740", "local element value")))
S("140a39690", "three-u32 fixed record", R("u32 (4)", 0), R("u32 (4)", 4), R("u32 (4)", 8))
S("140a395e0", "five-u32 fixed record",
  R("u32 (4)", 0), C("140a39690", 4, confidence="INFERRED", note="RDX stream omitted; preserved across thunk"),
  R("u32 (4)", 0x10))
S("140a39f30", "three-u32 fixed record", R("u32 (4)", 0), R("u32 (4)", 4), R("u32 (4)", 8))
S("140a3a210", "three-u32 value", R("u32 (4)", 0x10), R("u32 (4)", 0x14), R("u32 (4)", 0x18))
S("140a55ae0", "u32-plus-three-u32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), R("u32 (4)", "local element prefix"),
    C("140a3a210", "local element value")))

S("140a2b830", "u64+3xu32 value",
  R("u64 (8)", 0x40), R("u32 (4)", 0x48), R("u32 (4)", 0x4c), R("u32 (4)", 0x50))
S("140a57340", "u64-keyed u64+3xu32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u64 (8)", "local element key/hash key"), C("140a2b830", "local element value")))
S("140a57530", "u64-keyed two-u32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u64 (8)", "local element key/hash key"), R("u32 (4)", "local element value"),
    R("u32 (4)", "local element value", note="second read overwrites same destination")))
S("140a57910", "u64-keyed u64/u32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u64 (8)", "local element key/hash key"), R("u64 (8)", "local element value+0x00"),
    R("u32 (4)", "local element value+0x08")))
S("140a2b910", "three u64-keyed maps", C("140a57340", 0x08), C("140a57530", 0x2a0), C("140a57910", 0x3c8))
S("140a39300", "extended u64/u32 value",
  C("140a2b830", 0, confidence="INFERRED", note="decompiler omits arguments; entry RCX/RDX remain destination/stream"),
  R("u64 (8)", 0x78), R("u32 (4)", 0x80))
S("140a57760", "u64-keyed u64/u32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u64 (8)", "local element key/hash key"), C("140a39300", "local element value")))
S("140a393b0", "single-map aggregate",
  C("140a57760", 0x00))

S("140a3fa20", "2xu32+2xu64 value",
  R("u32 (4)", 0), R("u32 (4)", 4), R("u64 (8)", 8), R("u64 (8)", 0x10))
S("140a4f6f0", "complex map with trailing u32",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3fa20", "local element value"),
    R("u32 (4)", "local element tail")))
S("140a3b010", "base record plus two-u32/bool value",
  C("140a3fb00", 0, confidence="INFERRED", note="decompiler omits arguments; entry RCX/RDX remain destination/stream"),
  R("u32 (4)", 0x10), R("u32 (4)", 0x14), R("u8 (1)", 0x18, note="normalized to bool"))
S("140a4f8c0", "two-u32/bool-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3b010", "local element value")))
S("140a3ae80", "base record plus two-u32/bool value",
  C("140a3fb00", 0, confidence="INFERRED", note="decompiler omits arguments; entry RCX/RDX remain destination/stream"),
  R("u32 (4)", 0x10), R("u32 (4)", 0x14), R("u8 (1)", 0x18, note="normalized to bool"))
S("140a4f560", "two-u32/bool-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3ae80", "local element value")))
S("140a3af50", "four-part aggregate",
  C("140a3fa20", 0x00, confidence="INFERRED",
    note="decompiler omits both arguments; entry RCX/RDX remain destination/stream"),
  C("140a4f6f0", 0x18), C("140a3fb00", 0x80), C("140a4f8c0", 0x90),
  C("140a4f560", 0xf8), R("u8 (1)", 0x168))

S("140a460d0", "u32/u64/u32/bool value",
  R("u32 (4)", 0), R("u64 (8)", 8), R("u32 (4)", 0x10), R("u8 (1)", 0x14, note="normalized to bool"))
S("140a507a0", "u32/u64/u32/bool-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a460d0", "local element value")))
S("140a38d30", "u32/u64/u32 value", R("u32 (4)", 0), R("u64 (8)", 8), R("u32 (4)", 0x10))
S("140a55700", "u32/u64/u32-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a38d30", "local element value")))
S("140a56b50", "nested string/map record map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), R("u32 (4)", "local element value+0x00"),
    C("140b78f60", "local element value+0x08"), C("140a507a0", "local element value+0x48"),
    C("140a55700", "local element value+0x78")))
S("140a461b0", "three-map string aggregate",
  R("u32 (4)", 0), R("u32 (4)", 8), C("140b78f60", 0x10),
  C("140a507a0", 0x28), C("140a55700", 0xa8), C("140a56b50", 0x120))

S("140a3bbb0", "three-u32 value", R("u32 (4)", 0), R("u32 (4)", 4), R("u32 (4)", 0x20))
S("140a55fa0", "three-u32-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3bbb0", "local element value")))
S("140a3bb00", "map bracketed by scalars", R("u32 (4)", 0), C("140a55fa0", 8), R("u32 (4)", 0x38))

S("140a56280", "two-u32 element hash container",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), R("u32 (4)", "local element value")))
S("140a3c8d0", "u32/u64/i8 value", R("u32 (4)", 0), R("u64 (8)", 8), R("i8 (1)", 0x10))
S("140a3c820", "three-u32 fixed record", R("u32 (4)", 0), R("u32 (4)", 4), R("u32 (4)", 8))
S("140a3c980", "six-u32 fixed record",
  R("u32 (4)", 0), R("u32 (4)", 4), R("u32 (4)", 0x10), R("u32 (4)", 0x14),
  R("u32 (4)", 0x18), R("u32 (4)", 0x1c))
S("140a5bf00", "compound element",
  C("140a3c8d0", 0x30), C("140a3c820", 0x48), R("u32 (4)", 0x54), C("140a3c980", 0x60))
S("140a5b8e0", "value list",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a3c8d0", "local element header"), R("u32 (4)", "local element field A"),
    R("u32 (4)", "local element field B"), R("u32 (4)", "local element field C")))
S("140a5baa0", "value list",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a3c8d0", "local element header"), R("u32 (4)", "local element field A"),
    R("u32 (4)", "local element field B"), R("u32 (4)", "local element field C")))
S("140a5bc60", "value list",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a3c8d0", "local element header"), R("u32 (4)", "local element field A"),
    R("u32 (4)", "local element field B"), R("u64 (8)", "local element field C")))
S("140a5bfd0", "compound-element list",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", C("140a3c8d0", "local element header"), C("140a5bf00", "local element body")))
S("140a495b0", "five-list aggregate",
  C("140a5b8e0", 0x08), C("140a5baa0", 0x70), C("140a5bc60", 0xd8),
  C("140a5bfd0", 0x140), C("140a5bfd0", 0x170))

S("140a2fec0", "five-u64/two-byte value",
  R("u64 (8)", 0xd8), R("u64 (8)", 0xe0), R("u64 (8)", 0xe8), R("u64 (8)", 0xf0),
  R("u64 (8)", 0xf8), R("u8 (1)", 0x100, note="fixed loop iteration 1/2"),
  R("u8 (1)", 0x101, note="fixed loop iteration 2/2"))
S("140a56700", "five-u64/two-byte-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a2fec0", "local element value")))
S("140a45f80", "five-u64/u32 fixed record",
  R("u64 (8)", 0), R("u64 (8)", 8), R("u64 (8)", 0x10), R("u64 (8)", 0x18),
  R("u64 (8)", 0x20), R("u32 (4)", 0x28))
S("140a569c0", "two-u32 element hash container",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), R("u32 (4)", "local element value")))

S("140a4f410", "inventory-header map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a3aa60", "local element value")))
S("140a37290", "late nested value",
  R("u64 (8)", 0), R("u32 (4)", 8), R("u64 (8)", 0x10), R("u32 (4)", 0x2c),
  C("140a4f410", 0x48), R("u8 (1)", 0x29, note="normalized to bool"),
  R("u32 (4)", 0x30), R("u32 (4)", 0x34), R("u32 (4)", 0x38),
  R("u8 (1)", 0x3c, note="normalized to bool"))
S("140a55df0", "late nested-value map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a37290", "local element value")))
S("140a4eb30", "map with nested u32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), R("u32 (4)", "local element+0x00"),
    R("u32 (4)", "local element+0x04"), C("140a4d190", "local element+0x08")))
S("140a56ee0", "inner u32 map",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), R("u32 (4)", "local element value")))
S("140a56cd0", "map of inner maps",
  R("i32 list-count (4)", None, "n", "allocation/loop bound"),
  L("n", R("u32 (4)", "local element key/hash key"), C("140a56ee0", "local element value")))


# Root record, in exact call/read order from FUN_140a31140.
S("140a31140", "SendSelfToClient player record root",
  R("u64 (8)", 0x0d0), R("u64 (8)", 0x0d8, "guid", "required test GUID", default=0x1001),
  C("140a190f0", 0x0e0), R("u64 (8)", "local server time"), R("u32 (4)", 0xb9dc),
  C("140b78f60", 0xba00), C("140b78f60", 0xba18),
  R("u32 (4)", 0xb9e0), R("u32 (4)", 0xb9e4),
  C("140b78f60", 0xba30), C("140b78f60", 0xba48), C("140b78f60", 0xba60),
  R("u32 (4)", 0xb9e8), R("u32 (4)", 0xb9ec), R("u32 (4)", 0xb9f0),
  R("u32 (4)", 0xb9f4), R("u32 (4)", 0xb9f8),
  R("f32 (4)", 0x3b0, note="position[0]"), R("f32 (4)", 0x3b4, note="position[1]"),
  R("f32 (4)", 0x3b8, note="position[2]"), R("f32 (4)", 0x3bc, note="position[3]"),
  R("f32 (4)", 0x3c0, note="orientation[0]"), R("f32 (4)", 0x3c4, note="orientation[1]"),
  R("f32 (4)", 0x3c8, note="orientation[2]"), R("f32 (4)", 0x3cc, note="orientation[3]"),
  C("140a40000", 0x0e8), R("u32 (4)", 0xa638), C("140a4d190", 0xa640),
  R("u64 (8)", 0xa6f8), R("u32 (4)", 0xa700), R("u32 (4)", 0xa704),
  R("u8 (1)", 0x23c, note="normalized to bool"), R("u8 (1)", 0x23d, note="normalized to bool"),
  R("u32 (4)", 0x240), R("u32 (4)", 0x244),
  R("u8 (1)", 0x24c, note="normalized to bool"), R("u32 (4)", 0x254), R("u32 (4)", 0x250),
  R("u32 (4)", 0xa734), R("u32 (4)", 0xa738), R("u32 (4)", 0x238),
  R("u64 (8)", 0x3e8, note="server-time delta applied unless sentinel"),
  R("u64 (8)", 0x3f0, note="server-time delta applied unless sentinel"),
  R("u32 (4)", 0x3d0), R("u8 (1)", 0x3d4, note="normalized to bool"), R("u32 (4)", 0x3d8),

  R("u32 list-count (4)", None, "early", "loop bound"), L("early", C("140a1fc10", "local early element")),
  R("u32 list-count (4)", "local capacity for player+0x2b8", "cap",
    "allocation size only; there are no element bytes for this value"),
  R("i32 list-count (4)", None, "pairs", "loop bound"),
  L("pairs", R("u32 (4)", "local pair key"), R("u32 (4)", "local pair value")),
  R("u32 list-count (4)", None, "large", "loop bound"), L("large", C("140a30a70", "local large element")),
  C("140a331a0", 0xbdb8), R("u32 (4)", 0x200),
  C("140a33b50", 0x3480), C("140a30790", 0x5cf8),
  R("u32 list-count (4)", None, "notes", "loop bound/allocation size"),
  L("notes", C("140a44620", "local notification element")),
  C("140a54bc0", "local temporary state vector"),
  R("u32 list-count (4)", None, "smallstr", "loop bound"),
  L("smallstr", C("140a3e6d0", "local small-string element")),
  R("u8 (1)", 0xad80, note="normalized to bool"), C("140a4c970", 0xad88),
  R("u32 list-count (4)", None, "skip32", "loop bound"),
  L("skip32", R("u32 (4)", "local discarded value")),
  R("u32 list-count (4)", None, "keypayload", "loop bound"),
  L("keypayload", R("u32 (4)", "local key/hash key"), C("140db5280", "player-selected payload")),
  R("u32 list-count (4)", None, "selmap", "loop bound"),
  L("selmap", R("u32 (4)", "local key/hash key"), C("140a30280", "local selector value")),
  C("140a33390", 0xa428), C("140a4d020", 0x0e60),
  R("u32 list-count (4)", None, "c788", "loop bound"),
  L("c788", R("u32 (4)", "local key inserted into player+0xc788")),
  R("u32 (4)", 0x258),
  C("140a4caf0", 0x260, confidence="INFERRED", note="hidden RDX destination; size field is player+0x270"),
  C("140a4caf0", 0x278, confidence="INFERRED", note="hidden RDX destination; size field is player+0x288"),
  C("140a5d190", 0x290, confidence="INFERRED", note="hidden RDX destination; size field is player+0x2a0"),
  R("u32 (4)", 0x2a8), R("u32 (4)", 0x2ac),
  C("140a2b590", 0xc8c0), C("140a2b650", 0xcff8), C("140a2c000", 0xd1a0),
  C("140a2eb50", 0xd310), R("u32 (4)", 0xd338), C("140a30570", 0xd340),
  C("140a4d330", 0xd3c0), C("140a37670", 0xd428), C("140a55530", 0xd440),
  C("140a5a510", 0xd4a8), C("140a5a320", 0xd4d8), C("140a57220", 0xd888),
  C("140a57090", 0xd9b8), C("140a563e0", 0xda98), C("140a4edb0", 0xdd20),
  C("140a395e0", 0xddc8), C("140a39f30", 0xe738), C("140a55ae0", 0xe7f8),
  C("140a2b910", 0xe890), C("140a393b0", 0xed28), C("140a3af50", 0xf140),
  C("140a461b0", 0xf2f8), C("140a3bb00", 0xf548), C("140a56280", 0xf680),
  C("140a495b0", 0xfc20), C("140a56700", 0xfde0), C("140a45f80", 0x10008),
  C("140a569c0", 0x10040), C("140a55df0", 0x10320), C("140a4eb30", 0x103e8),
  C("140a56cd0", 0x105e0),
  R("u8 (1)", 0x106b8, note="normalized to bool"),
  R("u64 (8)", "local first relation id"), R("u32 (4)", "local first relation type"),
  R("u64 (8)", "local second relation id A"), R("u64 (8)", "local second relation id B"),
  R("u32 (4)", "local second relation type A"), R("u32 (4)", "local second relation type B"),
  R("u8 (1)", 0x106b9, note="normalized to bool"),
  R("u8 (1)", 0x106ba, note="normalized to bool"), R("u8 (1)", 0x106bc),
  R("u8 (1)", 0x106bb, note="normalized to bool"), R("u32 (4)", 0x106c0), R("u32 (4)", 0x106c4))


FIXED_WIDTHS = {
    "u8": 1, "i8": 1, "u16": 2, "u32": 4, "i32": 4, "f32": 4, "u64": 8,
}


def width_of(wire: str) -> int | None:
    if wire.startswith("str"):
        return 4
    if wire.startswith("varint"):
        return 1
    if wire.startswith("bytes"):
        return None
    for prefix, width in FIXED_WIDTHS.items():
        if wire.startswith(prefix + " ") or wire == prefix:
            return width
    raise ValueError(f"unknown wire width: {wire}")


def add_base(base: str, delta: int) -> str:
    if delta == 0:
        return base
    if base == "player":
        return f"player+0x{delta:x}"
    if base.startswith("player+0x"):
        try:
            return f"player+0x{int(base[9:], 16) + delta:x}"
        except ValueError:
            pass
    return f"{base}+0x{delta:x}"


def resolve_dest(base: str, dest: int | str | None) -> str:
    if dest is None:
        return "local"
    if isinstance(dest, int):
        return add_base(base, dest)
    return dest.replace("{base}", base)


def resolve_call_base(base: str, spec: int | str) -> str:
    return add_base(base, spec) if isinstance(spec, int) else spec.replace("{base}", base)


def join_condition(parts: list[str]) -> str:
    return "; ".join(p for p in parts if p) or "always"


@dataclass
class FlatRow:
    number: int
    offset: int | None
    wire: str
    dest: str
    func: str
    condition: str
    confidence: str
    note: str


def flatten_root() -> tuple[list[FlatRow], bytes]:
    rows: list[FlatRow] = []
    blob = bytearray()
    invocation = 0

    def emit_read(op: Read, base: str, func: str, conditions: list[str], active: bool,
                  labels: dict[str, int], defaults: dict[str, int], inherited_conf: str,
                  inherited_note: str) -> None:
        width = width_of(op.wire)
        offset = len(blob) if active else None
        confidence = "INFERRED" if inherited_conf == "INFERRED" or op.confidence == "INFERRED" else "PROVEN"
        note = "; ".join(x for x in [inherited_note, op.note] if x)
        row = FlatRow(len(rows) + 1, offset, op.wire, resolve_dest(base, op.dest),
                      f"FUN_{func}", join_condition(conditions), confidence, note)
        rows.append(row)
        if op.label:
            labels[op.label] = row.number
            defaults[op.label] = op.default
        if active:
            if width is None:
                raise AssertionError(f"active variable bytes row {row.number} lacks a concrete default width")
            if op.wire.startswith("varint"):
                if op.default != 0:
                    raise AssertionError("minimal varint encoder currently expects zero")
                blob.append(0)
            elif op.wire.startswith("str"):
                blob.extend(struct.pack("<i", 0))
            elif width == 1:
                blob.extend(struct.pack("<B", op.default & 0xff))
            elif width == 2:
                blob.extend(struct.pack("<H", op.default & 0xffff))
            elif width == 4:
                blob.extend(struct.pack("<I", op.default & 0xffffffff))
            elif width == 8:
                blob.extend(struct.pack("<Q", op.default & 0xffffffffffffffff))

    def walk_ops(ops: list[object], base: str, func: str, conditions: list[str], active: bool,
                 labels: dict[str, int], defaults: dict[str, int], inherited_conf="PROVEN",
                 inherited_note="") -> None:
        nonlocal invocation
        for op in ops:
            if isinstance(op, Read):
                emit_read(op, base, func, conditions, active, labels, defaults, inherited_conf, inherited_note)
            elif isinstance(op, Call):
                if op.addr not in SCHEMAS:
                    raise KeyError(f"missing schema for FUN_{op.addr}")
                invocation += 1
                child_labels = dict(labels)
                child_defaults = dict(defaults)
                child_conf = "INFERRED" if inherited_conf == "INFERRED" or op.confidence == "INFERRED" else "PROVEN"
                child_note = "; ".join(x for x in [inherited_note, op.note] if x)
                walk_ops(SCHEMAS[op.addr].ops, resolve_call_base(base, op.base), op.addr,
                         conditions, active, child_labels, child_defaults, child_conf, child_note)
                for exported in op.exports:
                    if exported not in child_labels:
                        raise KeyError(f"FUN_{op.addr} did not define exported label {exported}")
                    labels[exported] = child_labels[exported]
                    defaults[exported] = child_defaults[exported]
            elif isinstance(op, Loop):
                if op.count not in labels:
                    raise KeyError(f"FUN_{func}: unknown count label {op.count}")
                count_row = labels[op.count]
                cond = f"for each element of row {count_row}"
                if op.note:
                    cond += f" ({op.note})"
                walk_ops(op.body, base, func, conditions + [cond],
                         active and defaults.get(op.count, 0) > 0, dict(labels), dict(defaults),
                         inherited_conf, inherited_note)
            elif isinstance(op, Branch):
                row_ref = labels.get(op.control)
                control = f"row {row_ref}" if row_ref else f"external {op.control}"
                cond = f"if {control}: {op.test}"
                if op.note:
                    cond += f" ({op.note})"
                walk_ops(op.body, base, func, conditions + [cond], active and op.active_for_default,
                         dict(labels), dict(defaults), inherited_conf, inherited_note)
            elif isinstance(op, Alternative):
                cond = f"if {op.test}"
                if op.note:
                    cond += f" ({op.note})"
                walk_ops(op.body, base, func, conditions + [cond], active and op.active_for_default,
                         dict(labels), dict(defaults), inherited_conf, inherited_note)
            else:
                raise TypeError(op)

    walk_ops(SCHEMAS["140a31140"].ops, "player", "140a31140", [], True, {}, {})
    return rows, bytes(blob)


def md_escape(value: object) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ")


def appendix_ops(schema: Schema) -> list[tuple[str, str, str, str, str]]:
    out: list[tuple[str, str, str, str, str]] = []

    def walk(ops: list[object], condition="always", depth=0) -> None:
        for op in ops:
            prefix = "↳ " * depth
            if isinstance(op, Read):
                out.append((op.wire, resolve_dest("base", op.dest), condition,
                            op.confidence, prefix + (op.note or "direct read")))
            elif isinstance(op, Call):
                conf = op.confidence
                dest = resolve_call_base("base", op.base)
                out.append((f"call FUN_{op.addr}", dest, condition, conf,
                            prefix + (op.note or "callee consumes the next bytes")))
            elif isinstance(op, Loop):
                walk(op.body, f"for each element of local count `{op.count}`" + (f"; {op.note}" if op.note else ""), depth + 1)
            elif isinstance(op, Branch):
                walk(op.body, f"if `{op.control}`: {op.test}" + (f"; {op.note}" if op.note else ""), depth + 1)
            elif isinstance(op, Alternative):
                walk(op.body, f"if {op.test}" + (f"; {op.note}" if op.note else ""), depth + 1)
    walk(schema.ops)
    return out


def root_call_audit() -> list[str]:
    calls: list[str] = []

    def walk(ops: list[object], context="always") -> None:
        for op in ops:
            if isinstance(op, Call):
                calls.append(f"FUN_{op.addr} ({context})")
            elif isinstance(op, Loop):
                walk(op.body, f"loop `{op.count}`")
            elif isinstance(op, (Branch, Alternative)):
                walk(op.body, "conditional")
    walk(SCHEMAS["140a31140"].ops)
    return calls


def hex_dump(blob: bytes) -> str:
    lines = []
    for offset in range(0, len(blob), 16):
        lines.append(f"{offset:04x}: " + " ".join(f"{b:02x}" for b in blob[offset:offset+16]))
    return "\n".join(lines)


def find_min_offset(rows: list[FlatRow], dest: str) -> int:
    matches = [r.offset for r in rows if r.dest == dest and r.offset is not None]
    if not matches:
        raise AssertionError(f"no active row for {dest}")
    return matches[0]


def render_document(rows: list[FlatRow], blob: bytes) -> str:
    guid_off = find_min_offset(rows, "player+0xd8")
    pos_off = find_min_offset(rows, "player+0x3b0")
    orient_off = find_min_offset(rows, "player+0x3c0")
    name_off = find_min_offset(rows, "player+0xe8")
    assert guid_off == 0x08
    assert pos_off == 0x4d
    assert orient_off == 0x5d
    assert name_off == 0x79
    assert blob[guid_off:guid_off + 8] == struct.pack("<Q", 0x1001)
    assert all(b == 0 for i, b in enumerate(blob) if not (8 <= i < 10))

    inferred = sum(r.confidence == "INFERRED" for r in rows)
    direct_calls = root_call_audit()
    lines = [
        "# ClientProtocol_1148 `SendSelfToClient` self-record layout",
        "",
        "This is the complete byte-consumption layout for build `0.0.118.208059` as loaded by "
        "`FUN_140a31140(player, stream)`. It is a clean-room result based only on the client binary and "
        "the local Ghidra decompiles named below. Multi-byte fields are little-endian. `str` is `i32 N` "
        "plus exactly N bytes and no terminator. Counts are signed or unsigned exactly as shown.",
        "",
        "The stream is `{base +0x00, length +0x08, cursor +0x10, end +0x18, error u8 +0x20}`. "
        "At the end, `FUN_140a31140` calls `FUN_1409e1080` if the error flag is set **or** "
        "`cursor-base < length`; the latter writes `0xbadbeef` through null. Therefore success requires "
        "both no over-read and no leftover byte.",
        "",
        "## Minimal record",
        "",
        f"The mechanically assembled minimal blob is **{len(blob)} bytes**. Every count and string length is "
        "zero, every scalar is zero, and the sole non-zero value is GUID `0x1001`. Zero list counts select "
        "the documented minimal branches and suppress every element body.",
        "This length is the blob only; the zone `03 | i32 length` and gateway tunnel `05` framing bytes are not included.",
        "",
        "| Landmark | Blob offset | Bytes/value |",
        "|---|---:|---|",
        f"| GUID (`player+0xd8`) | `0x{guid_off:x}` | `01 10 00 00 00 00 00 00` (`0x1001`) |",
        f"| Position (`player+0x3b0`, four f32) | `0x{pos_off:x}` | 16 zero bytes |",
        f"| Orientation (`player+0x3c0`, four f32) | `0x{orient_off:x}` | 16 zero bytes |",
        f"| Name string prefix (`player+0xe8`) | `0x{name_off:x}` | `00 00 00 00`; empty data begins at `0x{name_off+4:x}` |",
        "",
        "Exact blob hex:",
        "",
        "```text",
        hex_dump(blob),
        "```",
        "",
        "## Flat ordered read sequence",
        "",
        "`Min offset` is populated only for reads reached by the all-zero minimal path. Loop-body and "
        "unselected-branch rows remain part of the complete grammar and show `—`. A list-count row's "
        "element condition cites that exact earlier row number.",
        "",
        "| # | Min offset | Wire type / width | Destination | Reader | Loop or condition | Confidence | Note |",
        "|---:|---:|---|---|---|---|---|---|",
    ]
    for r in rows:
        off = "—" if r.offset is None else f"`0x{r.offset:x}`"
        lines.append("| " + " | ".join(md_escape(x) for x in [
            r.number, off, r.wire, f"`{r.dest}`", f"`{r.func}`", r.condition, r.confidence, r.note or "—"
        ]) + " |")

    lines += [
        "",
        "## Root stream-call audit",
        "",
        "Re-walking `FUN_140a31140` top to bottom gives the following stream-consuming calls in order. "
        "Constructor/cleanup calls that do not consume the stream are intentionally absent. Calls inside "
        "a root loop are marked as such.",
        "",
    ]
    for i, call in enumerate(direct_calls, 1):
        lines.append(f"{i}. `{call}`")

    lines += [
        "",
        "## Per-sub-loader appendix",
        "",
        "Each appendix table is the loader's own direct order. A `call` row is the exact point at which the "
        "callee's appendix is spliced into the flat table. `base` means the destination object passed to that loader.",
        "",
    ]
    for addr, schema in SCHEMAS.items():
        lines += [f"### `FUN_{addr}` — {schema.name}", "",
                  "| Step | Direct read or call | Destination | Local condition | Confidence | Note |",
                  "|---:|---|---|---|---|---|"]
        for i, (wire, dest, cond, conf, note) in enumerate(appendix_ops(schema), 1):
            lines.append("| " + " | ".join(md_escape(x) for x in [i, wire, f"`{dest}`", cond, conf, note]) + " |")
        lines.append("")

    lines += [
        "## Hazards and required ranges",
        "",
        "- **Exact consumption:** `FUN_140a31140` is the only reachable loader in the collected corpus that "
        "calls `FUN_1409e1080`; the final predicate crashes for either an error/short read or any leftover byte. "
        "The decompile of `FUN_1409e1080` contains the sole reachable `0xbadbeef` write.",
        "- **All list counts are hazards:** every `list-count` row is a loop bound and most also drive a resize, "
        "allocation, or hash-container growth. Signed negative counts often skip loops but some loaders first "
        "perform capacity arithmetic or remaining-byte validation; use zero for the minimal record, never a negative count.",
        "- **Strings:** `FUN_140b78f60` treats a negative length or a length greater than remaining bytes as a stream "
        "error. Empty is exactly four zero bytes.",
        "- **Modifier selector:** the selector inside `FUN_140a45090` is a vtable/type selector. "
        "`FUN_1421e0600` accepts only `1,3,6,7,8,10,11,12,13,14,15,17,18,19,20,22,23,24,25,26,27,28`; "
        "any other value returns null and the caller dereferences it. The zero-count minimal path has no selector.",
        "- **Inventory type dispatch:** `FUN_140a331a0` uses the `FUN_140a3aa60` item-definition key as a client-data "
        "lookup/hash key. Definition type `0x14` selects the longer weapon tail; every other resolved type selects "
        "the generic one-byte tail. Zero inventory count avoids the lookup and dispatch.",
        "- **Compact weapon key:** `FUN_140a12af0` uses a u16 header as a length/type bitfield. With bit 15 clear it "
        "advances by `(header&0x1fff)+1` bytes after the header; bit 14 additionally consumes u32. This value is an "
        "intern/hash key and can cause large string-table work.",
        "- **Selector-dependent pair:** `FUN_140a30280` consumes two trailing u32 only for kind byte 0 or 1. "
        "Other kinds consume no tail; changing the kind changes record length.",
        "- **Time adjustment:** root u64 fields at player `+0x3e8` and `+0x3f0` are adjusted by the server-time delta "
        "unless equal to the client sentinel. Zero is structurally valid and does not alter byte consumption.",
        "- **Hash/index keys:** every row called `key`, `hash key`, `selector`, or `type` feeds lookup, hash-bucket, "
        "array-index, or factory logic. Counts of zero prevent those rows on the minimal path.",
        "- **Allocation pressure:** `FUN_141483fa0` and `FUN_141484300` use signed i8 counts; positive values allocate "
        "and iterate, while negative values have awkward unsigned intermediates in the decompile. Use 0 unless emitting "
        "fully populated weapon data.",
        "",
        "## Verification performed",
        "",
        f"- Root read/call walk: {len(direct_calls)} stream-consuming call sites represented in root order.",
        f"- Flat grammar: {len(rows)} read rows across {len(SCHEMAS)} loader schemas; {inferred} rows are marked INFERRED.",
        f"- Minimal arithmetic: {sum(r.offset is not None for r in rows)} reached rows sum to {len(blob)} bytes; the hex above is "
        "rendered from those same row objects, not transcribed separately.",
        "- Distinguished-offset assertions passed for GUID `0x08`, position `0x4d`, orientation `0x5d`, and name prefix `0x79`.",
        "- Manual spot-checks were repeated against `FUN_140a40000` (head/name block), `FUN_140a331a0` plus its "
        "generic/weapon vtables (inventory), and `FUN_140a30570`/`FUN_140a30370` (hidden-RDX fixed tail).",
        "- Crash scan covered the original 266-file corpus and the supplemental decompiles made for this analysis.",
        "",
        "## Source files",
        "",
        "Primary decompile: `FUN_140a31140`, supplemented by read-only decompiles of the "
        "loader functions identified above. Binary: `Client/H1Z1.exe`. The generator for this "
        "document is `tools/selfschema/build_self_layout.py`.",
        "",
        "## Open questions",
        "",
        "There is no unresolved byte-order, width, loop, or conditional-layout gap in the grammar above. The remaining questions are non-layout questions:",
        "",
        "- The client decompiles establish byte consumption but not authoritative semantic names for many unnamed u32/u64 fields; "
        "those rows intentionally use destination offsets rather than guessed names.",
        "- The structurally valid all-zero minimal record has not been replayed here into a live client after this full-tail reconstruction; "
        "downstream gameplay acceptance after `FUN_140a31140` returns is separate from parser/exact-length acceptance.",
        "- For `FUN_1421dd120` and `FUN_1421dcbe0`, the string destination offsets are reconstructed from object layout because "
        "the decompiler loses the destination expression; the string reads and their wire positions are direct and marked INFERRED.",
        "",
    ]
    return "\n".join(lines)


def main() -> None:
    rows, blob = flatten_root()
    repo = Path(__file__).resolve().parents[2]
    out = repo / "docs" / "10-self-record-layout.md"
    out.write_text(render_document(rows, blob), encoding="utf-8", newline="\n")
    print(f"wrote {out}")
    print(f"minimal_length={len(blob)} rows={len(rows)} schemas={len(SCHEMAS)}")


if __name__ == "__main__":
    main()
