"""Install/restore the verified team-queue fallback in the current August UIRoot.

python install-local-team-queue.py UIRoot.gfx --pack Assets_060.pack
       --backup NEW_BACKUP_DIRECTORY --apply
python install-local-team-queue.py --restore BACKUP/manifest.json --apply

Omit --apply for a dry run. H1Z1 must be closed for installation/restoration.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.NAME = 'UIRoot.gfx'
installer.PACK = 'Assets_060.pack'
installer.OLD = 'dac8eb53d1aeb2b50c63e8984f80260f126c99562e1fc2d0a4685623d9696bd4'
installer.NEW = 'd8e7c87b483e451ae72b000adb1671fd710af3da4499dde6563a1bc2db7f0715'
prepare = installer.prepare_update


def prepare_team_queue(original, patch):
    return prepare(original, patch, installer.OLD, installer.NEW)


installer.prepare_update = prepare_team_queue

if __name__ == '__main__':
    installer.main()
