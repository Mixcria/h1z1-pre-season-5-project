import importlib.util
from pathlib import Path
import struct
import unittest
import zlib

spec = importlib.util.spec_from_file_location('binocular_hud', Path(__file__).resolve().parents[1] / 'install-binocular-hud.py')
hud = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hud)


def make_pack(assets):
    size = 8 + sum(16 + len(name) for name, _ in assets)
    table, bodies = bytearray(struct.pack('>II', 0, len(assets))), bytearray()
    for name, data in assets:
        table.extend(struct.pack('>I', len(name)) + name.encode('ascii'))
        table.extend(struct.pack('>III', size + len(bodies), len(data), zlib.crc32(data)))
        bodies.extend(data)
    return bytes(table + bodies)


class BinocularHudInstallerTests(unittest.TestCase):
    def test_each_hud_targets_its_own_asset_and_retains_every_other_asset(self):
        for name in hud.PATCHES:
            with self.subTest(hud=name):
                installer = hud.installer_for(name)
                installer.OLD, installer.NEW = installer.digest(b'old'), installer.digest(b'patch')
                original = make_pack([('Other.gfx', b'keep this existing modification'), (installer.NAME, b'old')])
                updated, index, count = installer.prepare_update(original, b'patch')
                self.assertEqual(1, count)
                self.assertEqual(original[:index], updated[:index])
                self.assertEqual(original[index + 12:], updated[index + 12:len(original)])
                self.assertEqual(b'patch', updated[len(original):])
                self.assertEqual(updated, installer.prepare_update(updated, b'patch')[0])

    def test_unrecognized_source_and_patch_hashes_are_refused(self):
        for name in hud.PATCHES:
            with self.subTest(hud=name):
                installer = hud.installer_for(name)
                with self.assertRaisesRegex(ValueError, 'Unverified patch'):
                    installer.prepare_update(make_pack([(installer.NAME, b'old')]), b'other')
                installer.NEW = installer.digest(b'patch')
                with self.assertRaisesRegex(ValueError, 'another modification'):
                    installer.prepare_update(make_pack([(installer.NAME, b'old')]), b'patch')

    def test_duplicate_asset_refused_before_mutation(self):
        installer = hud.installer_for('ammo')
        installer.OLD, installer.NEW = installer.digest(b'old'), installer.digest(b'patch')
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            installer.prepare_update(make_pack([(installer.NAME, b'old'), (installer.NAME, b'old')]), b'patch')

    def test_ammo_targets_the_hud_pack_and_does_not_install_an_unverified_reticle_change(self):
        ammo = hud.installer_for('ammo')
        self.assertEqual('Assets_131.pack', ammo.PACK)
        self.assertEqual(['ammo'], list(hud.PATCHES))


if __name__ == '__main__':
    unittest.main()
