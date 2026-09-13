import importlib.util
from pathlib import Path
import struct
import unittest
import zlib

ROOT = Path(__file__).resolve().parents[1]


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load('hosted_builder', 'build-hosted-games.py')
pack = load('hosted_installer', 'install-hosted-games.py')


class HostedDisplayPatchTests(unittest.TestCase):
    def source(self):
        return '''   import ui.bindings.UIBindingPlayer;
   import flash.events.TimerEvent;
   import ui.core.events.UIEvent;
   import views.buttonlegend.LegendDataProvider;
      private var m_currentOpenWindow:uint = 0;
      public function deinitialize() : void
      {
         this.m_stage.removeEventListener(UIEvent.UI_ENTER,this.handleUIEnter,false);
      }
      protected function handleUIEnter(param1:Object) : void
      {
         this.m_bindings[BINDING_EVENTS_SPECIAL_JOIN_AN_EVENT].setValue(UIBindingLocale.translateCodeString("UI.Events.JoinAHostedEvent"));
      }
      protected function handleUIExit(param1:Object) : void
      {
         switch(param1)
         {
            case WidgetNames.SPECIAL_EVENTS_WINDOW:
               if(this.m_currentTimeTimer != null)
               {
               }
         }
      }
      protected function buildLegend() : void
      {
               if(_loc1_)
               {
                  if(this.m_canJoinGame) {}
               }
      }
      protected function handleSpecialEventDataChanged(param1:Object) : void
      {
         _loc2_ = uiDBManager.query("SELECT * FROM MatchSchedule WHERE IsHostedGame = 1");
      }
      protected function handleEventsListClicked(param1:Object) : void
      {
      }
'''

    def test_only_hosted_query_and_new_helper_change(self):
        source = self.source()
        patched = builder.patch_source(source)
        self.assertEqual(builder.unchanged_class(source), builder.unchanged_class(patched, patched=True))
        self.assertIn('this.hostedGameDisplayRows(uiDBManager.query(', patched)
        self.assertIn('UIBindingSystem.GetStringHashValue(key + ".Name",String(row.Name))', patched)

    def test_duplicate_patch_and_missing_source_anchor_fail_closed(self):
        with self.assertRaisesRegex(ValueError, 'already patched'):
            builder.patch_source(builder.patch_source(self.source()))
        with self.assertRaisesRegex(ValueError, 'Unexpected'):
            builder.patch_source(self.source().replace('IsHostedGame = 1', 'IsHostedGame = 2'))

    def test_unrelated_class_change_is_detected(self):
        source = self.source()
        patched = builder.patch_source(source).replace('handleEventsListClicked', 'anotherHandler')
        self.assertNotEqual(builder.unchanged_class(source), builder.unchanged_class(patched, patched=True))

    def test_key_listener_capture_target_and_cleanup_are_symmetric(self):
        patched = builder.patch_source(self.source())
        for event in ['KEY_DOWN', 'KEY_UP']:
            for capture in ['true', 'false']:
                self.assertEqual(1, patched.count('addEventListener(KeyboardEvent.' + event
                    + ',this.handleHostedManagementKey,' + capture + ',1000,true)'))
                self.assertEqual(2, patched.count('removeEventListener(KeyboardEvent.' + event
                    + ',this.handleHostedManagementKey,' + capture + ')'))

    def test_installer_refuses_unverified_output(self):
        with self.assertRaisesRegex(ValueError, 'Unverified patch'):
            pack.prepare_hosted_games(b'', b'not the verified built asset')

    def test_installer_preserves_siblings_and_refuses_stale_input(self):
        old, new = b'old hosted menu', b'updated hosted menu'
        assets = [('Other.dds', b'unchanged graphics'), ('UIRoot.gfx', old)]
        table_size = 8 + sum(16 + len(name) for name, _ in assets)
        table, bodies = bytearray(struct.pack('>II', 0, len(assets))), bytearray()
        for name, body in assets:
            table.extend(struct.pack('>I', len(name)) + name.encode('ascii'))
            table.extend(struct.pack('>III', table_size + len(bodies), len(body), zlib.crc32(body)))
            bodies.extend(body)
        original = bytes(table + bodies)
        updated, index, count = pack.prepare(original, new, pack.installer.digest(old), pack.installer.digest(new))
        self.assertEqual(count, 1)
        self.assertEqual(updated[:index], original[:index])
        self.assertEqual(updated[index + 12:len(original)], original[index + 12:])
        with self.assertRaisesRegex(ValueError, 'another modification'):
            pack.prepare(original, new, pack.installer.digest(b'stale source'), pack.installer.digest(new))


if __name__ == '__main__':
    unittest.main()
