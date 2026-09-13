"""Bounded host-watcher lifecycle tests; no Windows process is opened or patched."""

import importlib.util
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SOURCE = Path(__file__).resolve().parents[1] / "watch-local-client-qol.py"
SPEC = importlib.util.spec_from_file_location("watch_local_client_qol", SOURCE)
MODULE = importlib.util.module_from_spec(SPEC)
sys.path.insert(0, str(SOURCE.parent))
try:
    SPEC.loader.exec_module(MODULE)
finally:
    sys.path.pop(0)


class Host:
    running = True

    def alive(self):
        return self.running


class Child:
    def __init__(self, pid, code=None):
        self.pid = pid
        self.code = code
        self.terminations = 0
        self.waits = []

    def poll(self):
        return self.code

    def terminate(self):
        self.terminations += 1
        self.code = -15

    def wait(self, timeout):
        self.waits.append(timeout)
        return self.code


class HostWatcherTests(unittest.TestCase):
    def test_development_and_published_hosts_are_supported_without_accepting_other_executables(self):
        self.assertTrue(MODULE.supported_host_path(MODULE.HOST_PATH))
        self.assertTrue(MODULE.supported_host_path(MODULE.ROOT / "out/launcher-runtime/test-build/Cranberry.Host.exe"))
        for relative in ("out/launcher-runtime/test-build/H1Z1.exe",
                         "out/launcher-runtime-other/test-build/Cranberry.Host.exe",
                         "out/launcher-runtime/../elsewhere/Cranberry.Host.exe"):
            with self.subTest(path=relative):
                self.assertFalse(MODULE.supported_host_path(MODULE.ROOT / relative))

    def setUp(self):
        self.host = Host()
        self.events = []
        self.sleeps = []

    def emit(self, action, **fields):
        self.events.append((action, fields))

    def sleep_then_stop(self, seconds):
        self.sleeps.append(seconds)
        self.host.running = False

    def test_one_launch_per_pid_and_creation_and_fixed_poll_interval(self):
        launches = []
        iterations = [0]
        identities = [[(10, 100)], [(10, 100)], [(10, 200)]]

        def sleep(seconds):
            self.sleeps.append(seconds)
            iterations[0] += 1
            self.host.running = iterations[0] < len(identities)

        def launch(identity):
            launches.append(identity)
            return []

        MODULE.watch(self.host, lambda: identities[iterations[0]], launch, self.emit, sleep)
        self.assertEqual([(10, 100), (10, 200)], launches)
        self.assertEqual([1, 1, 1], self.sleeps)

    def test_host_exit_terminates_only_pending_owned_helper_children(self):
        reload = Child(500)
        scope = Child(501, code=0)
        unrelated_client = Child(10)
        jobs = [(reload, "reload", (10, 100)), (scope, "scope", (10, 100))]
        def sleep(seconds):
            self.sleeps.append(seconds)
            self.host.running = len(self.sleeps) < 2
        MODULE.watch(self.host, lambda: [(10, 100)], lambda _: jobs,
                     self.emit, sleep)
        self.assertEqual(1, reload.terminations)
        self.assertEqual([2], reload.waits)
        self.assertEqual(0, scope.terminations)
        self.assertEqual(0, unrelated_client.terminations)
        self.assertTrue(any(action == "helper exited" and fields["exitCode"] == 0
                            for action, fields in self.events))

    def test_dead_host_never_discovers_or_launches_a_client(self):
        self.host.running = False

        def forbidden(*_):
            self.fail("Work must not start after the bound host exits")

        MODULE.watch(self.host, forbidden, forbidden, self.emit, forbidden)
        self.assertEqual("watcher stopped", self.events[-1][0])

    def test_host_exit_during_discovery_prevents_helper_launch(self):
        def discover():
            self.host.running = False
            return [(10, 100)]

        MODULE.watch(self.host, discover, lambda _: self.fail("Host already exited"),
                     self.emit, self.sleep_then_stop)
        self.assertFalse(any(action == "helper started" for action, _ in self.events))

    def test_refused_identity_is_not_retried_on_every_poll(self):
        attempts = []

        def launch(identity):
            attempts.append(identity)
            raise ValueError("Unrecognized function")

        def sleep(seconds):
            self.sleeps.append(seconds)
            self.host.running = len(self.sleeps) < 3

        MODULE.watch(self.host, lambda: [(10, 100)], launch, self.emit, sleep)
        self.assertEqual([(10, 100)], attempts)
        self.assertEqual(1, sum(action == "client refused" for action, _ in self.events))

    def test_discovery_failure_also_cleans_up_owned_waiting_helpers(self):
        child = Child(500)
        calls = [0]

        def discover():
            calls[0] += 1
            if calls[0] > 1:
                raise OSError("Snapshot failed")
            return [(10, 100)]

        with self.assertRaisesRegex(OSError, "Snapshot failed"):
            MODULE.watch(self.host, discover, lambda _: [(child, "reload", (10, 100))],
                         self.emit, lambda seconds: self.sleeps.append(seconds))
        self.assertEqual(1, child.terminations)

    def test_already_applied_functions_do_not_spawn_duplicate_helpers(self):
        client = SimpleNamespace(created=100, base=0x140000000, path=Path("unused"),
                                 read=lambda address, size: b"patched")
        reload = SimpleNamespace(FUNCTION_RVA=1, evidence=SimpleNamespace(ORIGINAL_FUNCTION=b"original"),
                                 inspect_state=lambda _: "patched-v3", verify_disk=lambda _: None)
        scope = SimpleNamespace(FUNCTION_RVA=2, FUNCTION_SIZE=1551, function_state=lambda _: 3)
        with patch.object(MODULE, "LiveProcess") as process, \
                patch.object(MODULE, "load_helper", side_effect=[(Path("reload.py"), reload), (Path("scope.py"), scope)]), \
                patch.object(MODULE.subprocess, "Popen") as popen:
            process.return_value.__enter__.return_value = client
            result = MODULE.spawn_helpers((10, 100), True, self.emit)
            self.assertEqual([], result.jobs)
            self.assertFalse(result.retry)
            popen.assert_not_called()
        self.assertEqual(2, sum(action == "already applied" for action, _ in self.events))

    def test_new_helpers_are_hidden_bounded_and_use_the_observed_client_identity(self):
        client = SimpleNamespace(created=100, base=0x140000000, path=Path("unused"),
                                 read=lambda address, size: b"original")
        reload = SimpleNamespace(FUNCTION_RVA=1, evidence=SimpleNamespace(ORIGINAL_FUNCTION=b"original"),
                                 inspect_state=lambda _: "original", verify_disk=lambda _: None)
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(MODULE, "ROOT", Path(directory)), \
                patch.object(MODULE, "LiveProcess") as process, \
                patch.object(MODULE, "load_helper", return_value=(Path("reload.py"), reload)), \
                patch.object(MODULE.subprocess, "Popen", return_value=Child(500)) as popen:
            (Path(directory) / "logs").mkdir()
            process.return_value.__enter__.return_value = client
            result = MODULE.spawn_helpers((10, 100), False, self.emit)
            command = popen.call_args.args[0]
            self.assertEqual(["--pid", "10", "--apply", "--wait-seconds", "600"], command[3:8])
            self.assertEqual(MODULE.subprocess.CREATE_NO_WINDOW, popen.call_args.kwargs["creationflags"])
            self.assertFalse(popen.call_args.kwargs["shell"])
            self.assertEqual((10, 100), result.jobs[0][2])

    def test_real_original_v1_v2_upgrade_and_v3_is_already_applied(self):
        script, reload = MODULE.load_helper("loot-during-reload-live.py")
        for native, throttle, expect_spawn in (
            (reload.evidence.ORIGINAL_FUNCTION, reload.throttle.ORIGINAL_FUNCTION, True),
            (reload.evidence.PATCHED_FUNCTION, reload.throttle.ORIGINAL_FUNCTION, True),
            (reload.PATCHED_FUNCTION_V2, reload.throttle.ORIGINAL_FUNCTION, True),
            (reload.PATCHED_FUNCTION_V2, reload.throttle.PATCHED_FUNCTION, False),
        ):
            with self.subTest(state=reload.function_state(native), throttle=reload.throttle.function_state(throttle)):
                client = SimpleNamespace(created=100, base=0x140000000, path=Path("unused"),
                                         read=lambda address, size: native if address == 0x140000000 + reload.FUNCTION_RVA else throttle)
                with tempfile.TemporaryDirectory() as directory, \
                        patch.object(MODULE, "ROOT", Path(directory)), \
                        patch.object(MODULE, "LiveProcess") as process, \
                        patch.object(reload, "verify_disk"), \
                        patch.object(MODULE.subprocess, "Popen", return_value=Child(500)) as popen:
                    (Path(directory) / "logs").mkdir()
                    process.return_value.__enter__.return_value = client
                    result = MODULE.spawn_helpers((10, 100), False, self.emit)
                    self.assertEqual(expect_spawn, popen.called)
                    self.assertEqual(int(expect_spawn), len(result.jobs))

    def test_camera_look_starts_only_when_enabled_and_skips_an_applied_client(self):
        client = SimpleNamespace(created=100, base=0x140000000, path=Path("unused"))
        reload = SimpleNamespace(inspect_state=lambda _: "patched-v3", verify_disk=lambda _: None)
        for applied in (False, True):
            with self.subTest(applied=applied), tempfile.TemporaryDirectory() as directory, \
                    patch.object(MODULE, "ROOT", Path(directory)), \
                    patch.object(MODULE, "LiveProcess") as process, \
                    patch.object(MODULE, "load_helper", side_effect=[
                        (Path("reload.py"), reload),
                        (Path("camera-only-look-live.py"), SimpleNamespace(inspect=lambda _: applied))]), \
                    patch.object(MODULE.subprocess, "Popen", return_value=Child(500)) as popen:
                (Path(directory) / "logs").mkdir()
                process.return_value.__enter__.return_value = client
                result = MODULE.spawn_helpers((10, 100), False, self.emit, include_headlook=True)
                self.assertEqual(not applied, popen.called)
                self.assertEqual([] if applied else ["camera-look"], [job[1] for job in result.jobs])
                if not applied:
                    self.assertEqual("camera-only-look-live.py", popen.call_args.args[0][2])

    def test_same_client_can_wait_over_ten_minutes_before_world_is_ready(self):
        elapsed = [0]
        launches = []
        def sleep(seconds):
            elapsed[0] += seconds
            self.host.running = elapsed[0] < 702
        MODULE.watch(self.host, lambda: [(10, 100)], lambda identity: launches.append((elapsed[0], identity)) or [],
                     self.emit, sleep, ready=lambda _: elapsed[0] >= 700, now=lambda: elapsed[0])
        self.assertEqual([(700, (10, 100))], launches)

    def test_failed_helper_retries_after_cooldown_and_rechecks_world_readiness(self):
        elapsed = [0]
        launches = []
        first = Child(500, code=1)  # Includes a helper timing out during zoning.
        def launch(identity):
            launches.append(elapsed[0])
            return [(first, "reload", identity)] if len(launches) == 1 else []
        def sleep(seconds):
            elapsed[0] += seconds
            self.host.running = elapsed[0] < 10
        MODULE.watch(self.host, lambda: [(10, 100)], launch, self.emit, sleep,
                     ready=lambda _: elapsed[0] == 0 or elapsed[0] >= 8, now=lambda: elapsed[0])
        self.assertEqual([0, 8], launches)

    def test_transient_function_read_failure_retries_after_five_seconds(self):
        elapsed = [0]
        attempts = []
        def launch(identity):
            attempts.append(elapsed[0])
            if len(attempts) == 1:
                raise OSError("Process module temporarily unreadable")
            return []
        def sleep(seconds):
            elapsed[0] += seconds
            self.host.running = elapsed[0] < 7
        MODULE.watch(self.host, lambda: [(10, 100)], launch, self.emit, sleep, now=lambda: elapsed[0])
        self.assertEqual([0, 5], attempts)


if __name__ == "__main__":
    unittest.main()
