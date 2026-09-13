"""Capture known August car state on the next ordinary playtest, without changing it.

Usage: python watch_vehicle_visibility.py HOST_LOG NEW_OUTPUT_JSONL [SECONDS=14400]
Stops after four hours, 60 snapshots containing a vehicle, that client's exit, or
8 MiB of output. Only transients present in this host's parked-car log are read.
The sibling reader verifies the exact August executable and vehicle class.
"""
import ctypes
from ctypes import wintypes
import datetime
import json
from pathlib import Path
import re
import subprocess
import sys
import time


class ProcessEntry(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
                ('pid', wintypes.DWORD), ('heap', ctypes.c_size_t),
                ('module', wintypes.DWORD), ('threads', wintypes.DWORD),
                ('parent', wintypes.DWORD), ('priority', wintypes.LONG),
                ('flags', wintypes.DWORD), ('exe', wintypes.WCHAR * 260)]


kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]


def clients():
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise OSError(ctypes.get_last_error(), 'CreateToolhelp32Snapshot')
    result = []
    try:
        entry = ProcessEntry()
        entry.dwSize = ctypes.sizeof(entry)
        more = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            if entry.exe.lower() == 'h1z1.exe':
                result.append(entry.pid)
            more = kernel.Process32NextW(snapshot, ctypes.byref(entry))
        return result
    finally:
        kernel.CloseHandle(snapshot)


def known_cars(host_log):
    # Bound every rescan, even if the host has accumulated a very large log.
    with host_log.open('rb') as log:
        log.seek(0, 2)
        log.seek(max(0, log.tell() - 16 * 1024 * 1024))
        tail = log.read().decode('utf-8', errors='replace')
    return sorted({int(value) for value in re.findall(
        r'vehicles: parked [^\r\n]*?transient=(2\d{6})\b', tail)})[:150]


def main():
    host_log, output = map(Path, sys.argv[1:3])
    seconds = int(sys.argv[3]) if len(sys.argv) > 3 else 14400
    if not 1 <= seconds <= 14400:
        raise ValueError('Duration must be 1..14400 seconds')
    if not host_log.is_file():
        raise ValueError('Host log must exist')
    reader = Path(__file__).with_name('read_vehicle_visibility.py')
    if not reader.is_file():
        raise ValueError('Sibling reader is missing')
    deadline = time.monotonic() + seconds
    previous_targets = None
    sampled_pid = None
    samples = 0
    byte_count = 0
    output_limit = 8 * 1024 * 1024
    reason = 'duration'

    with output.open('xb') as handle:
        def record(item):
            nonlocal byte_count
            line = (json.dumps(item) + '\n').encode('utf-8')
            if byte_count + len(line) > output_limit:
                return False
            handle.write(line)
            byte_count += len(line)
            return True

        record({'watch': 'armed', 'hostLog': str(host_log.resolve()),
                'seconds': seconds, 'maximumSamples': 60, 'maximumBytes': output_limit,
                'utc': datetime.datetime.now(datetime.timezone.utc).isoformat()})
        while time.monotonic() < deadline:
            pids = clients()
            if sampled_pid is not None and sampled_pid not in pids:
                reason = 'sampled-client-exited'
                break
            transients = known_cars(host_log)
            targets = (pids, transients)
            if targets != previous_targets:
                event = {'watch': 'targets', 'pids': pids, 'transients': transients}
                print(json.dumps(event), flush=True)
                if not record(event):
                    reason = 'output-limit'
                    break
                previous_targets = targets
            for pid in pids:
                if not transients:
                    continue
                stamp = datetime.datetime.now(datetime.timezone.utc).isoformat()
                found = False
                try:
                    run = subprocess.run(
                        [sys.executable, str(reader), str(pid), *map(str, transients)],
                        capture_output=True, text=True, timeout=8,
                        creationflags=subprocess.CREATE_NO_WINDOW)
                    for line in run.stdout.splitlines():
                        try:
                            item = json.loads(line)
                        except json.JSONDecodeError:
                            continue
                        found |= item.get('found') is True
                        if not record({'utc': stamp, 'pid': pid, **item}):
                            reason = 'output-limit'
                            break
                    if run.returncode:
                        if not record({'utc': stamp, 'pid': pid, 'readError': run.stderr[-1200:]}):
                            reason = 'output-limit'
                except subprocess.TimeoutExpired:
                    if not record({'utc': stamp, 'pid': pid, 'readError': '8-second reader timeout'}):
                        reason = 'output-limit'
                if found:
                    sampled_pid = pid
                    samples += 1
                if samples >= 60 or reason == 'output-limit':
                    break
            handle.flush()
            if samples >= 60:
                reason = 'sample-limit'
                break
            if reason == 'output-limit':
                break
            time.sleep(min(5, max(0, deadline - time.monotonic())))
        record({'watch': 'complete', 'reason': reason, 'samples': samples})
    print(json.dumps({'watch': 'complete', 'reason': reason,
                      'samples': samples, 'output': str(output)}), flush=True)


if __name__ == '__main__':
    main()
