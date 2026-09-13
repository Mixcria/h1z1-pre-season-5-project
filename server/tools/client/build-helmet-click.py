#!/usr/bin/env python3
"""Build the carried-helmet click UI from the owner's extracted August asset.

Requires JPEXS 26.2.1 and git. Recompiles only InventoryWindow's script, then
transplants its DoABC tag into the original GFx, retaining every other tag and
the original trailing bytes. Outputs a patch asset; never changes the client.
"""
import argparse
import hashlib
from pathlib import Path
import struct
import subprocess
import zlib

STOCK_SHA256 = 'f469203efc5cd6a7f965f2bae5d0f4ecb8ff18cbd94050457b7265422aa619a2'


def unpack(raw):
    if raw[:3] != b'CFX':
        raise ValueError('Expected the August compressed GFx asset')
    data = raw[:8] + zlib.decompress(raw[8:])
    if len(data) < struct.unpack_from('<I', data, 4)[0]:
        raise ValueError('Truncated uncompressed GFx')
    return data


def tags(data):
    at = 8 + (5 + 4 * (data[8] >> 3) + 7) // 8 + 4
    result = []
    while at < len(data):
        start = at
        header = struct.unpack_from('<H', data, at)[0]
        at += 2
        size = header & 63
        if size == 63:
            size = struct.unpack_from('<I', data, at)[0]
            at += 4
        at += size
        if at > len(data):
            raise ValueError('Truncated GFx tag')
        result.append((header >> 6, start, at))
        if header >> 6 == 0:
            return result
    raise ValueError('Missing GFx End tag')


def preserve_tags(stock, compiled):
    original, replacement = unpack(stock), unpack(compiled)
    left, right = tags(original), tags(replacement)
    if len(left) != len(right):
        raise ValueError('Compiler changed the tag count')
    changes = []
    for a, b in zip(left, right):
        if a[0] != b[0]:
            raise ValueError('Compiler changed a tag type')
        if original[a[1]:a[2]] != replacement[b[1]:b[2]]:
            if a[0] != 82:
                raise ValueError('Compiler changed a non-script tag')
            changes.append((a, b))
    if len(changes) != 1:
        raise ValueError('Expected exactly one changed DoABC tag')
    a, b = changes[0]
    body = original[8:a[1]] + replacement[b[1]:b[2]] + original[a[2]:]
    # The shipped asset includes 1,943 bytes after its declared length/End tag.
    # Preserve that trailer while increasing the declared length by the tag delta.
    declared = struct.unpack_from('<I', stock, 4)[0] + (b[2] - b[1]) - (a[2] - a[1])
    return stock[:4] + struct.pack('<I', declared) + zlib.compress(body)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    raw = args.asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != STOCK_SHA256:
        raise ValueError('Unrecognized source asset; do not overwrite another UI patch')
    asset, out = args.asset.resolve(), args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-selectclass', 'views.inventory.InventoryWindow',
                          '-export', 'script', str(out / 'source'), str(asset)], check=True)
    scripts = out / 'source' / 'scripts'
    patch = Path(__file__).with_name('helmet-click.patch').resolve()
    subprocess.run(['git', 'apply', '--unsafe-paths', '-p0', str(patch)], cwd=scripts, check=True)
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset),
                          str(compiled), str(scripts)], check=True)
    built = preserve_tags(raw, compiled.read_bytes())
    destination = out / 'InventoryWindow.gfx'
    if destination == asset:
        raise ValueError('Output must differ from source')
    destination.write_bytes(built)
    subprocess.run(java + ['-selectclass', 'views.inventory.InventoryWindow',
                          '-export', 'script', str(out / 'verify'), str(destination)], check=True)
    source = (out / 'verify/scripts/views/inventory/InventoryWindow.as').read_text()
    for required in ('import ui.bindings.UIBindingLoadouts;', 'equipCarriedHelmetOnClick',
                     'item.isArmor', 'worn.length == 0', '"-1",11,1'):
        if required not in source:
            raise ValueError(f'Compiled UI is missing {required}')
    print(f'Built {destination}: {len(built)} bytes, SHA256 {hashlib.sha256(built).hexdigest()}')
    print('Verified one changed DoABC tag; all graphics, other tags and trailing bytes preserved.')


if __name__ == '__main__':
    main()
