#!/usr/bin/env python3
"""Stage category previews, isolated Rides data, and empty-news handling from current assets.

Recompile selected methods, preserve every non-script GFx tag, and re-export to
verify unrelated classes/methods. This builder does not install client files.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


emote = module('menu_source_tools', 'build-emote-preview.py')
gfx = module('menu_tag_tools', 'build-helmet-click.py')


def patch_customization(source):
    old = 'this.m_windowType != VEHICLES || this.m_windowType != EMOTES'
    if source.count(old) != 3:
        raise ValueError('Expected the three original cross-category data guards')
    source = source.replace(old, 'this.m_windowType != VEHICLES && this.m_windowType != EMOTES')
    anchor = '            UIBindingItem.SelectEditorSkinTargetPrototypeItemId(_loc4_);'
    return emote.replace_once(source, anchor, anchor + '''
            if(this.m_windowType == WEAPONS)
            {
               UIBindingStaticView.SetStaticView("kotkweaponpreview:" + _loc4_);
            }''')


def patch_news_controller(source):
    source = emote.replace_once(source, '         this._container.visible = !_purchaseSuccess;',
        '         this._container.visible = !_purchaseSuccess && this._bbpanels.selectedItem != null;')
    anchor = '''         if(!this._bbpanels.selectedItem)
         {
            return;
         }'''
    source = emote.replace_once(source, anchor, '''         if(!this._bbpanels.selectedItem)
         {
            this._container.visible = false;
            this._timerLabel.visible = false;
            return;
         }
         this.refreshViewState();''')
    # Teardown previously nulled _bbpanels before removing its listeners.
    source = emote.replace_once(source, '               this._bbpanels = null;', '')
    anchor = '               this._bbpanels.removeEventListener(Event.CHANGE,this.handleBillboardPanelChangeEvent,false);'
    return emote.replace_once(source, anchor, anchor + '\n               this._bbpanels = null;')


def patch_news_widget(source):
    anchor = '         this.timerLabel = this.container.getChildByName("timerLabel") as CountdownTimerLabel;'
    return emote.replace_once(source, anchor, anchor + '\n         this.timerLabel.visible = false;')


CUSTOM = 'views/customization/CustomizationWindow.as'
CONTROLLER = 'ui/viewControllers/MOTDViewController.as'
WIDGET = 'views/motd/MOTDWidget.as'
PATCHES = {
    'CustomizationWindow.gfx': {CUSTOM: (patch_customization, ('handleCategoryListIndexChange',
        'updateLoadoutSlotData', 'updateLoadoutCategoryData', 'updateLoadoutSkinsData'))},
    'CharacterSelectWindow.gfx': {CONTROLLER: (patch_news_controller, ('refreshViewState',
        'handleBillboardPanelChangeEvent', 'handleEvent')), WIDGET: (patch_news_widget, ('initialize',))},
    'MOTDWidget.gfx': {WIDGET: (patch_news_widget, ('initialize',))},
}


def extract(assets, destination):
    """Read pack indexes with seeks; do not load every multi-megabyte pack into memory."""
    wanted, found = set(PATCHES), {}
    for pack in sorted(assets.glob('Assets_*.pack')):
        with pack.open('rb') as stream:
            chunk, visited = 0, set()
            while True:
                if chunk in visited:
                    raise ValueError('Pack index cycle: ' + str(pack))
                visited.add(chunk)
                stream.seek(chunk)
                following, count = struct.unpack('>II', stream.read(8))
                for _ in range(count):
                    size, = struct.unpack('>I', stream.read(4))
                    name = stream.read(size).decode('ascii')
                    offset, length, crc = struct.unpack('>III', stream.read(12))
                    if name in wanted:
                        if name in found:
                            raise ValueError('Duplicate menu asset: ' + name)
                        index = stream.tell()
                        stream.seek(offset)
                        raw = stream.read(length)
                        import zlib
                        if zlib.crc32(raw) != crc:
                            raise ValueError('Invalid current asset CRC: ' + name)
                        stream.seek(index)
                        destination.mkdir(parents=True, exist_ok=True)
                        (destination / name).write_bytes(raw)
                        found[name] = {'pack': str(pack.resolve()), 'sha256': hashlib.sha256(raw).hexdigest()}
                if not following:
                    break
                chunk = following
    if set(found) != wanted:
        raise ValueError('Missing menu assets: ' + str(wanted - set(found)))
    return found


def unrelated(source, methods):
    for name in methods:
        start, end = emote.method_span(source, name)
        source = source[:start] + source[end:]
    # Normalize the established FFDec Boolean simplifications and harmless spacing.
    source = source.replace('Boolean(isInvalid(INVALIDATE_LOADOUT_SKINS))', 'isInvalid(INVALIDATE_LOADOUT_SKINS)')
    source = source.replace('Boolean(_loc6_.scrapValue > 0)', '_loc6_.scrapValue > 0')
    source = source.replace('Boolean(param1 && param1.itemRenderer) && Boolean(param1.buttonIdx == MouseEventEx.RIGHT_BUTTON)',
                            'param1 && param1.itemRenderer && param1.buttonIdx == MouseEventEx.RIGHT_BUTTON')
    return '\n'.join(line.strip() for line in source.splitlines() if line.strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    sources = extract(args.assets.resolve(), out / 'original')
    java = ['java', '-jar', str(args.ffdec.resolve())]
    manifest = {'assets': {}}
    for name, changes in PATCHES.items():
        asset = out / 'original' / name
        task = out / name.removesuffix('.gfx')
        task.mkdir(parents=True, exist_ok=True)
        with (task / 'compiler.log').open('w', encoding='utf-8') as log:
            def run(arguments):
                subprocess.run(java + arguments, stdout=log, stderr=subprocess.STDOUT, check=True)
            run(['-export', 'script', str(task / 'before'), str(asset)])
            for relative, (patch, _) in changes.items():
                source = (task / 'before/scripts' / relative).read_text(encoding='utf-8')
                patched = task / 'patch' / relative
                patched.parent.mkdir(parents=True, exist_ok=True)
                patched.write_text(patch(source), encoding='utf-8', newline='\n')
            run(['-onerror', 'abort', '-importScript', str(asset), str(task / 'compiled.gfx'), str(task / 'patch')])
            candidate = gfx.preserve_tags(asset.read_bytes(), (task / 'compiled.gfx').read_bytes())
            destination = task / name
            destination.write_bytes(candidate)
            run(['-export', 'script', str(task / 'after'), str(destination)])
        before, after = task / 'before/scripts', task / 'after/scripts'
        originals = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
        candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
        if originals.keys() != candidates.keys():
            raise ValueError('Exported class inventory changed')
        for relative, original in originals.items():
            left, right = original.read_text(encoding='utf-8'), candidates[relative].read_text(encoding='utf-8')
            if relative not in changes:
                if left != right:
                    raise ValueError('Unrelated class changed: ' + relative)
            elif unrelated(left, changes[relative][1]) != unrelated(right, changes[relative][1]):
                raise ValueError('Unrelated method changed: ' + relative)
        manifest['assets'][name] = {**sources[name], 'candidate': str(destination),
            'candidate_sha256': hashlib.sha256(candidate).hexdigest(), 'changed_classes': list(changes),
            'unchanged_classes': len(originals) - len(changes)}
        print(name + ': compiled and re-exported; unrelated classes and methods preserved', flush=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
