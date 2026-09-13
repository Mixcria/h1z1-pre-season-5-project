import importlib.util
from pathlib import Path
import struct
import sys
import unittest

CLIENT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(CLIENT))
spec = importlib.util.spec_from_file_location('camera_look_test', CLIENT / 'camera-only-look-live.py')
look = importlib.util.module_from_spec(spec)
spec.loader.exec_module(look)
ORIGINAL = bytes.fromhex((CLIENT.parents[1] / 'tests/Cranberry.Launcher.Tests/Fixtures/headlook-august.hex').read_text())


class Buffer:
    base = 0x150000000
    def __init__(self):
        self.data = bytearray(ORIGINAL)
        self.writes = []
        self.fail_after_write = False
    def read(self, address, size):
        if address == self.base + 0x30ef074: return bytes(4)
        if address == self.base + 0x30ef088: return struct.pack('<f', 1)
        assert address == self.base + look.FUNCTION_RVA and size == look.FUNCTION_SIZE
        return bytes(self.data)
    def write_byte(self, address, old, new):
        assert address == self.base + 0xc697f6
        assert self.data[look.PATCH_INDEX] == old
        self.writes.append((old,new))
        self.data[look.PATCH_INDEX] = new
        if self.fail_after_write:
            self.fail_after_write = False
            raise OSError('API failure after write')


class CameraLookTests(unittest.TestCase):
    def test_apply_is_one_byte_idempotent_and_restore_is_exact(self):
        p = Buffer()
        self.assertTrue(look.change_verified(p))
        self.assertEqual([(0x8e,0x7a)], p.writes)
        self.assertFalse(look.change_verified(p))
        self.assertTrue(look.change_verified(p, restore=True))
        self.assertEqual(ORIGINAL, bytes(p.data))

    def test_failed_write_rolls_back_without_leaving_a_partial_function(self):
        p = Buffer()
        p.fail_after_write = True
        with self.assertRaises(OSError): look.change_verified(p)
        self.assertEqual(ORIGINAL, bytes(p.data))

    def test_unrecognized_function_is_read_only(self):
        p = Buffer()
        p.data[200] ^= 1
        with self.assertRaises(ValueError): look.change_verified(p)
        self.assertEqual([], p.writes)

    def test_the_writer_cannot_modify_scope_or_reload_sites(self):
        permitted = look.CameraLookProcess.ALLOWED_CHANGES
        self.assertEqual({0xc697f6}, set(permitted))
        self.assertEqual({(0x8e,0x7a),(0x7a,0x8e)}, set(permitted[0xc697f6]))


if __name__ == '__main__': unittest.main()
