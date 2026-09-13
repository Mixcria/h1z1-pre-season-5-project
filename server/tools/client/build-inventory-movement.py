#!/usr/bin/env python3
"""Build the inventory keyboard-movement UI; preserve all other installed UIRoot scripts/tags."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = '2527f43f62afc3e36938d1731085b88cd550981e2c86c829a68a2266f4ee1a18'
PREVIOUS_SHA256 = 'bcb6246d5a658e06cbcb75bf7a4ca29690a6647fb5af7e64631b38deb214b8b9'
CLASS = 'ui.states.UIState_InGame'
RELATIVE = Path('scripts/ui/states/UIState_InGame.as')


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Unrecognized UI source anchor: ' + old[:100])
    return source.replace(old, new, 1)


def patch_source(source):
    if '      private function refreshInventoryMovement(' in source:
        # Upgrade the installed first patch. Native 14158fa07 tests the keyboard-enabled
        # gate before sampling MoveForward/Strafe, so movement permission alone is insufficient.
        return replace_once(source,
                            'UIBindingKeyboard.SetKeyboardEnabled(!blocked && !tabOpen);',
                            'UIBindingKeyboard.SetKeyboardEnabled(!blocked && (!tabOpen || inventoryOpen));')
    source = replace_once(source, '      private var m_matchOver:Boolean = false;',
                          '      private var m_matchOver:Boolean = false;\n      private var m_movementWindows:Object = {};')
    source = replace_once(source, '         this.openHud();',
                          '         this.m_movementWindows = {};\n         this.openHud();')
    start = source.index('      private function handleWidgetEvent(')
    end = source.index('      private function handleUIEvent(', start)
    event = source[start:end]
    for instruction in ('UIBindingKeyboard.SetMovementEnabled(false);',
                        'UIBindingKeyboard.SetMovementEnabled(true);',
                        'UIBindingKeyboard.SetKeyboardEnabled(false);',
                        'UIBindingKeyboard.SetKeyboardEnabled(true);'):
        event = replace_once(event, instruction, '')
    event = event.rstrip()
    assert event.endswith('      }')
    event = event[:-7] + '         this.refreshInventoryMovement(param1);\n      }\n      \n'
    source = source[:start] + event + MOVEMENT_METHOD + source[end:]
    # JPEXS private package helper dispatch workaround, matching the existing builders.
    source = source.replace('this._state_bindings.init();', 'Object(this._state_bindings)["init"]();')
    source = source.replace('this._state_bindings.deinit();', 'Object(this._state_bindings)["deinit"]();')
    source = source.replace('SpectatorManager.getInstance().init(', 'SpectatorManager.getInstance()["init"](')
    source = source.replace('SpectatorManager.getInstance().deinit(', 'SpectatorManager.getInstance()["deinit"](')
    return source


MOVEMENT_METHOD = '''      private function refreshInventoryMovement(param1:WidgetEvent) : void
      {
         switch(param1.widgetId)
         {
            case WidgetNames.INVENTORY_WINDOW:
            case WidgetNames.TAB_NAVIGATION_WINDOW:
            case WidgetNames.CONFIRMATION_DIALOG_WINDOW:
            case WidgetNames.INGAME_BROWSER_WINDOW:
            case WidgetNames.MAP_WINDOW:
            case WidgetNames.CONSOLE_WINDOW:
               break;
            default:
               if(param1.type != WidgetEvent.CLOSE_ALL)
               {
                  return;
               }
         }
         if(param1.type == WidgetEvent.CLOSE_ALL)
         {
            this.m_movementWindows = {};
         }
         else if(param1.type == WidgetEvent.WIDGET_OPENED)
         {
            this.m_movementWindows[param1.widgetId] = true;
         }
         else if(param1.type == WidgetEvent.CLOSE_WIDGET)
         {
            delete this.m_movementWindows[param1.widgetId];
         }
         else
         {
            return;
         }
         var blocked:Boolean = Boolean(this.m_movementWindows[WidgetNames.CONFIRMATION_DIALOG_WINDOW]) ||
            Boolean(this.m_movementWindows[WidgetNames.INGAME_BROWSER_WINDOW]) ||
            Boolean(this.m_movementWindows[WidgetNames.MAP_WINDOW]) ||
            Boolean(this.m_movementWindows[WidgetNames.CONSOLE_WINDOW]);
         var tabOpen:Boolean = Boolean(this.m_movementWindows[WidgetNames.TAB_NAVIGATION_WINDOW]);
         var inventoryOpen:Boolean = Boolean(this.m_movementWindows[WidgetNames.INVENTORY_WINDOW]);
         UIBindingKeyboard.SetMovementEnabled(!blocked && (!tabOpen || inventoryOpen));
         UIBindingKeyboard.SetKeyboardEnabled(!blocked && (!tabOpen || inventoryOpen));
      }

'''

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    source_hash = hashlib.sha256(raw).hexdigest()
    if source_hash not in (SOURCE_SHA256, PREVIOUS_SHA256):
        raise ValueError('Source UIRoot changed; review/rebase the patch instead of overwriting other work')
    out.mkdir(parents=True, exist_ok=True)
    destination = out / 'UIRoot.gfx'
    if destination == asset:
        raise ValueError('Output must differ from source')
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-selectclass', CLASS, '-export', 'script', str(out / 'source'), str(asset)], check=True)
    original = (out / 'source' / RELATIVE).read_text(encoding='utf-8')
    changed = patch_source(original)
    (out / 'source' / RELATIVE).write_text(changed, encoding='utf-8', newline='\n')
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled),
                          str(out / 'source' / 'scripts')], check=True)
    spec = importlib.util.spec_from_file_location('helmet_builder', Path(__file__).with_name('build-helmet-click.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    built = helper.preserve_tags(raw, compiled.read_bytes())
    destination.write_bytes(built)
    subprocess.run(java + ['-selectclass', CLASS, '-export', 'script', str(out / 'verify'), str(destination)], check=True)
    verified = (out / 'verify' / RELATIVE).read_text(encoding='utf-8')
    for required in ('refreshInventoryMovement', 'm_movementWindows', 'SetMovementEnabled', 'SetKeyboardEnabled'):
        if required not in verified:
            raise ValueError('Compiled patch missing ' + required)
    manifest = {'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack', 'source_sha256': source_hash,
                'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(destination),
                'source_script_sha256': hashlib.sha256(original.encode()).hexdigest(),
                'verified_script_sha256': hashlib.sha256(verified.encode()).hexdigest(),
                'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
