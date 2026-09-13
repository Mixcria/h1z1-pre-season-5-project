#!/usr/bin/env python3
"""Build, never install, August emote-slot preview, repeat-click, and cleanup UI.

The existing right-grid selection preview remains unchanged. Slot previews resolve
the current EmoteItems assignment. Only CustomizationWindow is recompiled; every
other exported class, GFx tag, and trailer is verified unchanged.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess

SOURCE_SHA256 = '086a6db1a7fc7283e51d953577401e6eb7b7741097001bda4aedaff713d3c3d2'
CLASS = 'views.customization.CustomizationWindow'
RELATIVE = Path('scripts/views/customization/CustomizationWindow.as')
CHANGED_METHODS = ('enter', 'exit', 'handleEmoteSlotListIndexChange')
NEW_METHODS = ('stopEmotePreview', 'previewEmoteSlot', 'handleEmoteSlotListItemClick',
               'handleSkinsListItemClick')


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Unrecognized UI source anchor: ' + old[:100])
    return source.replace(old, new, 1)


def patch_source(source):
    for list_name, handler in (('m_emoteSlotsList', 'handleEmoteSlotListItemClick'),
                               ('m_loadoutSkinsList', 'handleSkinsListItemClick')):
        anchor = ('         this.' + list_name + '.addEventListener(ListEvent.INDEX_CHANGE,this.'
                  + ('handleEmoteSlotListIndexChange' if list_name == 'm_emoteSlotsList'
                     else 'handleSkinsListIndexChange') + ',false,0,true);')
        source = replace_once(source, anchor, anchor + '\n         this.' + list_name
                              + '.addEventListener(ListEvent.ITEM_CLICK,this.' + handler + ',false,0,true);')
        anchor = anchor.replace('addEventListener', 'removeEventListener').replace(',false,0,true', '')
        source = replace_once(source, anchor, anchor + '\n         this.' + list_name
                              + '.removeEventListener(ListEvent.ITEM_CLICK,this.' + handler + ');')
    source = replace_once(source, '         super.exit();',
                          '         super.exit();\n         this.stopEmotePreview();')
    source = replace_once(source, '         UIBindingItem.SelectEditorEmoteAnimationSlotId(_loc3_);',
                          '         UIBindingItem.SelectEditorEmoteAnimationSlotId(_loc3_);\n'
                          '         this.previewEmoteSlot(_loc3_);')
    methods = '''      private function stopEmotePreview() : void
      {
         if(this.m_windowType != EMOTES)
         {
            return;
         }
         clearTimeout(this.m_previewEmoteTimeout);
         this.m_previewEmoteTimeout = 0;
         UIBindingItem.ResetPreviewEmoteAnimation();
      }

      private function previewEmoteSlot(param1:int) : void
      {
         if(this.m_windowType != EMOTES || param1 <= 0)
         {
            return;
         }
         this.stopEmotePreview();
         var rows:Array = uiDBManager.query("SELECT EmoteAnimationItemId FROM EmoteItems WHERE EmoteAnimationSlotId=" + param1);
         if(rows && rows.length > 0)
         {
            var itemId:int = int(rows[0].EmoteAnimationItemId);
            if(itemId > 0)
            {
               this.requestPreviewEmoteTimeout(itemId);
            }
         }
      }

      private function handleEmoteSlotListItemClick(param1:ListEvent) : void
      {
         if(this.m_windowType != EMOTES || !param1 || param1.buttonIdx != MouseEventEx.LEFT_BUTTON ||
            param1.index < 0 || param1.index != this.m_emoteSlotsList.selectedIndex || !param1.itemData)
         {
            return;
         }
         this.previewEmoteSlot(int(param1.itemData.slotId));
      }

      private function handleSkinsListItemClick(param1:ListEvent) : void
      {
         if(this.m_windowType != EMOTES || !param1 || param1.buttonIdx != MouseEventEx.LEFT_BUTTON ||
            param1.index < 0 || param1.index != this.m_loadoutSkinsList.selectedIndex || !param1.itemData)
         {
            return;
         }
         UIBindingItem.ResetPreviewEmoteAnimation();
         this.requestPreviewEmoteTimeout(int(param1.itemData.itemId));
      }

'''
    return replace_once(source, '      private function requestPreviewEmoteTimeout(param1:int = 0) : void',
                        methods + '      private function requestPreviewEmoteTimeout(param1:int = 0) : void')


def method_span(source, name):
    match = re.search(r'^\s*(?:override )?(?:public|protected|private) function ' + re.escape(name) + r'\(',
                      source, re.MULTILINE)
    if not match:
        raise ValueError('Missing method ' + name)
    start = source.index('{', match.start())
    depth, end = 1, start + 1
    while depth:
        if source[end] == '{':
            depth += 1
        elif source[end] == '}':
            depth -= 1
        end += 1
    return match.start(), end


def unrelated_source(source, patched=False):
    for method in CHANGED_METHODS + (NEW_METHODS if patched else ()):
        start, end = method_span(source, method)
        source = source[:start] + source[end:]
    # These three observed FFDec simplifications remove Boolean conversions in
    # conditional expressions. Keep the whitelist exact so other drift fails.
    source = source.replace('Boolean(isInvalid(INVALIDATE_LOADOUT_SKINS))',
                            'isInvalid(INVALIDATE_LOADOUT_SKINS)')
    source = source.replace('Boolean(_loc6_.scrapValue > 0)', '_loc6_.scrapValue > 0')
    source = source.replace('Boolean(param1 && param1.itemRenderer) && Boolean(param1.buttonIdx == MouseEventEx.RIGHT_BUTTON)',
                            'param1 && param1.itemRenderer && param1.buttonIdx == MouseEventEx.RIGHT_BUTTON')
    return '\n'.join(line.strip() for line in source.splitlines() if line.strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError('CustomizationWindow changed; review/rebase instead of overwriting another patch')
    destination = out / 'CustomizationWindow.gfx'
    if destination == asset:
        raise ValueError('Output must differ from source')
    out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-export', 'script', str(out / 'all-original'), str(asset)], check=True)
    original = (out / 'all-original' / RELATIVE).read_text(encoding='utf-8')
    script = out / 'patch' / RELATIVE.relative_to('scripts')
    script.parent.mkdir(parents=True, exist_ok=True)
    script.write_text(patch_source(original), encoding='utf-8', newline='\n')
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled),
                          str(out / 'patch')], check=True)
    spec = importlib.util.spec_from_file_location('tag_preserver', Path(__file__).with_name('build-helmet-click.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    built = helper.preserve_tags(raw, compiled.read_bytes())
    destination.write_bytes(built)
    subprocess.run(java + ['-export', 'script', str(out / 'all-patched'), str(destination)], check=True)
    before, after = out / 'all-original/scripts', out / 'all-patched/scripts'
    originals = {path.relative_to(before): path for path in before.rglob('*.as')}
    patched = {path.relative_to(after): path for path in after.rglob('*.as')}
    if originals.keys() != patched.keys():
        raise ValueError('Compiled movie changed its class set')
    changed = [path for path in originals if originals[path].read_bytes() != patched[path].read_bytes()]
    if changed != [RELATIVE.relative_to('scripts')]:
        raise ValueError('An unrelated class changed: ' + str(changed))
    verified_path = out / 'all-patched' / RELATIVE
    verified = verified_path.read_text(encoding='utf-8')
    if unrelated_source(original) != unrelated_source(verified, patched=True):
        raise ValueError('An unrelated part of CustomizationWindow changed during compilation')
    subprocess.run(['node', str(Path(__file__).with_name('verify-emote-preview.cjs')), str(verified_path)], check=True)
    manifest = {
        'asset': 'CustomizationWindow.gfx', 'pack': 'Assets_162.pack',
        'source_sha256': SOURCE_SHA256, 'output_sha256': hashlib.sha256(built).hexdigest(),
        'output': str(destination), 'changed_class': CLASS,
        'changed_methods': CHANGED_METHODS, 'new_methods': NEW_METHODS,
        'source_script_sha256': hashlib.sha256(original.encode()).hexdigest(),
        'verified_script_sha256': hashlib.sha256(verified.encode()).hexdigest(),
        'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True,
        'other_class_methods_unchanged': True, 'existing_right_selection_preview_unchanged': True,
        'other_exported_scripts_unchanged': len(originals) - 1,
        'handler_tests': 'verify-emote-preview.cjs on re-exported candidate',
        'compiler_normalizations': ['three existing conditional expressions elide redundant Boolean casts'],
    }
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
