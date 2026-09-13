#!/usr/bin/env python3
"""Build the bounded loot-bag refresh from the installed August inventory-movement UI."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = '85744a320cf22deb7f48815a48849a61e617ff3897e8fac2436454330cd84329'
CLASS = 'views.inventory.UIInventoryManager'
RELATIVE = Path('scripts/views/inventory/UIInventoryManager.as')


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError('Source UI changed; review/rebase instead of replacing other work')
    destination = out / 'UIRoot.gfx'
    if destination == asset:
        raise ValueError('Output must differ from source')
    out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-selectclass', CLASS, '-export', 'script', str(out / 'source'), str(asset)], check=True)
    source = out / 'source' / RELATIVE
    original = source.read_text(encoding='utf-8')
    source.write_text(module('inventory_refresh_patch', 'patch-inventory-refresh.py').patch(original),
                      encoding='utf-8', newline='\n')
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled),
                          str(out / 'source/scripts')], check=True)
    built = module('inventory_refresh_tags', 'build-helmet-click.py').preserve_tags(raw, compiled.read_bytes())
    destination.write_bytes(built)
    subprocess.run(java + ['-export', 'script', str(out / 'verify'), str(destination)], check=True)
    subprocess.run(['node', str(Path(__file__).with_name('verify-inventory-refresh.cjs')),
                    str(out / 'verify' / RELATIVE)], check=True)
    subprocess.run(['node', str(Path(__file__).with_name('verify-inventory-movement.cjs')),
                    str(out / 'verify/scripts/ui/states/UIState_InGame.as'), str(out / 'verify/scripts')], check=True)
    manifest = {'asset': 'UIRoot.gfx', 'source_sha256': SOURCE_SHA256,
                'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(destination),
                'changed_class': CLASS, 'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
