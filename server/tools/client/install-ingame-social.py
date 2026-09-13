#!/usr/bin/env python3
"""Install a verified build-ingame-social manifest, retaining a complete pack backup.

Dry run is the default. --apply requires a new --backup directory and a closed
H1Z1 client. Existing later client changes are never overwritten.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', nargs='?', type=Path)
    parser.add_argument('--pack', type=Path)
    parser.add_argument('--backup', type=Path)
    parser.add_argument('--restore', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('social_pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    installer.NAME, installer.PACK = 'UIRoot.gfx', 'Assets_060.pack'
    if args.restore:
        if args.manifest or args.pack or args.backup:
            parser.error('--restore cannot be combined with installation arguments')
        installer.restore(args.restore, args.apply)
        return
    if not args.manifest or not args.pack or (args.apply and not args.backup):
        parser.error('manifest and --pack are required; --apply also requires --backup')
    manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
    if manifest['asset'] != installer.NAME or manifest['pack'] != installer.PACK \
        or not all(manifest.get(key) is True for key in ('non_script_tags_preserved', 'unrelated_methods_preserved', 'handler_tests_passed')):
        raise ValueError('Expected a fully verified in-game social build')
    installer.OLD, installer.NEW = manifest['source_sha256'], manifest['output_sha256']
    prepare = installer.prepare_update
    installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)
    sys.argv = [__file__, manifest['output'], '--pack', str(args.pack)]
    if args.backup:
        sys.argv += ['--backup', str(args.backup)]
    if args.apply:
        sys.argv += ['--apply']
    installer.main()


if __name__ == '__main__':
    main()
