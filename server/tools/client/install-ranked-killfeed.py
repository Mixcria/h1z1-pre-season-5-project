"""Install/restore the ranked feed using the existing verified pack backup workflow.

Usage: python install-ranked-killfeed.py asset.gfx --pack Assets_014.pack
       --backup NEW_BACKUP_DIRECTORY --apply
Restore: python install-ranked-killfeed.py --restore BACKUP/manifest.json --apply
Omit --apply for a dry run. H1Z1 must be closed for installation or restoration.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('pack_installer', Path(__file__).with_name('install-bounty-lobby.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
installer.NAME = 'HudKillFeedWindow.gfx'
installer.PACK = 'Assets_014.pack'
installer.OLD = 'f98bc0f8876ed282d2da6962ea1be998a56d45340025fc43b53256fbae24d971'
installer.NEW = 'db70f52c0fa2b51e15cdeebf33baea5cf88a02d1cc13ad30cae85253bf02e03d'
PREVIOUS = (
    '6245bd4e90c03be89b9e784a23a916a3d784873b923cce98b4045d71590ad20e',
    '8ad063d4b81e0522b05cb1e2cf56502a6296d75e1c10fe3c2d6a606abc83edbb',
    '43f4fce7dcf1540886b6abe245f743b2713c5bf72a28f3093395425b64eeb9fa',
)
prepare = installer.prepare_update

def prepare_ranked(original, patch):
    targets = [entry for entry in installer.entries(original) if entry[0] == installer.NAME]
    if len(targets) != 1:
        raise ValueError('Expected exactly one HudKillFeedWindow.gfx')
    _, _, offset, size, _ = targets[0]
    current = installer.digest(original[offset:offset + size])
    if current not in (installer.OLD, *PREVIOUS, installer.NEW):
        raise ValueError('Kill feed has an unrecognized modification; refusing to replace it')
    # Also record the actual input asset version in the backup manifest for upgrades.
    installer.OLD = current
    return prepare(original, patch, current, installer.NEW)

installer.prepare_update = prepare_ranked

if __name__ == '__main__':
    installer.main()
