#!/usr/bin/env python3
"""Withdrawn disk patch: inspect historical bytes or restore a saved executable.

Installation caused an August-client startup failure on 2026-09-06. The command
refuses new --apply installations; only manifest-based restoration may write disk.

One guarded instruction byte skips the interaction routine's reload cancellation.
Firing, weapon switching, the reload acknowledgement gate, and server ammo timers remain.
The entire affected function must match the reviewed August build. Other executable
patches are retained, including when restoring this individual patch.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile

IMAGE_BASE = 0x140000000
FUNCTION_VA = 0x1411C6D70
PATCH_VA = 0x1411C6ECC
BRANCH_TARGET = 0x1411C6EF7
ORIGINAL_FUNCTION = bytes.fromhex('''
40535657415441564883ec30488bf1498bf94881c1200300004d8be04c8bf2bb01000000
488b01ff90a800000084c00f8591010000488b8ed81800000fb6c1c0e80684c30f857c
01000048c1e91b84cb0f8570010000440fb6c34c897c247033d2488bcee81260e5fe49
8b0e488d54246048894c24604c8bf8488b0d4026da02e80ffbe5fe4c8bf04885c00f84
30010000488bd0488bcee8f94eebfe84c00f841d010000498b16498bce48896c2468ff
5250488b16488bce488be8ff928804000084c0742b488bcee83f6cedfe84c0751f4885
ff0f84a9000000488d15efe4f601488bcfe84e57ebfe33dbe9cc0000004885ed743483
bd48430000007f2b488b06488bceff90d005000084c0751b4885ff7470488d15568701
02488bcfe81557ebfe33dbe99300000083be080900000275184885ff744c488d156287
0102488bcfe8f156ebfe33dbeb724d85ff7438418b87ec00000083e80a83f803772983
bd48430000007f20488bcee89b38ebfe4885ff740f488d1535870102488bcfe8b456eb
fe33dbeb35498b8ed0010000488d54246048894c24604183c9ff0fb68c248000000045
0bc1884c2428488b0d7e27da024c89642420e8f9c8e6fe488b6c24684c8b7c24708b
c34883c430415e415c5f5e5bc3
''')
PATCH_INDEX = PATCH_VA - FUNCTION_VA
PATCHED_FUNCTION = ORIGINAL_FUNCTION[:PATCH_INDEX] + b'\xeb' + ORIGINAL_FUNCTION[PATCH_INDEX + 1:]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def function_offset(data):
    """Map the complete reviewed function through a validated AMD64 PE .text section."""
    try:
        if data[:2] != b'MZ':
            raise ValueError('Expected an August AMD64 PE executable')
        pe, = struct.unpack_from('<I', data, 0x3c)
        if data[pe:pe + 4] != b'PE\0\0':
            raise ValueError('Invalid PE signature')
        machine, count = struct.unpack_from('<HH', data, pe + 4)
        optional_size, = struct.unpack_from('<H', data, pe + 20)
        optional = pe + 24
        magic, = struct.unpack_from('<H', data, optional)
        base, = struct.unpack_from('<Q', data, optional + 24)
        if machine != 0x8664 or magic != 0x20b or base != IMAGE_BASE or optional_size < 112:
            raise ValueError('Executable does not match the August AMD64 image layout')
        rva = FUNCTION_VA - base
        matches = []
        for index in range(count):
            at = optional + optional_size + index * 40
            name = data[at:at + 8].rstrip(b'\0')
            _, virtual, raw_size, raw = struct.unpack_from('<IIII', data, at + 8)
            flags, = struct.unpack_from('<I', data, at + 36)
            if name == b'.text' and flags & 0x20000000 and virtual <= rva:
                relative = rva - virtual
                if relative + len(ORIGINAL_FUNCTION) <= raw_size:
                    offset = raw + relative
                    if offset + len(ORIGINAL_FUNCTION) <= len(data):
                        matches.append(offset)
        if len(matches) != 1:
            raise ValueError('Reviewed interaction function is not in one backed executable .text section')
        return matches[0]
    except struct.error as error:
        raise ValueError('Truncated PE executable') from error


def prepare_update(original, restore=False):
    offset = function_offset(original)
    function = original[offset:offset + len(ORIGINAL_FUNCTION)]
    if function not in (ORIGINAL_FUNCTION, PATCHED_FUNCTION):
        raise ValueError('Interaction function differs from the reviewed August bytes; refusing to modify it')
    wanted = ORIGINAL_FUNCTION if restore else PATCHED_FUNCTION
    updated = bytearray(original)
    updated[offset + PATCH_INDEX] = wanted[PATCH_INDEX]
    return bytes(updated), offset + PATCH_INDEX


def require_client_closed():
    running = subprocess.check_output([
        'powershell', '-NoProfile', '-Command',
        "@(Get-Process -Name H1Z1* -ErrorAction SilentlyContinue).Count"], text=True)
    if int(running.strip()):
        raise ValueError('Close H1Z1 before changing its executable')


def replace_verified(target, replacement, expected_current_hash):
    descriptor, name = tempfile.mkstemp(prefix=target.name + '.loot-reload-', suffix='.tmp', dir=target.parent)
    staged = Path(name)
    try:
        with os.fdopen(descriptor, 'wb') as output:
            output.write(replacement)
            output.flush()
            os.fsync(output.fileno())
        if digest(staged.read_bytes()) != digest(replacement):
            raise ValueError('Staged executable verification failed')
        require_client_closed()
        if digest(target.read_bytes()) != expected_current_hash:
            raise ValueError('Executable changed during preparation; refusing to overwrite it')
        os.replace(staged, target)
    finally:
        if staged.exists():
            staged.unlink()


def install(target, backup=None, apply=False):
    target = target.resolve()
    original = target.read_bytes()
    updated, offset = prepare_update(original)
    if original == updated:
        print('Verified loot-during-reload patch is already installed')
        return
    manifest = {
        'patch': 'august-loot-during-reload-v1', 'executable': str(target),
        'file_offset': offset, 'virtual_address': hex(PATCH_VA),
        'original_byte': '77', 'installed_byte': 'eb',
        'original_sha256': digest(original), 'installed_sha256': digest(updated),
        'reviewed_function_sha256': digest(ORIGINAL_FUNCTION),
    }
    if apply:
        if backup is None:
            raise ValueError('--apply requires a new --backup directory')
        require_client_closed()
        backup = backup.resolve()
        backup.mkdir(parents=True, exist_ok=False)
        saved = backup / target.name
        with saved.open('xb') as output:
            output.write(original)
            output.flush()
            os.fsync(output.fileno())
        if digest(saved.read_bytes()) != manifest['original_sha256']:
            raise ValueError('Backup verification failed')
        manifest['backup'] = str(saved)
        with (backup / 'manifest.json').open('x', encoding='utf-8') as output:
            output.write(json.dumps(manifest, indent=2) + '\n')
            output.flush()
            os.fsync(output.fileno())
        replace_verified(target, updated, manifest['original_sha256'])
    print(json.dumps({'action': 'installed' if apply else 'install dry run', **manifest}, indent=2))


def restore(manifest_path, apply=False):
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    if manifest.get('patch') != 'august-loot-during-reload-v1':
        raise ValueError('Manifest is not for the loot-during-reload patch')
    target, saved = Path(manifest['executable']).resolve(), Path(manifest['backup']).resolve()
    if saved == target or saved.name != target.name:
        raise ValueError('Invalid backup manifest paths')
    original = saved.read_bytes()
    if digest(original) != manifest['original_sha256']:
        raise ValueError('Backup no longer matches the saved original')
    expected, offset = prepare_update(original)
    if original == expected or digest(expected) != manifest['installed_sha256'] or offset != manifest['file_offset']:
        raise ValueError('Backup and manifest do not describe this patch')
    current = target.read_bytes()
    updated, current_offset = prepare_update(current, restore=True)
    if current_offset != offset:
        raise ValueError('Executable section layout changed after installation')
    if apply and current != updated:
        replace_verified(target, updated, digest(current))
    print(json.dumps({'action': 'restored' if apply else 'restore dry run', 'executable': str(target),
                      'other_changes_preserved': True, 'sha256': digest(updated)}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path)
    parser.add_argument('--backup', type=Path, help='New directory for the current executable and manifest')
    parser.add_argument('--restore', type=Path, help='Saved manifest.json; reverses only this one-byte patch')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if args.restore:
        if args.exe or args.backup:
            parser.error('--restore cannot be combined with --exe or --backup')
        restore(args.restore, args.apply)
    else:
        if args.apply:
            parser.error('Disk installation was withdrawn after a verified startup failure; use the deferred live helper')
        if not args.exe or (args.apply and not args.backup):
            parser.error('--exe is required; --apply also requires --backup')
        install(args.exe, args.backup, args.apply)


if __name__ == '__main__':
    main()
