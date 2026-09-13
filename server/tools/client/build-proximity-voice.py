"""Connect the August native speaker rows to Cranberry's authenticated voice activity.

Extracts the CURRENT installed assets, changes only the three named classes,
and preserves all non-script tags. Produces candidates; never installs them.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


gfx = module('voice_gfx', 'build-helmet-click.py')
pack = module('voice_pack', 'install-bounty-lobby.py')
methods = module('voice_methods', 'build-ingame-social.py')
CONSOLE = 'views/console/UIConsoleManager.as'
WINDOW = 'views/hudgroupvoice/HudGroupVoiceWindow.as'
ROW = 'views/hudgroupvoice/VoiceItemRenderer.as'


def replace(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Expected exactly one source anchor: ' + old)
    return source.replace(old, new)


def patch_console(source):
    if 'private function deliverCompanionVoice(' in source:
        return source
    source = replace(source, '   import ui.core.Widget;', '   import ui.core.Widget;\n   import ui.constants.WidgetNames;')
    source = replace(source, '      private var m_stage:Stage;', '      private var m_stage:Stage;\n      private var m_voiceRouteStatus:String = "";')
    left, right = methods.method_span(source, 'handlePrintConsole')
    body = source[left:right]
    old_bridge = '''         if(_loc2_.indexOf("@cranberry/voice/1;") == 0)
         {
            var voiceEvent:GameEvent = new GameEvent("CranberryVoiceHud");
            voiceEvent.data = [_loc2_];
            this.m_stage.dispatchEvent(voiceEvent);
            return;
         }'''
    if old_bridge in body:
        body = replace(body, old_bridge, '')
    body = replace(body, '         var _loc2_:String = String(param1.data[0]);', '''         var _loc2_:String = String(param1.data[0]);
         if(_loc2_.indexOf("@cranberry/voice/1;") == 0)
         {
            this.deliverCompanionVoice(_loc2_);
            return;
         }''')
    source = source[:left] + body + source[right:]
    extra = Path(__file__).with_name('proximity-voice-console-methods.as').read_text(encoding='utf-8')
    left, _ = methods.method_span(source, 'handlePrintConsole')
    return source[:left] + '\n' + extra + source[left:]


def patch_row(source):
    if 'public function setCompanion(' in source:
        return source
    source = replace(source, '      private var m_index:int;', '''      private var m_index:int;
      private var m_companion:Boolean = false;
      private var m_companionName:String = "";

      public function setCompanion(param1:String) : void
      {
         this.m_companion = true;
         this.m_companionName = param1;
         this.update();
      }''')
    return replace(source, '         var _loc1_:VivoxParticipantRow = HudGroupVoiceManager.getInstance().getVoiceMember(this.m_index);', '''         var _loc1_:Object = this.m_companion ? (this.m_companionName.length == 0 ? null : {displayName:this.m_companionName,twitchName:"",isSpeaking:true,isMutedForMe:false}) : HudGroupVoiceManager.getInstance().getVoiceMember(this.m_index);''')


def patch_window(source):
    extra = Path(__file__).with_name('proximity-voice-hud-methods.as').read_text(encoding='utf-8')
    if 'public function receiveCompanionVoice(' in source:
        for name in ('clearCompanionVoice', 'receiveCompanionVoice', 'reportCompanionVoice', 'expireCompanionVoice', 'isCompanionVoiceId', 'layoutCompanionVoice'):
            if 'function ' + name + '(' not in source:
                continue
            left, right = methods.method_span(source, name)
            source = source[:left] + source[right:]
        return source.rsplit('   }', 1)[0] + extra + '\n   }\n}\n'
    if 'private function receiveCompanionVoice(' in source:
        source = replace(source, '   import ui.core.events.GameEvent;', '   import flash.display.DisplayObject;\n   import flash.geom.Point;')
        source = replace(source, '      private var m_voiceReceivedAt:int = -10000;', '      private var m_voiceReceivedAt:int = -10000;\n      private var m_voiceReport:String = "";')
        source = replace(source, '         stage.addEventListener("CranberryVoiceHud",this.receiveCompanionVoice,false,0,true);', '')
        source = replace(source, '         stage.removeEventListener("CranberryVoiceHud",this.receiveCompanionVoice);', '')
        for name in ('clearCompanionVoice', 'receiveCompanionVoice', 'expireCompanionVoice'):
            left, right = methods.method_span(source, name)
            source = source[:left] + source[right:]
        return source.rsplit('   }', 1)[0] + extra + '\n   }\n}\n'
    source = replace(source, '   import flash.display.MovieClip;', '''   import flash.display.MovieClip;
   import flash.display.DisplayObject;
   import flash.geom.Point;
   import flash.utils.Timer;
   import flash.utils.getTimer;
   import flash.events.TimerEvent;
   import ui.bindings.UIBindingSystem;''')
    source = replace(source, '      private var m_groupMembersDb:uiDB;', '''      private var m_groupMembersDb:uiDB;
      private var m_voiceRefresh:Timer;
      private var m_voiceReceivedAt:int = -10000;
      private var m_voiceReport:String = "";''')
    source = replace(source, '         HudGroupVoiceManager.getInstance().init();', '''         HudGroupVoiceManager.getInstance().init();
         this.clearCompanionVoice();
         this.m_voiceRefresh = new Timer(200);
         this.m_voiceRefresh.addEventListener(TimerEvent.TIMER,this.expireCompanionVoice,false,0,true);
         this.m_voiceRefresh.start();
         UIBindingSystem.DispatchWallOfData("CRANBERRY_VOICE_HUD_V1","open");''')
    source = replace(source, '         HudGroupVoiceManager.getInstance().deinit();', '''         UIBindingSystem.DispatchWallOfData("CRANBERRY_VOICE_HUD_V1","close");
         if(this.m_voiceRefresh)
         {
            this.m_voiceRefresh.stop();
            this.m_voiceRefresh.removeEventListener(TimerEvent.TIMER,this.expireCompanionVoice);
            this.m_voiceRefresh = null;
         }
         this.clearCompanionVoice();
         HudGroupVoiceManager.getInstance().deinit();''')
    # JPEXS loses the return type for this same-package chained call on recompile.
    source = source.replace('HudGroupVoiceManager.getInstance().init()', 'HudGroupVoiceManager(HudGroupVoiceManager.getInstance()).init()')
    source = source.replace('HudGroupVoiceManager.getInstance().deinit()', 'HudGroupVoiceManager(HudGroupVoiceManager.getInstance()).deinit()')
    return source.rsplit('   }', 1)[0] + extra + '\n   }\n}\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    specs = [('UIRoot.gfx', 'Assets_060.pack', {CONSOLE: (patch_console, {'handlePrintConsole'})}),
             ('HudGroupVoiceWindow.gfx', 'Assets_029.pack', {WINDOW: (patch_window, {'enter', 'exit', 'clearCompanionVoice', 'receiveCompanionVoice', 'expireCompanionVoice'}), ROW: (patch_row, {'update'})})]
    manifests = []
    for asset_name, pack_name, patches in specs:
        build = out / Path(asset_name).stem
        build.mkdir(exist_ok=True)
        pack_path = args.client.resolve() / 'Resources/Assets' / pack_name
        raw_pack = pack_path.read_bytes()
        entries = [e for e in pack.entries(raw_pack) if e[0] == asset_name]
        if len(entries) != 1:
            raise ValueError('Missing or duplicate asset ' + asset_name)
        entry = entries[0]
        original = raw_pack[entry[2]:entry[2] + entry[3]]
        asset = build / ('original-' + asset_name)
        asset.write_bytes(original)
        with (build / 'compiler.log').open('w', encoding='utf-8') as log:
            def run(arguments):
                subprocess.run(java + arguments, stdout=log, stderr=subprocess.STDOUT, check=True)
            run(['-export', 'script', str(build / 'before'), str(asset)])
            expected_changed = set()
            for name, (patch, _) in patches.items():
                previous = (build / 'before/scripts' / name).read_text(encoding='utf-8')
                updated = patch(previous)
                if previous == updated:
                    continue
                expected_changed.add(name)
                target = build / 'patch' / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(updated, encoding='utf-8', newline='\n')
            if expected_changed:
                run(['-onerror', 'abort', '-importScript', str(asset), str(build / 'compiled.gfx'), str(build / 'patch')])
                built = gfx.preserve_tags(original, (build / 'compiled.gfx').read_bytes())
            else:
                built = original
            candidate = build / asset_name
            candidate.write_bytes(built)
            run(['-export', 'script', str(build / 'after'), str(candidate)])
        before, after = build / 'before/scripts', build / 'after/scripts'
        originals = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
        candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
        if originals.keys() != candidates.keys():
            raise ValueError('Class inventory changed')
        changed = {name for name, path in originals.items() if path.read_bytes() != candidates[name].read_bytes()}
        if changed != expected_changed:
            raise ValueError('Unexpected changed classes: ' + str(changed))
        for name, (_, allowed) in patches.items():
            methods.verify_unrelated_methods(originals[name].read_text(encoding='utf-8'), candidates[name].read_text(encoding='utf-8'), allowed)
        manifest = dict(asset=asset_name, pack=pack_name, pack_path=str(pack_path),
                        source_pack_sha256=hashlib.sha256(raw_pack).hexdigest(),
                        source_sha256=hashlib.sha256(original).hexdigest(), output_sha256=hashlib.sha256(built).hexdigest(),
                        output=str(candidate), changed_classes=sorted(changed), unchanged_classes=len(originals)-len(changed),
                        non_script_tags_preserved=True, unrelated_methods_preserved=True)
        (build / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        manifests.append(manifest)
    subprocess.run(['node', str(Path(__file__).with_name('verify-proximity-voice.cjs')), str(out)], check=True)
    (out / 'manifest.json').write_text(json.dumps(manifests, indent=2), encoding='utf-8')
    print(json.dumps(manifests, indent=2))


if __name__ == '__main__':
    main()
