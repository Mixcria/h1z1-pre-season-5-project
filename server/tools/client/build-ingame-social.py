#!/usr/bin/env python3
"""Build the August friends/lobby adapter from the currently installed UIRoot.

No executable or Steam DLL changes. Preserve non-script tags and every unrelated
class; compile and re-export the candidate before it can be installed.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


methods = module('social_method_tools', 'build-emote-preview.py')
gfx = module('social_gfx_tools', 'build-helmet-click.py')
LOBBY = 'views/lobbyfriends/UILobbyFriendsManager.as'
CONSOLE = 'views/console/UIConsoleManager.as'
MENU = 'views/mainmenu/UiMainMenuManager.as'


def replace(source, old, new):
    return methods.replace_once(source, old, new)


def method_span(source, name):
    match = re.search(r'^\s*(?:override )?(?:public|protected|private) (?:static )?function ' + re.escape(name) + r'\(', source, re.MULTILINE)
    if not match:
        raise ValueError('Missing method ' + name)
    start = source.index('{', match.start())
    depth, end = 1, start + 1
    while depth:
        if source[end] == '{': depth += 1
        elif source[end] == '}': depth -= 1
        end += 1
    return match.start(), end


def patch_lobby(source):
    source = replace(source, '   import flash.display.Stage;', '''   import flash.display.Stage;
   import flash.utils.Timer;
   import flash.events.TimerEvent;
   import ui.bindings.UIBindingChat;
   import ui.core.events.GameEvent;''')
    source = replace(source, '      private static const LOBBY_MAX:int = 5;', '''      private static const LOBBY_MAX:int = 5;
      private static var m_local:UILobbyFriendsManager;
      private static var m_localSelf:String = "0";
      private static var m_localLeader:String = "0";
      private static var m_localMembers:Array = [];
      private static var m_localFriends:Array = [];
      private static var m_localInvite:String = "";
      private static var m_localQueue:String = "";
      private var m_localTimer:Timer;''')
    source = replace(source, '         this.m_isInitalized = true;', '''         this.m_isInitalized = true;
         m_local = this;''')
    # Both the initial binding and the Friends tab previously created Steam-backed views.
    source = source.replace('new uiDBProvider(uiDBManager.createView("UIFriends",LobbyFriendsQueryStrings.getFriendsQuery()))', 'new DataProvider()')
    source = replace(source, '         this.createLobby();\n      }', '''         this.m_localTimer = new Timer(2000);
         this.m_localTimer.addEventListener(TimerEvent.TIMER,this.pollLocalSocial,false,0,true);
         this.m_localTimer.start();
         this.m_stage.dispatchEvent(new WidgetEvent(WidgetEvent.OPEN_WIDGET,WidgetNames.HUD_SYSTEM_MESSAGES_WINDOW));
         this.createLobby();
      }''')
    source = replace(source, '         UIBindingLobby.ClearRecentGroupMembers();', '''         this.m_localTimer.stop();
         this.m_localTimer.removeEventListener(TimerEvent.TIMER,this.pollLocalSocial);
         this.m_stage.dispatchEvent(new WidgetEvent(WidgetEvent.CLOSE_WIDGET,WidgetNames.HUD_SYSTEM_MESSAGES_WINDOW));
         m_local = null;
         m_localSelf = m_localLeader = "0";
         m_localMembers = [];
         m_localFriends = [];
         m_localQueue = "";
         m_localInvite = "";''')
    source = replace(source, '         this.m_friendsButton.count.text = _loc3_ ? "(" + this.m_friendsDB.length + ")" : "";',
        '         this.m_friendsButton.count.text = "(" + m_localFriends.length + ")";')
    for kind in ('recent', 'suggested'):
        source = replace(source, f'         this.m_{kind}Button.enabled = _loc3_;', f'         this.m_{kind}Button.enabled = false;')
        source = replace(source, f'         this.m_{kind}Button.count.text = _loc3_ ? "(" + this.m_{kind}DB.length + ")" : "";',
                         f'         this.m_{kind}Button.count.text = "";')
    additions = Path(__file__).with_name('social-lobby-methods.as').read_text(encoding='utf-8')
    for name in re.findall(r'function (\w+)\(', additions):
        start, end = method_span(additions, name)
        new = additions[start:end]
        if re.search(r'function ' + name + r'\(', source):
            left, right = method_span(source, name)
            source = source[:left] + new + source[right:]
        else:
            source = source.rsplit('   }', 1)[0] + new + '\n   }\n}\n'
    return source


def patch_console(source):
    source = replace(source, '   import views.MainController;', '   import views.MainController;\n   import views.lobbyfriends.UILobbyFriendsManager;')
    start, end = methods.method_span(source, 'handlePrintConsole')
    body = replace(source[start:end], '         var _loc2_:String = String(param1.data[0]);', '''         var _loc2_:String = String(param1.data[0]);
         if(UILobbyFriendsManager.LocalReceive(_loc2_)) return;''')
    return source[:start] + body + source[end:]


def patch_menu(source):
    source = replace(source, '   import ui.bindings.UIBindingLobby;', '   import ui.bindings.UIBindingLobby;\n   import views.lobbyfriends.UILobbyFriendsManager;')
    anchor = '         var _loc3_:Boolean = UIBindingLobby.IsLobbyOwner() as Boolean;'
    source = replace(source, anchor, anchor + '''
         if(UILobbyFriendsManager.LocalActive())
         {
            if(!UILobbyFriendsManager.LocalLeader())
            {
               UILobbyFriendsManager.LocalNotice("The group leader chooses the match.");
               return;
            }
            if(!UILobbyFriendsManager.LocalCanQueue(param1 >= 0 ? param1 : this.m_lobbyType))
            {
               UILobbyFriendsManager.LocalNotice("Choose a game mode with room for your whole group.");
               return;
            }
            _loc2_ = false;
            _loc3_ = true;
         }''')
    source = replace(source, '''         UIBindingLobby.AutoFillTeam(this.m_isAutoFill);
         UIBindingLobby.SendStartGameMessage(this.m_currentlySelectedServer);''', '''         if(!UILobbyFriendsManager.LocalActive())
         {
            UIBindingLobby.AutoFillTeam(this.m_isAutoFill);
            UIBindingLobby.SendStartGameMessage(this.m_currentlySelectedServer);
         }''')
    anchor = '''      protected function handleSteamLobbyStartGame(param1:GameEvent) : void
      {'''
    source = replace(source, anchor, anchor + '''
         if(UILobbyFriendsManager.LocalActive())
         {
            if(param1.data[1] != "cranberry" || UILobbyFriendsManager.LocalLeader()) return;
            this.setCurrentlySelectedServer(int(param1.data[0]));
            this.m_lobbyType = int(param1.data[2]);
            SharedGlobalData.GetInstance().isTeam2 = this.m_lobbyType == 2;
            SharedGlobalData.GetInstance().isInGroupGame = true;
            UIBindingCharacterCreate.TransferCharacter(this.m_playerGuid,this.m_currentlySelectedServer);
            return;
         }''')
    return source


def verify_unrelated_methods(original, compiled, changed):
    def normal(text):
        text = text.replace('Number(NaN)', 'NaN')
        return re.sub(r'\s+', '', text)
    names = re.findall(r'function (\w+)\(', original)
    for name in names:
        if name in changed:
            continue
        a, b = method_span(original, name)
        c, d = method_span(compiled, name)
        if normal(original[a:b]) != normal(compiled[c:d]):
            raise ValueError('Unrelated method changed: ' + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--asset', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    asset = args.asset.resolve()
    original = asset.read_bytes()
    java = ['java', '-jar', str(args.ffdec.resolve())]
    patches = {LOBBY: patch_lobby, CONSOLE: patch_console, MENU: patch_menu}
    with (out / 'compiler.log').open('w', encoding='utf-8') as log:
        def run(arguments):
            subprocess.run(java + arguments, stdout=log, stderr=subprocess.STDOUT, check=True)
        run(['-export', 'script', str(out / 'before'), str(asset)])
        for name, patch in patches.items():
            target = out / 'patch' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(patch((out / 'before/scripts' / name).read_text(encoding='utf-8')), encoding='utf-8', newline='\n')
        run(['-onerror', 'abort', '-importScript', str(asset), str(out / 'compiled.gfx'), str(out / 'patch')])
        built = gfx.preserve_tags(original, (out / 'compiled.gfx').read_bytes())
        candidate = out / 'UIRoot.gfx'
        candidate.write_bytes(built)
        run(['-export', 'script', str(out / 'after'), str(candidate)])
    before, after = out / 'before/scripts', out / 'after/scripts'
    originals = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
    candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
    if originals.keys() != candidates.keys():
        raise ValueError('Class inventory changed')
    changed = []
    for name, path in originals.items():
        if path.read_bytes() != candidates[name].read_bytes():
            if name not in patches:
                raise ValueError('Unrelated class changed: ' + name)
            changed.append(name)
    if set(changed) != set(patches):
        raise ValueError('Expected exactly three changed classes')
    lobby_methods = set(re.findall(r'function (\w+)\(', Path(__file__).with_name('social-lobby-methods.as').read_text(encoding='utf-8')))
    allowed = {LOBBY: lobby_methods | {'initialize', 'deinitialize', 'processFriendsButton', 'processRecentButton', 'processSuggestedButton'},
               CONSOLE: {'handlePrintConsole'}, MENU: {'requestEnterGameQueue', 'CallGroupTransfer', 'handleSteamLobbyStartGame'}}
    for name in patches:
        verify_unrelated_methods(originals[name].read_text(encoding='utf-8'), candidates[name].read_text(encoding='utf-8'), allowed[name])
    subprocess.run(['node', str(Path(__file__).with_name('verify-ingame-social.cjs')), str(after)], check=True)
    manifest = {'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack', 'source_sha256': hashlib.sha256(original).hexdigest(),
        'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(candidate), 'changed_classes': changed,
        'unchanged_classes': len(originals) - len(changed), 'non_script_tags_preserved': True,
        'unrelated_methods_preserved': True, 'handler_tests_passed': True}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
