#!/usr/bin/env python3
"""Stage event-driven August UI input recovery against a reviewed asset copy. Never installs."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent
SOURCE_SHA256 = '2459e01eee1823470fe5b4fde46b9288e56d8ede3dc667e0d976f138a5a311d1'
STATE = 'ui/states/UIState_InGame.as'
CONSOLE = 'views/console/UIConsoleManager.as'
MANAGER = 'ui/core/managers/WidgetManager.as'


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


social = module('interactions_social', 'build-ingame-social.py')
gfx = module('interactions_gfx', 'build-helmet-click.py')


def patch_state(source):
    source = social.replace(source, '   import flash.display.Stage;',
                            '   import flash.display.Stage;\n   import flash.events.Event;')
    source = social.replace(source, '      private var m_movementWindows:Object = {};',
                            '      private var m_movementWindows:Object = {};\n'
                            '      private var m_interactionInputTraceCount:int = 0;\n'
                            '      private var m_interactionInputTraceLast:String = "";')
    a, b = social.method_span(source, 'OnEnter')
    enter = social.replace(source[a:b], '         this.m_movementWindows = {};',
                            '         this.m_movementWindows = {};\n'
                            '         this.m_interactionInputTraceCount = 0;\n'
                            '         this.m_interactionInputTraceLast = "";')
    source = source[:a] + enter + source[b:]
    for action in ('add', 'remove'):
        weak = ',0,true' if action == 'add' else ''
        anchor = (f'         this.m_stageRef.{action}EventListener(WidgetEvent.CLOSE_ALL,'
                  f'this.handleWidgetEvent,false{weak});')
        source = social.replace(source, anchor, anchor + '\n'
            f'         this.m_stageRef.{action}EventListener("cranberryInputRefresh",'
            f'this.handleInteractionInputRefresh,false{weak});')
    # Keep event accounting; extract just the old permission policy for explicit reconciliation.
    a, b = social.method_span(source, 'refreshInventoryMovement')
    old = source[a:b]
    start = old.index('         var blocked:Boolean = ')
    changed = old[:start] + ('         this.applyInventoryMovement();\n'
        '         this.traceInteractionInput(param1.type + ":" + param1.widgetId);\n      }')
    source = source[:a] + changed + source[b:]
    # An unrelated widget can update cursor capture while the overlay still owns input.
    source = social.replace(source,
        'var _loc5_:Boolean = this.m_displayStack.windowDepth > 0 || this.m_displayStack.modalOpen || _loc2_ || _loc3_;',
        'var _loc5_:Boolean = UIConsoleManager.OverlayActive() || this.m_displayStack.windowDepth > 0 || this.m_displayStack.modalOpen || _loc2_ || _loc3_;')
    at = source.index('\n   }\n}')
    return source[:at] + '\n' + (HERE / 'interaction-input-methods.as').read_text(encoding='utf-8') + source[at:]


def patch_console(source):
    a, b = social.method_span(source, 'overlayClose')
    method = source[a:b]
    anchor = '         UIBindingInput.SetInputFlags(this.m_overlayFlags);'
    method = social.replace(method, anchor, '''         // The current UI releases overlay capture after checking native input ownership.
         if(!this.m_stage.dispatchEvent(new flash.events.DataEvent("cranberryInputRefresh",false,true,String(this.m_overlayFlags))))
         {
            return;
         }
         // Outside InGame no recovery listener exists: retain the original menu restoration.
''' + anchor)
    return source[:a] + method + source[b:]


def patch_manager(source):
    for name, anchor in (
        ('unloadWidget', '            delete this._loadingWidgets[param1.toString()];'),
        ('handleIOError', '         delete this._loadingWidgets[_loc3_.toString()];'),
    ):
        a, b = social.method_span(source, name)
        method = source[a:b]
        indent = '            ' if name == 'unloadWidget' else '         '
        method = social.replace(method, anchor, anchor + '\n' + indent +
            'this._stageRef.dispatchEvent(new Event("cranberryInputRefresh"));')
        source = source[:a] + method + source[b:]
    return source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--asset', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    if asset == out / 'UIRoot.gfx' or out == asset.parent or out.is_relative_to(asset.parent):
        raise ValueError('Use a separate staging directory, outside the source asset directory')
    original = asset.read_bytes()
    if hashlib.sha256(original).hexdigest() != SOURCE_SHA256:
        raise ValueError('UIRoot changed; review/rebase rather than overwrite unrelated work')
    out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    patches = {STATE: patch_state, CONSOLE: patch_console, MANAGER: patch_manager}
    with (out / 'compiler.log').open('w', encoding='utf-8') as log:
        def run(arguments):
            subprocess.run(java + arguments, stdout=log, stderr=subprocess.STDOUT, check=True)
        run(['-export', 'script', str(out / 'before'), str(asset)])
        for name, patch in patches.items():
            target = out / 'patch' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(patch((out / 'before/scripts' / name).read_text(encoding='utf-8')),
                              encoding='utf-8', newline='\n')
        run(['-onerror', 'abort', '-importScript', str(asset), str(out / 'compiled.gfx'), str(out / 'patch')])
        candidate = out / 'UIRoot.gfx'
        candidate.write_bytes(gfx.preserve_tags(original, (out / 'compiled.gfx').read_bytes()))
        run(['-export', 'script', str(out / 'after'), str(candidate)])
    before, after = out / 'before/scripts', out / 'after/scripts'
    originals = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
    candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
    if originals.keys() != candidates.keys():
        raise ValueError('Class inventory changed')
    changed = [name for name in originals if originals[name].read_bytes() != candidates[name].read_bytes()]
    if set(changed) != set(patches):
        raise ValueError('Unexpected class changes: ' + str(changed))
    allowed = {STATE: {'OnEnter', 'OnExit', 'refreshInventoryMovement', 'handleSetMouse'},
               CONSOLE: {'overlayClose'}, MANAGER: {'unloadWidget', 'handleIOError'}}
    for name in patches:
        social.verify_unrelated_methods(originals[name].read_text(encoding='utf-8'),
                                        candidates[name].read_text(encoding='utf-8'), allowed[name])
    for verifier in ('verify-inventory-movement.cjs', 'verify-interactions-input.cjs'):
        subprocess.run(['node', str(HERE / verifier), str(after / STATE), str(after)], check=True)
    subprocess.run(['node', str(HERE / 'verify-social-overlay.cjs'), str(after)], check=True)
    manifest = {'asset': 'UIRoot.gfx', 'source_sha256': SOURCE_SHA256,
                'output_sha256': hashlib.sha256(candidate.read_bytes()).hexdigest(), 'output': str(candidate),
                'changed_classes': changed, 'unchanged_classes': len(originals) - len(changed),
                'non_script_tags_and_trailer_preserved': True, 'unrelated_methods_preserved': True,
                'native_test': 'NATIVE TEST PENDING'}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
