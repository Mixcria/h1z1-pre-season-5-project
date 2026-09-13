"""Inspect/restore the deferred August head-look animation experiment.

The owner found this did not stop body/gun turning. Automatic activation is removed;
--apply is retained only for explicit future investigation. Inspection is the default.
Disk bytes never change.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

from loot_reload_live_process import CameraLookProcess

spec = importlib.util.spec_from_file_location('camera_look_startup', Path(__file__).with_name('loot-during-reload-live.py'))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)
FUNCTION_RVA, FUNCTION_SIZE, PATCH_INDEX = 0xC69200, 0x660, 0x5F6
ORIGINAL_HASH = 'd87893d8eb0e129e625a4c826a5714f89d5f387c1b137aac122204d917124dc0'


def function_state(data):
    if len(data) != FUNCTION_SIZE or data[PATCH_INDEX] not in (0x8e, 0x7a):
        raise ValueError('Unrecognized head-look function')
    normalized = bytearray(data)
    normalized[PATCH_INDEX] = 0x8e
    if hashlib.sha256(normalized).hexdigest() != ORIGINAL_HASH:
        raise ValueError('Full August head-look function differs')
    return data[PATCH_INDEX] == 0x7a


def inspect(process):
    return function_state(process.read(process.base + FUNCTION_RVA, FUNCTION_SIZE))


def change_verified(process, restore=False):
    initial = inspect(process)
    wanted = not restore
    if initial == wanted:
        return False
    if process.read(process.base + 0x30EF074, 4) != bytes(4):
        raise ValueError('The reviewed zero animation constant differs')
    if process.read(process.base + 0x30EF088, 4) != struct.pack('<f', 1):
        raise ValueError('The reviewed unit animation constant differs')
    if inspect(process) != initial:
        raise ValueError('Head-look function changed before write')
    address = process.base + FUNCTION_RVA + PATCH_INDEX
    old, new = (0x7a, 0x8e) if restore else (0x8e, 0x7a)
    try:
        process.write_byte(address, old, new)
        if inspect(process) != wanted:
            raise ValueError('Head-look function readback differs')
    except (OSError, ValueError) as error:
        if inspect(process) == wanted:
            process.write_byte(address, new, old)
            if inspect(process) != initial:
                raise ValueError('Head-look rollback failed verification') from error
        raise
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', type=int, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply', action='store_true')
    mode.add_argument('--restore', action='store_true')
    parser.add_argument('--wait-seconds', type=float, default=180)
    parser.add_argument('--log', type=Path)
    args = parser.parse_args()
    if args.pid <= 0 or not 3 <= args.wait_seconds <= 600:
        parser.error('Expected a positive PID and wait-seconds in [3, 600]')
    def emit(action, **fields):
        line = json.dumps(dict(timeUtc=datetime.now(timezone.utc).isoformat(), pid=args.pid, action=action, **fields))
        print(line, flush=True)
        if args.log:
            with args.log.open('a', encoding='utf-8') as output:
                output.write(line + '\n')
    try:
        with CameraLookProcess(args.pid) as process:
            startup.verify_disk(process.path)
            if args.apply:
                startup.wait_ready(process, args.wait_seconds, process.path.parent / 'Logs' / startup.CLIENT_LOG)
                startup.verify_disk(process.path)
                with process.patch_lock():
                    change_verified(process)
                emit('camera-only look applied', patched=inspect(process))
            elif args.restore:
                with process.patch_lock():
                    change_verified(process, restore=True)
                emit('camera-only look restored', patched=inspect(process))
            else:
                emit('inspection only', patched=inspect(process))
        return 0
    except (OSError, ValueError, struct.error) as error:
        emit('refused or failed', reason=str(error))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
