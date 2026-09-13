#!/usr/bin/env python3
"""Install either verified footwear Shred menu, retaining a complete pack backup.

Install InventoryWindow.gfx and Component_Library.gfx together. Defaults to dry run;
--apply requires the client closed. --restore uses the original install manifest.
"""
import importlib.util
import json
from pathlib import Path
import sys

ASSETS = {
    'InventoryWindow.gfx': ('Assets_177.pack',
        '34d3396db90131b4fa2a087444d5e616997277ecd18d76c49e010d263345557a',
        'aff92e77a35cb44a3fff520ce4e98528b9bf0a19f69a56b66d4ca9a9859f1ced'),
    'Component_Library.gfx': ('Assets_114.pack',
        '75662f3bbd4325d1391fe441c2d0af907ec87e7463dfae068f2e1ad16499daf6',
        '32e50de5291d347fa071d06cc0a6f2581e7150cd39847e010f53415dd95dbf60'),
}

if __name__ == '__main__':
    spec = importlib.util.spec_from_file_location('pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    installer.__doc__ = __doc__
    if '--restore' in sys.argv:
        manifest = json.loads(Path(sys.argv[sys.argv.index('--restore') + 1]).read_text(encoding='utf-8'))
        name = next(name for name, (_, _, digest) in ASSETS.items()
                    if digest == manifest['installed_asset_sha256'])
    else:
        name = Path(sys.argv[1]).name if len(sys.argv) > 1 else ''
    if name not in ASSETS:
        raise SystemExit('Pass InventoryWindow.gfx or Component_Library.gfx, --pack, --backup and --apply')
    installer.NAME = name
    installer.PACK, installer.OLD, installer.NEW = ASSETS[name]
    prepare = installer.prepare_update
    installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)
    installer.main()
