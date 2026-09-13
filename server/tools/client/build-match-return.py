"""Repair current match-result actions, pause-menu exit and teardown callbacks."""
import argparse, hashlib, importlib.util, json, os, re, subprocess
from pathlib import Path
HERE = Path(__file__).resolve().parent
def module(name, file):
    spec = importlib.util.spec_from_file_location(name, HERE / file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value
base = module('return_base', 'build-hosted-panel.py')
old = module('return_old', 'build-match-exit.py')
MANAGER, SETTINGS = old.MANAGER, old.SETTINGS
GROUP = 'ui/managers/UIGroupManager.as'
STATE = 'ui/states/UIState_InGame.as'
ALLOWED = {
    MANAGER: {'deinitialize','cleanUpMatchData','processActionButton','handleButtonEvent','handleGroupWin',
        'handleMatchSessionReady','requestMatchAction','handleNativeMatchExit','finishNativeMatchExit',
        'installResultButtons','removeResultButtons','handleResultClick','handleResultKey'},
    SETTINGS: {'handleLogoutConfirmationResponse'},
    GROUP: {'handleSpectateExitButtonEvent'},
    STATE: {'OnEnter','OnExit'},
}
def patch_manager(source):
    source = base.replace(source, '      private var m_victoryTimeoutId:uint = 0;',
        '      private var m_victoryTimeoutId:uint = 0;\n      private var m_nativeExitTimeoutId:uint = 0;')
    extra = (HERE / 'match-return-methods.as').read_text()
    for name in ['handleMatchSessionReady','requestMatchAction','handleNativeMatchExit','finishNativeMatchExit',
                 'installResultButtons','removeResultButtons']:
        a,b = base.methods.method_span(extra, name)
        source = base.replace_method(source, name, extra[a:b]); extra = extra[:a]+extra[b:]
    source = source.rsplit('   }', 1)[0] + extra + '\n   }\n}\n'
    source = base.method_replace(source,'deinitialize',
        '         this._stage.removeEventListener("CranberryMatchExit",this.handleNativeMatchExit);',
        '''         clearTimeout(this.m_nativeExitTimeoutId);
         this.m_nativeExitTimeoutId = 0;
         this.m_nativeExitStarted = false;
         this.cleanUpMatchData();
         this._stage.removeEventListener("CranberryMatchExit",this.handleNativeMatchExit);''')
    source = base.method_replace(source,'cleanUpMatchData',
        '         UIBindingMatch.ClearMatchData();\n         this.removeMatchListeners();',
        '         this.removeMatchListeners();\n         this.clearResultButtons();\n         UIBindingMatch.ClearMatchData();')
    source = base.method_replace(source,'handleGroupWin',
        '         setTimeout(this.showTheGroupVictoryScreen,_loc2_);',
        '         clearTimeout(this.m_victoryTimeoutId);\n         this.m_victoryTimeoutId = setTimeout(this.showTheGroupVictoryScreen,_loc2_);')
    source = base.method_replace(source,'handleButtonEvent',
        '            case "processActionButton":\n               this.requestMatchAction("play");',
        '            case "processActionButton":\n               this.requestMatchAction("menu");')
    source = base.method_replace(source,'processActionButton',
        '            param2.removeEventListener(ButtonEvent.CLICK,this.handleButtonEvent,false);',
        '            this.detachResultButton(param2);\n            param2.removeEventListener(ButtonEvent.CLICK,this.handleButtonEvent,false);')
    for name in ['handleResultClick','handleResultKey']:
        source = base.method_replace(source,name,
            '         this.requestMatchAction(event.currentTarget.name);' if name == 'handleResultClick' else '            this.requestMatchAction(event.currentTarget.name);',
            ('         ' if name == 'handleResultClick' else '            ') + 'event.stopPropagation();\n' +
            ('         ' if name == 'handleResultClick' else '            ') + 'this.requestMatchAction(event.currentTarget.name);')
    return source
def patch_state(source):
    for name,method in [('OnEnter','addEventListener'),('OnExit','removeEventListener')]:
        suffix = ',false,0,true' if method == 'addEventListener' else ''
        source = base.method_replace(source,name,'         this.m_hostedMapWorld = "";',
            '         this.m_hostedMapWorld = "";\n         this.m_stageRef.'+method+'("CranberryCloseMatchMenu",this.handleCloseMatchMenu'+suffix+');')
    source = base.method_replace(source,'OnExit',
        '         this.m_hostedMapWorld = "";',
        '         this.m_hostedMapWorld = "";\n         this.m_stageRef.removeEventListener(Event.ENTER_FRAME,this.closeMatchMenuAfterConfirmation);')
    at = source.index('\n   }\n}')
    return source[:at]+'''
      private function handleCloseMatchMenu(event:GameEvent) : void
      {
         // ConfirmationDialogWindow invokes its callback before clearing it.
         // Closing that modal synchronously re-enters the callback indefinitely.
         this.m_stageRef.addEventListener(Event.ENTER_FRAME,this.closeMatchMenuAfterConfirmation,false,0,true);
      }

      private function closeMatchMenuAfterConfirmation(event:Event) : void
      {
         if(this.m_displayStack.modalOpen) return;
         this.m_stageRef.removeEventListener(Event.ENTER_FRAME,this.closeMatchMenuAfterConfirmation);
         this.closeMenu();
      }
'''+source[at:]
def patch_group(source):
    return base.method_replace(source,'handleSpectateExitButtonEvent','         UIBindingSystem.Logout();',
        '         UIBindingSystem.DispatchWallOfData("CRANBERRY_MATCH_ACTION_V1","menu");')
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--client',type=Path,required=True);p.add_argument('--ffdec',type=Path,required=True);p.add_argument('--out',type=Path,required=True)
    args=p.parse_args();out=args.out.resolve();out.mkdir(parents=True,exist_ok=False)
    pack_path=args.client.resolve()/'Resources/Assets/Assets_060.pack';raw_pack=pack_path.read_bytes()
    entry,=[e for e in base.pack.entries(raw_pack) if e[0]=='UIRoot.gfx'];raw=raw_pack[entry[2]:entry[2]+entry[3]]
    asset=out/'original.gfx';asset.write_bytes(raw);classes=out/'java';classes.mkdir()
    patches={MANAGER:patch_manager,GROUP:patch_group,STATE:patch_state}
    with (out/'compiler.log').open('w',encoding='utf-8') as log:
        def run(args): subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,check=True)
        def ff(args_):run(['java','-jar',str(args.ffdec),*args_])
        ff(['-export','script',str(out/'before'),str(asset)])
        run(['javac','-cp',str(args.ffdec),'-d',str(classes),str(HERE/'BinocularHudBytecode.java'),str(HERE/'MatchReturnSettingsBytecode.java'),str(HERE/'VerifyMatchExitBytecode.java')])
        settings=out/'settings.gfx'
        run(['java','-cp',os.pathsep.join([str(classes),str(args.ffdec)]),'MatchReturnSettingsBytecode',str(asset),str(settings)])
        for name,patch in patches.items():
            target=out/'patch'/name;target.parent.mkdir(parents=True,exist_ok=True)
            target.write_text(patch((out/'before/scripts'/name).read_text(encoding='utf-8')),encoding='utf-8')
        ff(['-onerror','abort','-importScript',str(settings),str(out/'compiled.gfx'),str(out/'patch')])
        built=base.gfx.preserve_tags(raw,(out/'compiled.gfx').read_bytes());candidate=out/'UIRoot.gfx';candidate.write_bytes(built)
        ff(['-export','script',str(out/'after'),str(candidate)])
    before,after=out/'before/scripts',out/'after/scripts'
    originals={p.relative_to(before).as_posix():p for p in before.rglob('*.as')}
    candidates={p.relative_to(after).as_posix():p for p in after.rglob('*.as')}
    assert originals.keys()==candidates.keys()
    changed={n for n in originals if originals[n].read_bytes()!=candidates[n].read_bytes()}
    if changed != set(ALLOWED): raise ValueError('Unexpected changed classes: '+str(changed))
    for name,allowed in ALLOWED.items():
        base.methods.verify_unrelated_methods(originals[name].read_text(encoding='utf-8'),candidates[name].read_text(encoding='utf-8'),allowed)
    result=dict(asset='UIRoot.gfx',pack='Assets_060.pack',pack_path=str(pack_path),source_pack_sha256=hashlib.sha256(raw_pack).hexdigest(),
        source_sha256=hashlib.sha256(raw).hexdigest(),output_sha256=hashlib.sha256(built).hexdigest(),output=str(candidate),
        changed_classes=sorted(changed),unchanged_classes=len(originals)-len(changed),non_script_tags_preserved=True,unrelated_methods_preserved=True)
    (out/'manifest.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))
if __name__=='__main__': main()
