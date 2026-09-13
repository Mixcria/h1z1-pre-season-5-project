import contextlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import zlib

spec = importlib.util.spec_from_file_location('crate_installer', Path(__file__).resolve().parents[1] / 'install-crate-unlock-ten.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


def make_pack(assets):
    table_size = 8 + sum(16 + len(name) for name, _ in assets)
    table, bodies = bytearray(struct.pack('>II', 0, len(assets))), bytearray()
    for name, data in assets:
        encoded = name.encode('ascii')
        table.extend(struct.pack('>I', len(encoded)))
        table.extend(encoded)
        table.extend(struct.pack('>III', table_size + len(bodies), len(data), zlib.crc32(data)))
        bodies.extend(data)
    return bytes(table + bodies)


class CrateUnlockInstallerTests(unittest.TestCase):
    old, new = b'original UI', b'new longer UI'

    def original(self):
        return make_pack([('First.dds', b'first'), (installer.NAME, self.old), ('Last.gfx', b'last')])

    def update(self, original=None):
        return installer.prepare_update(self.original() if original is None else original, self.new,
                                        installer.digest(self.old), installer.digest(self.new))

    def test_only_target_index_changes_and_all_original_data_is_preserved(self):
        original = self.original()
        updated, index, count = self.update(original)
        self.assertEqual(2, count)
        self.assertEqual(original[:index], updated[:index])
        self.assertEqual(original[index + 12:], updated[index + 12:len(original)])
        self.assertEqual(self.new, updated[len(original):])
        for entry in installer.entries(updated):
            if entry[0] == installer.NAME:
                self.assertEqual(len(original), entry[2])
                self.assertEqual(self.new, updated[entry[2]:entry[2] + entry[3]])
            else:
                self.assertIn(entry, installer.entries(original))
        self.assertEqual(updated, self.update(updated)[0])

    def test_modified_asset_or_patch_is_rejected(self):
        original = make_pack([(installer.NAME, b'another patch')])
        with self.assertRaisesRegex(ValueError, 'another modification'):
            self.update(original)
        with self.assertRaisesRegex(ValueError, 'Unverified patch'):
            installer.prepare_update(self.original(), self.new)

    def test_upgrade_accepts_only_reviewed_asset_versions_and_keeps_previous_bytes(self):
        fixed = b'fixed crate UI'
        previous, _, _ = self.update()
        accepted = (installer.digest(self.old), installer.digest(self.new))
        for original in (self.original(), previous):
            updated, index, count = installer.prepare_update(
                original, fixed, accepted, installer.digest(fixed))
            self.assertEqual(2, count)
            self.assertEqual(original[:index], updated[:index])
            self.assertEqual(original[index + 12:], updated[index + 12:len(original)])
            self.assertEqual(fixed, updated[len(original):])
            self.assertEqual(updated, installer.prepare_update(
                updated, fixed, accepted, installer.digest(fixed))[0])
        unknown = make_pack([(installer.NAME, b'unrelated UI change')])
        with self.assertRaisesRegex(ValueError, 'another modification'):
            installer.prepare_update(unknown, fixed, accepted, installer.digest(fixed))

    def test_duplicate_target_bad_crc_and_truncated_pack_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            self.update(make_pack([(installer.NAME, self.old), (installer.NAME, self.old)]))
        broken = bytearray(self.original())
        target = next(entry for entry in installer.entries(broken) if entry[0] == installer.NAME)
        broken[target[2]] ^= 1
        with self.assertRaisesRegex(ValueError, 'CRC'):
            self.update(bytes(broken))
        for count in range(len(self.original())):
            with self.assertRaises(ValueError):
                self.update(self.original()[:count])

    def test_restore_is_dry_run_by_default_and_refuses_later_changes(self):
        original = self.original()
        updated, _, _ = self.update(original)
        with tempfile.TemporaryDirectory(prefix='cranberry-crate-installer-') as directory:
            root = Path(directory)
            target = root / installer.PACK
            backup = root / 'backup'
            backup.mkdir()
            saved = backup / installer.PACK
            target.write_bytes(updated)
            saved.write_bytes(original)
            manifest = backup / 'manifest.json'
            manifest.write_text(json.dumps({
                'pack': str(target), 'backup': str(saved),
                'original_pack_sha256': installer.digest(original),
                'installed_pack_sha256': installer.digest(updated)}), encoding='utf-8')
            with contextlib.redirect_stdout(io.StringIO()):
                installer.restore(manifest, False)
            self.assertEqual(updated, target.read_bytes())
            target.write_bytes(updated + b'another modification')
            with self.assertRaisesRegex(ValueError, 'later changes'):
                installer.restore(manifest, True)
            target.write_bytes(updated)
            with patch.object(installer, 'require_client_closed') as require_closed, contextlib.redirect_stdout(io.StringIO()):
                installer.restore(manifest, True)
                require_closed.assert_called_once()
            self.assertEqual(original, target.read_bytes())
            self.assertEqual(original, saved.read_bytes())


if __name__ == '__main__':
    unittest.main()
