#!/usr/bin/env python3
"""Build the shared ItemActionMenu used by August's inventory window.

Component_Library loads into the UI application domain before InventoryWindow;
its class and authored list symbol must carry the same behavior as the window copy.
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('actions', Path(__file__).with_name('build-inventory-equipment-actions.py'))
actions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(actions)

if __name__ == '__main__':
    actions.builder.SOURCE_SHA = '8ff1fed5d2a04651387bae99a6f8efa3caa84ea10890c63806493ed1d288763a'
    actions.builder.ASSET_NAME = 'Component_Library.gfx'
    actions.builder.transform = actions.transform
    actions.builder.main()
