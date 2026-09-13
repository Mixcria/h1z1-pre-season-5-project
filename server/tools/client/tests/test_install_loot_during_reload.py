import contextlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('loot_reload_installer',
    Path(__file__).resolve().parents[1] / 'install-loot-during-reload.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


def make_image():
    data = bytearray(2048)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3c, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<HH', data, 0x84, 0x8664, 1)
    struct.pack_into('<H', data, 0x94, 0xf0)
    struct.pack_into('<H', data, 0x98, 0x20b)
    struct.pack_into('<Q', data, 0xb0, installer.IMAGE_BASE)
    data[0x188:0x190] = b'.text\0\0\0'
    struct.pack_into('<IIII', data, 0x190, 1024,
        installer.FUNCTION_VA - installer.IMAGE_BASE, 1024, 512)
    struct.pack_into('<I', data, 0x1ac, 0x60000020)
    data[512:512 + len(installer.ORIGINAL_FUNCTION)] = installer.ORIGINAL_FUNCTION
    data[-24:] = b'previous unrelated patch'
    return bytes(data)


class LootDuringReloadInstallerTests(unittest.TestCase):
    def test_patch_changes_one_opcode_and_keeps_dispatch_target(self):
        original = make_image()
        updated, offset = installer.prepare_update(original)
        self.assertEqual([offset], [i for i, pair in enumerate(zip(original, updated)) if pair[0] != pair[1]])
        self.assertEqual(b'\x77\x29', original[offset:offset + 2])
        self.assertEqual(b'\xeb\x29', updated[offset:offset + 2])
        self.assertEqual(installer.BRANCH_TARGET, installer.PATCH_VA + 2 + updated[offset + 1])
        # The dispatch block is unchanged, including its call to the original interaction sender.
        dispatch = 512 + installer.BRANCH_TARGET - installer.FUNCTION_VA
        self.assertEqual(original[dispatch:], updated[dispatch:])
        self.assertEqual(updated, installer.prepare_update(updated)[0])
        self.assertEqual(original, installer.prepare_update(updated, restore=True)[0])

    def test_reviewed_function_matches_independently_recorded_hash(self):
        self.assertEqual(468, len(installer.ORIGINAL_FUNCTION))
        self.assertEqual('a4250571c76ac95a827b1fa1cbeec04c6107c0b62e86e4122d13c6cd5a14d769',
                         installer.digest(installer.ORIGINAL_FUNCTION))

    def test_changed_function_or_branch_displacement_is_rejected(self):
        for index in (0, installer.PATCH_INDEX, installer.PATCH_INDEX + 1, len(installer.ORIGINAL_FUNCTION) - 1):
            changed = bytearray(make_image())
            changed[512 + index] ^= 1
            with self.assertRaisesRegex(ValueError, 'reviewed August bytes'):
                installer.prepare_update(changed)

    def test_wrong_image_base_architecture_and_unbacked_function_are_rejected(self):
        for offset, value in ((0, 0), (0x84, 0), (0x98, 0), (0xb0, 1), (0x199, 0), (0x1af, 0)):
            changed = bytearray(make_image())
            changed[offset] = value
            with self.assertRaises(ValueError):
                installer.prepare_update(changed)
        for length in (0, 63, 200, 513, 900):
            with self.assertRaises(ValueError):
                installer.prepare_update(make_image()[:length])

    def test_install_is_dry_by_default_and_backups_current_patches_before_apply(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            root = Path(directory)
            target, backup = root / 'H1Z1.exe', root / 'backup'
            original = make_image()
            target.write_bytes(original)
            installer.install(target)
            self.assertEqual(original, target.read_bytes())
            self.assertFalse(backup.exists())
            with patch.object(installer, 'require_client_closed'):
                installer.install(target, backup, apply=True)
            manifest = json.loads((backup / 'manifest.json').read_text())
            self.assertEqual(original, (backup / 'H1Z1.exe').read_bytes())
            self.assertEqual(manifest['installed_sha256'], installer.digest(target.read_bytes()))
            self.assertEqual(b'previous unrelated patch', target.read_bytes()[-24:])

    def test_restore_preserves_later_unrelated_patches_and_is_dry_by_default(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            root = Path(directory)
            target, backup = root / 'H1Z1.exe', root / 'backup'
            original = make_image()
            target.write_bytes(original)
            with patch.object(installer, 'require_client_closed'):
                installer.install(target, backup, apply=True)
                later = target.read_bytes() + b'a later executable patch'
                target.write_bytes(later)
                manifest = backup / 'manifest.json'
                installer.restore(manifest)
                self.assertEqual(later, target.read_bytes())
                installer.restore(manifest, apply=True)
                self.assertEqual(original + b'a later executable patch', target.read_bytes())
                installer.restore(manifest, apply=True)
                self.assertEqual(original + b'a later executable patch', target.read_bytes())

    def test_restore_refuses_damaged_backup_and_changes_to_reviewed_function(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            root = Path(directory)
            target, backup = root / 'H1Z1.exe', root / 'backup'
            target.write_bytes(make_image())
            with patch.object(installer, 'require_client_closed'):
                installer.install(target, backup, apply=True)
            changed = bytearray(target.read_bytes())
            changed[512] ^= 1
            target.write_bytes(changed)
            with self.assertRaisesRegex(ValueError, 'reviewed August bytes'):
                installer.restore(backup / 'manifest.json', apply=True)
            (backup / 'H1Z1.exe').write_bytes(b'damaged')
            with self.assertRaisesRegex(ValueError, 'Backup no longer'):
                installer.restore(backup / 'manifest.json', apply=True)

    def test_running_client_and_file_race_prevent_replacement(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'H1Z1.exe'
            target.write_bytes(make_image())
            updated, _ = installer.prepare_update(target.read_bytes())
            with patch.object(installer, 'require_client_closed', side_effect=ValueError('client running')):
                with self.assertRaisesRegex(ValueError, 'client running'):
                    installer.replace_verified(target, updated, installer.digest(target.read_bytes()))
            self.assertEqual(make_image(), target.read_bytes())
            with patch.object(installer, 'require_client_closed'):
                with self.assertRaisesRegex(ValueError, 'changed during preparation'):
                    installer.replace_verified(target, updated, 'wrong-hash')
            self.assertEqual(make_image(), target.read_bytes())
            self.assertEqual([target], list(target.parent.iterdir()))


if __name__ == '__main__':
    unittest.main()
