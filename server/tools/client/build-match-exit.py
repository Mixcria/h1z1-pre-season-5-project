"""Build cancellable match exit and explicit result choices on the installed August UI.

The output is a candidate. Preserve every other script and all non-script tags.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


pack = module('exit_pack', 'install-bounty-lobby.py')
gfx = module('exit_gfx', 'build-helmet-click.py')
methods = module('exit_methods', 'build-ingame-social.py')
MANAGER = 'ui/managers/UIMatchManager.as'
SETTINGS = 'views/settings/UISettingsManager.as'
CONSOLE = 'views/console/UIConsoleManager.as'


def replace(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Expected exactly one anchor: ' + old)
    return source.replace(old, new)


def edit_method(source, name, change):
    a, b = methods.method_span(source, name)
    return source[:a] + change(source[a:b]) + source[b:]


def patch_settings(source):
    return edit_method(source, 'handleLogoutConfirmationResponse', lambda body: replace(body,
        'UIBindingSystem.Logout();', 'this.handleCloseSettings();\n            UIBindingSystem.DispatchWallOfData("CRANBERRY_MATCH_ACTION_V1","exit");'))


def patch_console(source):
    return edit_method(source, 'handlePrintConsole', lambda body: replace(body,
        '         var _loc2_:String = String(param1.data[0]);', '''         var _loc2_:String = String(param1.data[0]);
         if(_loc2_ == "@cranberry/match-exit/1;logout")
         {
            this.m_stage.dispatchEvent(new GameEvent("CranberryMatchExit"));
            return;
         }
         if(_loc2_ == "@cranberry/match-exit/1;ready")
         {
            this.m_stage.dispatchEvent(new GameEvent("CranberryMatchReady"));
            return;
         }'''))


def patch_manager(source):
    source = replace(source, '   import flash.display.Stage;', '''   import flash.display.Stage;
   import flash.display.Sprite;
   import flash.events.Event;
   import flash.events.MouseEvent;
   import flash.events.KeyboardEvent;
   import flash.text.TextField;
   import flash.text.TextFormat;
   import flash.utils.Dictionary;''')
    source = replace(source, '      public static var displayStack:IDisplayStack;', '''      public static var displayStack:IDisplayStack;
      private var m_resultRows:Dictionary = new Dictionary(true);
      private var m_exitRequested:Boolean = false;
      private var m_nativeExitStarted:Boolean = false;
      private var m_victoryTimeoutId:uint = 0;''')
    source = replace(source, '         this._stage = param1;', '''         this._stage = param1;
         this._stage.addEventListener("CranberryMatchExit",this.handleNativeMatchExit,false,0,true);
         this._stage.addEventListener("CranberryMatchReady",this.handleMatchSessionReady,false,0,true);''')
    source = edit_method(source, 'deinitialize', lambda body: body.replace(
        '{', '{\n         this._stage.removeEventListener("CranberryMatchExit",this.handleNativeMatchExit);\n         this._stage.removeEventListener("CranberryMatchReady",this.handleMatchSessionReady);', 1))
    source = edit_method(source, 'handleEventStartMatch', lambda body: body.replace('{', '''{
         this.m_exitRequested = false;
         this.m_nativeExitStarted = false;''', 1))
    source = edit_method(source, 'cleanUpMatchData', lambda body: body.replace('{', '''{
         clearTimeout(this.m_windowTimeoutId);
         clearTimeout(this.m_victoryTimeoutId);''', 1))
    source = replace(source, '         setTimeout(this.showTheVictoryScreen,_loc2_);',
        '         this.m_victoryTimeoutId = setTimeout(this.showTheVictoryScreen,_loc2_);')
    source = edit_method(source, 'exitMatch', lambda body: body[:body.index('{')+1] + '''
         this.requestMatchAction("menu");
      }''')
    source = edit_method(source, 'processActionButton', lambda body: replace(body,
        '            param2.data = _loc3_;', '''            param2.data = _loc3_;
            this.installResultButtons(param2);'''))
    source = edit_method(source, 'handleButtonEvent', lambda body: replace(body,
        '            case "processActionButton":\n            case "_processGroupExitButton":', '''            case "processActionButton":
               this.requestMatchAction("play");
               break;
            case "_processGroupExitButton":'''))
    extra = Path(__file__).with_name('match-exit-methods.as').read_text(encoding='utf-8')
    return source.rsplit('   }', 1)[0] + extra + '\n   }\n}\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    pack_path = args.client.resolve() / 'Resources/Assets/Assets_060.pack'
    raw_pack = pack_path.read_bytes()
    entry, = [e for e in pack.entries(raw_pack) if e[0] == 'UIRoot.gfx']
    original = raw_pack[entry[2]:entry[2]+entry[3]]
    asset = out / 'original.gfx'
    asset.write_bytes(original)
    patches = {
        SETTINGS: (patch_settings, {'handleLogoutConfirmationResponse'}),
        CONSOLE: (patch_console, {'handlePrintConsole'}),
        MANAGER: (patch_manager, {'initialize', 'deinitialize', 'handleEventStartMatch',
            'cleanUpMatchData', 'handleMatchVictory', 'exitMatch', 'processActionButton', 'handleButtonEvent'})}
    java = ['java', '-jar', str(args.ffdec.resolve())]
    with (out / 'compiler.log').open('w', encoding='utf-8') as log:
        def run(arguments):
            subprocess.run(java + arguments, stdout=log, stderr=subprocess.STDOUT, check=True)
        run(['-export', 'script', str(out / 'before'), str(asset)])
        classes = out / 'java'
        classes.mkdir()
        subprocess.run(['javac', '-cp', str(args.ffdec), '-d', str(classes),
            str(Path(__file__).with_name('BinocularHudBytecode.java')),
            str(Path(__file__).with_name('MatchExitSettingsBytecode.java')),
            str(Path(__file__).with_name('VerifyMatchExitBytecode.java'))], stdout=log, stderr=subprocess.STDOUT, check=True)
        settings_asset = out / 'settings.gfx'
        subprocess.run(['java', '-cp', os.pathsep.join([str(classes), str(args.ffdec)]),
            'MatchExitSettingsBytecode', str(asset), str(settings_asset)], stdout=log, stderr=subprocess.STDOUT, check=True)
        for name, (patch, _) in patches.items():
            if name == SETTINGS: continue
            target = out / 'patch' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(patch((out / 'before/scripts' / name).read_text(encoding='utf-8')), encoding='utf-8', newline='\n')
        run(['-onerror', 'abort', '-importScript', str(settings_asset), str(out / 'compiled.gfx'), str(out / 'patch')])
        built = gfx.preserve_tags(original, (out / 'compiled.gfx').read_bytes())
        candidate = out / 'UIRoot.gfx'
        candidate.write_bytes(built)
        run(['-export', 'script', str(out / 'after'), str(candidate)])
    before, after = out / 'before/scripts', out / 'after/scripts'
    sources = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
    candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
    if sources.keys() != candidates.keys():
        raise ValueError('Class inventory changed')
    changed = {name for name in sources if sources[name].read_bytes() != candidates[name].read_bytes()}
    decompiler_only = []
    twitch = 'ui/managers/UITwitchManager.as'
    if twitch in changed:
        # FFDec occasionally renders this existing stack assignment as push/pop
        # pseudo-operations. Accept only this exact text delta, with identical
        # native method bytecode and frame limits verified independently.
        artifact = '''            §§push(_loc8_);
            §§push(_loc7_ >= 0.9 ? 3 : (_loc7_ >= 0.6 ? 2 : (_loc7_ >= 0.2 ? 1 : 0)));
            §§pop().Population = §§pop();'''
        ordinary = '            _loc8_.Population = _loc7_ >= 0.9 ? 3 : (_loc7_ >= 0.6 ? 2 : (_loc7_ >= 0.2 ? 1 : 0));'
        normalized = replace(candidates[twitch].read_text(encoding='utf-8'), artifact, ordinary)
        if normalized != sources[twitch].read_text(encoding='utf-8'):
            raise ValueError('Unrelated Twitch source changed beyond the known decompiler artifact')
        subprocess.run(['java', '-cp', os.pathsep.join([str(classes), str(args.ffdec)]),
            'VerifyMatchExitBytecode', str(asset), str(candidate), 'UITwitchManager', '_fakeServers'], check=True)
        changed.remove(twitch)
        decompiler_only.append(twitch)
    if changed != patches.keys():
        raise ValueError('Unexpected changed classes: ' + str(changed))
    for name, (_, allowed) in patches.items():
        def normal(source):
            return re.sub(r'\(Boolean\(([^()]*)\)\)', r'Boolean(\1)', source)
        if name == MANAGER:
            # A compiler-only for/continue -> while/if rewrite. Verify the complete
            # method against that exact equivalent form instead of exempting it.
            a = sources[name].read_text(encoding='utf-8')
            b = candidates[name].read_text(encoding='utf-8')
            i, j = methods.method_span(a, 'handlePlayerDeath')
            k, l = methods.method_span(b, 'handlePlayerDeath')
            def compact(text):
                return re.sub(r'\s+', '', text.replace('Number(NaN)', 'NaN'))
            expected = compact(a[i:j])
            expected = replace(expected, 'for(;_loc19_<_loc18_;_loc19_++)', 'while(_loc19_<_loc18_)')
            expected = replace(expected, 'if(!_loc20_){continue;}switch', 'if(_loc20_){switch')
            expected = replace(expected, '_loc6_=int(_loc20_["ParticipantTier"]);}}}',
                '_loc6_=int(_loc20_["ParticipantTier"]);}}_loc19_++;}}')
            if expected != compact(b[k:l]):
                raise ValueError('Player death handler changed beyond the equivalent loop rewrite')
            allowed = allowed | {'handlePlayerDeath'}
        methods.verify_unrelated_methods(normal(sources[name].read_text(encoding='utf-8')),
            normal(candidates[name].read_text(encoding='utf-8')), allowed)
    manifest = dict(asset='UIRoot.gfx', pack='Assets_060.pack', pack_path=str(pack_path),
        source_pack_sha256=hashlib.sha256(raw_pack).hexdigest(), source_sha256=hashlib.sha256(original).hexdigest(),
        output_sha256=hashlib.sha256(built).hexdigest(), output=str(candidate), changed_classes=sorted(changed),
        unchanged_classes=len(sources)-len(changed), non_script_tags_preserved=True, unrelated_methods_preserved=True,
        decompiler_only_classes=decompiler_only)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
