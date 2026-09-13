#!/usr/bin/env python3
"""Extend the installed August UIRoot with friends/chat and account avatars; verify unrelated code and tags."""
import argparse, hashlib, importlib.util, json, re, subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
def module(name, file):
    spec = importlib.util.spec_from_file_location(name, HERE / file)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result); return result
social = module('overlay_social', 'build-ingame-social.py')
gfx = module('overlay_gfx', 'build-helmet-click.py')
CONSOLE = social.CONSOLE
LOBBY = social.LOBBY
STATE = 'ui/states/UIState_InGame.as'

def add_methods(source, file):
    additions = (HERE / file).read_text(encoding='utf-8')
    at = source.index('\n   }\n}')
    return source[:at] + '\n' + additions + source[at:]

def patch_console(source):
    source = social.replace(source, '   import flash.display.Stage;', '''   import flash.display.Stage;
   import flash.display.Sprite;
   import flash.display.DisplayObjectContainer;
   import flash.display.InteractiveObject;
   import flash.text.TextField;
   import flash.text.TextFieldType;
   import flash.text.TextFormat;
   import flash.events.TimerEvent;
   import flash.utils.Timer;
   import flash.utils.getTimer;''')
    source = social.replace(source, '      private var m_stage:Stage;', '''      private var m_stage:Stage;
      private static var m_overlay:UIConsoleManager;
      private var m_overlayRoot:Sprite;
      private var m_overlayPanel:Sprite;
      private var m_overlayList:Sprite;
      private var m_overlayPeer:TextField;
      private var m_overlayHistory:TextField;
      private var m_overlayInput:TextField;
      private var m_overlayStatus:TextField;
      private var m_overlayTimer:Timer;
      private var m_overlayFlags:uint;
      private var m_overlayMouseHidden:Boolean;
      private var m_overlayMovement:Boolean;
      private var m_overlayKeyboard:Boolean;
      private var m_overlayFocus:InteractiveObject;
      private var m_overlayJobs:Array = [];
      private var m_overlayFriends:Array = [];
      private var m_overlayAvatars:Object = {};
      private var m_overlayPainted:Dictionary = new Dictionary(true);
      private var m_overlayDrafts:Object = {};
      private var m_overlayPending:Object;
      private var m_overlayFriend:String = "";
      private var m_overlaySelf:String = "";
      private var m_overlayWaiting:String = "";
      private var m_overlayToggleId:String = "";
      private var m_overlayHistoryKey:String = "";
      private var m_overlayStateKey:String = "";
      private var m_overlaySentAt:int;
      private var m_overlayNextPoll:int;
      private var m_overlayScroll:int;''')
    source = social.replace(source, '         this.m_stage = param1;', '         this.m_stage = param1;\n         this.overlayInitialize();')
    source = social.replace(source, '      public function deinitialize() : void\n      {', '      public function deinitialize() : void\n      {\n         this.overlayDeinitialize();')
    source = social.replace(source, '         if(UILobbyFriendsManager.LocalReceive(_loc2_))', '         if(this.overlayReceive(_loc2_)) return;\n         if(UILobbyFriendsManager.LocalReceive(_loc2_))')
    return add_methods(source, 'social-overlay-methods.as')

def patch_lobby(source):
    source = social.replace(source, '   import flash.display.Stage;', '''   import flash.display.Stage;
   import flash.display.DisplayObject;
   import flash.display.Sprite;
   import flash.geom.Rectangle;
   import views.console.UIConsoleManager;''')
    # Keep existing M/F rows compatible with clients from before avatars. P rows add metadata.
    source = social.replace(source, '         m_localSelf = header[1];', '''         for each(row in rows.slice(1))
         {
            fields = row.split("|");
            if(fields[0] == "P" && fields.length == 5)
            {
               for each(var person:Object in members.concat(friends))
               {
                  if(person.Name == decodeURIComponent(fields[4]))
                  { person.AccountId = decodeURIComponent(fields[2]); person.AvatarVersion = fields[3]; }
               }
            }
         }
         m_localSelf = header[1];''')
    return add_methods(source, 'social-lobby-avatars.as')

def patch_state(source):
    source = social.replace(source, '   import ui.bindings.UIBindingKeyboard;', '   import ui.bindings.UIBindingKeyboard;\n   import views.console.UIConsoleManager;')
    return social.replace(source, 'var blocked:Boolean = ', 'var blocked:Boolean = UIConsoleManager.OverlayActive() || ')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--asset', type=Path, required=True); parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True); args = parser.parse_args()
    out = args.out.resolve(); out.mkdir(parents=True, exist_ok=True)
    original = args.asset.read_bytes(); java = ['java','-jar',str(args.ffdec.resolve())]
    patches = {CONSOLE:patch_console, LOBBY:patch_lobby, STATE:patch_state}
    with (out/'compiler.log').open('w',encoding='utf-8') as log:
        def run(arguments): subprocess.run(java + arguments,stdout=log,stderr=subprocess.STDOUT,check=True)
        run(['-export','script',str(out/'before'),str(args.asset.resolve())])
        for name,patch in patches.items():
            target = out/'patch'/name; target.parent.mkdir(parents=True,exist_ok=True)
            target.write_text(patch((out/'before/scripts'/name).read_text(encoding='utf-8')),encoding='utf-8',newline='\n')
        run(['-onerror','abort','-importScript',str(args.asset.resolve()),str(out/'compiled.gfx'),str(out/'patch')])
        built = gfx.preserve_tags(original,(out/'compiled.gfx').read_bytes()); candidate = out/'UIRoot.gfx'; candidate.write_bytes(built)
        run(['-export','script',str(out/'after'),str(candidate)])
    before,after = out/'before/scripts',out/'after/scripts'
    originals = {p.relative_to(before).as_posix():p for p in before.rglob('*.as')}
    candidates = {p.relative_to(after).as_posix():p for p in after.rglob('*.as')}
    if originals.keys() != candidates.keys(): raise ValueError('Class inventory changed')
    changed = [name for name in originals if originals[name].read_bytes() != candidates[name].read_bytes()]
    if set(changed) != set(patches): raise ValueError('Unexpected class changes: ' + str(changed))
    allowed = {CONSOLE:{'initialize','deinitialize','handlePrintConsole'},LOBBY:{'LocalReceive'},STATE:{'refreshInventoryMovement'}}
    for name in patches:
        social.verify_unrelated_methods(originals[name].read_text(encoding='utf-8'),candidates[name].read_text(encoding='utf-8'),allowed[name])
    subprocess.run(['node',str(HERE/'verify-social-overlay.cjs'),str(after)],check=True)
    manifest = {'asset':'UIRoot.gfx','pack':'Assets_060.pack','source_sha256':hashlib.sha256(original).hexdigest(),
        'output_sha256':hashlib.sha256(built).hexdigest(),'output':str(candidate),'changed_classes':changed,
        'unchanged_classes':len(originals)-len(changed),'non_script_tags_preserved':True,
        'unrelated_methods_preserved':True,'handler_tests_passed':True}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8'); print(json.dumps(manifest,indent=2))
if __name__ == '__main__': main()
