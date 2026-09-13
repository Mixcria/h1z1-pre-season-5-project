"""Keep forced first-person scope entry coherent with aiming during an August jump.

Inspection only by default. This applies two guarded, individually atomic bytes
after the current launch has an initialized world. Original disk bytes never change.
The entire 1,551-byte SecondaryFire handler is checked before and after each byte.
Partial failures roll back to the starting state if the full function still matches.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

from loot_reload_live_process import ScopeProcess

_spec = importlib.util.spec_from_file_location(
    "loot_reload_startup_guards", Path(__file__).with_name("loot-during-reload-live.py")
)
startup = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(startup)

FUNCTION_RVA = 0x158b700
FUNCTION_SIZE = 1551
FUNCTION_FILE_OFFSET = 0x158ad00
# Mask 1 opens held/allowed-controller entry; mask 2 suppresses synthetic release.
SITES = {1: (0x158bae1, 0x33, 0x41), 2: (0x158bb5e, 0x74, 0xeb)}
STATE_HASHES = {
    "c2953412538ef98c84959b78bee1a4870cc7d22008386c3c5755e7b364824053": 0,
    "0800dd7b6844351482171c025a7014a29be9049af359b40151f5bcbad188cc87": 1,
    "4bcec25ad42e9f0f43d4bae686f32274fc07eb2461b4907fbd5f764b97787776": 2,
    "9a1b5e0634454bcb9a5d6350d3b83f224dc5efdc76533ff3ebebd6d839ffef12": 3,
}


def function_state(data):
    if len(data) != FUNCTION_SIZE:
        raise ValueError("SecondaryFire function is not the reviewed 1,551-byte body")
    state = STATE_HASHES.get(hashlib.sha256(data).hexdigest())
    if state is None:
        raise ValueError("Full SecondaryFire function differs from the reviewed August bytes")
    return state


def inspect(process):
    return function_state(process.read(process.base + FUNCTION_RVA, FUNCTION_SIZE))


def transition(process, target):
    current = inspect(process)
    # Close entry before restoring release; suppress synthetic release before opening entry.
    order = (1, 2) if not target & 1 else (2, 1)
    for mask in order:
        if bool(current & mask) == bool(target & mask):
            continue
        if inspect(process) != current:
            raise ValueError("Scope function changed between guarded writes")
        rva, original, patched = SITES[mask]
        expected = patched if current & mask else original
        replacement = patched if target & mask else original
        process.write_byte(process.base + rva, expected, replacement)
        current ^= mask
        if inspect(process) != current:
            raise ValueError("Scope function readback differs after a one-byte write")


def change_verified(process, restore=False):
    with process.patch_lock():
        return _change_locked(process, restore)


def _change_locked(process, restore):
    initial = inspect(process)
    wanted = 0 if restore else 3
    if initial == wanted:
        return False
    try:
        transition(process, wanted)
    except (OSError, ValueError) as failure:
        try:
            # Also covers a failed API that wrote its byte before returning an error.
            transition(process, initial)
        except (OSError, ValueError) as rollback_failure:
            raise OSError(f"Scope change failed: {failure}; rollback also refused or failed: {rollback_failure}") from failure
        raise OSError(f"Scope change failed and was rolled back to state {initial}: {failure}") from failure
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--apply", action="store_true")
    mode.add_argument("--restore", action="store_true")
    parser.add_argument("--wait-seconds", type=float, default=180)
    parser.add_argument("--log", type=Path)
    args = parser.parse_args()
    if args.pid <= 0 or not 3 <= args.wait_seconds <= 600:
        parser.error("PID must be positive; wait-seconds must be in [3, 600]")
    def emit(action, **fields):
        line = json.dumps({"timeUtc": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
                           "pid": args.pid, "action": action, **fields})
        print(line, flush=True)
        if args.log:
            with args.log.open("a", encoding="utf-8") as output:
                output.write(line + "\n")
    process = None
    try:
        process = ScopeProcess(args.pid)
        digest = startup.verify_disk(process.path)
        state = inspect(process)
        emit("verified", diskSha256=digest, liveState=state, base=hex(process.base))
        if args.apply:
            emit("waiting for initialized world", maximumSeconds=args.wait_seconds)
            startup.wait_ready(process, args.wait_seconds, process.path.parent / "Logs" / startup.CLIENT_LOG)
            startup.verify_disk(process.path)
            changed = change_verified(process)
            emit("live scope patch applied" if changed else "live scope patch already present", liveState=inspect(process))
        elif args.restore:
            changed = change_verified(process, restore=True)
            emit("live scope patch restored" if changed else "live scope patch already original", liveState=inspect(process))
        else:
            emit("inspection only")
        return 0
    except (OSError, ValueError, struct.error) as error:
        emit("refused or failed", reason=str(error))
        return 1
    finally:
        if process is not None:
            process.close()


if __name__ == "__main__":
    raise SystemExit(main())
