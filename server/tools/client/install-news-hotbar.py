#!/usr/bin/env python3
"""Install verified news candidates, retaining full original pack backups.

Default is a dry run. --restore accepts one saved pack manifest and refuses to
overwrite subsequent edits, including the withdrawn hotbar experiment's backups.
The game must be closed during installation/restore.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('news_hotbar_installer',
    Path(__file__).with_name('install-menu-polish.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
ALLOWED = {'UIRoot.gfx', 'CharacterSelectWindow.gfx'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', nargs='*', type=Path)
    parser.add_argument('--backup', type=Path)
    parser.add_argument('--restore', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    guard = installer.guarded()
    if args.restore:
        if args.manifest or args.backup:
            parser.error('Use restore separately from installation')
        saved = json.loads(args.restore.read_text(encoding='utf-8'))
        guard.PACK = Path(saved['pack']).name
        if guard.PACK not in {'Assets_060.pack', 'Assets_030.pack', 'Assets_217.pack', 'Assets_232.pack'}:
            raise ValueError('Not a news/hotbar asset pack')
        guard.restore(args.restore, args.apply)
        return
    if not args.manifest or (args.apply and not args.backup):
        parser.error('Manifests are required; --apply also requires a new backup directory')
    assets = {}
    for path in args.manifest:
        rows = json.loads(path.read_text(encoding='utf-8'))['assets']
        if assets.keys() & rows.keys() or not rows.keys() <= ALLOWED:
            raise ValueError('Unexpected or duplicate asset')
        assets.update(rows)
    if len({Path(row['pack']).resolve() for row in assets.values()}) != len(assets):
        raise ValueError('Expected one changed asset per pack')
    prepare = guard.prepare_update
    for name, row in assets.items():
        installer.configure(guard, name, row)
        prepare(Path(row['pack']).read_bytes(), Path(row['candidate']).read_bytes(), guard.OLD, guard.NEW)
        print(f'{name}: source, candidate, CRC and unrelated asset indexes verified', flush=True)
    if args.apply:
        guard.require_client_closed()
    for name, row in assets.items():
        installer.configure(guard, name, row)
        guard.prepare_update = lambda original, patch: prepare(original, patch, guard.OLD, guard.NEW)
        sys.argv = [__file__, row['candidate'], '--pack', row['pack']]
        if args.backup:
            sys.argv += ['--backup', str(args.backup / Path(row['pack']).stem)]
        if args.apply:
            sys.argv += ['--apply']
        guard.main()


if __name__ == '__main__':
    main()
