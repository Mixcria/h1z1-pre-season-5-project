#!/usr/bin/env python3
"""Install build-menu-polish candidates with verified full-pack backups.

Dry run is the default. Every candidate is checked before any pack changes.
Each pack is replaced atomically by the existing guarded installer. --restore
accepts an individual saved pack manifest and refuses to overwrite later edits.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys


def guarded():
    spec = importlib.util.spec_from_file_location(
        'guarded_menu_installer', Path(__file__).with_name('install-bounty-lobby.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def configure(module, name, row):
    module.NAME, module.PACK = name, Path(row['pack']).name
    if not re.fullmatch(r'Assets_\d{3}\.pack', module.PACK):
        raise ValueError('Expected an August asset pack')
    module.OLD, module.NEW = row['sha256'], row['candidate_sha256']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', nargs='?', type=Path)
    parser.add_argument('--backup', type=Path)
    parser.add_argument('--restore', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    module = guarded()
    if args.restore:
        if args.manifest or args.backup:
            parser.error('--restore cannot be combined with manifest or backup')
        saved = json.loads(args.restore.read_text(encoding='utf-8'))
        module.PACK = Path(saved['pack']).name
        if not re.fullmatch(r'Assets_\d{3}\.pack', module.PACK):
            raise ValueError('Expected an August asset pack')
        module.restore(args.restore, args.apply)
        return
    if not args.manifest or (args.apply and not args.backup):
        parser.error('manifest is required; --apply also requires --backup')
    assets = json.loads(args.manifest.read_text(encoding='utf-8'))['assets']
    if set(assets) != {'CustomizationWindow.gfx', 'CharacterSelectWindow.gfx', 'MOTDWidget.gfx'}:
        raise ValueError('Expected the three menu-polish candidates')
    if len({Path(row['pack']).resolve() for row in assets.values()}) != len(assets):
        raise ValueError('This installer expects one candidate per pack')
    prepare = module.prepare_update
    for name, row in assets.items():
        configure(module, name, row)
        prepare(Path(row['pack']).read_bytes(), Path(row['candidate']).read_bytes(), module.OLD, module.NEW)
        print(f'{name}: candidate hash, source hash, CRC and unrelated asset indexes verified', flush=True)
    if args.apply:
        module.require_client_closed()
    for name, row in assets.items():
        configure(module, name, row)
        module.prepare_update = lambda original, patch: prepare(original, patch, module.OLD, module.NEW)
        sys.argv = [__file__, row['candidate'], '--pack', row['pack']]
        if args.backup:
            sys.argv += ['--backup', str(args.backup / Path(row['pack']).stem)]
        if args.apply:
            sys.argv += ['--apply']
        module.main()


if __name__ == '__main__':
    main()
