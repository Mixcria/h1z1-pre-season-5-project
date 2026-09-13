"""Build hosted event names/modes from the exact current August UIRoot asset.

The native schedule resolves names through locale ids. This small UI adaptation
reads account-specific display metadata from the existing string-hash binding.
It also maps console /help to the registered /commands spelling. It changes no
admission behavior, unrelated scripts, graphics or pack files.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = 'd8e7c87b483e451ae72b000adb1671fd710af3da4499dde6563a1bc2db7f0715'
CLASS = 'views.events.UIEventsManager'
RELATIVE = Path('scripts/views/events/UIEventsManager.as')
CONSOLE_CLASS = 'views.console.UIConsoleManager'
CONSOLE_RELATIVE = Path('scripts/views/console/UIConsoleManager.as')
HELPER = '''      private function hostedGameDisplayRows(param1:Array) : Array
      {
         var rows:Array = [];
         for each(var sourceRow:Object in param1)
         {
            var row:Object = new Object();
            for(var field:String in sourceRow)
            {
               row[field] = sourceRow[field];
            }
            var key:String = "Cranberry.Hosted." + String(row.WorldId);
            var hostedName:String = String(UIBindingSystem.GetStringHashValue(key + ".Name",String(row.Name)));
            var hostedMode:String = String(UIBindingSystem.GetStringHashValue(key + ".Mode",String(row.GameModeName)));
            if(hostedName != String(row.Name) || hostedMode != String(row.GameModeName))
            {
               row.Name = hostedName + (hostedMode.length > 0 ? " (" + hostedMode + ")" : "");
            }
            row.GameModeName = hostedMode;
            rows.push(row);
         }
         return rows;
      }

      private function handleHostedManagementKey(param1:KeyboardEvent) : void
      {
         if(param1.keyCode != Keyboard.F1 && param1.keyCode != Keyboard.ESCAPE)
         {
            return;
         }
         if(param1.type == KeyboardEvent.KEY_UP)
         {
            if(this.m_hostedManagementKeys[param1.keyCode] || param1.keyCode == Keyboard.F1 && this.m_currentOpenWindow == WidgetNames.EVENTS_HOSTED_GAMES)
            {
               delete this.m_hostedManagementKeys[param1.keyCode];
               param1.stopImmediatePropagation();
            }
            return;
         }
         if(param1.type != KeyboardEvent.KEY_DOWN || this.m_currentOpenWindow != WidgetNames.EVENTS_HOSTED_GAMES)
         {
            return;
         }
         if(param1.keyCode == Keyboard.ESCAPE && !this.m_hostedManagementKeys[param1.keyCode] && !MainController.getWidgetManager().isWidgetLoaded(WidgetNames.CONSOLE_WINDOW) && !MainController.getWidgetManager().isWidgetLoading(WidgetNames.CONSOLE_WINDOW))
         {
            return;
         }
         param1.stopImmediatePropagation();
         if(this.m_hostedManagementKeys[param1.keyCode])
         {
            return;
         }
         this.m_hostedManagementKeys[param1.keyCode] = true;
         this.handleHostedManagement();
      }

      private function handleHostedManagement(param1:GamePadEvent = null) : void
      {
         if(this.m_currentOpenWindow == WidgetNames.EVENTS_HOSTED_GAMES)
         {
            var wasOpen:Boolean = MainController.getWidgetManager().isWidgetLoaded(WidgetNames.CONSOLE_WINDOW) || MainController.getWidgetManager().isWidgetLoading(WidgetNames.CONSOLE_WINDOW);
            if(wasOpen)
            {
               this.m_stage.focus = null;
            }
            this.m_stage.dispatchEvent(new GameEvent(GameEvent.EVENT_TOGGLE_DEBUG_CONSOLE));
            if(!wasOpen)
            {
               UIBindingChat.ProcessChatCommand("/hostgame");
            }
         }
      }

'''

KEY_REGISTRATIONS = '\n'.join(
    '               this.m_stage.addEventListener(KeyboardEvent.' + kind
    + ',this.handleHostedManagementKey,' + capture + ',1000,true);'
    for kind in ['KEY_DOWN', 'KEY_UP'] for capture in ['true', 'false'])
KEY_REMOVALS = '\n'.join(
    '               this.m_stage.removeEventListener(KeyboardEvent.' + kind
    + ',this.handleHostedManagementKey,' + capture + ');'
    for kind in ['KEY_DOWN', 'KEY_UP'] for capture in ['true', 'false'])


def method_span(source, name):
    marker = 'function ' + name + '('
    if source.count(marker) != 1:
        raise ValueError('Expected exactly one method ' + name)
    header = source.rfind('\n', 0, source.index(marker)) + 1
    start = source.index('{', source.index(marker))
    depth, at = 1, start + 1
    while depth and at < len(source):
        depth += (source[at] == '{') - (source[at] == '}')
        at += 1
    if depth:
        raise ValueError('Unbalanced method ' + name)
    return header, at


def patch_source(source):
    if 'Cranberry.Hosted.' in source or 'function hostedGameDisplayRows(' in source:
        raise ValueError('Hosted game display is already patched')
    changes = [
        ('   import flash.events.TimerEvent;',
         '   import flash.events.TimerEvent;\n   import flash.events.KeyboardEvent;\n   import flash.ui.Keyboard;'),
        ('   import ui.bindings.UIBindingPlayer;',
         '   import ui.bindings.UIBindingPlayer;\n   import ui.bindings.UIBindingSystem;\n   import ui.bindings.UIBindingChat;'),
        ('   import ui.core.events.UIEvent;',
         '   import ui.core.events.UIEvent;\n   import ui.core.events.GameEvent;'),
        ('   import views.buttonlegend.LegendDataProvider;',
         '   import views.MainController;\n   import views.buttonlegend.LegendDataProvider;'),
        ('      private var m_currentOpenWindow:uint = 0;',
         '      private var m_currentOpenWindow:uint = 0;\n\n      private var m_hostedManagementKeys:Object = {};'),
        ('this.m_bindings[BINDING_EVENTS_SPECIAL_JOIN_AN_EVENT].setValue(UIBindingLocale.translateCodeString("UI.Events.JoinAHostedEvent"));',
         'this.m_bindings[BINDING_EVENTS_SPECIAL_JOIN_AN_EVENT].setValue("F1: OPEN / CLOSE CONSOLE - ESC: CLOSE");\n' + KEY_REGISTRATIONS),
        ('            case WidgetNames.SPECIAL_EVENTS_WINDOW:\n               if(this.m_currentTimeTimer != null)',
         '            case WidgetNames.SPECIAL_EVENTS_WINDOW:\n' + KEY_REMOVALS + '\n               this.m_hostedManagementKeys = {};\n               if(this.m_currentTimeTimer != null)'),
        ('         this.m_stage.removeEventListener(UIEvent.UI_ENTER,this.handleUIEnter,false);',
         KEY_REMOVALS + '\n         this.m_hostedManagementKeys = {};\n         this.m_stage.removeEventListener(UIEvent.UI_ENTER,this.handleUIEnter,false);'),
        ('               if(_loc1_)\n               {\n                  if(this.m_canJoinGame',
         '               if(this.m_currentOpenWindow == WidgetNames.EVENTS_HOSTED_GAMES)\n               {\n                  this.m_legendDataProvider.addEntry(1,NavigationCode.GAMEPAD_Y,"Open / close console (F1)",this.handleHostedManagement);\n               }\n               if(_loc1_)\n               {\n                  if(this.m_canJoinGame'),
        ('_loc2_ = uiDBManager.query("SELECT * FROM MatchSchedule WHERE IsHostedGame = 1");',
         '_loc2_ = this.hostedGameDisplayRows(uiDBManager.query("SELECT * FROM MatchSchedule WHERE IsHostedGame = 1"));'),
        ('      protected function handleEventsListClicked(',
         HELPER + '      protected function handleEventsListClicked('),
    ]
    for old, new in changes:
        if source.count(old) != 1:
            raise ValueError('Unexpected August hosted menu source: ' + old)
        source = source.replace(old, new, 1)
    return source


def unchanged_class(source, patched=False):
    if patched:
        for name in ['hostedGameDisplayRows', 'handleHostedManagementKey', 'handleHostedManagement']:
            start, end = method_span(source, name)
            source = source[:start] + source[end:]
        for name in ['ui.bindings.UIBindingSystem', 'ui.bindings.UIBindingChat',
                     'flash.events.KeyboardEvent', 'flash.ui.Keyboard', 'ui.core.events.GameEvent', 'views.MainController']:
            source = source.replace('   import ' + name + ';\n', '')
        source = source.replace('      private var m_hostedManagementKeys:Object = {};', '')
    for name in ['handleSpecialEventDataChanged', 'handleUIEnter', 'handleUIExit', 'buildLegend', 'deinitialize']:
        if 'function ' + name + '(' in source:
            start, end = method_span(source, name)
            source = source[:start] + source[end:]
    # FFDec preserves this class except blank lines around newly imported methods.
    return '\n'.join(line for line in source.splitlines() if line.strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError('Installed UIRoot changed; rebase and verify instead of replacing later edits')
    if out == asset.parent:
        raise ValueError('Build output must be separate from the source asset')
    out.mkdir(parents=True, exist_ok=False)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    for class_name in [CLASS, CONSOLE_CLASS]:
        subprocess.run(java + ['-selectclass', class_name, '-export', 'script', str(out / 'source'), str(asset)], check=True)
    original = (out / 'source' / RELATIVE).read_text()
    patch = out / 'patch' / RELATIVE.relative_to('scripts')
    patch.parent.mkdir(parents=True)
    patch.write_text(patch_source(original))
    alias_spec = importlib.util.spec_from_file_location('console_help_alias', Path(__file__).with_name('console-help-alias.py'))
    alias = importlib.util.module_from_spec(alias_spec)
    alias_spec.loader.exec_module(alias)
    console_original = (out / 'source' / CONSOLE_RELATIVE).read_text()
    console_patch = out / 'patch' / CONSOLE_RELATIVE.relative_to('scripts')
    console_patch.parent.mkdir(parents=True)
    console_patch.write_text(alias.patch_source(console_original))
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled), str(out / 'patch')], check=True)
    spec = importlib.util.spec_from_file_location('tag_preserver', Path(__file__).with_name('build-helmet-click.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    built = helper.preserve_tags(raw, compiled.read_bytes())
    destination = out / 'UIRoot.gfx'
    destination.write_bytes(built)
    subprocess.run(java + ['-export', 'script', str(out / 'all-original'), str(asset)], check=True)
    subprocess.run(java + ['-export', 'script', str(out / 'all-patched'), str(destination)], check=True)
    before, after = out / 'all-original/scripts', out / 'all-patched/scripts'
    originals = {path.relative_to(before): path for path in before.rglob('*.as')}
    patched = {path.relative_to(after): path for path in after.rglob('*.as')}
    if originals.keys() != patched.keys():
        raise ValueError('Compiled movie changed its class set')
    changed = [path for path in originals if originals[path].read_bytes() != patched[path].read_bytes()]
    if set(changed) != {RELATIVE.relative_to('scripts'), CONSOLE_RELATIVE.relative_to('scripts')}:
        raise ValueError('An unrelated class changed: ' + str(changed))
    verified_path = out / 'all-patched' / RELATIVE
    verified = verified_path.read_text()
    if unchanged_class(original) != unchanged_class(verified, patched=True):
        raise ValueError('An unrelated part of UIEventsManager changed')
    def normalized(value):
        # Compiler sorts imports and elides optional enumeration annotations.
        value = value.replace('for(var field:String in', 'for(var field in').replace(
            'for each(var sourceRow:Object in', 'for each(var sourceRow in')
        lines = [line.strip() for line in value.splitlines() if line.strip()]
        return '\n'.join(sorted(line for line in lines if line.startswith('import '))
                         + [line for line in lines if not line.startswith('import ')])
    if normalized(patch_source(original)) != normalized(verified):
        raise ValueError('Re-exported class differs from the exact intended patch')
    subprocess.run(['node', str(Path(__file__).with_name('verify-hosted-games.cjs')), str(verified_path)], check=True)
    console_verified_path = out / 'all-patched' / CONSOLE_RELATIVE
    # FFDec shortens the existing uiWidgetCommand diagnostic string concatenations.
    # This local is a String; assignment-plus and += have the same evaluation here.
    console_expected = alias.patch_source(console_original).replace('_loc10_ = _loc10_ + ', '_loc10_ += ')
    if normalized(console_expected) != normalized(console_verified_path.read_text()):
        raise ValueError('Re-exported console differs from the exact intended help alias patch')
    subprocess.run(['node', str(Path(__file__).with_name('verify-console-help.cjs')), str(console_verified_path)], check=True)
    manifest = {
        'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack', 'source_sha256': SOURCE_SHA256,
        'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(destination),
        'changed_classes': [CLASS, CONSOLE_CLASS], 'key_prefix': 'Cranberry.Hosted.<WorldId>',
        'keys': ['Name', 'Mode'], 'native_fallback_preserved': True,
        'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True,
        'other_class_methods_unchanged': True, 'other_exported_scripts_unchanged': len(originals) - 2,
        'management_entry': 'Hosted Games: F1/gamepad Y toggles the console; Escape closes; repeat/release events cannot reopen it',
        'help_alias': '/help [arguments] maps to the registered /commands [arguments] before native dispatch',
        'console_compiler_normalizations': ['uiWidgetCommand _loc10_ String concatenation uses +='],
    }
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
