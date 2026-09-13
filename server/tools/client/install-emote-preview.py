#!/usr/bin/env python3
"""Install the verified August emote preview UI, preserving the other pack assets.

Uses the shared guarded installer, including full-pack backup, atomic replacement,
source/target hash checks and restoration that refuses to overwrite later changes.
Dry run is the default; --apply requires the client closed and a new --backup directory.
"""
import importlib.util
from pathlib import Path


def installer():
    spec = importlib.util.spec_from_file_location(
        'guarded_emote_installer', Path(__file__).with_name('install-bounty-lobby.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.NAME = 'CustomizationWindow.gfx'
    module.PACK = 'Assets_162.pack'
    module.OLD = '086a6db1a7fc7283e51d953577401e6eb7b7741097001bda4aedaff713d3c3d2'
    module.NEW = 'c3cf22624085b756300806b2b64960c6218fbef0f657886c7d4b8f726b338342'
    prepare = module.prepare_update
    module.prepare_update = lambda original, patch: prepare(original, patch, module.OLD, module.NEW)
    return module


if __name__ == '__main__':
    installer().main()
