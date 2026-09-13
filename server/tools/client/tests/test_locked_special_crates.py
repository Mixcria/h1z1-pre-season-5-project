import csv
import hashlib
import importlib.util
from pathlib import Path
import sys
import unittest

TOOLS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(TOOLS / 'data'))
from locked_special_crates import extend_client_sheet, locked_rows, SPECIAL_CRATES

spec = importlib.util.spec_from_file_location('locked_installer', TOOLS / 'client/install-locked-special-crates.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class LockedSpecialCratesTests(unittest.TestCase):
    def native(self):
        raw = Path('C:/Aug2017/out/data_aug/ClientItemDefinitions.txt').read_bytes()
        return raw, {int(r['ID']): r for r in csv.DictReader(raw.decode('utf-8-sig').lstrip('#*').splitlines(), delimiter='^')}

    def test_native_definitions_are_byte_preserved_and_wrappers_use_native_lock_behavior(self):
        raw, items = self.native()
        extended = extend_client_sheet(raw)
        self.assertTrue(extended.startswith(raw))
        rows = {int(r['ID']): r for r in csv.DictReader(extended.decode('utf-8-sig').lstrip('#*').splitlines(), delimiter='^')}
        self.assertEqual(len(rows), len(items) + 13)
        for target in SPECIAL_CRATES:
            locked = rows[target + 10000]
            self.assertEqual('LockedRewardCrate', locked['CODE_FACTORY_NAME'])
            for field in ('ITEM_TYPE', 'CATEGORY_ID', 'FLAG_ACCOUNT_SCOPE'):
                self.assertEqual(items[3620][field], locked[field])
            self.assertEqual(str(target), locked['PARAM1'])
            self.assertEqual(str(target + 10000), locked['PARAM2'])
            self.assertEqual(items[target]['IMAGE_SET_ID'], locked['IMAGE_SET_ID'])
            self.assertEqual(items[target], rows[target])

    def test_item_id_collision_is_rejected(self):
        _, items = self.native()
        items[13807] = items[3807]
        with self.assertRaises(ValueError):
            list(locked_rows(items))

    def test_legacy_label_append_preserves_data_and_rejects_repeated_key(self):
        record = b'1\tugdt\tExisting'
        dat = record + b'\r\n'
        directory = (b'## Count:\t1\r\n## MD5Checksum: ' + hashlib.md5(dat).hexdigest().upper().encode()
                     + b'\r\n1\t0\t' + str(len(record)).encode() + b'\td\r\n')
        changed, index = installer.extend_locale(dat, directory)
        self.assertTrue(changed.startswith(dat))
        self.assertIn(b'Legacy Crate', changed)
        self.assertIn(b'## Count:\t2', index)
        self.assertIn(b'## MD5Checksum: ' + hashlib.md5(changed).hexdigest().upper().encode(), index)
        with self.assertRaises(ValueError):
            installer.extend_locale(changed, index)

    def test_checksum_repair_changes_only_digest_and_hashes_bom(self):
        # Reproduce G33: data has a new record, while its index retains the old hash.
        data = b'\xef\xbb\xbf1\tugdt\tExisting\r\n2\tugdt\tNew\r\n'
        stale = b'B05149F19C9D040AD50AB3BBBC3B7253'
        index = b'\xef\xbb\xbf## Count:\t2\r\n## MD5Checksum: ' + stale + b'\r\n1\t3\t15\td\r\n'
        corrected = installer.update_locale_checksum(data, index)
        expected = hashlib.md5(data).hexdigest().upper().encode()
        self.assertEqual(index.replace(stale, expected), corrected)
        self.assertNotIn(hashlib.md5(data[3:]).hexdigest().upper().encode(), corrected)
        self.assertEqual(corrected, installer.update_locale_checksum(data, corrected))

    def test_checksum_header_must_be_present_unique_and_valid(self):
        valid = b'## MD5Checksum: ' + b'0' * 32 + b'\r\n'
        for index in (b'## Count:\t1\r\n', valid + valid, valid.replace(b'0', b'x')):
            with self.subTest(index=index), self.assertRaisesRegex(ValueError, 'MD5Checksum'):
                installer.update_locale_checksum(b'data', index)

    def test_label_insert_keeps_native_offsets_increasing_and_existing_records_verbatim(self):
        # Legacy's key sorts before the final native key. Appending its data then
        # sorting only the index produced offset 454396 followed by 448360 (G33).
        bodies = [b'1\tugdt\tExisting\r\nMultiline', b'4294289198\tucdt\tLast']
        data = b'\xef\xbb\xbf' + b'\r\n'.join(bodies) + b'\r\n'
        directory = (b'\xef\xbb\xbf## Count:\t2\r\n## MD5Checksum: '
                     + hashlib.md5(data).hexdigest().upper().encode() + b'\r\n')
        cursor = 3
        for body in bodies:
            directory += body.split(b'\t')[0] + f'\t{cursor}\t{len(body)}\td\r\n'.encode()
            cursor += len(body) + 2
        changed, index = installer.extend_locale(data, directory)
        rows = [line.split('\t') for line in index.decode('utf-8-sig').splitlines()
                if line and not line.startswith('##')]
        offsets = [int(row[1]) for row in rows]
        self.assertEqual(sorted(set(offsets)), offsets)
        self.assertEqual('4294289198', rows[-1][0])
        self.assertEqual(str(installer.locale_tools.text_key(installer.LEGACY_NAME_ID)), rows[1][0])
        self.assertEqual(bodies[0], changed[int(rows[0][1]):int(rows[0][1]) + int(rows[0][2])])
        self.assertEqual(bodies[1], changed[int(rows[-1][1]):int(rows[-1][1]) + int(rows[-1][2])])
        self.assertTrue(changed.startswith(b'\xef\xbb\xbf'))
        self.assertIn(hashlib.md5(changed).hexdigest().upper().encode(), index)


if __name__ == '__main__':
    unittest.main()
