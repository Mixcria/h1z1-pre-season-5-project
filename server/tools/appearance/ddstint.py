#!/usr/bin/env python3
r"""ddstint - decode a client ``.dds`` colour map and measure the tint ramp it implies.

WHY THIS EXISTS

  docs/69 ranked thirty ground-loot colour maps by their near-white fraction with "a decoder
  written for this pass" that was never checked in, so every later lane that wanted a measured
  colour had to either re-write it or hand-type a number. This is that decoder, kept.

  It answers one question: **given a mesh's colour map, what three-point tint ramp does the
  August material model need in order to land the mesh on a chosen colour?**

  The material model is docs/69 section 3.4: ``TintSemanticTables.txt`` row 1 ``Default`` is
  highlight ``1,1,1`` / midtone ``0.5,0.5,0.5`` / shadow ``0,0,0`` - an identity remap of the
  albedo's own luminance. A ``_Tintable`` mesh is authored as a greyscale luminance mask and
  stays grey until a shader parameter group replaces those three anchors. So the ramp a group
  must carry is the map's own luminance structure, recoloured:

      shadow  <- the map's dark anchor    (default percentile  8)
      midtone <- the map's mid anchor     (default percentile 50)
      highlight <- the map's light anchor (default percentile 92)

  Measuring the anchors rather than assuming ``0 / 0.5 / 1`` is the whole point: a map whose
  texels sit between 0.35 and 0.75 needs a compressed ramp, and a map that already runs the full
  range needs a wide one. The percentiles are taken over the map's *painted* texels - see
  ``--alpha-floor`` and the ``ignoreBlack`` rule below - because a character map's unused UV
  space is flat black and would drag every anchor down.

FORMAT FACTS (verified against the four August character colour maps this lane measures)

  D1  ``DDS `` magic, then a 124-byte ``DDSURFACEDESC2``: at +8 flags, +12 height, +16 width,
      +20 pitch-or-linear-size, +24 depth, +28 mipmap count, then 11 reserved u32, then the
      32-byte pixel format at +72 (size, flags, fourCC, RGB bit count, and four masks).
  D2  ``pfFlags & 0x4`` means the fourCC is meaningful. All four August character colour maps
      measured here are ``DXT1``: 512x512 or 1024x1024, 10 or 11 mips, no alpha block.
  D3  A DXT1 block is 8 bytes: ``u16 c0``, ``u16 c1`` (RGB565) and 16 2-bit indices, row 0 in the
      low byte. When ``c0 > c1`` the two interpolants are 2/3 and 1/3 mixes; otherwise the block
      is 1-bit-alpha and index 3 is transparent black. DXT3 and DXT5 prefix the same colour block
      with an 8-byte alpha block (explicit 4-bit, and interpolated 8-bit respectively).
  D4  Mip 0 is the first ``max(1, ceil(w/4)) * max(1, ceil(h/4)) * blockBytes`` bytes after the
      header. Only mip 0 is decoded; the smaller mips are the same picture.

USAGE

    python ddstint.py measure <file.dds> [...] [--json OUT] [--alpha-floor 0.5]
    python ddstint.py ramp    <file.dds> --colour 0.12,0.30,0.62 [--alpha 0.5]

Nothing is ever written back into the client; every file is opened read-only.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

DDS_MAGIC = b"DDS "
HEADER_SIZE = 124
PF_FOURCC = 0x4
PF_RGB = 0x40
PF_ALPHAPIXELS = 0x1

#: Percentile anchors of the three-point ramp. Cranberry's choice, documented rather than tuned:
#: 8/50/92 keeps the anchors off the extreme texels (a single blown-out specular pixel or one
#: black seam must not define the ramp) while still spanning the map's real range.
SHADOW_PERCENTILE = 8.0
MIDTONE_PERCENTILE = 50.0
HIGHLIGHT_PERCENTILE = 92.0

#: docs/69 section 3.2's "near-white" threshold, kept identical so the two documents' numbers
#: are comparable.
NEAR_WHITE = 190

#: Texels darker than this in every channel are unpainted UV space on a character map, not art.
#: Measured: dropping them moves the ManSport backpack's median from 0.30 to 0.42 and leaves the
#: 92nd percentile unchanged, i.e. it removes background and not shading.
BLACK_FLOOR = 8


# ======================================================================================
# DDS
# ======================================================================================


class DdsError(ValueError):
    """The file is not a DDS this tool can decode."""


def _rgb565(value: int) -> tuple[int, int, int]:
    r = (value >> 11) & 0x1F
    g = (value >> 5) & 0x3F
    b = value & 0x1F
    return (r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)


def _decode_dxt_colour_block(
    block: bytes, opaque_only: bool
) -> tuple[list[tuple[int, int, int]], list[bool]]:
    """One 8-byte DXT colour block -> 16 RGB texels plus their 1-bit-alpha mask (D3)."""
    c0, c1, bits = struct.unpack("<HHI", block)
    p0 = _rgb565(c0)
    p1 = _rgb565(c1)
    if c0 > c1 or opaque_only:
        palette = [
            p0,
            p1,
            tuple((2 * a + b + 1) // 3 for a, b in zip(p0, p1)),
            tuple((a + 2 * b + 1) // 3 for a, b in zip(p0, p1)),
        ]
        transparent = [False, False, False, False]
    else:
        palette = [
            p0,
            p1,
            tuple((a + b) // 2 for a, b in zip(p0, p1)),
            (0, 0, 0),
        ]
        transparent = [False, False, False, True]

    texels = []
    alpha = []
    for index in range(16):
        selector = (bits >> (2 * index)) & 0x3
        texels.append(palette[selector])
        alpha.append(not transparent[selector])
    return texels, alpha


def _decode_dxt5_alpha_block(block: bytes) -> list[int]:
    a0 = block[0]
    a1 = block[1]
    if a0 > a1:
        table = [a0, a1] + [((7 - i) * a0 + (i + 1) * a1) // 7 for i in range(6)]
    else:
        table = [a0, a1] + [((5 - i) * a0 + (i + 1) * a1) // 5 for i in range(4)] + [0, 255]
    bits = int.from_bytes(block[2:8], "little")
    return [table[(bits >> (3 * i)) & 0x7] for i in range(16)]


def _decode_dxt3_alpha_block(block: bytes) -> list[int]:
    bits = int.from_bytes(block[0:8], "little")
    return [((bits >> (4 * i)) & 0xF) * 17 for i in range(16)]


def decode_mip0(path: Path) -> tuple[int, int, str, bytearray, bytearray]:
    """Decode mip 0 to (width, height, format, RGB bytes, alpha bytes)."""
    raw = path.read_bytes()
    if raw[:4] != DDS_MAGIC:
        raise DdsError(f"{path.name}: not a DDS file")
    size, flags, height, width = struct.unpack("<4I", raw[4:20])
    if size != HEADER_SIZE:
        raise DdsError(f"{path.name}: header size {size}, expected {HEADER_SIZE}")
    pf = raw[76:108]
    pf_flags, fourcc = struct.unpack("<4xI4s", pf[:12])
    rgb_bits, r_mask, g_mask, b_mask, a_mask = struct.unpack("<I4I", pf[12:32])
    body = raw[4 + HEADER_SIZE:]
    if pf_flags & PF_FOURCC and fourcc == b"DX10":
        raise DdsError(f"{path.name}: DX10 extension headers are not decoded")

    rgb = bytearray(width * height * 3)
    alpha = bytearray(b"\xff" * (width * height))

    if pf_flags & PF_FOURCC and fourcc in (b"DXT1", b"DXT3", b"DXT5"):
        block_bytes = 8 if fourcc == b"DXT1" else 16
        blocks_x = max(1, (width + 3) // 4)
        blocks_y = max(1, (height + 3) // 4)
        need = blocks_x * blocks_y * block_bytes
        if len(body) < need:
            raise DdsError(f"{path.name}: mip 0 wants {need} bytes, file holds {len(body)}")
        for by in range(blocks_y):
            for bx in range(blocks_x):
                offset = (by * blocks_x + bx) * block_bytes
                block = body[offset:offset + block_bytes]
                if fourcc == b"DXT1":
                    texels, mask = _decode_dxt_colour_block(block, opaque_only=False)
                    alphas = [255 if keep else 0 for keep in mask]
                else:
                    texels, _ = _decode_dxt_colour_block(block[8:16], opaque_only=True)
                    alphas = (
                        _decode_dxt3_alpha_block(block[0:8])
                        if fourcc == b"DXT3"
                        else _decode_dxt5_alpha_block(block[0:8])
                    )
                for index in range(16):
                    x = bx * 4 + (index % 4)
                    y = by * 4 + (index // 4)
                    if x >= width or y >= height:
                        continue
                    at = y * width + x
                    rgb[at * 3], rgb[at * 3 + 1], rgb[at * 3 + 2] = texels[index]
                    alpha[at] = alphas[index]
        return width, height, fourcc.decode("ascii"), rgb, alpha

    if pf_flags & PF_RGB and rgb_bits in (24, 32):
        stride = rgb_bits // 8
        need = width * height * stride
        if len(body) < need:
            raise DdsError(f"{path.name}: mip 0 wants {need} bytes, file holds {len(body)}")

        def shift_of(mask: int) -> int:
            return (mask & -mask).bit_length() - 1 if mask else 0

        for at in range(width * height):
            value = int.from_bytes(body[at * stride:at * stride + stride], "little")
            rgb[at * 3] = (value & r_mask) >> shift_of(r_mask)
            rgb[at * 3 + 1] = (value & g_mask) >> shift_of(g_mask)
            rgb[at * 3 + 2] = (value & b_mask) >> shift_of(b_mask)
            if pf_flags & PF_ALPHAPIXELS and a_mask:
                alpha[at] = (value & a_mask) >> shift_of(a_mask)
        return width, height, f"RGB{rgb_bits}", rgb, alpha

    raise DdsError(
        f"{path.name}: unsupported pixel format (flags {pf_flags:#x}, fourCC {fourcc!r})")


# ======================================================================================
# measurement
# ======================================================================================


def _percentile(sorted_values: list[float], percentile: float) -> float:
    """Linear-interpolated percentile over an already sorted list."""
    if not sorted_values:
        return 0.0
    if len(sorted_values) == 1:
        return sorted_values[0]
    position = (percentile / 100.0) * (len(sorted_values) - 1)
    low = int(position)
    high = min(low + 1, len(sorted_values) - 1)
    fraction = position - low
    return sorted_values[low] * (1.0 - fraction) + sorted_values[high] * fraction


def luminance(r: int, g: int, b: int) -> float:
    """Rec.709 relative luminance of an 8-bit sRGB triple, 0..1."""
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0


def measure(path: Path, alpha_floor: float = 0.5, ignore_black: bool = True) -> dict:
    """Every number this lane derives from one colour map."""
    width, height, fmt, rgb, alpha = decode_mip0(path)
    cutoff = int(round(alpha_floor * 255))
    total = width * height
    kept: list[float] = []
    sum_r = sum_g = sum_b = 0
    near_white = 0
    for at in range(total):
        if alpha[at] < cutoff:
            continue
        r = rgb[at * 3]
        g = rgb[at * 3 + 1]
        b = rgb[at * 3 + 2]
        if ignore_black and r < BLACK_FLOOR and g < BLACK_FLOOR and b < BLACK_FLOOR:
            continue
        sum_r += r
        sum_g += g
        sum_b += b
        if r >= NEAR_WHITE and g >= NEAR_WHITE and b >= NEAR_WHITE:
            near_white += 1
        kept.append(luminance(r, g, b))

    count = len(kept)
    kept.sort()
    mean = (
        (round(sum_r / count, 3), round(sum_g / count, 3), round(sum_b / count, 3))
        if count
        else (0.0, 0.0, 0.0)
    )
    return {
        "file": path.name,
        "format": fmt,
        "width": width,
        "height": height,
        "texels": total,
        "measuredTexels": count,
        "alphaFloor": alpha_floor,
        "ignoreBlackBelow": BLACK_FLOOR if ignore_black else None,
        "meanRgb": list(mean),
        "nearWhiteFraction": round(near_white / count, 6) if count else 0.0,
        "luminance": {
            "p02": round(_percentile(kept, 2.0), 6),
            "shadow": round(_percentile(kept, SHADOW_PERCENTILE), 6),
            "p25": round(_percentile(kept, 25.0), 6),
            "midtone": round(_percentile(kept, MIDTONE_PERCENTILE), 6),
            "p75": round(_percentile(kept, 75.0), 6),
            "highlight": round(_percentile(kept, HIGHLIGHT_PERCENTILE), 6),
            "p98": round(_percentile(kept, 98.0), 6),
        },
        "percentiles": {
            "shadow": SHADOW_PERCENTILE,
            "midtone": MIDTONE_PERCENTILE,
            "highlight": HIGHLIGHT_PERCENTILE,
        },
    }


def ramp(
    measurement: dict,
    colour: tuple[float, float, float],
    alpha: float = 0.5,
) -> dict[str, tuple[float, float, float, float]]:
    """The three ``BaseTint*`` float4s that land ``colour`` on this map.

    The midtone anchor **is** the chosen colour: at the map's median luminance the mesh must read
    as the colour its own name promises. The other two anchors are that colour scaled by the
    map's own measured luminance ratios, so a map with little contrast produces a flat ramp and a
    map with a lot produces a wide one. Nothing here is hand-typed per item - the shape comes out
    of the ``.dds`` and only the hue is a ruling.
    """
    lum = measurement["luminance"]
    mid = max(lum["midtone"], 1e-3)
    shadow_ratio = lum["shadow"] / mid
    highlight_ratio = lum["highlight"] / mid

    def scale(factor: float) -> tuple[float, float, float, float]:
        return (
            round(min(1.0, max(0.0, colour[0] * factor)), 6),
            round(min(1.0, max(0.0, colour[1] * factor)), 6),
            round(min(1.0, max(0.0, colour[2] * factor)), 6),
            alpha,
        )

    return {
        "highlight": scale(highlight_ratio),
        "midtone": scale(1.0),
        "shadow": scale(shadow_ratio),
        "highlightRatio": round(highlight_ratio, 6),
        "shadowRatio": round(shadow_ratio, 6),
    }


# ======================================================================================
# CLI
# ======================================================================================


def _parse_colour(text: str) -> tuple[float, float, float]:
    parts = [float(p) for p in text.split(",")]
    if len(parts) != 3:
        raise argparse.ArgumentTypeError("--colour wants three comma-separated floats")
    return parts[0], parts[1], parts[2]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    subs = parser.add_subparsers(dest="command", required=True)

    p_measure = subs.add_parser("measure", help="decode and measure one or more colour maps")
    p_measure.add_argument("files", nargs="+")
    p_measure.add_argument("--json", help="write the measurements to this file")
    p_measure.add_argument("--alpha-floor", type=float, default=0.5)

    p_ramp = subs.add_parser("ramp", help="the BaseTint ramp that lands a colour on a map")
    p_ramp.add_argument("file")
    p_ramp.add_argument("--colour", type=_parse_colour, required=True)
    p_ramp.add_argument("--alpha", type=float, default=0.5)

    args = parser.parse_args(argv)

    if args.command == "measure":
        results = [measure(Path(f), alpha_floor=args.alpha_floor) for f in args.files]
        text = json.dumps(results, indent=2)
        if args.json:
            Path(args.json).write_text(text + "\n", encoding="utf-8")
        print(text)
        return 0

    result = ramp(measure(Path(args.file)), args.colour, args.alpha)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
