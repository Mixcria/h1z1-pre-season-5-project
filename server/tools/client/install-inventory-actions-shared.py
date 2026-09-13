"""Install the shared inventory action menu, preserving the authored component symbols.

Install together with install-inventory-loot-hood.py. The game must be closed.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('shared_actions_pack', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.__doc__ = __doc__
installer.OLD = '6dbf6ecff445aa87f90eb3044721360651be905225de662b18046102dbc42699'
installer.NEW = 'f2c50bec4db9879171528a6c0164f204637c25e5e0d78429e5b1b3d73edae269'
installer.NAME = 'Component_Library.gfx'
installer.PACK = 'Assets_114.pack'
prepare = installer.prepare_update
installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)

if __name__ == '__main__':
    installer.main()
