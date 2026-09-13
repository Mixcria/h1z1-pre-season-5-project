#!/usr/bin/env python3
"""Build, never install, the public-Solo pregame bounty UI from the installed August UIRoot.

The server's 67 10 costs are the offer: all zero revokes it. ce 15 retains its actual
IsInBox meaning for every pregame mode. Only ui.states.UIState_InGame is recompiled.
All non-DoABC tags and source trailer bytes are preserved, and the output is re-exported.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = 'dac8eb53d1aeb2b50c63e8984f80260f126c99562e1fc2d0a4685623d9696bd4'
CLASS = 'ui.states.UIState_InGame'
RELATIVE = Path('scripts/ui/states/UIState_InGame.as')


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Unrecognized UI source anchor: ' + old[:100])
    return source.replace(old, new, 1)


def patch_source(source):
    source = replace_once(source, '   import flash.display.Stage;', '''   import flash.display.Stage;
   import ui.db.uiDB;
   import ui.db.uiDBEvent;
   import ui.db.uiDBManager;''')
    source = replace_once(source, '      private var m_matchOver:Boolean = false;', '''      private var m_matchOver:Boolean = false;
      private var m_bountyOfferDb:uiDB;
      private var m_bountyArrivalOpened:Boolean = false;
      private var m_bountyMatchStarted:Boolean = false;
      private var m_bountyOfferVisible:Boolean = false;
      private var m_bountyMenuPending:Boolean = false;''')
    source = replace_once(source, '         this._state_bindings.init();', '''         this._state_bindings.init();
         this.m_bountyArrivalOpened = false;
         this.m_bountyMatchStarted = false;
         this.m_bountyOfferVisible = false;
         this.m_bountyMenuPending = false;
         this.m_bountyOfferDb = uiDBManager.getTable("MatchBounty");
         if(this.m_bountyOfferDb)
         {
            this.m_bountyOfferDb.addEventListener(uiDBEvent.CHANGED,this.handleBountyOfferChanged,false,0,true);
         }
         this.m_stageRef.addEventListener(GameEvent.EVENT_START_MATCH,this.handleBountyMatchStart,false,0,true);''')
    source = replace_once(source, '''            UIBindingMatch.ClearMatchData();
            this.openMenu();
            this.hideLeftSideHud();''', '''            UIBindingMatch.ClearMatchData();''')
    source = replace_once(source, '''         if(UIMatchManager.gameType == UIMatchManager.GAMETYPE_TRAINING)
         {
            this.m_matchOver = false;
         }
      }''', '''         if(UIMatchManager.gameType == UIMatchManager.GAMETYPE_TRAINING)
         {
            this.m_matchOver = false;
         }
         this.handleBountyOfferChanged();
      }''')
    source = replace_once(source, '''         SpectatorManager.getInstance().deinit();''', '''         SpectatorManager.getInstance().deinit();
         this.m_stageRef.removeEventListener(GameEvent.EVENT_START_MATCH,this.handleBountyMatchStart,false);
         if(this.m_bountyOfferDb)
         {
            this.m_bountyOfferDb.removeEventListener(uiDBEvent.CHANGED,this.handleBountyOfferChanged,false);
            this.m_bountyOfferDb = null;
         }''')
    original = 'Boolean(UIBindingMatch.IsInBox()) && Boolean(UIMatchManager.gameType == UIMatchManager.GAMETYPE_SOLO) && !UIBindingSystem.isController()'
    if source.count(original) != 2:
        raise ValueError('Expected two bounty tab gates')
    source = source.replace(original, 'this.isBountyOffered()')
    original = 'Boolean(UIBindingMatch.IsInBox()) && UIMatchManager.gameType == UIMatchManager.GAMETYPE_SOLO'
    if source.count(original) != 2:
        raise ValueError('Expected two inventory tab-index gates')
    source = source.replace(original, 'this.isBountyOffered()')
    for method in ('handleEscapeToggleEvent', 'handleInventoryToggleEvent'):
        anchor = '      private function ' + method + '(param1:GameEvent) : void\n      {'
        source = replace_once(source, anchor, anchor + '\n         this.m_bountyMenuPending = false;')
    source = replace_once(source, '''      private function handleWidgetEvent(param1:WidgetEvent) : void
      {''', '''      private function handleWidgetEvent(param1:WidgetEvent) : void
      {
         if(param1.type == WidgetEvent.WIDGET_OPENED)
         {
            if(!this.isBountyOffered() && (param1.widgetId == WidgetNames.BOUNTY_WINDOW ||
               this.m_bountyMenuPending && (param1.widgetId == WidgetNames.TAB_NAVIGATION_WINDOW ||
               param1.widgetId == WidgetNames.TAB_NAVIGATION_BACKGROUND)))
            {
               this.m_stageRef.dispatchEvent(new WidgetEvent(WidgetEvent.CLOSE_WIDGET,param1.widgetId));
               this.closeMenu(false,true);
               return;
            }
            if(param1.widgetId == WidgetNames.BOUNTY_WINDOW)
            {
               this.m_bountyMenuPending = false;
            }
         }''')
    methods = '''      private function isBountyOffered() : Boolean
      {
         if(this.m_bountyMatchStarted || !UIBindingMatch.IsInBox() ||
            UIMatchManager.gameType != UIMatchManager.GAMETYPE_SOLO ||
            UIBindingSystem.isController() || UIBindingSystem.InInvitational())
         {
            return false;
         }
         var rows:Array = uiDBManager.query("SELECT * FROM MatchBounty");
         if(!rows || rows.length == 0)
         {
            return false;
         }
         return Number(rows[0]["HardCurrencyBounty"]) > 0 ||
            Number(rows[0]["SoftCurrencyBounty"]) > 0 || Number(rows[0]["FreeCurrencyBounty"]) > 0;
      }

      private function handleBountyOfferChanged(param1:uiDBEvent = null) : void
      {
         var offered:Boolean = this.isBountyOffered();
         if(this.m_bountyOfferVisible && !offered)
         {
            this.closeMenu(false,true);
         }
         this.m_bountyOfferVisible = offered;
         this.m_stageRef.dispatchEvent(new UIDataBindingEvent(UIDataBindingEvent.UPDATE,
            UITabNavigationManager.KEY_NAVIGATION_DATA,this.getMenuDataProvider()));
         if(offered && !this.m_bountyArrivalOpened && !this.m_displayStack.modalOpen)
         {
            this.m_bountyArrivalOpened = true;
            this.m_bountyMenuPending = true;
            this.openMenu(0);
         }
      }

      private function handleBountyMatchStart(param1:GameEvent) : void
      {
         this.m_bountyMatchStarted = true;
         this.closeMenu(false,true);
         this.handleBountyOfferChanged();
      }

'''
    source = replace_once(source, '      private function getMenuDataProvider() : IDataProvider',
                          methods + '      private function getMenuDataProvider() : IDataProvider')
    # JPEXS exports this script's private helper outside the package block. Dynamic
    # public lookup retains the original init/deinit dispatch without its compiler's
    # incorrect package-internal resolution for that helper.
    source = source.replace('this._state_bindings.init();', 'Object(this._state_bindings)["init"]();')
    source = source.replace('this._state_bindings.deinit();', 'Object(this._state_bindings)["deinit"]();')
    source = source.replace('SpectatorManager.getInstance().init(', 'SpectatorManager.getInstance()["init"](')
    source = source.replace('SpectatorManager.getInstance().deinit(', 'SpectatorManager.getInstance()["deinit"](')
    return source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
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
    for required in ('isBountyOffered', 'handleBountyMatchStart', 'handleBountyOfferChanged',
                     'm_bountyMenuPending', 'HardCurrencyBounty', 'SoftCurrencyBounty',
                     'FreeCurrencyBounty', 'InInvitational'):
        if required not in verified:
            raise ValueError('Compiled patch missing ' + required)
    manifest = {'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack', 'source_sha256': SOURCE_SHA256,
                'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(destination),
                'source_script_sha256': hashlib.sha256(original.encode()).hexdigest(),
                'verified_script_sha256': hashlib.sha256(verified.encode()).hexdigest(),
                'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
