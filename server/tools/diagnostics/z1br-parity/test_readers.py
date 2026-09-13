import json
import pathlib
import struct
import unittest

from bounded import Cursor, DecodeError
from movement_1315 import parse as movement
from weapon_1315 import parse as weapons
from events_1315 import dto, appearance, bounce, fire, weapon_leaves

FIXTURES = pathlib.Path(__file__).parent / "fixtures"


class MovementTests(unittest.TestCase):
    def test_valid_sparse_and_full_fields_reject_every_truncated_prefix(self):
        for case in json.loads((FIXTURES / "movement-synthetic.json").read_text()):
            data = bytes.fromhex(case["hex"])
            parsed = movement(data, case["direction"])
            self.assertEqual(parsed["mask"], case["mask"])
            self.assertEqual(parsed["consumed"], len(data))
            for end in range(len(data)):
                with self.subTest(direction=case["direction"], mask=case["mask"], end=end):
                    with self.assertRaises(DecodeError): movement(data[:end], case["direction"])
            with self.assertRaises(DecodeError): movement(data + b"\x00", case["direction"])
            with self.assertRaises(DecodeError): movement(data, "s2c" if case["direction"] == "c2s" else "c2s")

    def test_unknown_mask_and_packed_width_bounds(self):
        with self.assertRaises(DecodeError): movement(b"\x46" + struct.pack("<HIB", 0x8000, 0, 0), "c2s")
        for raw, signed in ((b"\x03", False), (b"\x06", True), (b"\x07\x00\x00", True)):
            with self.assertRaises(DecodeError): Cursor(raw).packed(signed)


class WeaponTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.body = (FIXTURES / "weapon-mini.bin").read_bytes()

    def test_two_variable_arrays_and_changed_lists_are_consumed(self):
        result = weapons(self.body)
        self.assertEqual(result["consumed"], len(self.body))
        self.assertFalse(any(result["joins"].values()))
        self.assertTrue(all(span["count"] for span in result["spans"]))
        modes = {r["id"]: r for r in result["lists"][2]}
        self.assertEqual([(modes[i]["array0"]["count"], modes[i]["array1"]["count"]) for i in (8, 176)], [(0, 0), (15, 15)])
        self.assertEqual(modes[176]["bytes"] - modes[8]["bytes"], 30 * 9)
        self.assertEqual(result["lists"][3][0]["bytes"], 12 + 6 * 77)
        self.assertEqual(result["lists"][6][0]["bytes"], 12 + 11 * 16 + 1)

    def test_every_truncated_prefix_and_extra_trailer_are_rejected(self):
        for end in range(len(self.body)):
            with self.subTest(end=end):
                with self.assertRaises(DecodeError): weapons(self.body[:end])
        with self.assertRaises(DecodeError): weapons(self.body + b"\0")

    def test_bad_counts_duplicate_keys_and_missing_join_are_rejected(self):
        good = weapons(self.body)
        variable = next(r for r in good["lists"][2] if r["id"] == 176)
        targets = [0, variable["array0"]["offset"], variable["array1"]["offset"]]
        for at in targets:
            data = bytearray(self.body); struct.pack_into("<I", data, at, 0xffffffff)
            with self.assertRaises(DecodeError): weapons(bytes(data))
        data = bytearray(self.body)
        at = good["lists"][2][1]["offset"]
        struct.pack_into("<II", data, at, 8, 8)
        with self.assertRaises(DecodeError): weapons(bytes(data))
        for at in (good["lists"][0][0]["offset"] + good["lists"][0][0]["bytes"] - 4,
                   good["lists"][1][0]["offset"] + 12,
                   good["lists"][7][0]["offset"] + 12):
            data = bytearray(self.body)
            struct.pack_into("<I", data, at, 999999)
            with self.assertRaises(DecodeError): weapons(bytes(data))


class EventTests(unittest.TestCase):
    def test_sanitized_shapes_and_all_truncated_prefixes(self):
        for case in json.loads((FIXTURES / "event-synthetic.json").read_text())["cases"]:
            raw = bytes.fromhex(case["hex"])
            parser = (lambda b: dto(b, case["direction"])) if case["schema"].startswith("dto") else {
                "appearance": appearance, "bounce": bounce, "fire": fire}[case["schema"]]
            parser(raw)
            for end in range(len(raw)):
                with self.subTest(schema=case["schema"], end=end):
                    with self.assertRaises(DecodeError): parser(raw[:end])
            with self.assertRaises(DecodeError): parser(raw + b"\0")

    def test_nested_weapon_framing_checks_counts_lengths_and_depth(self):
        leaf = b"\x72" + struct.pack("<IBQ", 100, 43, 3)
        bundle = b"\x72" + struct.pack("<IBII", 100, 31, 1, len(leaf)) + leaf
        self.assertEqual(list(weapon_leaves(bundle))[0][:3], (15, 100, 43))
        for raw in (bundle[:-1], bundle + b"\0", bundle[:6] + struct.pack("<I", 0xffffffff),
                    bundle[:10] + struct.pack("<I", len(leaf) + 1) + leaf):
            with self.assertRaises(DecodeError): list(weapon_leaves(raw))
        for _ in range(10): bundle = b"\x72" + struct.pack("<IBII", 100, 31, 1, len(bundle)) + bundle
        with self.assertRaises(DecodeError): list(weapon_leaves(bundle))


if __name__ == "__main__": unittest.main()
