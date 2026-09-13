"""Maintain the reviewed runtime fixes while this local Cranberry host is alive.

This development-wrapper companion covers clients launched directly from Explorer.
It never writes an executable or sends game input. Existing deferred helpers retain
their full-image, full-function, process identity, and initialized-world checks.
Only children created by this watcher are stopped when the bound host exits.
"""

import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import importlib.util
from functools import lru_cache
import json
from pathlib import Path
import struct
import subprocess
import sys
import time

from loot_reload_live_process import LiveProcess


ROOT = Path("C:/Aug2017")
HOST_PATH = ROOT / "Server/src/Cranberry.Host/bin/Debug/net10.0/Cranberry.Host.exe"
HELPERS = Path(__file__).resolve().parent


def supported_host_path(path):
    """Accept the development host and this project's separately published runtimes."""
    path = Path(path).resolve()
    if path == HOST_PATH.resolve():
        return True
    if path.name.lower() != "cranberry.host.exe":
        return False
    runtime_root = (ROOT / "out/launcher-runtime").resolve()
    return path.is_relative_to(runtime_root) and path.parent != runtime_root


class ProcessEntry(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD),
                ("pid", wintypes.DWORD), ("heap", ctypes.c_size_t),
                ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                ("parent", wintypes.DWORD), ("priority", wintypes.LONG),
                ("flags", wintypes.DWORD), ("exe", wintypes.WCHAR * 260)]


class HostLease:
    """A handle to one exact host instance, rather than a periodically reopened PID."""
    def __init__(self, pid, created):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        declarations = [
            ("OpenProcess", [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            ("CloseHandle", [wintypes.HANDLE], wintypes.BOOL),
            ("WaitForSingleObject", [wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
            ("QueryFullProcessImageNameW",
             [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            ("GetProcessTimes", [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4, wintypes.BOOL),
            ("CreateToolhelp32Snapshot", [wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
            ("Process32FirstW", [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)], wintypes.BOOL),
            ("Process32NextW", [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)], wintypes.BOOL),
        ]
        for name, args, result in declarations:
            function = getattr(self.kernel, name)
            function.argtypes, function.restype = args, result
        self.handle = self.kernel.OpenProcess(0x101000, False, pid)  # SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "OpenProcess(host)")
        try:
            buffer = ctypes.create_unicode_buffer(32768)
            length = wintypes.DWORD(len(buffer))
            if not self.kernel.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(length)):
                raise OSError(ctypes.get_last_error(), "QueryFullProcessImageNameW(host)")
            if not supported_host_path(buffer.value):
                raise ValueError("Watcher requires a C:/Aug2017 development or published gameplay host")
            times = [wintypes.FILETIME() for _ in range(4)]
            if not self.kernel.GetProcessTimes(self.handle, *(ctypes.byref(value) for value in times)):
                raise OSError(ctypes.get_last_error(), "GetProcessTimes(host)")
            actual = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
            if actual != created:
                raise ValueError("Host PID creation time changed before watcher startup")
            if not self.alive():
                raise ValueError("Host has already exited")
        except BaseException:
            self.close()
            raise

    def alive(self):
        state = self.kernel.WaitForSingleObject(self.handle, 0)
        if state == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error(), "WaitForSingleObject(host)")
        return state == 258  # WAIT_TIMEOUT; signaled handles refer to exited processes

    def clients(self):
        snapshot = self.kernel.CreateToolhelp32Snapshot(2, 0)
        if snapshot == ctypes.c_void_p(-1).value:
            raise OSError(ctypes.get_last_error(), "CreateToolhelp32Snapshot")
        pids = []
        try:
            entry = ProcessEntry()
            entry.size = ctypes.sizeof(entry)
            more = self.kernel.Process32FirstW(snapshot, ctypes.byref(entry))
            while more:
                if entry.exe.lower() == "h1z1.exe":
                    pids.append(entry.pid)
                more = self.kernel.Process32NextW(snapshot, ctypes.byref(entry))
        finally:
            self.kernel.CloseHandle(snapshot)
        found = []
        for pid in pids:
            try:
                with LiveProcess(pid) as client:
                    found.append((pid, client.created))
            except (OSError, ValueError):
                # A module may not be readable yet, or this may be another client install.
                continue
        return found

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


@lru_cache(maxsize=3)
def load_helper(name):
    script = HELPERS / name
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return script, module


def client_ready(identity):
    """Keep title/lobby waiting in this watcher, outside the helper's 600-second limit."""
    _, startup = load_helper("loot-during-reload-live.py")
    try:
        with LiveProcess(identity[0]) as client:
            if client.created != identity[1]:
                return False
            log = startup.read_log(client.path.parent / "Logs" / startup.CLIENT_LOG)
            return startup.ready_identity(client, log) is not None
    except (OSError, ValueError, struct.error):
        # Modules, logs, and world objects can be unavailable while this launch initializes.
        return False


class SpawnResult:
    def __init__(self, jobs, retry=False):
        self.jobs, self.retry = jobs, retry


def spawn_helpers(identity, include_scope, emit, include_headlook=False):
    pid, created = identity
    reload_script, reload = load_helper("loot-during-reload-live.py")
    specs = [("reload", reload_script, reload.inspect_state, "patched-v3")]
    if include_scope:
        scope_script, scope = load_helper("binocular-scope-live.py")
        specs.append(("scope", scope_script,
                      lambda client: scope.function_state(client.read(client.base + scope.FUNCTION_RVA,
                                                                       scope.FUNCTION_SIZE)), 3))
    if include_headlook:
        look_script, look = load_helper("camera-only-look-live.py")
        specs.append(("camera-look", look_script, look.inspect, True))
    jobs = []
    retry = False
    with LiveProcess(pid) as client:
        if client.created != created:
            raise ValueError("Client PID changed before helper launch")
        reload.verify_disk(client.path)
        for name, script, state, desired in specs:
            try:
                if state(client) == desired:
                    emit("already applied", clientPid=pid, created=created, helper=name)
                    continue
                stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S-%f")
                log = ROOT / "logs" / f"client-qol-{name}-{pid}-{stamp}.jsonl"
                with log.with_suffix(".stderr.log").open("xb") as errors:
                    child = subprocess.Popen(
                        [sys.executable, "-u", str(script), "--pid", str(pid), "--apply",
                         "--wait-seconds", "600", "--log", str(log)],
                        cwd=ROOT / "Server", stdin=subprocess.DEVNULL,
                        stdout=subprocess.DEVNULL, stderr=errors,
                        creationflags=subprocess.CREATE_NO_WINDOW, shell=False,
                    )
                jobs.append((child, name, identity))
                emit("helper started", clientPid=pid, created=created, helper=name,
                     helperPid=child.pid, log=str(log))
            except (OSError, ValueError) as error:
                retry |= isinstance(error, OSError)
                emit("helper refused", clientPid=pid, created=created, helper=name, reason=str(error))
    return SpawnResult(jobs, retry)


def watch(host, discover, launch, emit, sleep=time.sleep, *, ready=lambda _: True, now=time.monotonic):
    seen = set()
    retry_after = {}
    children = []
    try:
        while host.alive():
            pending = []
            for child, name, identity in children:
                code = child.poll()
                if code is None:
                    pending.append((child, name, identity))
                else:
                    emit("helper exited", helper=name, helperPid=child.pid,
                         clientPid=identity[0], exitCode=code)
                    if code != 0:
                        seen.discard(identity)
                        retry_after[identity] = now() + 5
            children = pending
            active = {identity for _, _, identity in children}
            for identity in discover():
                if (identity in seen or identity in active or
                        now() < retry_after.get(identity, 0) or not host.alive()):
                    continue
                if not ready(identity) or not host.alive():
                    continue
                try:
                    result = launch(identity)
                    if isinstance(result, SpawnResult):
                        children.extend(result.jobs)
                        if result.retry:
                            retry_after[identity] = now() + 5
                        else:
                            seen.add(identity)
                    else:
                        children.extend(result)
                        seen.add(identity)
                except (OSError, ValueError) as error:
                    if isinstance(error, OSError):
                        retry_after[identity] = now() + 5
                    else:
                        seen.add(identity)  # Unexpected build/function bytes require review.
                    emit("client refused", clientPid=identity[0], created=identity[1], reason=str(error))
            sleep(1)
    finally:
        for child, name, identity in children:
            if child.poll() is None:
                try:
                    child.terminate()
                    try:
                        child.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait(timeout=2)
                    emit("pending helper stopped", helper=name, helperPid=child.pid, clientPid=identity[0])
                except (OSError, subprocess.TimeoutExpired) as error:
                    emit("helper stop failed", helper=name, helperPid=child.pid,
                         clientPid=identity[0], reason=str(error))
        emit("watcher stopped")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host-pid", type=int, required=True)
    parser.add_argument("--host-created", type=int, required=True, help="Exact Windows creation FILETIME")
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--include-scope", action="store_true")
    parser.add_argument("--include-headlook", action="store_true")
    args = parser.parse_args()
    if not 0 < args.host_pid <= 0xFFFFFFFF or args.host_created <= 0:
        parser.error("Host PID and creation time must be positive")
    host = None
    with args.log.open("x", encoding="utf-8", buffering=1) as output:
        def emit(action, **fields):
            output.write(json.dumps({"timeUtc": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
                                     "hostPid": args.host_pid, "action": action, **fields}) + "\n")
        try:
            host = HostLease(args.host_pid, args.host_created)
            emit("watcher started", hostCreated=args.host_created, includeScope=args.include_scope)
            watch(host, host.clients, lambda identity: spawn_helpers(identity, args.include_scope, emit, args.include_headlook),
                  emit, ready=client_ready)
        except (OSError, ValueError) as error:
            emit("watcher failed", reason=str(error))
            return 1
        finally:
            if host is not None:
                host.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
