#!/usr/bin/env python3
"""Prepare/install the verified bounty UI, or restore its exact pack backup.

Dry run is the default. --apply requires H1Z1 to be closed. Only UIRoot.gfx in
Assets_060.pack changes; the existing helmet patch in Assets_177.pack is untouched.
Restore refuses a pack changed since installation, preserving later modifications.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import zlib

OLD = 'dac8eb53d1aeb2b50c63e8984f80260f126c99562e1fc2d0a4685623d9696bd4'
NEW = '11c7e0bdbdcb25c592bb16fe4c68c9cd8aef3c7ac31733ce9368d5da9989de6a'
NAME = 'UIRoot.gfx'
PACK = 'Assets_060.pack'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def entries(data):
    result, chunk, previous = [], 0, -1
    while True:
        if chunk <= previous or chunk + 8 > len(data):
            raise ValueError('Invalid pack chunk chain')
        previous = chunk
        next_chunk, count = struct.unpack_from('>II', data, chunk)
        at = chunk + 8
        if count > (len(data) - at) // 17:
            raise ValueError('Invalid pack entry count')
        for _ in range(count):
            if at + 4 > len(data):
                raise ValueError('Truncated name length')
            length, = struct.unpack_from('>I', data, at)
            at += 4
            if length < 1 or length > 512 or at + length + 12 > len(data):
                raise ValueError('Invalid pack name length')
            name = data[at:at + length].decode('ascii')
            at += length
            offset, size, crc = struct.unpack_from('>III', data, at)
            if offset + size > len(data):
                raise ValueError('Asset outside pack')
            result.append((name, at, offset, size, crc))
            at += 12
        if not next_chunk:
            break
        chunk = next_chunk
    return result


def prepare_update(original, patch, old_hash=OLD, new_hash=NEW):
    if digest(patch) != new_hash:
        raise ValueError('Unverified patch asset')
    before = entries(original)
    targets = [entry for entry in before if entry[0] == NAME]
    if len(targets) != 1:
        raise ValueError('Expected exactly one UIRoot.gfx')
    _, index, offset, size, crc = targets[0]
    source = original[offset:offset + size]
    if zlib.crc32(source) != crc:
        raise ValueError('Existing UIRoot CRC does not match the index')
    if digest(source) == new_hash:
        return original, index, len(before) - 1
    if digest(source) != old_hash:
        raise ValueError('UIRoot has another modification; refusing to replace it')
    if len(original) + len(patch) > 0xffffffff:
        raise ValueError('Updated pack exceeds its 32-bit format')
    for _, _, asset_offset, asset_size, _ in before:
        if asset_offset < index + 12 and asset_offset + asset_size > index:
            raise ValueError('Asset data overlaps the modified index entry')
    updated = bytearray(original)
    struct.pack_into('>III', updated, index, len(original), len(patch), zlib.crc32(patch))
    updated.extend(patch)
    if updated[:index] != original[:index] or updated[index + 12:len(original)] != original[index + 12:]:
        raise ValueError('Unexpected change outside the target index')
    for left, right in zip(before, entries(updated), strict=True):
        if left[0] == NAME:
            if bytes(updated[right[2]:right[2] + right[3]]) != patch:
                raise ValueError('Updated UIRoot verification failed')
        elif left != right:
            raise ValueError('Another asset index changed')
    return bytes(updated), index, len(before) - 1


def require_client_closed():
    running = subprocess.check_output([
        'powershell', '-NoProfile', '-Command',
        "@(Get-Process -Name H1Z1* -ErrorAction SilentlyContinue).Count"], text=True)
    if int(running.strip()):
        raise ValueError('Close H1Z1 before changing its asset pack')


def replace_verified(pack, replacement, expected_current_hash):
    descriptor, name = tempfile.mkstemp(prefix=pack.name + '.bounty-', suffix='.tmp', dir=pack.parent)
    staged = Path(name)
    try:
        with os.fdopen(descriptor, 'wb') as output:
            output.write(replacement)
            output.flush()
            os.fsync(output.fileno())
        if digest(staged.read_bytes()) != digest(replacement):
            raise ValueError('Staged pack verification failed')
        require_client_closed()
        if digest(pack.read_bytes()) != expected_current_hash:
            raise ValueError('Pack changed during preparation; refusing to overwrite it')
        os.replace(staged, pack)
    finally:
        if staged.exists():
            staged.unlink()


def restore(manifest_path, apply):
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    pack, saved = Path(manifest['pack']).resolve(), Path(manifest['backup']).resolve()
    if pack.name != PACK or saved.name != PACK or saved == pack:
        raise ValueError('Invalid backup manifest paths')
    original = saved.read_bytes()
    if digest(original) != manifest['original_pack_sha256']:
        raise ValueError('Backup no longer matches the saved original')
    current_hash = digest(pack.read_bytes())
    if current_hash == manifest['original_pack_sha256']:
        print('Original bounty UI pack is already restored')
        return
    if current_hash != manifest['installed_pack_sha256']:
        raise ValueError('Pack changed after installation; refusing to overwrite later changes')
    if apply:
        replace_verified(pack, original, current_hash)
    print(json.dumps({'action': 'restored' if apply else 'restore dry run', 'pack': str(pack)}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', nargs='?', type=Path)
    parser.add_argument('--pack', type=Path)
    parser.add_argument('--backup', type=Path, help='New directory for the full original pack and manifest')
    parser.add_argument('--restore', type=Path, help='Saved manifest.json (dry run unless --apply)')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if args.restore:
        if args.asset or args.pack or args.backup:
            parser.error('--restore cannot be combined with asset/pack/backup')
        restore(args.restore, args.apply)
        return
    if not args.asset or not args.pack or (args.apply and not args.backup):
        parser.error('asset and --pack are required; --apply also requires --backup')
    pack = args.pack.resolve()
    if pack.name != PACK:
        raise ValueError('Expected the August Assets_060.pack')
    original = pack.read_bytes()
    updated, index, other_count = prepare_update(original, args.asset.read_bytes())
    if original == updated:
        print('Verified bounty UI is already installed')
        return
    manifest = {
        'pack': str(pack), 'index_offset': index,
        'original_pack_sha256': digest(original), 'installed_pack_sha256': digest(updated),
        'original_asset_sha256': OLD, 'installed_asset_sha256': NEW,
        'other_assets_unchanged': other_count,
    }
    if args.apply:
        require_client_closed()
        backup = args.backup.resolve()
        backup.mkdir(parents=True, exist_ok=False)
        saved = backup / PACK
        with saved.open('xb') as output:
            output.write(original)
            output.flush()
            os.fsync(output.fileno())
        if digest(saved.read_bytes()) != manifest['original_pack_sha256']:
            raise ValueError('Backup verification failed')
        manifest['backup'] = str(saved)
        with (backup / 'manifest.json').open('x', encoding='utf-8') as output:
            output.write(json.dumps(manifest, indent=2) + '\n')
            output.flush()
            os.fsync(output.fileno())
        replace_verified(pack, updated, manifest['original_pack_sha256'])
    print(json.dumps({'action': 'installed' if args.apply else 'install dry run', **manifest}, indent=2))


if __name__ == '__main__':
    main()
