"""Exercise the runtime writer's failure cleanup without opening a real process."""

import ctypes
import importlib.util
from pathlib import Path
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "loot_reload_live_process.py"
SPEC = importlib.util.spec_from_file_location("loot_reload_live_process", SOURCE)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class FakeKernel:
    def __init__(self):
        self.events = []
        self.opcode = 0x77
        self.created = 1234
        self.write_fails = False
        self.restore_fails = False
        self.restore_failures_remaining = 0
        self.protection = 0x20
        self.opcode_after_open = None

    def OpenProcess(self, access, inherit, pid):
        self.events.append(("open", access, inherit, pid))
        if self.opcode_after_open is not None:
            self.opcode = self.opcode_after_open
        return 456

    def CloseHandle(self, handle):
        self.events.append(("close", handle))
        return True

    def GetExitCodeProcess(self, handle, code):
        code._obj.value = MODULE.STILL_ACTIVE
        return True

    def GetProcessTimes(self, handle, created, exited, kernel, user):
        self.events.append(("identity", handle))
        created._obj.dwLowDateTime = self.created & 0xFFFFFFFF
        created._obj.dwHighDateTime = self.created >> 32
        return True

    def ReadProcessMemory(self, handle, address, data, size, count):
        self.events.append(("read", handle, address, size))
        ctypes.memmove(data, bytes([self.opcode]), 1)
        count._obj.value = size
        return True

    def VirtualProtectEx(self, handle, address, size, protection, previous):
        self.events.append(("protect", handle, address, size, protection))
        previous._obj.value = self.protection
        if protection == 0x20:
            if self.restore_fails:
                return False
            if self.restore_failures_remaining:
                self.restore_failures_remaining -= 1
                return False
        self.protection = protection
        return True

    def WriteProcessMemory(self, handle, address, value, size, count):
        self.events.append(("write", handle, address, value._obj.value, size))
        count._obj.value = 0 if self.write_fails else 1
        if self.write_fails:
            return False
        self.opcode = value._obj.value
        return True

    def FlushInstructionCache(self, handle, address, size):
        self.events.append(("flush", handle, address, size))
        return True


class LiveProcessWriterTests(unittest.TestCase):
    def setUp(self):
        self.client = MODULE.LiveProcess.__new__(MODULE.LiveProcess)
        self.client.pid = 42
        self.client.base = 0x140000000
        self.client.created = 1234
        self.client.handle = 123
        self.client.kernel = FakeKernel()
        self.kernel = self.client.kernel
        self.address = self.client.base + MODULE.PATCH_RVA

    def names(self):
        return [event[0] for event in self.kernel.events]

    def test_only_the_reviewed_address_and_opcode_pair_can_be_written(self):
        for address, expected, replacement in (
            (self.address + 1, 0x77, 0xEB),
            (self.address, 0x77, 0x90),
            (self.address, 0xEB, 0xEB),
            (self.address, 0x75, 0xEB),
        ):
            with self.subTest(address=address, expected=expected, replacement=replacement):
                with self.assertRaises(ValueError):
                    self.client.write_byte(address, expected, replacement)
        self.assertEqual([], self.kernel.events)

    def test_wrong_initial_byte_does_not_open_a_write_handle(self):
        self.kernel.opcode = 0x90
        with self.assertRaisesRegex(ValueError, "before write preparation"):
            self.client.write_byte(self.address, 0x77, 0xEB)
        self.assertEqual(["read"], self.names())

    def test_pid_reuse_is_rejected_and_new_handle_closed(self):
        self.kernel.created += 1
        with self.assertRaisesRegex(ValueError, "different process"):
            self.client.write_byte(self.address, 0x77, 0xEB)
        self.assertNotIn("protect", self.names())
        self.assertNotIn("write", self.names())
        self.assertEqual(("close", 456), self.kernel.events[-1])

    def test_byte_is_rechecked_through_the_write_handle(self):
        self.kernel.opcode_after_open = 0x90
        with self.assertRaisesRegex(ValueError, "before write$"):
            self.client.write_byte(self.address, 0x77, 0xEB)
        self.assertNotIn("protect", self.names())
        self.assertEqual(("close", 456), self.kernel.events[-1])

    def test_failed_write_still_restores_protection_flushes_and_closes(self):
        self.kernel.write_fails = True
        with self.assertRaisesRegex(OSError, "WriteProcessMemory"):
            self.client.write_byte(self.address, 0x77, 0xEB)
        self.assertEqual(0x77, self.kernel.opcode)
        self.assertEqual([
            ("protect", 456, self.address, 1, 0x20),
            ("flush", 456, self.address, 1),
            ("close", 456),
        ], self.kernel.events[-3:])

    def test_protection_restore_failure_is_reported_and_cache_still_flushed(self):
        self.kernel.restore_fails = True
        with self.assertRaisesRegex(OSError, "VirtualProtectEx\\(restore\\)"):
            self.client.write_byte(self.address, 0x77, 0xEB)
        self.assertEqual(["flush", "close"], self.names()[-2:])
        self.assertEqual(2, sum(event[0] == "protect" and event[-1] == 0x20
                                for event in self.kernel.events))
        self.assertEqual({self.address: 0x20}, self.client._pending_protection_restores)

    def test_transient_protection_restore_failure_retries_original_protection(self):
        self.kernel.restore_failures_remaining = 1
        self.assertEqual(0xEB, self.client.write_byte(self.address, 0x77, 0xEB))
        self.assertEqual(0x20, self.kernel.protection)
        self.assertEqual({}, self.client._pending_protection_restores)
        self.assertEqual(2, sum(event[0] == "protect" and event[-1] == 0x20
                                for event in self.kernel.events))

    def test_opcode_rollback_recovers_saved_protection_after_cleanup_failed(self):
        self.kernel.restore_failures_remaining = 2
        with self.assertRaisesRegex(OSError, "VirtualProtectEx\\(restore\\)"):
            self.client.write_byte(self.address, 0x77, 0xEB)
        self.assertEqual(0xEB, self.kernel.opcode)
        self.assertEqual(MODULE.PAGE_EXECUTE_READWRITE, self.kernel.protection)
        self.assertEqual(0x77, self.client.write_byte(self.address, 0xEB, 0x77))
        self.assertEqual(0x77, self.kernel.opcode)
        self.assertEqual(0x20, self.kernel.protection)
        self.assertEqual({}, self.client._pending_protection_restores)

    def test_unrecoverable_protection_refuses_further_writes_and_keeps_original(self):
        self.kernel.restore_fails = True
        with self.assertRaises(OSError):
            self.client.write_byte(self.address, 0x77, 0xEB)
        writes_before = sum(event[0] == "write" for event in self.kernel.events)
        with self.assertRaisesRegex(OSError, "VirtualProtectEx\\(restore\\)"):
            self.client.write_byte(self.address, 0xEB, 0x77)
        self.assertEqual(writes_before, sum(event[0] == "write" for event in self.kernel.events))
        self.assertEqual({self.address: 0x20}, self.client._pending_protection_restores)
        self.assertEqual(("close", 456), self.kernel.events[-1])

    def test_success_writes_one_byte_restores_and_verifies_before_closing(self):
        self.assertEqual(0xEB, self.client.write_byte(self.address, 0x77, 0xEB))
        self.assertIn(("open", 0x438, False, 42), self.kernel.events)
        self.assertEqual([("write", 456, self.address, 0xEB, 1)],
                         [event for event in self.kernel.events if event[0] == "write"])
        self.assertEqual([
            ("protect", 456, self.address, 1, 0x20),
            ("flush", 456, self.address, 1),
            ("read", 456, self.address, 1),
            ("close", 456),
        ], self.kernel.events[-4:])
        self.assertEqual(123, self.client.handle)

    def test_restore_is_also_only_a_one_byte_write(self):
        self.kernel.opcode = 0xEB
        self.assertEqual(0x77, self.client.write_byte(self.address, 0xEB, 0x77))
        self.assertIn(("write", 456, self.address, 0x77, 1), self.kernel.events)

    def test_ack_gate_writer_allows_only_its_reviewed_transition(self):
        address = self.client.base + MODULE.ACK_PATCH_RVA
        for original, target in ((0x75, 0xeb), (0xeb, 0x75)):
            self.kernel.opcode = original
            self.assertEqual(target, self.client.write_byte(address, original, target))
        for candidate, original, target in ((address + 1, 0x75, 0xeb),
                                             (address, 0x77, 0xeb),
                                             (address, 0x75, 0x90)):
            with self.assertRaises(ValueError):
                self.client.write_byte(candidate, original, target)

    def test_throttle_writer_allows_only_its_reviewed_transition(self):
        address = self.client.base + MODULE.THROTTLE_PATCH_RVA
        for original, target in ((0xf6, 0xce), (0xce, 0xf6)):
            self.kernel.opcode = original
            self.assertEqual(target, self.client.write_byte(address, original, target))
        for candidate, original, target in ((address + 1, 0xf6, 0xce),
                                             (address, 0x75, 0xeb),
                                             (address, 0xf6, 0x90)):
            with self.assertRaises(ValueError):
                self.client.write_byte(candidate, original, target)

    def test_scope_writer_is_limited_to_its_two_reviewed_bytes(self):
        scope = MODULE.ScopeProcess.__new__(MODULE.ScopeProcess)
        scope.__dict__.update(self.client.__dict__)
        for rva, original, replacement in ((0x158BAE1, 0x33, 0x41), (0x158BB5E, 0x74, 0xEB)):
            with self.assertRaises(ValueError):
                self.client.write_byte(self.client.base + rva, original, replacement)
            for expected, target in ((original, replacement), (replacement, original)):
                self.kernel.opcode = expected
                self.assertEqual(target, scope.write_byte(scope.base + rva, expected, target))
            with self.assertRaises(ValueError):
                scope.write_byte(scope.base + rva, original, 0x90)
        with self.assertRaises(ValueError):
            scope.write_byte(self.address, 0x77, 0xEB)


if __name__ == "__main__":
    unittest.main()
