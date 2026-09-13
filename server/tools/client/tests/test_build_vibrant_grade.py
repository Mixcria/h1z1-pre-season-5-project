import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zlib


HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('vibrant_grade', HERE.parent / 'build-vibrant-grade.py')
grade = importlib.util.module_from_spec(spec)
spec.loader.exec_module(grade)
ORIGINAL = (HERE / 'fixtures' / 'z2_colorkey_day.dds').read_bytes()


def make_pack(extra=None):
    assets = [('other.txt', b'pre-existing unrelated asset\x00\xff'), (grade.NAME, ORIGINAL)]
    if extra:
        assets.append(extra)
    offset = 8 + sum(4 + len(name) + 12 for name, _ in assets)
    index, payload = bytearray(struct.pack('>II', 0, len(assets))), bytearray()
    for name, data in assets:
        index += struct.pack('>I', len(name)) + name.encode('ascii')
        index += struct.pack('>III', offset, len(data), zlib.crc32(data))
        payload += data
        offset += len(data)
    return bytes(index + payload + b'preserve trailing bytes')


class VibrantGradeTests(unittest.TestCase):
    def test_fixture_matches_complete_native_identity_and_format(self):
        self.assertEqual(grade.ORIGINAL_SHA256, grade.digest(ORIGINAL))
        self.assertEqual(16512, len(ORIGINAL))
        self.assertEqual(b'DDS ', ORIGINAL[:4])
        self.assertEqual((16, 256), struct.unpack_from('<II', ORIGINAL, 12))
        self.assertEqual((65, 0, 32, 0xff0000, 0xff00, 0xff, 0xff000000),
                         struct.unpack_from('<7I', ORIGINAL, 80))

    def test_any_signature_header_alpha_or_rgb_modification_is_refused(self):
        for at in (0, 4, 12, 16, 80, 92, 127, 128, 131, len(ORIGINAL) - 1):
            changed = bytearray(ORIGINAL)
            changed[at] ^= 1
            with self.subTest(at=at), self.assertRaisesRegex(ValueError, 'complete reviewed'):
                grade.grade_dds(changed)
        for data in (b'', ORIGINAL[:-1], ORIGINAL + b'\x00'):
            with self.assertRaises(ValueError):
                grade.grade_dds(data)

    def test_every_node_keeps_alpha_gamut_neutrals_and_luma(self):
        candidate, metrics = grade.grade_dds(ORIGINAL)
        self.assertEqual(ORIGINAL[:128], candidate[:128])
        self.assertEqual(ORIGINAL[131::4], candidate[131::4])
        neutrals, dark, changed = 0, 0, 0
        for at in range(128, len(ORIGINAL), 4):
            rgb = tuple(reversed(ORIGINAL[at:at + 3]))
            result = tuple(reversed(candidate[at:at + 3]))
            self.assertTrue(all(0 <= x <= 255 for x in result))
            self.assertLessEqual(abs(grade.luma(result) - grade.luma(rgb)), 0.5)
            if len(set(rgb)) == 1:
                neutrals += 1
                self.assertEqual(rgb, result)
            if grade.luma(rgb) <= 20:
                dark += 1
                self.assertEqual(rgb, result)
            # Quantization may add at most one code value to the chroma bound.
            old_chroma, new_chroma = max(rgb) - min(rgb), max(result) - min(result)
            self.assertLessEqual(new_chroma, old_chroma * 1.12 + 1)
            self.assertGreaterEqual(new_chroma, old_chroma - 1)
            changed += rgb != result
        self.assertEqual(16, neutrals)
        self.assertGreater(dark, 16)
        self.assertEqual(2708, changed)
        self.assertEqual(changed, metrics['changed_nodes'])

    def test_colour_increases_without_changing_greys_or_clipping_extremes(self):
        self.assertEqual((0, 0, 0), grade.grade_pixel((0, 0, 0)))
        self.assertEqual((255, 255, 255), grade.grade_pixel((255, 255, 255)))
        self.assertEqual((102, 102, 102), grade.grade_pixel((102, 102, 102)))
        self.assertEqual((255, 0, 0), grade.grade_pixel((255, 0, 0)))
        source = (80, 150, 80)
        result = grade.grade_pixel(source)
        self.assertGreater(result[1] - result[0], source[1] - source[0])
        self.assertLessEqual(abs(grade.luma(result) - grade.luma(source)), 0.5)

    def test_pack_only_changes_lut_rgb_and_target_crc(self):
        original = make_pack(('later-patch.bin', b'preserve another modification'))
        updated, source, candidate, metrics = grade.prepare_pack(original)
        self.assertEqual(ORIGINAL, source)
        self.assertEqual(2, metrics['other_assets_preserved'])
        self.assertEqual(len(original), len(updated))
        offset, crc = metrics['asset_offset'], metrics['crc_offset']
        for at, (before, after) in enumerate(zip(original, updated, strict=True)):
            if before != after:
                self.assertTrue(crc <= at < crc + 4 or
                                offset + 128 <= at < offset + 16512 and (at - offset - 128) % 4 != 3)
        self.assertEqual(candidate, updated[offset:offset + len(candidate)])
        self.assertEqual(zlib.crc32(candidate), struct.unpack_from('>I', updated, crc)[0])
        for entry in grade.entries(updated):
            self.assertEqual(entry[4], zlib.crc32(updated[entry[2]:entry[2] + entry[3]]))

    def test_bad_crc_duplicate_missing_and_overlapping_assets_are_refused(self):
        original = make_pack()
        target = next(e for e in grade.entries(original) if e[0] == grade.NAME)
        bad = bytearray(original)
        bad[target[1] + 8] ^= 1
        with self.assertRaisesRegex(ValueError, 'CRC'):
            grade.prepare_pack(bad)
        duplicate = make_pack((grade.NAME, ORIGINAL))
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            grade.prepare_pack(duplicate)
        missing = original.replace(grade.NAME.encode(), b'x' * len(grade.NAME), 1)
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            grade.prepare_pack(missing)
        aliased = make_pack(('alias.dds', ORIGINAL))
        rows = grade.entries(aliased)
        bad = bytearray(aliased)
        struct.pack_into('>I', bad, rows[-1][1], rows[1][2])
        with self.assertRaisesRegex(ValueError, 'overlaps the LUT'):
            grade.prepare_pack(bad)

    def test_stage_writes_reviewable_artifacts_preserves_input_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'source' / grade.PACK
            source.parent.mkdir()
            original = make_pack()
            source.write_bytes(original)
            out = root / 'stage'
            report = grade.stage(source, out)
            self.assertEqual(original, source.read_bytes())
            self.assertEqual(report, json.loads((out / 'manifest.json').read_text()))
            self.assertEqual(grade.digest((out / grade.PACK).read_bytes()), report['candidate_pack_sha256'])
            self.assertEqual(ORIGINAL, (out / ('original-' + grade.NAME)).read_bytes())
            self.assertEqual('pending', report['native_visual_acceptance'])
            with self.assertRaisesRegex(ValueError, 'already exists'):
                grade.stage(source, out)
            with self.assertRaisesRegex(ValueError, 'outside'):
                grade.stage(source, source.parent / 'stage')


if __name__ == '__main__':
    unittest.main()
