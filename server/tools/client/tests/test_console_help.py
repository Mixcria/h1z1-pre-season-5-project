import importlib.util
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('console_help_alias', ROOT / 'console-help-alias.py')
alias = importlib.util.module_from_spec(spec)
spec.loader.exec_module(alias)


class ConsoleHelpAliasTests(unittest.TestCase):
    def test_changes_only_the_native_submit_argument(self):
        source = '''this._commandsProcessor.processCommand(param1.currentTarget.text);
                     UIBindingChat.ProcessChatCommand(param1.currentTarget.text);
                     this.inputText(param1.currentTarget as TextInput);
                     param1.stopImmediatePropagation();'''
        patched = alias.patch_source(source)
        self.assertEqual(source, patched.replace(alias.PATCHED, alias.ORIGINAL, 1))
        self.assertEqual(1, patched.count(alias.PATCHED))

    def test_rejects_duplicate_or_unexpected_submit_source(self):
        for source in ['', alias.ORIGINAL * 2, alias.HELP_ONLY * 2,
                       alias.ORIGINAL + alias.HELP_ONLY,
                       alias.ORIGINAL.replace('currentTarget', 'target')]:
            with self.subTest(source=source):
                with self.assertRaisesRegex(ValueError, 'Unexpected'):
                    alias.patch_source(source)
        with self.assertRaisesRegex(ValueError, 'already patched'):
            alias.patch_source(alias.patch_source(alias.ORIGINAL))

    def test_upgrades_exact_help_alias_without_altering_surrounding_handler(self):
        source = '// existing UI changes\n' + alias.HELP_ONLY + '\nthis.inputText(param1.currentTarget as TextInput);'
        patched = alias.patch_source(source)
        self.assertEqual(source, patched.replace(alias.PATCHED, alias.HELP_ONLY, 1))
        with self.assertRaisesRegex(ValueError, 'Unexpected'):
            alias.patch_source(alias.HELP_ONLY.replace('/commands', '/different'))

    def test_preserves_line_endings_and_non_ascii_arguments(self):
        source = '// Été 杯\r\n' + alias.ORIGINAL + '\r\n// unchanged\r\n'
        patched = alias.patch_source(source)
        self.assertEqual(source, patched.replace(alias.PATCHED, alias.ORIGINAL, 1))

    @unittest.skipUnless(shutil.which('node'), 'Node is required to execute ActionScript-compatible expressions')
    def test_expression_preserves_native_commands_and_normalizes_prefixes(self):
        expression = alias.PATCHED.removeprefix('UIBindingChat.ProcessChatCommand(').removesuffix(');')
        script = '''const assert = require('node:assert/strict');
const rewrite = text => { const param1 = {currentTarget: {text}}; return EXPRESSION; };
for (const [input, expected] of [
  ['/help', '/commands'], ['/HeLp HostGame 2', '/commands HostGame 2'],
  [' \\t/help\\t3 ', ' \\t/commands\\t3 '],
  ['/helper', '/helper'], ['/helpful', '/helpful'], ['/help?', '/help?'],
  ['/hostgame redeem HGK-AbCd_0123', '/hostgame redeem HGK-AbCd_0123'],
  ['/commands', '/commands'], ['', ''],
  ['./vehicle 1', '/vehicle 1'], ['./VEHICLE list', '/VEHICLE list'],
  ['/vehicle 5 0 1', '/vehicle 5 0 1'], ['/item add 2229 1 0', '/item add 2229 1 0'],
  ['/goto MixedCaseName', '/goto MixedCaseName'], ['god', '/god'], ['./loc', '/loc']
]) assert.equal(rewrite(input), expected);
for (const name of ['car', 'vehicle', 'spawncar', 'spawnvehicle', 'item', 'goto', 'god', 'loc', 'fog', 'netstats']) {
  for (const prefix of ['', '/', './']) {
    for (const token of [name, name.toUpperCase()]) {
      for (const args of ['', ' ATV', '\\tspawn PickupTruck  ', ' MixedCase /vehicle \\u00c9t\\u00e9 \\u676f']) {
        assert.equal(rewrite(' \\t' + prefix + token + args), ' \\t/' + token + args);
      }
    }
  }
}
for (const line of ['/vehicles', '/vehicle?', '/vehicle/list', '//vehicle',
                   '.vehicle', '../vehicle', '/carpet', '/spawnvehicles', '/say /vehicle']) {
  assert.equal(rewrite(line), line);
}
'''.replace('EXPRESSION', expression)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'alias.cjs'
            path.write_text(script, encoding='utf-8')
            result = subprocess.run(['node', str(path)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
