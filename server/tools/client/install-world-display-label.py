"""Install/restore the world label using the verified pack backup workflow.

python install-world-display-label.py HudCompassWindow.gfx --pack Assets_110.pack
       --backup NEW_BACKUP_DIRECTORY --apply
python install-world-display-label.py --restore BACKUP/manifest.json --apply

Omit --apply for a dry run. H1Z1 must be closed for installation/restoration.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.NAME = 'HudCompassWindow.gfx'
installer.PACK = 'Assets_110.pack'
installer.OLD = 'ba0445f20700c65a71c7aa52afa348c635a31a00f1f72bc3fe43f38e96d2f1c7'
installer.NEW = '7506d3ed145ac83ff900ffe3c35921988cc82581de855130f85720ae3b106ce3'
prepare = installer.prepare_update


def prepare_world_label(original, patch):
    return prepare(original, patch, installer.OLD, installer.NEW)


installer.prepare_update = prepare_world_label

if __name__ == '__main__':
    installer.main()
