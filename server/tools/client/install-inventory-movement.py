"""Install/restore the reviewed inventory movement UI without changing other assets.

Omit --apply for inspection. The common pack installer requires the client closed,
verifies both asset hashes, backs up the full pack and preserves unrelated entries.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('inventory_movement_pack', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.OLD = '2527f43f62afc3e36938d1731085b88cd550981e2c86c829a68a2266f4ee1a18'
installer.NEW = '85744a320cf22deb7f48815a48849a61e617ff3897e8fac2436454330cd84329'
PREVIOUS = 'bcb6246d5a658e06cbcb75bf7a4ca29690a6647fb5af7e64631b38deb214b8b9'
prepare = installer.prepare_update


def prepare_inventory(original, patch):
    targets = [entry for entry in installer.entries(original) if entry[0] == installer.NAME]
    if len(targets) != 1:
        raise ValueError('Expected exactly one UIRoot.gfx')
    _, _, offset, size, _ = targets[0]
    current = installer.digest(original[offset:offset + size])
    if current not in (installer.OLD, PREVIOUS, installer.NEW):
        raise ValueError('UIRoot has an unrecognized modification; refusing to overwrite it')
    installer.OLD = current
    return prepare(original, patch, current, installer.NEW)


installer.prepare_update = prepare_inventory

if __name__ == '__main__':
    installer.main()
