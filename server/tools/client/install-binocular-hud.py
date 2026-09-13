#!/usr/bin/env python3
"""Install one verified binocular HUD asset using the shared backup/restore pack installer.

Use --hud ammo followed by the usual asset/--pack/--backup options.
Dry run is the default; --apply requires the client to be closed. Restore uses the
same --hud selection plus --restore <manifest.json> [--apply].
"""
import argparse
import importlib.util
from pathlib import Path
import sys

PATCHES = {
    'ammo': ('HudPlayerResourcesWindow.gfx', 'Assets_131.pack',
             'c9dabbb639cbe7124ad9803864fe9b7af92467786678937d5993b6136f16bacb',
             'd41966ec348dc120709430230a4c454af64413daa2e397bee45988f381af26e2'),
}


def installer_for(hud):
    spec = importlib.util.spec_from_file_location('guarded_pack_installer',
                                                Path(__file__).with_name('install-bounty-lobby.py'))
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    installer.NAME, installer.PACK, installer.OLD, installer.NEW = PATCHES[hud]
    prepare = installer.prepare_update
    # Defaults in the shared function were bound when its module loaded.
    installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)
    return installer


def main():
    parser = argparse.ArgumentParser(description=__doc__, add_help=False)
    parser.add_argument('--hud', choices=PATCHES, required=True)
    args, remaining = parser.parse_known_args()
    sys.argv = [sys.argv[0], *remaining]
    installer_for(args.hud).main()


if __name__ == '__main__':
    main()
