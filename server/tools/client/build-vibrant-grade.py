#!/usr/bin/env python3
"""Stage a bounded chroma grade for August's Z2 daylight LUT. Never installs.

The complete original DDS must match the reviewed August asset. The pack keeps
its layout and size: only the LUT's RGB bytes and its index CRC change. This is
colour lookup data, not a native screenshot or a claim of ROTK visual parity.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import zlib

HERE = Path(__file__).resolve().parent
PACK = 'Assets_028.pack'
NAME = 'z2_colorkey_day.dds'
ORIGINAL_SHA256 = '7b247d77c7917e6fe49dbd5fb5dfe0807a7e1c20e6af0156ff25734d65f9c2d1'
DEFAULT_PACK = Path(r'C:\Aug2017\Client\Resources\Assets') / PACK
MAX_CHROMA_BOOST = 0.12
SHADOW_UNCHANGED = 20.0
SHADOW_FULL_STRENGTH = 64.0
LUMA_WEIGHTS = (0.2126, 0.7152, 0.0722)

_spec = importlib.util.spec_from_file_location('vibrancy_pack_entries', HERE / 'install-crate-unlock-ten.py')
_pack = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_pack)
entries = _pack.entries


def digest(data):
    return hashlib.sha256(data).hexdigest()


def luma(rgb):
    """Weighted encoded RGB, not a measurement of linear scene luminance."""
    return sum(c * w for c, w in zip(rgb, LUMA_WEIGHTS, strict=True))


def grade_pixel(rgb):
    y = luma(rgb)
    if y <= SHADOW_UNCHANGED or min(rgb) == max(rgb):
        return tuple(rgb)
    t = min(1.0, (y - SHADOW_UNCHANGED) / (SHADOW_FULL_STRENGTH - SHADOW_UNCHANGED))
    scale = 1.0 + MAX_CHROMA_BOOST * t * t * (3.0 - 2.0 * t)
    # Stay on the same chroma ray. Clipping channels independently would alter
    # both hue and luma near the gamut boundary.
    for c in rgb:
        delta = c - y
        if delta > 0:
            scale = min(scale, (255.0 - y) / delta)
        elif delta < 0:
            scale = min(scale, -y / delta)
    return tuple(round(y + scale * (c - y)) for c in rgb)


def grade_dds(original):
    if digest(original) != ORIGINAL_SHA256:
        raise ValueError('DDS differs from the complete reviewed August daylight LUT')
    # Native asset is a 16^3 RGB strip: x = R + 16*B, y = G, uncompressed BGRA.
    expected_header = (124, 528391, 16, 256, 16384, 0, 0, *([0] * 11),
                       32, 65, 0, 32, 0xff0000, 0xff00, 0xff, 0xff000000,
                       4096, 0, 0, 0, 0)
    if (len(original) != 16512 or original[:4] != b'DDS '
            or struct.unpack('<31I', original[4:128]) != expected_header):
        raise ValueError('Unsupported native DDS layout')
    updated = bytearray(original)
    errors, ratios, changed, max_delta = [], [], 0, 0
    for at in range(128, len(original), 4):
        b, g, r, a = original[at:at + 4]
        if a != 255:
            raise ValueError('Unexpected non-opaque native LUT node')
        rgb = (r, g, b)
        new = grade_pixel(rgb)
        if not all(0 <= c <= 255 for c in new):
            raise ValueError('Graded node escaped the RGB gamut')
        updated[at:at + 3] = bytes(reversed(new))
        changed += new != rgb
        errors.append(abs(luma(new) - luma(rgb)))
        max_delta = max(max_delta, *(abs(a - b) for a, b in zip(rgb, new, strict=True)))
        chroma = max(rgb) - min(rgb)
        if chroma:
            ratios.append((max(new) - min(new)) / chroma)
    if updated[:128] != original[:128] or updated[131::4] != original[131::4]:
        raise ValueError('DDS header or alpha changed')
    if max(errors) > 0.500001:
        raise ValueError('Encoded luma changed beyond 8-bit rounding')
    return bytes(updated), {
        'nodes': 4096, 'changed_nodes': changed,
        'maximum_channel_delta': max_delta,
        'maximum_encoded_luma_error': max(errors),
        'mean_absolute_encoded_luma_error': sum(errors) / len(errors),
        'mean_chroma_ratio': sum(ratios) / len(ratios),
        'header_and_alpha_preserved': True,
    }


def overlaps(a, b):
    return a[0] < b[1] and b[0] < a[1]


def prepare_pack(original):
    before = entries(original)
    targets = [e for e in before if e[0] == NAME]
    if len(targets) != 1:
        raise ValueError('Expected exactly one August daylight LUT')
    _, index, offset, size, crc = targets[0]
    source = original[offset:offset + size]
    if zlib.crc32(source) != crc:
        raise ValueError('Original LUT CRC does not match its pack index')
    candidate, metrics = grade_dds(source)
    payload, crc_span = (offset, offset + size), (index + 8, index + 12)
    # Refuse aliased assets/indexes before mutating any bytes, including otherwise
    # well-formed hostile chunk offsets accepted by the shared entry reader.
    chunk = 0
    while True:
        if overlaps(payload, (chunk, chunk + 8)) or overlaps(crc_span, (chunk, chunk + 8)):
            raise ValueError('Target overlaps a pack chunk header')
        chunk = struct.unpack_from('>I', original, chunk)[0]
        if not chunk:
            break
    for name, at, pos, length, checksum in before:
        if overlaps(payload, (at - len(name) - 4, at + 12)):
            raise ValueError('LUT data overlaps a pack index entry')
        if overlaps(crc_span, (pos, pos + length)):
            raise ValueError('Asset overlaps the target index CRC')
        if name != NAME and overlaps(payload, (pos, pos + length)):
            raise ValueError('Another asset overlaps the LUT')
        if zlib.crc32(original[pos:pos + length]) != checksum:
            raise ValueError('Pack contains an invalid asset CRC: ' + name)
    updated = bytearray(original)
    updated[offset:offset + size] = candidate
    struct.pack_into('>I', updated, index + 8, zlib.crc32(candidate))
    cursor = 0
    for start, end in sorted((payload, crc_span)):
        if updated[cursor:start] != original[cursor:start]:
            raise ValueError('Bytes outside target changed')
        cursor = end
    if updated[cursor:] != original[cursor:] or len(updated) != len(original):
        raise ValueError('Pack size or trailing bytes changed')
    after = entries(updated)
    for left, right in zip(before, after, strict=True):
        if left[0] == NAME:
            if left[:4] != right[:4] or right[4] != zlib.crc32(candidate):
                raise ValueError('Unexpected target index change')
        elif left != right or original[left[2]:left[2] + left[3]] != updated[right[2]:right[2] + right[3]]:
            raise ValueError('Unrelated pack entry or asset changed')
    return bytes(updated), source, candidate, {
        **metrics, 'other_assets_preserved': len(before) - 1,
        'pack_length_preserved': len(original), 'asset_offset': offset,
        'crc_offset': index + 8,
    }


def stage(pack, out):
    pack, out = Path(pack).resolve(), Path(out).resolve()
    if out.is_relative_to(pack.parent) or (out / PACK).resolve() == pack:
        raise ValueError('Stage output must be outside the source asset-pack directory')
    if out.exists():
        raise ValueError('Stage directory already exists; choose a new output directory')
    original = pack.read_bytes()
    updated, source, candidate, metrics = prepare_pack(original)
    manifest = {
        'purpose': 'Cranberry August Z2 daylight chroma grade; staged only',
        'native_visual_acceptance': 'pending', 'rotk_visual_parity': 'not claimed',
        'source_pack': str(pack), 'asset': NAME,
        'original_pack_sha256': digest(original), 'candidate_pack_sha256': digest(updated),
        'original_dds_sha256': digest(source), 'candidate_dds_sha256': digest(candidate),
        'maximum_chroma_boost': MAX_CHROMA_BOOST,
        'shadow_encoded_luma_unchanged_through': SHADOW_UNCHANGED,
        'shadow_encoded_luma_full_strength_at': SHADOW_FULL_STRENGTH,
        'metrics': metrics,
    }
    out.mkdir(parents=True, exist_ok=False)
    for name, data in ((PACK, updated), ('original-' + NAME, source), (NAME, candidate)):
        target = out / name
        with target.open('xb') as stream:
            stream.write(data)
        if digest(target.read_bytes()) != digest(data):
            raise ValueError('Staged artifact read-back failed: ' + name)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pack', type=Path, default=DEFAULT_PACK)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(stage(args.pack, args.out), indent=2))


if __name__ == '__main__':
    main()
