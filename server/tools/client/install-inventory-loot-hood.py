"""Install or restore the verified weapon-slot Skin and hoodie menu update.

Defaults to dry run; --apply requires the game closed and saves an exact pack backup.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('inventory_loot_hood_pack', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.__doc__ = __doc__
installer.OLD = 'dd94d065fe56e19d3bb2052656e65ba6ff19171b21998026008ff107a58846f4'
installer.NEW = '59b8c69fd2cd3be8a1a67160f82789b199046fefe6c8fafd1fdd9bdc2c689759'
installer.NAME = 'InventoryWindow.gfx'
installer.PACK = 'Assets_177.pack'
prepare = installer.prepare_update
installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)

if __name__ == '__main__':
    installer.main()
