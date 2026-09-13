"""Install the verified loot-bag refresh UI, preserving the installed movement correction.

Omit --apply for inspection. Installation requires the client closed and saves
the original pack; unknown later modifications are refused.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('inventory_refresh_pack', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.OLD = '85744a320cf22deb7f48815a48849a61e617ff3897e8fac2436454330cd84329'
installer.NEW = '3ea539539dd032687eb1ab14f28b3303bcde1f1227f20d2138aec066c7f3aed4'
prepare = installer.prepare_update
installer.prepare_update = lambda original, patch: prepare(original, patch, installer.OLD, installer.NEW)

if __name__ == '__main__':
    installer.main()
