"""Install/restore the verified direct main-menu startup patch; --apply writes.

Uses the established pack backup, process and concurrent-change guards.
Only the UIRoot entry is replaced. Restore with --restore BACKUP/manifest.json.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('main_menu_pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.NAME = 'UIRoot.gfx'
installer.PACK = 'Assets_060.pack'
installer.OLD = '818b3795988979bfaad3987f4436166d5f77f3d85efe626fd5726a643a751b53'
installer.NEW = '2459e01eee1823470fe5b4fde46b9288e56d8ede3dc667e0d976f138a5a311d1'
prepare = installer.prepare_update


def prepare_main_menu(original, patch):
    return prepare(original, patch, installer.OLD, installer.NEW)


installer.prepare_update = prepare_main_menu

if __name__ == '__main__':
    installer.main()
