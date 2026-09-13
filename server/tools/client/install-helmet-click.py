#!/usr/bin/env python3
"""Install the verified helmet-click GFx with an exact pack backup and manifest.

Close H1Z1 first. Only InventoryWindow.gfx's index offset/size/CRC changes; its
new contents are appended. All other asset offsets and bytes remain intact.
Restore the saved Assets_177.pack to undo. Rebuilds with other hashes must be
reviewed and this installer updated before use.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'pack'))
from packread import read_pack_index, read_asset_bytes

OLD = 'f469203efc5cd6a7f965f2bae5d0f4ecb8ff18cbd94050457b7265422aa619a2'
NEW = '88317f538ab870f5e98aca9f09a3c664437345a502a401bd822fab7af6e90c1d'
NAME = 'InventoryWindow.gfx'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--pack', type=Path, required=True)
    parser.add_argument('--backup', type=Path, required=True)
    args = parser.parse_args()
    running = subprocess.check_output(['powershell', '-NoProfile', '-Command',
                                      "@(Get-Process -Name H1Z1 -ErrorAction SilentlyContinue).Count"], text=True)
    if int(running.strip()) != 0:
        raise ValueError('Close H1Z1 before replacing its asset pack')
    patch = args.asset.read_bytes()
    if digest(patch) != NEW:
        raise ValueError('Unverified patch asset')
    pack = args.pack.resolve()
    if pack.name != 'Assets_177.pack':
        raise ValueError('Expected the August Assets_177.pack')
    entries = list(read_pack_index(pack))
    target, = [entry for entry in entries if entry.name == NAME]
    if digest(read_asset_bytes(target)) == NEW:
        print('Helmet click UI is already installed')
        return
    if digest(read_asset_bytes(target)) != OLD:
        raise ValueError('InventoryWindow has another modification; refusing to replace it')
    original = pack.read_bytes()
    chunk, found = 0, []
    while True:
        next_chunk, count = struct.unpack_from('>II', original, chunk)
        at = chunk + 8
        for _ in range(count):
            length, = struct.unpack_from('>I', original, at)
            at += 4
            name = original[at:at + length].decode('ascii')
            at += length
            if name == NAME:
                found.append(at)
            at += 12
        if not next_chunk:
            break
        chunk = next_chunk
    index, = found
    assert struct.unpack_from('>III', original, index) == (target.offset, target.size, target.crc32)
    updated = bytearray(original)
    struct.pack_into('>III', updated, index, len(original), len(patch), zlib.crc32(patch))
    updated.extend(patch)
    assert updated[:index] == original[:index]
    assert updated[index + 12:len(original)] == original[index + 12:]
    backup = args.backup.resolve()
    backup.mkdir(parents=True, exist_ok=False)
    saved = backup / pack.name
    shutil.copy2(pack, saved)
    assert digest(saved.read_bytes()) == digest(original)
    temporary = pack.with_name(pack.name + '.helmet-click.tmp')
    with temporary.open('xb') as output:
        output.write(updated)
    staged_entries = list(read_pack_index(temporary))
    for before, after in zip(entries, staged_entries, strict=True):
        assert before.name == after.name
        if before.name != NAME:
            assert (before.offset, before.size, before.crc32) == (after.offset, after.size, after.crc32)
        else:
            assert read_asset_bytes(after) == patch
    manifest = {'pack': str(pack), 'backup': str(saved), 'index_offset': index,
                'original_pack_sha256': digest(original), 'installed_pack_sha256': digest(updated),
                'original_asset_sha256': OLD, 'installed_asset_sha256': NEW,
                'other_assets_unchanged': len(entries) - 1}
    (backup / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    if digest(pack.read_bytes()) != digest(original):
        raise ValueError('Pack changed during preparation; refusing to overwrite it')
    temporary.replace(pack)
    assert digest(pack.read_bytes()) == manifest['installed_pack_sha256']
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
