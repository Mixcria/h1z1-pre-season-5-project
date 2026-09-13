"""Install the verified native console input correction, preserving the current UI.

Dry run by default. --apply requires the client closed, backs up the whole pack,
checks exact source/output hashes and verifies every sibling asset is unchanged.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    'console_pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.OLD = '3ea539539dd032687eb1ab14f28b3303bcde1f1227f20d2138aec066c7f3aed4'
installer.NEW = '6e61f26ad71f34b55bd0258bd228a5f7057c3ec0cd95f0a6a062c00d09702e81'
prepare = installer.prepare_update


def prepare_console(original, patch):
    return prepare(original, patch, installer.OLD, installer.NEW)


installer.prepare_update = prepare_console

if __name__ == '__main__':
    installer.main()
