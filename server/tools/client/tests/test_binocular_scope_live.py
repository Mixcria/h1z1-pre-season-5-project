import importlib.util
from contextlib import contextmanager
import itertools
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location("scope_live", Path(__file__).resolve().parents[1] / "binocular-scope-live.py")
scope = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)
with Path("C:/Aug2017/Client/H1Z1.exe").open("rb") as source:
    source.seek(scope.FUNCTION_FILE_OFFSET)
    ORIGINAL = source.read(scope.FUNCTION_SIZE)


class BufferProcess:
    base = 0x150000000

    def __init__(self, fail_at=None, fail_after_write=False):
        self.data = bytearray(ORIGINAL)
        self.calls = []
        self.fail_at = fail_at
        self.fail_after_write = fail_after_write
        self.locked = False

    @contextmanager
    def patch_lock(self):
        assert not self.locked
        self.locked = True
        try:
            yield
        finally:
            self.locked = False

    def read(self, address, size):
        assert address == self.base + scope.FUNCTION_RVA
        return bytes(self.data[:size])

    def write_byte(self, address, expected, replacement):
        assert self.locked, "Full transition and rollback must own the launcher/watcher lock"
        index = address - self.base - scope.FUNCTION_RVA
        assert self.data[index] == expected
        self.calls.append((address, expected, replacement))
        fail = len(self.calls) == self.fail_at
        if fail and not self.fail_after_write:
            raise OSError("simulated failed second write")
        self.data[index] = replacement
        if fail:
            raise OSError("simulated API failure after writing its byte")


def scope_flow(body, *, force=True, airborne=False, held=True, release=False,
               busy=False, inventory=False, scoped=False):
    """Control-flow model of reviewed b700 branches, not a substitute for live QA.

    Reads the two actual branch bytes, including their short-jump destinations.
    The separate native mode-switch/animation path is intentionally not modeled.
    """
    camera_fp = scoped
    synthetic_release = release or airborne or busy
    force_path = force and not inventory
    new_entry = body[0x3e1] == 0x41  # 14158bae1: jz target bb23 instead of bb15
    new_exit = body[0x45e] == 0xeb   # 14158bb5e: unconditional jump to bb93
    if force_path and held and busy and scoped and camera_fp:
        scoped = False
        camera_fp = False
    skip_early_return = new_entry and force_path and held and not busy
    if held and synthetic_release and not skip_early_return:
        return camera_fp, scoped
    if force_path:
        if held and not camera_fp:
            scoped = True
            camera_fp = True
        if synthetic_release and scoped and camera_fp and not new_exit:
            scoped = False
            camera_fp = False
        if scoped and not held:
            scoped = False
            camera_fp = False
    return camera_fp, scoped


class BinocularScopeLiveTests(unittest.TestCase):
    def test_launcher_mutex_owns_entire_transition_and_releases_on_failure(self):
        for wait_result in (0, 0x80, 258):  # owned, abandoned-but-owned, timeout
            events = []
            native = scope.ScopeProcess.__new__(scope.ScopeProcess)
            native.pid = 123
            native.kernel = SimpleNamespace(
                CreateMutexW=lambda *args: events.append(("create", args)) or 456,
                WaitForSingleObject=lambda *args: wait_result,
                ReleaseMutex=lambda handle: events.append(("release", handle)),
                CloseHandle=lambda handle: events.append(("close", handle)),
            )
            if wait_result == 258:
                with self.assertRaisesRegex(OSError, "Another binocular helper"):
                    with native.patch_lock():
                        self.fail("Timed-out helper cannot inspect/write a transition")
                self.assertNotIn(("release", 456), events)
            else:
                with self.assertRaisesRegex(ValueError, "simulated transition failure"):
                    with native.patch_lock():
                        raise ValueError("simulated transition failure")
                self.assertIn(("release", 456), events)
            self.assertEqual("Local\\Cranberry.BinocularScope.123", events[0][1][2])
            self.assertEqual(("close", 456), events[-1])

    def test_failed_upgrade_restores_recognized_partial_states_inside_lock(self):
        for initial in range(4):
            for restore in (False, True):
                for after in (False, True):
                    target = 0 if restore else 3
                    count = (initial ^ target).bit_count()
                    for fail_at in range(1, count + 1):
                        process = BufferProcess(fail_at=fail_at, fail_after_write=after)
                        for mask, (rva, _, patched) in scope.SITES.items():
                            if initial & mask:
                                process.data[rva - scope.FUNCTION_RVA] = patched
                        before = bytes(process.data)
                        with self.assertRaisesRegex(OSError, "rolled back to state"):
                            scope.change_verified(process, restore=restore)
                        self.assertEqual(before, bytes(process.data))
                        self.assertFalse(process.locked)

    def test_original_full_handler_hash_and_exact_branch_targets(self):
        self.assertEqual(0, scope.function_state(ORIGINAL))
        self.assertEqual(b"\x74\x33", ORIGINAL[0x3e0:0x3e2])
        self.assertEqual(b"\x74\x33", ORIGINAL[0x45e:0x460])
        self.assertEqual(0x158bb23, 0x158bae0 + 2 + 0x41)
        self.assertEqual(0x158bb93, 0x158bb5e + 2 + 0x33)

    def test_two_atomic_writes_in_safe_order_and_reversal(self):
        process = BufferProcess()
        self.assertTrue(scope.change_verified(process))
        self.assertEqual(3, scope.inspect(process))
        self.assertEqual([0x45e, 0x3e1], [address - process.base - scope.FUNCTION_RVA for address, _, _ in process.calls])
        self.assertEqual([0x3e1, 0x45e], [i for i, (a, b) in enumerate(zip(ORIGINAL, process.data)) if a != b])
        self.assertFalse(scope.change_verified(process))
        self.assertTrue(scope.change_verified(process, restore=True))
        self.assertEqual(ORIGINAL, bytes(process.data))
        self.assertFalse(scope.change_verified(process, restore=True))

    def test_partial_failure_rolls_back_even_when_failed_api_wrote_byte(self):
        for after in (False, True):
            process = BufferProcess(fail_at=2, fail_after_write=after)
            with self.assertRaisesRegex(OSError, "rolled back to state 0"):
                scope.change_verified(process)
            self.assertEqual(ORIGINAL, bytes(process.data))

    def test_unknown_function_is_refused_without_writing(self):
        for index in (0, 0x3e0, 0x45f, len(ORIGINAL) - 1):
            process = BufferProcess()
            process.data[index] ^= 1
            with self.assertRaisesRegex(ValueError, "Full SecondaryFire"):
                scope.change_verified(process)
            self.assertEqual([], process.calls)

    def test_airborne_held_enters_scope_and_release_exits(self):
        process = BufferProcess()
        scope.change_verified(process)
        self.assertEqual((False, False), scope_flow(ORIGINAL, airborne=True))
        self.assertEqual((True, True), scope_flow(process.data, airborne=True))
        self.assertEqual((False, False), scope_flow(process.data, airborne=True, held=False, release=True, scoped=True))
        self.assertEqual((True, True), scope_flow(process.data, airborne=False))
        self.assertEqual((False, False), scope_flow(process.data, held=False, release=True, scoped=True))

    def test_non_force_inventory_and_busy_controller_paths_are_unchanged(self):
        process = BufferProcess()
        scope.change_verified(process)
        for force, airborne, held, release, busy, inventory, scoped in itertools.product((False, True), repeat=7):
            if force and not inventory and not busy:
                continue
            args = dict(force=force, airborne=airborne, held=held, release=release,
                        busy=busy, inventory=inventory, scoped=scoped)
            self.assertEqual(scope_flow(ORIGINAL, **args), scope_flow(process.data, **args), args)


if __name__ == "__main__":
    unittest.main()
