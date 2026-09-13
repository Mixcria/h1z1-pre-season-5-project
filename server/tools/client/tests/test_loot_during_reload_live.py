import importlib.util
from pathlib import Path
import struct
import sys
import unittest
from unittest.mock import patch

CLIENT_TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(CLIENT_TOOLS))
spec = importlib.util.spec_from_file_location("loot_reload_live", CLIENT_TOOLS / "loot-during-reload-live.py")
live = importlib.util.module_from_spec(spec)
spec.loader.exec_module(live)


class FakeProcess:
    base = 0x150000000  # Deliberately relocated, not the preferred image base.
    started_unix = 1788701248.25

    def __init__(self):
        self.function = bytearray(live.evidence.ORIGINAL_FUNCTION)
        self.throttle = bytearray(live.throttle.ORIGINAL_FUNCTION)
        self.writes = []
        self.memory = {}
        for address, value in (
            (self.base + 0x3f696a0, 0x100000), (self.base + 0x3f69430, 0x200000),
            (self.base + 0x3f6a200, 0x300000), (0x200000 + 0x1948, 0x400000),
            (0x100000 + 0x321a0, 0x500000), (0x400000, self.base + 0x31dddc0),
            (0x500000, self.base + 0x315aec0), (0x300000 + 0x88, 0x600000),
            (0x600000, self.base + 0x3250158),
        ):
            self.memory[address] = struct.pack("<Q", value)

    def read(self, address, size):
        for rva, data in ((live.FUNCTION_RVA, self.function), (live.throttle.FUNCTION_RVA, self.throttle)):
            start = self.base + rva
            if start <= address < start + len(data):
                index = address - start
                return bytes(data[index:index + size])
        return self.memory[address][:size]

    def write_byte(self, address, expected, replacement):
        assert address in (self.base + live.PATCH_RVA, self.base + live.ACK_PATCH_RVA,
                           self.base + live.throttle.PATCH_RVA)
        is_throttle = address == self.base + live.throttle.PATCH_RVA
        data = self.throttle if is_throttle else self.function
        index = address - self.base - (live.throttle.FUNCTION_RVA if is_throttle else live.FUNCTION_RVA)
        assert data[index] == expected
        self.writes.append((address, expected, replacement))
        data[index] = replacement
        return replacement


def transition(state, session=1788701248):
    return f"2026-09-06\t14:35:10\tuser\t{session}\t1\t4\t123\tTransitionClientRunState: oldState=anything, newState={state}\n"


class LiveReloadGuardTests(unittest.TestCase):
    def test_exact_three_bytes_on_relocated_image_and_idempotent_restore(self):
        process = FakeProcess()
        self.assertTrue(live.change_verified(process))
        self.assertEqual(live.PATCHED_FUNCTION_V2, bytes(process.function))
        self.assertEqual(live.throttle.PATCHED_FUNCTION, bytes(process.throttle))
        self.assertEqual("patched-v3", live.inspect_state(process))
        self.assertFalse(live.change_verified(process))
        self.assertEqual([(process.base + live.PATCH_RVA, 0x77, 0xeb),
                          (process.base + live.ACK_PATCH_RVA, 0x75, 0xeb),
                          (process.base + live.throttle.PATCH_RVA, 0xf6, 0xce)], process.writes)
        self.assertTrue(live.change_verified(process, restore=True))
        self.assertFalse(live.change_verified(process, restore=True))
        self.assertEqual(live.evidence.ORIGINAL_FUNCTION, bytes(process.function))
        self.assertEqual(live.throttle.ORIGINAL_FUNCTION, bytes(process.throttle))
        self.assertEqual([(process.base + live.throttle.PATCH_RVA, 0xce, 0xf6),
                          (process.base + live.ACK_PATCH_RVA, 0xeb, 0x75),
                          (process.base + live.PATCH_RVA, 0xeb, 0x77)], process.writes[-3:])

    def test_existing_cancel_only_patch_upgrades_with_two_writes(self):
        process = FakeProcess()
        process.function[:] = live.evidence.PATCHED_FUNCTION
        self.assertEqual("patched-v1", live.function_state(process.function))
        self.assertTrue(live.change_verified(process))
        self.assertEqual("patched-v2", live.function_state(process.function))
        self.assertEqual("patched-v3", live.inspect_state(process))
        self.assertEqual([(process.base + live.ACK_PATCH_RVA, 0x75, 0xeb),
                          (process.base + live.throttle.PATCH_RVA, 0xf6, 0xce)], process.writes)

    def test_existing_v2_upgrades_with_only_the_throttle_write(self):
        process = FakeProcess()
        process.function[:] = live.PATCHED_FUNCTION_V2
        self.assertEqual("patched-v2", live.inspect_state(process))
        self.assertTrue(live.change_verified(process))
        self.assertEqual("patched-v3", live.inspect_state(process))
        self.assertEqual([(process.base + live.throttle.PATCH_RVA, 0xf6, 0xce)], process.writes)

    def test_failed_throttle_write_restores_exact_original_v1_or_v2_baseline(self):
        for initial in live.FUNCTION_STATES.values():
            for mutate_first in (False, True):
                with self.subTest(initial=live.function_state(initial), mutated=mutate_first):
                    process = FakeProcess()
                    process.function[:] = initial
                    write, failed = process.write_byte, False
                    def fail_throttle(address, expected, replacement):
                        nonlocal failed
                        if address == process.base + live.throttle.PATCH_RVA and not failed:
                            failed = True
                            if mutate_first:
                                write(address, expected, replacement)
                            raise OSError("simulated throttle failure")
                        return write(address, expected, replacement)
                    process.write_byte = fail_throttle
                    with self.assertRaisesRegex(OSError, "simulated"):
                        live.change_verified(process)
                    self.assertEqual(initial, bytes(process.function))
                    self.assertEqual(live.throttle.ORIGINAL_FUNCTION, bytes(process.throttle))

    def test_unexpected_throttle_bytes_refuse_before_either_function_changes(self):
        for index in (0, live.throttle.PATCH_INDEX, live.throttle.PATCH_INDEX + 1, 282):
            process = FakeProcess()
            process.throttle[index] ^= 1
            with self.assertRaisesRegex(ValueError, "283-byte"):
                live.change_verified(process)
            self.assertEqual([], process.writes)

    def test_racing_change_between_snapshot_and_first_write_never_becomes_a_trusted_baseline(self):
        for mutated_read in (1, 2, 3):
            with self.subTest(mutated_read=mutated_read):
                process = FakeProcess()
                read, calls = process.read, 0
                def racing_read(address, size):
                    nonlocal calls
                    calls += 1
                    if calls == mutated_read:
                        process.function[0] ^= 1
                    return read(address, size)
                process.read = racing_read
                with self.assertRaisesRegex(ValueError, "468-byte"):
                    live.change_verified(process)
                self.assertEqual([], process.writes)

    def test_throttle_only_mixed_image_is_rejected(self):
        process = FakeProcess()
        process.throttle[:] = live.throttle.PATCHED_FUNCTION
        with self.assertRaisesRegex(ValueError, "mixed image"):
            live.change_verified(process)
        self.assertEqual([], process.writes)

    def test_v1_restore_leaves_original_ack_branch_untouched(self):
        process = FakeProcess()
        process.function[:] = live.evidence.PATCHED_FUNCTION
        self.assertTrue(live.change_verified(process, restore=True))
        self.assertEqual("original", live.function_state(process.function))
        self.assertEqual([(process.base + live.PATCH_RVA, 0xeb, 0x77)], process.writes)

    def test_reviewed_branches_keep_their_targets_and_other_gates(self):
        original = live.evidence.ORIGINAL_FUNCTION
        patched = live.PATCHED_FUNCTION_V2
        self.assertEqual([live.ACK_PATCH_INDEX, live.evidence.PATCH_INDEX],
                         [i for i, (a, b) in enumerate(zip(original, patched)) if a != b])
        for index, expected_original, target in (
            (live.ACK_PATCH_INDEX, b"\x75\x1b", 0x1411c6e99),
            (live.evidence.PATCH_INDEX, b"\x77\x29", 0x1411c6ef7),
        ):
            self.assertEqual(expected_original, original[index:index + 2])
            self.assertEqual(0xeb, patched[index])
            self.assertEqual(target, live.evidence.FUNCTION_VA + index + 2
                             + struct.unpack("b", patched[index + 1:index + 2])[0])
        # The acknowledgement bypass lands at the complete native iron-sights
        # test, not at the sender. All earlier target/busy checks are unchanged.
        ads = 0x1411c6e99 - live.evidence.FUNCTION_VA
        self.assertEqual(b"\x83\xbe\x08\x09\x00\x00\x02", patched[ads:ads + 7])
        self.assertEqual(original[:live.ACK_PATCH_INDEX], patched[:live.ACK_PATCH_INDEX])

    def test_failed_second_write_rolls_back_only_this_operation(self):
        for initial in (live.evidence.ORIGINAL_FUNCTION, live.evidence.PATCHED_FUNCTION):
            for fail_after_write in (False, True):
                with self.subTest(initial=live.function_state(initial), mutated=fail_after_write):
                    process = FakeProcess()
                    process.function[:] = initial
                    write = process.write_byte
                    failed = False
                    def fail_ack(address, expected, replacement):
                        nonlocal failed
                        if address == process.base + live.ACK_PATCH_RVA and not failed:
                            failed = True
                            if fail_after_write:
                                write(address, expected, replacement)
                            raise OSError("simulated ack write failure")
                        return write(address, expected, replacement)
                    process.write_byte = fail_ack
                    with self.assertRaisesRegex(OSError, "simulated"):
                        live.change_verified(process)
                    self.assertEqual(initial, bytes(process.function))
                    if initial == live.evidence.PATCHED_FUNCTION:
                        self.assertTrue(all(a == process.base + live.ACK_PATCH_RVA for a, _, _ in process.writes))

    def test_failed_restore_rolls_back_to_v2(self):
        process = FakeProcess()
        process.function[:] = live.PATCHED_FUNCTION_V2
        write = process.write_byte
        failed = False
        def fail_cancel_restore(address, expected, replacement):
            nonlocal failed
            if address == process.base + live.PATCH_RVA and not failed:
                failed = True
                raise OSError("simulated restore failure")
            return write(address, expected, replacement)
        process.write_byte = fail_cancel_restore
        with self.assertRaisesRegex(OSError, "simulated"):
            live.change_verified(process, restore=True)
        self.assertEqual(live.PATCHED_FUNCTION_V2, bytes(process.function))

    def test_ack_only_image_is_not_an_accepted_intermediate_state(self):
        process = FakeProcess()
        process.function[live.ACK_PATCH_INDEX] = 0xeb
        with self.assertRaisesRegex(ValueError, "468-byte"):
            live.change_verified(process)
        self.assertEqual([], process.writes)

    def test_any_unexpected_function_byte_refuses_without_writing(self):
        for index in (0, live.evidence.PATCH_INDEX, live.evidence.PATCH_INDEX + 1, 467):
            process = FakeProcess()
            process.function[index] ^= 1
            for restore in (False, True):
                with self.assertRaisesRegex(ValueError, "468-byte"):
                    live.change_verified(process, restore=restore)
            self.assertEqual([], process.writes)

    def test_readback_must_match_the_whole_function(self):
        process = FakeProcess()
        original_write = process.write_byte
        def changed_after_write(*args):
            original_write(*args)
            process.function[0] = live.evidence.ORIGINAL_FUNCTION[0] ^ 1
        process.write_byte = changed_after_write
        with self.assertRaisesRegex(ValueError, "468-byte"):
            live.change_verified(process)

    def test_readiness_requires_current_launch_and_latest_state_running(self):
        process = FakeProcess()
        running = transition("cClientRunStateRunning")
        self.assertIsNotNone(live.ready_identity(process, running))
        self.assertIsNone(live.ready_identity(process, transition("cClientRunStateRunning", 1788701000)))
        self.assertIsNone(live.ready_identity(process, running + transition("cClientRunStateWaitForZoneLoad")))
        self.assertIsNone(live.ready_identity(process, "newState=cClientRunStateRunning"))
        process.memory[0x400000] = struct.pack("<Q", process.base + 0x31dddc8)
        self.assertIsNone(live.ready_identity(process, running))

    def test_readiness_stability_restarts_after_world_identity_changes(self):
        process = FakeProcess()
        clock = [0.0]
        identity_calls = []
        def identity(*_):
            identity_calls.append(clock[0])
            return (1, 2, 3) if clock[0] < 2 else (1, 4, 5)
        with patch.object(live, "ready_identity", side_effect=identity):
            result = live.wait_ready(process, 8, Path("unused"), now=lambda: clock[0],
                                     sleep=lambda seconds: clock.__setitem__(0, clock[0] + seconds),
                                     log_reader=lambda _: "")
        self.assertEqual((1, 4, 5), result)
        self.assertGreaterEqual(clock[0], 5)
        self.assertEqual([], process.writes)

    def test_not_initialized_times_out_without_writing(self):
        process = FakeProcess()
        clock = [0.0]
        with self.assertRaisesRegex(ValueError, "no patch applied"):
            live.wait_ready(process, 4, Path("unused"), now=lambda: clock[0],
                            sleep=lambda seconds: clock.__setitem__(0, clock[0] + seconds),
                            log_reader=lambda _: transition("cClientRunStateWaitForFirstZone"))
        self.assertEqual(4, clock[0])
        self.assertEqual([], process.writes)

    def test_withdrawn_disk_command_refuses_apply_before_install(self):
        with patch.object(sys, "argv", ["installer", "--exe", "unused", "--backup", "unused", "--apply"]), \
             patch.object(live.evidence, "install") as install, \
             self.assertRaises(SystemExit) as refused:
            live.evidence.main()
        self.assertEqual(2, refused.exception.code)
        install.assert_not_called()


if __name__ == "__main__":
    unittest.main()
