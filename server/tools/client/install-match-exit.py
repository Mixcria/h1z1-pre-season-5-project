"""Install a verified match-exit candidate, with full pack backup and concurrent-change guards."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', type=Path)
    parser.add_argument('--backup', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
    if manifest['asset'] != 'UIRoot.gfx' or manifest['pack'] != 'Assets_060.pack':
        raise ValueError('Unexpected match-exit asset')
    asset, pack = Path(manifest['output']), Path(manifest['pack_path'])
    if hashlib.sha256(asset.read_bytes()).hexdigest() != manifest['output_sha256']:
        raise ValueError('Candidate changed')
    if hashlib.sha256(pack.read_bytes()).hexdigest() != manifest['source_pack_sha256']:
        raise ValueError('Installed pack changed; rebuild on the current asset')
    subprocess.run(['node', str(Path(__file__).with_name('verify-match-exit.cjs')),
        str(args.manifest.parent/'after/scripts')], check=True)
    spec = importlib.util.spec_from_file_location('exit_installer', Path(__file__).with_name('install-bounty-lobby.py'))
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    installer.NAME = 'UIRoot.gfx'
    installer.PACK = 'Assets_060.pack'
    installer.OLD = manifest['source_sha256']
    installer.NEW = manifest['output_sha256']
    prepare = installer.prepare_update
    installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)
    sys.argv = [sys.argv[0], str(asset), '--pack', str(pack)]
    if args.backup: sys.argv += ['--backup', str(args.backup)]
    if args.apply: sys.argv += ['--apply']
    installer.main()


if __name__ == '__main__':
    main()
