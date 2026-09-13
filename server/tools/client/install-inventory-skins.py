"""Install/restore the verified InventoryWindow weapon Skin submenu.

Dry run is the default. Apply requires the game closed and preserves the carried-helmet fix,
all unrelated Assets_177 entries and an exact backup with hash-guarded restore.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('inventory_skins_pack', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.__doc__ = __doc__
installer.OLD = '9f78820b621b7a3965cf2a131244a90e985c449d23e9707b162a17300252d50c'
installer.NEW = '88d415d47d56fd0d2eb5064936d31619ea59398a3959daccbbc71f7ef292c45f'
installer.NAME = 'InventoryWindow.gfx'
installer.PACK = 'Assets_177.pack'
prepare = installer.prepare_update
installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)

if __name__ == '__main__':
    installer.main()
