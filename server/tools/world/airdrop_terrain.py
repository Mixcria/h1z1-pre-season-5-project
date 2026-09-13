#!/usr/bin/env python3
"""Extract the August client's CNK3 heightfield samples for Z2's playable +/-4096m.

Requires Python 3.12 and cnkdec (the pycnkdec library); no client assets are modified.
CNK3 was independently decoded from client 142d55910 / 142d50cf0. Unlike CNK2,
texture images live in CTG files: the tile footer is three u32s, u32 byte count, bytes.
The 16 65x65 PxHeightField samples retain signed height and both material/tess bytes.
File names use (worldZ/64, worldX/64), confirmed against the client transform and
ground vehicle placements. Height is signed i16 /32 metres, sample spacing is 1m.

Output is a deterministic indexed collection of gzip-compressed original chunk samples.
Each chunk remains independent so the server need only inflate queried terrain.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MAGIC = b"CBAHT01\0"
GENERATOR = "tools/world/airdrop_terrain.py"


def file_sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check_output(output: Path) -> int:
    """Check the standalone provenance gate without importing extraction dependencies."""
    try:
        report = json.loads(output.with_suffix(".provenance.json").read_text(encoding="utf-8"))
        if not isinstance(report, dict):
            raise ValueError("Provenance report must be a JSON object")
        expected = {
            "grade": "CLIENT",
            "generator": GENERATOR,
            "generatorSha256": file_sha256(Path(__file__)),
            "outputSha256": file_sha256(output),
            "outputBytes": output.stat().st_size,
        }
        stale = [key for key, value in expected.items() if report.get(key) != value]
        if stale:
            raise ValueError("Stale terrain provenance: " + ", ".join(stale))
    except (OSError, ValueError) as error:
        print(f"Terrain check FAILED: {error}", file=sys.stderr)
        return 1
    print(f"Terrain check passed: {output} ({expected['outputBytes']} bytes, CLIENT)")
    return 0


def height_samples(blob: bytes, decompress) -> bytes:
    if len(blob) < 16 or blob[:4] != b"CNK0" or struct.unpack_from("<I", blob, 4)[0] != 3:
        raise ValueError("Expected the August CNK0 v3 heightfield")
    unpacked, packed = struct.unpack_from("<II", blob, 8)
    if len(blob) != 16 + packed:
        raise ValueError("CNK compressed length mismatch")
    data = decompress(blob[16:], unpacked)
    if len(data) != unpacked:
        raise ValueError("CNK decompressed length mismatch")
    offset = 0

    def u32():
        nonlocal offset
        value = struct.unpack_from("<I", data, offset)[0]
        offset += 4
        return value

    tiles = u32()
    if tiles != 16:
        raise ValueError(f"Expected all 16 terrain tiles, found {tiles}")
    coordinates = []
    for _ in range(tiles):
        z, x, _, _, ecos = struct.unpack_from("<iiiiI", data, offset)
        offset += 20
        coordinates.append((z, x))
        if ecos > 4096:
            raise ValueError("Invalid eco count")
        for _ in range(ecos):
            u32()  # eco id
            floras = u32()
            if floras > 4096:
                raise ValueError("Invalid flora count")
            for _ in range(floras):
                layers = u32()
                offset += layers * 8
                if offset > len(data):
                    raise ValueError("Invalid flora sample count")
        # CNK version3 relocates texture image bytes into the CTG sidecar.
        # 142d50cf0: +e4, +e8, +114; then +194 byte-array length.
        u32()
        u32()
        u32()
        texture_layers = u32()
        offset += texture_layers

    first_z, first_x = coordinates[0]
    expected = [(first_z + z, first_x + x) for z in range(4) for x in range(4)]
    if coordinates != expected:
        raise ValueError("Unexpected terrain tile order")
    grid, count = u32(), u32()
    if grid != 65 or count != 16 * 65 * 65:
        raise ValueError(f"Unexpected height grid {grid}/{count}")
    end = offset + 4 * count
    if end > len(data):
        raise ValueError("Truncated height samples")
    return data[offset:end]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Read-only binary and generator provenance check; no decoder or client assets required")
    parser.add_argument("--deps", type=Path, help="Directory containing the compiled cnkdec Python package")
    parser.add_argument("--assets", type=Path, help="Client asset packs; defaults to C:/Aug2017/Client/Resources/Assets")
    parser.add_argument("--output", type=Path, default=ROOT / "src/Cranberry.Zone/Data/Loot/z2-terrain.bin")
    args = parser.parse_args()
    if args.check:
        return check_output(args.output)
    sys.path.insert(0, str(ROOT / "tools" / "pack"))
    import packread

    if args.deps:
        sys.path.insert(0, str(args.deps))
    from cnkdec import Decompressor

    entries, _ = packread.build_index(packread.find_packs(args.assets or packread.DEFAULT_ASSETS_DIR))
    index = {entry.name: entry for entry in entries}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    source_hash = hashlib.sha256()
    min_height, max_height = 32767, -32768
    count = 0
    with args.output.open("wb") as output:
        output.write(MAGIC)
        output.write(struct.pack("<IiiII", 1, -4096, 4096, 256, 32 * 32))
        for z in range(-64, 64, 4):
            for x in range(-64, 64, 4):
                name = f"Z2_{z}_{x}_0.cnk"
                blob = packread.read_asset_bytes(index[name])
                source_hash.update(name.encode("ascii") + b"\0")
                source_hash.update(hashlib.sha256(blob).digest())
                samples = height_samples(blob, Decompressor().decompress)
                hs = [value[0] for value in struct.iter_unpack("<hBB", samples)]
                min_height, max_height = min(min_height, min(hs)), max(max_height, max(hs))
                packed = gzip.compress(samples, compresslevel=6, mtime=0)
                output.write(struct.pack("<iiI", x * 64, z * 64, len(packed)))
                output.write(packed)
                count += 1
            print(f"Terrain: {count}/1024 chunks", flush=True)
    report = {
        "grade": "CLIENT",
        "clientBuild": "0.0.118.208059",
        "generator": GENERATOR,
        "generatorSha256": file_sha256(Path(__file__)),
        "chunks": count,
        "bounds": [-4096, 4096],
        "sampleSpacingMetres": 1,
        "heightScale": 1 / 32,
        "heightRangeMetres": [min_height / 32, max_height / 32],
        "sourceDigestAlgorithm": "sha256(concat(ascii(assetName),NUL,sha256(assetBytes))) in Z-major order",
        "sourceDigest": source_hash.hexdigest(),
        "outputSha256": file_sha256(args.output),
        "outputBytes": args.output.stat().st_size,
    }
    args.output.with_suffix(".provenance.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
