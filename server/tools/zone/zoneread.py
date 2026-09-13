#!/usr/bin/env python3
r"""zoneread - Cranberry's reader for the August-2017 H1Z1 client's Forgelight ``.zone`` files.

INPUT
    ``C:\Aug2017\out\world_aug\Z2.zone``        45,124,881 B  (from ``Assets_146.pack``)
    ``C:\Aug2017\out\world_aug\LoginZone.zone``    174,808 B  (from ``Assets_117.pack``)
    Both are ZONE **version 5** for client build 0.0.118.208059. Extract them with
    ``tools/pack/packread.py extract "Z2.zone" -o C:\Aug2017\out\world_aug``.

OUTPUT
    ``header``            the file header: version, section table, tile/chunk geometry.
    ``ecos``              the terrain eco (texture + flora layer) definitions.
    ``floras``            the flora (grass/detail mesh) definitions.
    ``invisible-walls``   the invisible-wall section (empty in both August zones).
    ``objects``           placed actor instances, filterable by model glob / XZ radius.
    ``export``            every placed instance streamed to JSON or JSON Lines.
    ``sections``          raw section table with byte spans (diagnostic).

    The tool never writes back into the client or into ``C:\Aug2017\Client``.

FORMAT FACTS THIS TOOL RELIES ON
    The ZONE container is public Forgelight protocol-level knowledge (clean-room rule 5);
    every field below was re-measured on the two August ``.zone`` files themselves, and the
    parser self-checks by requiring each section to be consumed to its exact byte end.
    See ``docs/29-z2-world-data.md`` for the byte tables and the evidence per field.

    Z1  Magic is ASCII ``ZONE`` at +0. Every scalar after it is LITTLE-endian
        (``Z2.zone`` +4 reads 5 little-endian; big-endian the same bytes give 0x05000000,
        which is not a version this client's other fields agree with).
    Z2  +4 ``u32 version`` = 5 in both August zones.
    Z3  +8 ``u32 sectionCount`` = 7, followed by ``sectionCount`` x ``u32`` absolute file
        offsets. Evidence: offset[0] is 76 = 0x4C in both files, and 0x4C is exactly where
        the eco count lives (12 + 4*7 = 40 header bytes of geometry follow the table and
        end at 76). Section order, from what each offset actually decodes to:
            0 ecos   1 floras   2 invisibleWalls   3 objects   4 lights
            5 unknown5 (u32 count, 0 in both files)   6 decals
    Z4  The geometry block that follows the section table, all little-endian:
            u32 quadsPerTile      Z2/LoginZone: 128
            f32 tileSize          64.0
            f32 tileHeight        0.03125  (= 1/32)
            u32 verticesPerTile   65       (= 64 + 1)
            u32 tilesPerChunk     8
            i32 startX, i32 startY   Z2: -128,-128   LoginZone: -8,-8
            u32 chunksX, u32 chunksY Z2: 256,256     LoginZone: 16,16
        The start/count pair matches the ``Z2_<x>_<y>_lod*.cnk`` chunk names in the pack
        index (docs/07 §8.1 read the same four numbers out of the pack name tables).
    Z5  Strings are NUL-terminated ASCII with no length prefix.
    Z6  ecos section: ``u32 ecoCount``, then per eco:
            u32 index
            string name, string colorNXMap(.dds), string specNyMap(.dds)
            u32 detailRepeat, f32 blendStrength,
            f32 specMin, f32 specMax, f32 specSmoothnessMin, f32 specSmoothnessMax
            string physicsMaterial
            u32 layerCount, then per layer:
                f32 density, f32 minScale, f32 maxScale, f32 slopePeak, f32 slopeExtent,
                f32 minElevation, f32 maxElevation, u8 minAlpha,
                string flora, u32 tintCount, then tintCount x (u32 colorRGBA, u32 percent)
    Z7  floras section: ``u32 floraCount``, then per flora:
            string name, string texture(.dds), string model(.dme),
            f32 unk0, f32 unk1, f32 unk2, u8 unk3, f32 unk4, f32 unk5
    Z8  invisibleWalls section: ``u32 count``. It is 0 in both August zones, so the element
        layout is UNVERIFIED and the tool refuses to guess (it reports the count and stops).
    Z9  objects section: ``u32 objectCount``, then per object:
            string actorFile (".adr"), f32 renderDistance, u32 instanceCount,
            then instanceCount instances of:
                f32[4] position   (x, y, z, w=1)      y is UP (height)
                f32[4] rotation   (yaw/H, pitch/P, roll/R, w) in radians
                f32[4] scale      (x, y, z, w)
                u32    instanceId
                u8     unknownByte
                f32    unknownFloat        (1.0 in every instance seen)
                u32 nA,  nA  x 8 bytes      (u32 nameHash, u32 value)
                u32 nB,  nB  x 8 bytes      (u32 nameHash, f32 value)   scalar overrides
                u32 nC,  nC  x ?            always 0; element size UNVERIFIED
                u32 nD,  nD  x 20 bytes     (u32 nameHash, f32[4] value) vector overrides
                u32    unknownTail
                u8     unknownTailByte
            A fully-defaulted instance is therefore 78 bytes. Evidence that this is right:
            walking all 148 objects of ``LoginZone.zone`` consumes 166,634 of 166,634
            section bytes exactly, and all 915 objects / 501,987 instances of ``Z2.zone``
            consume 45,090,721 of 45,090,721 bytes exactly. A wrong instance size desyncs
            within the first object.
    Z10 lights section (index 4) and unknown5 (index 5): ``u32 count``, 0 in both zones, so
        their element layouts are UNVERIFIED here.
    Z11 decals section (index 6): ``u32 count`` (269 in Z2, 0 in LoginZone), then per decal
            u32 unknown0 (0 in every Z2 decal)
            f32[4] position, f32[4] rotation
            u32 unknown1
            string name    (Blood_Trail, Blood_Splatter, BulletHole_Wood_sm, ...)
            u32 unknown2, f32 unknown3, u32 unknown4, u32 unknown5
        The record SIZE is verified - 269 decals consume 18,911 of 18,911 bytes exactly -
        but only position, rotation and name are interpreted; the five raw fields are
        surfaced unnamed rather than guessed at.

USAGE
    python zoneread.py C:\Aug2017\out\world_aug\Z2.zone header
    python zoneread.py C:\Aug2017\out\world_aug\Z2.zone objects --model "ItemSpawner*" --count-only
    python zoneread.py C:\Aug2017\out\world_aug\Z2.zone objects --near 3500,3600,200
    python zoneread.py C:\Aug2017\out\world_aug\Z2.zone export --json C:\Aug2017\out\world_aug\z2-objects.jsonl

Run ``python zoneread.py <file> <subcommand> --help`` for per-subcommand options.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import mmap
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator, Sequence

# --------------------------------------------------------------------------------------
# Format constants (see Z1-Z11 in the module docstring)
# --------------------------------------------------------------------------------------

MAGIC = b"ZONE"
SUPPORTED_VERSIONS = (5,)

#: Names for the seven ZONE v5 sections, in the order the offset table lists them (Z3).
SECTION_NAMES = (
    "ecos",
    "floras",
    "invisibleWalls",
    "objects",
    "lights",
    "unknown5",
    "decals",
)

_U8 = struct.Struct("<B")
_U32 = struct.Struct("<I")
_I32 = struct.Struct("<i")
_F32 = struct.Struct("<f")
_VEC4 = struct.Struct("<4f")
#: Z4: the geometry block after the section table.
_GEOMETRY = struct.Struct("<IffIIiiII")

#: Z4b: world metres per ``startX``/``chunksX`` grid unit.
#:
#: This is DERIVED, not a header field, and it is deliberately not ``tileSize``. Three
#: independent measurements agree on 32 m and none of them is the header's own arithmetic:
#:
#:   1. The chunk-file grid. ``Z2_<x>_<y>_0.cnk`` in the pack index: 4,096 names, x and y
#:      each -128..124 stepping by 4 - i.e. 64 chunk files per axis, 4 grid units apart,
#:      spanning exactly the header's 256 units. LoginZone: 16 names, x in {-8,-4,0,4},
#:      4 files per axis over its 16 units. Same 4-units-per-chunk-file in both zones.
#:   2. Z2's content. Over all 501,987 placements, excluding the 1,624 lobby props above
#:      y = 400, X runs -4080.07..4073.66 and Z runs -3964.63..4043.76. A 512 m histogram
#:      has zero placements outside +/-4096 and a populated outermost ring (up to 3,617 in
#:      one bin), so the map ends at +/-4096 m - not at the half-way point of a +/-8192 one.
#:      256 units over 8,192 m is 32 m per unit, and 8,192/64 chunk files = 128 m per file.
#:   3. ``verticesPerTile`` 65 = 64 quads per chunk edge; at 128 m per chunk file that is a
#:      2 m quad, a round number, where a 256 m chunk would give 4 m.
#:
#: ``tileSize`` (64.0) cannot be the metres-per-unit: it would put Z2's terrain at
#: +/-8,192 m, twice the measured content, and ``tileSize`` x ``tilesPerChunk`` (8) = 512 m
#: per chunk file x 64 files = 32,768 m, which is not that +/-8,192 m either - i.e. the
#: header is not self-consistent under a metres reading of ``tileSize``, so what those two
#: fields count is left UNRESOLVED here rather than guessed.
#:
#: LoginZone's placements do NOT bear on this: all 1,675 of them sit at y 489.6..534.2, a
#: backdrop scene floating above the terrain, so the one at X = -455.4 m is outside the
#: +/-256 m grid without contradicting it.
GRID_UNIT_METRES = 32.0
#: Z9: object header after the actor-file string.
_OBJ_HEADER = struct.Struct("<fI")
#: Z9: the fixed prefix of an instance, up to (and excluding) the first variable list.
_INSTANCE_FIXED = struct.Struct("<4f4f4fIBf")
_INSTANCE_FIXED_SIZE = _INSTANCE_FIXED.size  # 57

#: Z9: parameter-list element sizes, in the order the lists appear inside an instance.
#: ``None`` means the element size was never observed (the list is always empty) and the
#: parser must refuse to continue rather than guess.
_PARAM_LIST_ELEMENT_SIZES = (8, 8, None, 20)


class ZoneFormatError(RuntimeError):
    """Raised when the bytes do not match the layout this tool verified."""


# --------------------------------------------------------------------------------------
# A tiny cursor over a memory-mapped file: no section is ever copied into a bytes object.
# --------------------------------------------------------------------------------------


class Cursor:
    """Sequential little-endian reader over a buffer (an ``mmap`` in normal use)."""

    __slots__ = ("buf", "pos", "limit")

    def __init__(self, buf, pos: int = 0, limit: int | None = None) -> None:
        self.buf = buf
        self.pos = pos
        self.limit = len(buf) if limit is None else limit

    def _need(self, n: int) -> int:
        p = self.pos
        if p + n > self.limit:
            raise ZoneFormatError(
                f"read of {n} byte(s) at {p} runs past the section end {self.limit}"
            )
        self.pos = p + n
        return p

    def u8(self) -> int:
        return _U8.unpack_from(self.buf, self._need(1))[0]

    def u32(self) -> int:
        return _U32.unpack_from(self.buf, self._need(4))[0]

    def i32(self) -> int:
        return _I32.unpack_from(self.buf, self._need(4))[0]

    def f32(self) -> float:
        return _F32.unpack_from(self.buf, self._need(4))[0]

    def vec4(self) -> tuple[float, float, float, float]:
        return _VEC4.unpack_from(self.buf, self._need(16))

    def string(self) -> str:
        """Z5: a NUL-terminated ASCII string."""
        end = self.buf.find(b"\x00", self.pos, self.limit)
        if end < 0:
            raise ZoneFormatError(f"unterminated string at {self.pos}")
        s = bytes(self.buf[self.pos : end]).decode("ascii", "replace")
        self.pos = end + 1
        return s

    def skip(self, n: int) -> None:
        self._need(n)


# --------------------------------------------------------------------------------------
# Header
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class ZoneHeader:
    """The ZONE v5 header (Z1-Z4)."""

    version: int
    section_offsets: tuple[int, ...]
    quads_per_tile: int
    tile_size: float
    tile_height: float
    vertices_per_tile: int
    tiles_per_chunk: int
    start_x: int
    start_y: int
    chunks_x: int
    chunks_y: int
    header_size: int

    def section(self, name: str) -> int:
        """Absolute file offset of a named section, by the Z3 order."""
        try:
            return self.section_offsets[SECTION_NAMES.index(name)]
        except (ValueError, IndexError) as exc:  # pragma: no cover - defensive
            raise ZoneFormatError(f"no section {name!r} in this file") from exc

    def section_spans(self, file_size: int) -> list[tuple[str, int, int]]:
        """(name, start, end) for every section; end = next offset, or EOF for the last."""
        spans = []
        for i, off in enumerate(self.section_offsets):
            end = (
                self.section_offsets[i + 1]
                if i + 1 < len(self.section_offsets)
                else file_size
            )
            spans.append((SECTION_NAMES[i] if i < len(SECTION_NAMES) else f"section{i}", off, end))
        return spans

    @property
    def world_extent(self) -> tuple[float, float, float, float]:
        """(minX, minZ, maxX, maxZ) of the nominal terrain grid, in world metres.

        ``startX``/``chunksX`` count grid units; the metres-per-unit is ``GRID_UNIT_METRES``
        = 32, which is **derived, not read out of the header** - see that constant for the
        evidence and for why ``tileSize`` is not it. Z2: start -128, 256 units ->
        -4096..4096 m. LoginZone: start -8, 16 units -> -256..256 m.
        """
        return (
            self.start_x * GRID_UNIT_METRES,
            self.start_y * GRID_UNIT_METRES,
            (self.start_x + self.chunks_x) * GRID_UNIT_METRES,
            (self.start_y + self.chunks_y) * GRID_UNIT_METRES,
        )


def read_header(buf) -> ZoneHeader:
    """Parse the header at offset 0 (Z1-Z4)."""
    if bytes(buf[:4]) != MAGIC:
        raise ZoneFormatError(f"not a ZONE file: magic {bytes(buf[:4])!r}")
    version = _U32.unpack_from(buf, 4)[0]
    if version not in SUPPORTED_VERSIONS:
        raise ZoneFormatError(
            f"ZONE version {version} is not one this tool has verified {SUPPORTED_VERSIONS}"
        )
    count = _U32.unpack_from(buf, 8)[0]
    if not 1 <= count <= 32:
        raise ZoneFormatError(f"implausible section count {count}")
    offsets = struct.unpack_from(f"<{count}I", buf, 12)
    geo_at = 12 + 4 * count
    geo = _GEOMETRY.unpack_from(buf, geo_at)
    header_size = geo_at + _GEOMETRY.size
    if offsets[0] != header_size:
        raise ZoneFormatError(
            f"section table says the first section starts at {offsets[0]} but the header "
            f"ends at {header_size}; the layout is not the one this tool verified"
        )
    return ZoneHeader(version, offsets, *geo, header_size=header_size)


# --------------------------------------------------------------------------------------
# ecos / floras / invisible walls
# --------------------------------------------------------------------------------------


def read_ecos(buf, header: ZoneHeader, file_size: int) -> list[dict]:
    """Z6. Returns one dict per eco; raises if the section is not consumed exactly."""
    spans = dict((n, (a, b)) for n, a, b in header.section_spans(file_size))
    start, end = spans["ecos"]
    c = Cursor(buf, start, end)
    ecos = []
    for _ in range(c.u32()):
        eco = {
            "index": c.u32(),
            "name": c.string(),
            "colorNXMap": c.string(),
            "specNyMap": c.string(),
            "detailRepeat": c.u32(),
            "blendStrength": c.f32(),
            "specMin": c.f32(),
            "specMax": c.f32(),
            "specSmoothnessMin": c.f32(),
            "specSmoothnessMax": c.f32(),
            "physicsMaterial": c.string(),
            "layers": [],
        }
        for _ in range(c.u32()):
            layer = {
                "density": c.f32(),
                "minScale": c.f32(),
                "maxScale": c.f32(),
                "slopePeak": c.f32(),
                "slopeExtent": c.f32(),
                "minElevation": c.f32(),
                "maxElevation": c.f32(),
                "minAlpha": c.u8(),
                "flora": c.string(),
                "tints": [],
            }
            for _ in range(c.u32()):
                layer["tints"].append({"colorRGBA": c.u32(), "percent": c.u32()})
            eco["layers"].append(layer)
        ecos.append(eco)
    _require_exhausted(c, "ecos")
    return ecos


def read_floras(buf, header: ZoneHeader, file_size: int) -> list[dict]:
    """Z7. Returns one dict per flora; raises if the section is not consumed exactly."""
    spans = dict((n, (a, b)) for n, a, b in header.section_spans(file_size))
    start, end = spans["floras"]
    c = Cursor(buf, start, end)
    floras = []
    for _ in range(c.u32()):
        floras.append(
            {
                "name": c.string(),
                "texture": c.string(),
                "model": c.string(),
                "unk0": c.f32(),
                "unk1": c.f32(),
                "unk2": c.f32(),
                "unk3": c.u8(),
                "unk4": c.f32(),
                "unk5": c.f32(),
            }
        )
    _require_exhausted(c, "floras")
    return floras


def read_decals(buf, header: ZoneHeader, file_size: int) -> list[dict]:
    """Z11. Returns one dict per decal; raises if the section is not consumed exactly."""
    spans = dict((n, (a, b)) for n, a, b in header.section_spans(file_size))
    start, end = spans["decals"]
    c = Cursor(buf, start, end)
    decals = []
    for _ in range(c.u32()):
        decals.append(
            {
                "unknown0": c.u32(),
                "position": c.vec4(),
                "rotation": c.vec4(),
                "unknown1": c.u32(),
                "name": c.string(),
                "unknown2": c.u32(),
                "unknown3": c.f32(),
                "unknown4": c.u32(),
                "unknown5": c.u32(),
            }
        )
    _require_exhausted(c, "decals")
    return decals


def read_counted_section(buf, header: ZoneHeader, file_size: int, name: str) -> int:
    """Z8/Z10: read only the leading ``u32 count`` of a section whose elements are unverified."""
    spans = dict((n, (a, b)) for n, a, b in header.section_spans(file_size))
    start, end = spans[name]
    return Cursor(buf, start, end).u32()


def _require_exhausted(c: Cursor, name: str) -> None:
    if c.pos != c.limit:
        raise ZoneFormatError(
            f"{name} section: parsed to {c.pos} but the section ends at {c.limit} "
            f"({c.limit - c.pos:+d} bytes); the layout does not match this file"
        )


# --------------------------------------------------------------------------------------
# objects
# --------------------------------------------------------------------------------------


@dataclass
class Instance:
    """One placed actor instance (Z9)."""

    model: str
    render_distance: float
    instance_id: int
    position: tuple[float, float, float, float]
    rotation: tuple[float, float, float, float]
    scale: tuple[float, float, float, float]
    flag_byte: int
    unknown_float: float
    scalar_params: list[tuple[int, float]] = field(default_factory=list)
    dword_params: list[tuple[int, int]] = field(default_factory=list)
    vector_params: list[tuple[int, tuple[float, float, float, float]]] = field(
        default_factory=list
    )

    def to_dict(self, params: bool = False) -> dict:
        # Rounded to 4 / 6 decimals: the source values are float32, whose resolution at a
        # +-8192 m world coordinate is already ~0.0005 m, so this loses nothing real and
        # keeps the export a third smaller.
        d = {
            "model": self.model,
            "id": self.instance_id,
            "pos": [round(v, 4) for v in self.position[:3]],
            "rot": [round(v, 6) for v in self.rotation[:3]],
            "scale": [round(v, 4) for v in self.scale[:3]],
            "renderDistance": self.render_distance,
            "flags": self.flag_byte,
        }
        if params:
            if self.dword_params:
                d["dwordParams"] = [[h, v] for h, v in self.dword_params]
            if self.scalar_params:
                d["scalarParams"] = [[h, v] for h, v in self.scalar_params]
            if self.vector_params:
                d["vectorParams"] = [[h, list(v)] for h, v in self.vector_params]
        return d


def iter_objects(buf, header: ZoneHeader, file_size: int, *, with_params: bool = False):
    """Stream (model, renderDistance, instanceCount, instances-iterator) over the objects
    section. Nothing larger than one instance is held at a time (Z9)."""
    spans = dict((n, (a, b)) for n, a, b in header.section_spans(file_size))
    start, end = spans["objects"]
    c = Cursor(buf, start, end)
    object_count = c.u32()
    for _ in range(object_count):
        model = c.string()
        render_distance, instance_count = _OBJ_HEADER.unpack_from(buf, c._need(_OBJ_HEADER.size))
        for _ in range(instance_count):
            yield _read_instance(c, model, render_distance, with_params)
    _require_exhausted(c, "objects")


def _read_instance(c: Cursor, model: str, render_distance: float, with_params: bool) -> Instance:
    at = c._need(_INSTANCE_FIXED_SIZE)
    v = _INSTANCE_FIXED.unpack_from(c.buf, at)
    inst = Instance(
        model=model,
        render_distance=render_distance,
        position=v[0:4],
        rotation=v[4:8],
        scale=v[8:12],
        instance_id=v[12],
        flag_byte=v[13],
        unknown_float=v[14],
    )
    # Z9: four parameter lists, then a u32 + u8 tail.
    for list_index, elem_size in enumerate(_PARAM_LIST_ELEMENT_SIZES):
        n = c.u32()
        if n == 0:
            continue
        if elem_size is None:
            raise ZoneFormatError(
                f"instance {inst.instance_id} of {model}: parameter list {list_index} has "
                f"{n} element(s) but its element size has never been observed in this "
                f"client's zones, so the size cannot be assumed"
            )
        if not with_params:
            c.skip(n * elem_size)
            continue
        for _ in range(n):
            if list_index == 0:
                inst.dword_params.append((c.u32(), c.u32()))
            elif list_index == 1:
                inst.scalar_params.append((c.u32(), c.f32()))
            else:
                inst.vector_params.append((c.u32(), c.vec4()))
    c.u32()
    c.u8()
    return inst


# --------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------


def _open(path: Path):
    f = open(path, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    return f, mm


def _matches(inst: Instance, globs: Sequence[str], near: tuple[float, float, float] | None) -> bool:
    if globs and not any(fnmatch.fnmatch(inst.model, g) for g in globs):
        return False
    if near is not None:
        x, z, r = near
        dx = inst.position[0] - x
        dz = inst.position[2] - z
        if dx * dx + dz * dz > r * r:
            return False
    return True


def _parse_near(s: str) -> tuple[float, float, float]:
    parts = s.split(",")
    if len(parts) != 3:
        raise argparse.ArgumentTypeError("--near takes X,Z,R (world metres)")
    return tuple(float(p) for p in parts)  # type: ignore[return-value]


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="zoneread",
        description=(
            "Read the August-2017 H1Z1 client's Forgelight ZONE v5 world files: header, "
            "ecos, floras, invisible walls, placed object instances, and a streaming "
            "JSON/JSONL export. Read-only; memory-maps the file so the 45 MB Z2.zone is "
            "never copied into memory."
        ),
        epilog=(
            "Format facts (verified 2026-08-29 against Z2.zone and LoginZone.zone, byte "
            "tables in docs/29-z2-world-data.md): little-endian ZONE version 5; a 7-entry "
            "absolute section-offset table (ecos, floras, invisibleWalls, objects, lights, "
            "unknown5, decals); objects = per-model (name, renderDistance, instanceCount) "
            "with 78-byte default instances carrying float4 position/rotation/scale, a u32 "
            "instance id and four parameter lists. Verified by requiring each section to be "
            "consumed to its exact byte end (LoginZone 166,634/166,634; Z2 45,090,721/"
            "45,090,721)."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("file", type=Path, help="the .zone file to read")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("header", help="print the file header and terrain grid")
    sub.add_parser("sections", help="print the section table with byte spans")
    for name, helptext in (
        ("ecos", "print the terrain eco definitions"),
        ("floras", "print the flora definitions"),
        ("decals", "print the world decal placements"),
        ("invisible-walls", "print the invisible-wall section"),
    ):
        s = sub.add_parser(name, help=helptext)
        s.add_argument("--json", action="store_true", help="print as JSON instead of text")

    o = sub.add_parser("objects", help="list placed actor instances")
    o.add_argument("--model", action="append", default=[], metavar="GLOB",
                   help="only models matching this glob (repeatable)")
    o.add_argument("--near", type=_parse_near, metavar="X,Z,R",
                   help="only instances within R metres of world X,Z")
    o.add_argument("--limit", type=int, default=0, help="stop after N instances (0 = all)")
    o.add_argument("--count-only", action="store_true",
                   help="print per-model instance counts instead of every instance")
    o.add_argument("--params", action="store_true", help="also decode the parameter lists")

    e = sub.add_parser("export", help="stream every instance to JSON or JSON Lines")
    e.add_argument("--json", required=True, type=Path, metavar="OUT",
                   help="output path; a .jsonl suffix writes one instance per line")
    e.add_argument("--model", action="append", default=[], metavar="GLOB",
                   help="only models matching this glob (repeatable)")
    e.add_argument("--near", type=_parse_near, metavar="X,Z,R",
                   help="only instances within R metres of world X,Z")
    e.add_argument("--params", action="store_true", help="also write the parameter lists")
    return p


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    path: Path = args.file
    if not path.is_file():
        print(f"zoneread: no such file: {path}", file=sys.stderr)
        return 2
    file_size = path.stat().st_size
    f, mm = _open(path)
    try:
        header = read_header(mm)
        if args.cmd == "header":
            minx, minz, maxx, maxz = header.world_extent
            print(f"file              {path}")
            print(f"size              {file_size:,} bytes")
            print(f"version           {header.version}")
            print(f"sections          {len(header.section_offsets)}")
            print(f"quadsPerTile      {header.quads_per_tile}")
            print(f"tileSize          {header.tile_size}")
            print(f"tileHeight        {header.tile_height}")
            print(f"verticesPerTile   {header.vertices_per_tile}")
            print(f"tilesPerChunk     {header.tiles_per_chunk}")
            print(f"start             ({header.start_x}, {header.start_y}) chunks")
            print(f"chunks            {header.chunks_x} x {header.chunks_y}")
            print(f"terrain extent    X {minx:.0f}..{maxx:.0f}  Z {minz:.0f}..{maxz:.0f} m "
                  f"(at {GRID_UNIT_METRES:g} m/grid unit - DERIVED from the .cnk grid and "
                  f"the placement bounds, not from tileSize; see GRID_UNIT_METRES)")
            return 0
        if args.cmd == "sections":
            for name, start, end in header.section_spans(file_size):
                count = _U32.unpack_from(mm, start)[0] if end - start >= 4 else 0
                print(f"{name:<16} offset {start:>10,}  bytes {end - start:>12,}  count {count:>8,}")
            return 0
        if args.cmd in ("ecos", "floras", "decals"):
            reader = {"ecos": read_ecos, "floras": read_floras, "decals": read_decals}[args.cmd]
            rows = reader(mm, header, file_size)
            if args.json:
                json.dump(rows, sys.stdout, indent=1)
                print()
            elif args.cmd == "ecos":
                for eco in rows:
                    print(
                        f"[{eco['index']:>3}] {eco['name']:<24} {eco['physicsMaterial']:<12} "
                        f"detailRepeat={eco['detailRepeat']:<4} layers={len(eco['layers'])}"
                    )
                    for layer in eco["layers"]:
                        print(
                            f"        flora={layer['flora']:<28} density={layer['density']:<8g} "
                            f"scale={layer['minScale']:g}..{layer['maxScale']:g} "
                            f"elev={layer['minElevation']:g}..{layer['maxElevation']:g} "
                            f"tints={len(layer['tints'])}"
                        )
            elif args.cmd == "floras":
                for fl in rows:
                    print(f"{fl['name']:<28} {fl['model']:<44} {fl['texture']}")
            else:
                for dc in rows:
                    px, py, pz, _ = dc["position"]
                    print(
                        f"{dc['name']:<24} pos=({px:9.2f},{py:8.2f},{pz:10.2f}) "
                        f"rot=({dc['rotation'][0]:7.4f},{dc['rotation'][1]:7.4f},"
                        f"{dc['rotation'][2]:7.4f}) "
                        f"raw=[{dc['unknown0']},{dc['unknown1']},{dc['unknown2']},"
                        f"{dc['unknown3']:g},{dc['unknown4']},{dc['unknown5']}]"
                    )
            print(f"# {len(rows)} {args.cmd}", file=sys.stderr)
            return 0
        if args.cmd == "invisible-walls":
            n = read_counted_section(mm, header, file_size, "invisibleWalls")
            if args.json:
                print(json.dumps({"count": n, "walls": []}))
            else:
                print(f"invisibleWalls count = {n}")
                if n:
                    print(
                        "zoneread: this client's zones all carry 0 invisible walls, so the "
                        "element layout was never verified; refusing to guess it.",
                        file=sys.stderr,
                    )
                    return 1
            return 0
        if args.cmd == "objects":
            if args.count_only:
                # Filter per INSTANCE, not per model name: --near is a position test, so it
                # can only be applied while the instance's own position is in hand. Counting
                # first and filtering the model names afterwards would ignore --near entirely
                # and report every instance of a matching model.
                counts: dict[str, int] = {}
                scanned = 0
                for inst in iter_objects(mm, header, file_size):
                    scanned += 1
                    if not _matches(inst, args.model, args.near):
                        continue
                    counts[inst.model] = counts.get(inst.model, 0) + 1
                total = 0
                for model, n in sorted(counts.items(), key=lambda kv: -kv[1]):
                    print(f"{n:>8,}  {model}")
                    total += n
                # len(counts) is the number of models that PASSED the filter, which is what
                # the printed table shows; the scanned total is reported separately.
                print(
                    f"# {total:,} instances in {len(counts):,} model(s) matched, "
                    f"of {scanned:,} instances scanned",
                    file=sys.stderr,
                )
                return 0
            shown = 0
            for inst in iter_objects(mm, header, file_size, with_params=args.params):
                if not _matches(inst, args.model, args.near):
                    continue
                x, y, z, _ = inst.position
                print(
                    f"{inst.model:<52} id={inst.instance_id:<12} "
                    f"pos=({x:10.2f},{y:8.2f},{z:10.2f}) "
                    f"rot=({inst.rotation[0]:7.4f},{inst.rotation[1]:7.4f},{inst.rotation[2]:7.4f}) "
                    f"scale=({inst.scale[0]:.3f},{inst.scale[1]:.3f},{inst.scale[2]:.3f}) "
                    f"flags={inst.flag_byte}"
                )
                if args.params and (inst.dword_params or inst.scalar_params or inst.vector_params):
                    for h, v in inst.dword_params:
                        print(f"        dword  0x{h:08x} = {v}")
                    for h, v in inst.scalar_params:
                        print(f"        scalar 0x{h:08x} = {v:g}")
                    for h, vec in inst.vector_params:
                        print(f"        vector 0x{h:08x} = ({vec[0]:g},{vec[1]:g},{vec[2]:g},{vec[3]:g})")
                shown += 1
                if args.limit and shown >= args.limit:
                    break
            print(f"# {shown:,} instances", file=sys.stderr)
            return 0
        if args.cmd == "export":
            out: Path = args.json
            out.parent.mkdir(parents=True, exist_ok=True)
            jsonl = out.suffix.lower() == ".jsonl"
            written = 0
            with out.open("w", encoding="utf-8", newline="\n") as fh:
                if not jsonl:
                    fh.write("[\n")
                for inst in iter_objects(mm, header, file_size, with_params=args.params):
                    if not _matches(inst, args.model, args.near):
                        continue
                    line = json.dumps(inst.to_dict(params=args.params), separators=(",", ":"))
                    if jsonl:
                        fh.write(line + "\n")
                    else:
                        fh.write(("" if written == 0 else ",\n") + " " + line)
                    written += 1
                if not jsonl:
                    fh.write("\n]\n")
            print(f"wrote {written:,} instance(s) -> {out}", file=sys.stderr)
            return 0
    except ZoneFormatError as exc:
        print(f"zoneread: {exc}", file=sys.stderr)
        return 1
    finally:
        mm.close()
        f.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
