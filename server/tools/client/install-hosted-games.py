"""Install/restore the verified hosted display patch in the current August UIRoot.

Omit --apply for a dry run. H1Z1 must be closed. The pack installer checks the
exact current source hash, backs up the entire pack and preserves other assets.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.NAME = 'UIRoot.gfx'
installer.PACK = 'Assets_060.pack'
installer.OLD = 'd8e7c87b483e451ae72b000adb1671fd710af3da4499dde6563a1bc2db7f0715'
installer.NEW = '2527f43f62afc3e36938d1731085b88cd550981e2c86c829a68a2266f4ee1a18'
SOURCE_HASHES = frozenset((installer.OLD,
    'e329b25c02d1895912a0d640a36a345177eba83ac153aadb7b32e8f8206a67cf',
    'c48fbcbc50d167885214d5248f6ec7ebd8ac3a600a358be276f3dfe20b8af2b2'))
prepare = installer.prepare_update


def prepare_hosted_games(original, patch):
    if installer.digest(patch) != installer.NEW:
        raise ValueError('Unverified patch asset')
    targets = [entry for entry in installer.entries(original) if entry[0] == installer.NAME]
    if len(targets) != 1:
        raise ValueError('Expected exactly one UIRoot.gfx')
    _, _, offset, size, _ = targets[0]
    current_hash = installer.digest(original[offset:offset + size])
    if current_hash not in SOURCE_HASHES and current_hash != installer.NEW:
        raise ValueError('UIRoot has another modification; refusing to replace it')
    # Keep the shared installer's backup manifest accurate for each supported upgrade.
    installer.OLD = current_hash
    return prepare(original, patch, installer.OLD, installer.NEW)


installer.prepare_update = prepare_hosted_games

if __name__ == '__main__':
    installer.main()
