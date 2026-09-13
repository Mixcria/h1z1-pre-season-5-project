"""Deferred, reversible, immediate F-interaction patch for the August client.

Default is inspection only. --apply waits up to --wait-seconds for this process's
client log to reach cClientRunStateRunning, then requires its native player/camera/
reticle to remain initialized for three seconds. No disk image is modified, no
remote thread is created, and no input is sent. --restore reverses all three reviewed bytes.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import time

from loot_reload_live_process import LiveProcess

_throttle_spec = importlib.util.spec_from_file_location(
    "interaction_throttle_evidence", Path(__file__).with_name("interaction_throttle_evidence.py")
)
throttle = importlib.util.module_from_spec(_throttle_spec)
_throttle_spec.loader.exec_module(throttle)

_spec = importlib.util.spec_from_file_location(
    "loot_reload_disk_evidence", Path(__file__).with_name("install-loot-during-reload.py")
)
evidence = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(evidence)

EXPECTED_PATH = Path("C:/Aug2017/Client/H1Z1.exe")
EXPECTED_SIZE = 72_818_304
EXPECTED_SHA256 = "d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd"
CLIENT_LOG = "H1Z1 KOTK PlayClient (Live).log"
FUNCTION_RVA = evidence.FUNCTION_VA - evidence.IMAGE_BASE
PATCH_RVA = evidence.PATCH_VA - evidence.IMAGE_BASE
# FUN_1411c6d70: after virtual +0x5d0 (outstanding reload response), jump to
# the existing ADS check regardless of its result. This affects F interaction
# only; reload counters, ammunition, firing and weapon switching remain native.
ACK_PATCH_RVA = 0x11C6E7C
ACK_PATCH_INDEX = ACK_PATCH_RVA - FUNCTION_RVA
ACK_BRANCH_TARGET = 0x1411C6E99
assert evidence.ORIGINAL_FUNCTION[ACK_PATCH_INDEX:ACK_PATCH_INDEX + 2] == b"\x75\x1b"
PATCHED_FUNCTION_V2 = (evidence.PATCHED_FUNCTION[:ACK_PATCH_INDEX] + b"\xeb"
                       + evidence.PATCHED_FUNCTION[ACK_PATCH_INDEX + 1:])
FUNCTION_STATES = {
    "original": evidence.ORIGINAL_FUNCTION,
    "patched-v1": evidence.PATCHED_FUNCTION,
    "patched-v2": PATCHED_FUNCTION_V2,
}


def function_state(data):
    for state, expected in FUNCTION_STATES.items():
        if data == expected:
            return state
    raise ValueError("Full 468-byte interaction function differs from reviewed bytes; refusing")


def classify_state(interaction_bytes, throttle_bytes):
    interaction = function_state(interaction_bytes)
    pacing = throttle.function_state(throttle_bytes)
    if pacing == "patched":
        if interaction != "patched-v2":
            raise ValueError("F throttle bypass without both interaction branches; refusing mixed image")
        return "patched-v3"
    return interaction


def inspect_state(process):
    return classify_state(process.read(process.base + FUNCTION_RVA, len(evidence.ORIGINAL_FUNCTION)),
                          process.read(process.base + throttle.FUNCTION_RVA, len(throttle.ORIGINAL_FUNCTION)))


def verify_disk(path):
    if path.resolve() != EXPECTED_PATH.resolve():
        raise ValueError("Target must be exactly C:/Aug2017/Client/H1Z1.exe")
    if path.stat().st_size != EXPECTED_SIZE:
        raise ValueError("Executable size differs from the verified August build")
    with path.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if digest != EXPECTED_SHA256:
        raise ValueError("Executable must be the verified original August disk image")
    return digest


def running_log(text, started_unix):
    """Require the latest run-state transition from THIS launch, not an old log."""
    last = None
    for line in text.splitlines():
        if "TransitionClientRunState:" not in line:
            continue
        fields = line.split("\t")
        try:
            same_launch = len(fields) >= 4 and abs(int(fields[3]) - started_unix) <= 2
        except ValueError:
            same_launch = False
        if same_launch:
            last = line.rsplit("newState=", 1)[-1].strip()
    return last == "cClientRunStateRunning"


def ready_identity(process, log_text):
    if not running_log(log_text, process.started_unix):
        return None
    def u64(location):
        return struct.unpack("<Q", process.read(location, 8))[0]
    base = process.base
    client = u64(base + 0x3f696a0)
    manager = u64(base + 0x3f69430)
    reticle = u64(base + 0x3f6a200)
    if not client or not manager or not reticle:
        return None
    player = u64(manager + 0x1948)
    controller = u64(client + 0x321a0)
    if not player or not controller:
        return None
    if u64(player) != base + 0x31dddc0:
        return None
    if u64(controller) not in (base + 0x315a458, base + 0x315aec0):
        return None
    # The legacy datasource is constructed for the active world's reticle owner.
    datasource = u64(reticle + 0x88)
    if not datasource or u64(datasource) != base + 0x3250158:
        return None
    return client, player, reticle


def read_log(path):
    # Bound each read; run-state lines are short and startup logs are normally < 1 MB.
    with path.open("rb") as source:
        source.seek(0, 2)
        source.seek(max(0, source.tell() - 2 * 1024 * 1024))
        return source.read(2 * 1024 * 1024).decode("utf-8", errors="replace")


def wait_ready(process, wait_seconds, log_path, *, now=time.monotonic, sleep=time.sleep, log_reader=read_log):
    deadline = now() + wait_seconds
    previous = None
    stable_since = None
    while True:
        try:
            identity = ready_identity(process, log_reader(log_path))
        except (OSError, ValueError, struct.error):
            # Confirm the original process handle is still readable, then retry objects in flight.
            process.read(process.base + 0x3f696a0, 8)
            identity = None
        current = now()
        if identity is not None and identity == previous:
            if current - stable_since >= 3:
                return identity
        else:
            previous = identity
            stable_since = current if identity is not None else None
        if current >= deadline:
            raise ValueError("Initialized world was not stable for three seconds within the wait limit; no patch applied")
        sleep(min(0.25, deadline - current))


def change_verified(process, restore=False):
    originals = {FUNCTION_RVA: evidence.ORIGINAL_FUNCTION,
                 throttle.FUNCTION_RVA: throttle.ORIGINAL_FUNCTION}
    before = {rva: process.read(process.base + rva, len(data)) for rva, data in originals.items()}
    # Validate the exact snapshot used as the write/rollback baseline; never
    # accept a second unclassified read after an earlier successful inspection.
    state = classify_state(before[FUNCTION_RVA], before[throttle.FUNCTION_RVA])
    wanted = "original" if restore else "patched-v3"
    if state == wanted:
        return False
    targets = originals if restore else {FUNCTION_RVA: PATCHED_FUNCTION_V2,
                                        throttle.FUNCTION_RVA: throttle.PATCHED_FUNCTION}
    # First disable cancellation, then bypass acknowledgement, then remove the
    # input throttle. Restore in reverse. Every completed intermediate state is
    # original/v1/v2/v3, and unmodified weapon state guards remain active.
    sites = ((FUNCTION_RVA, evidence.PATCH_INDEX), (FUNCTION_RVA, ACK_PATCH_INDEX),
             (throttle.FUNCTION_RVA, throttle.PATCH_INDEX))
    if restore:
        sites = tuple(reversed(sites))
    attempted = []
    expected_functions = {rva: bytearray(data) for rva, data in before.items()}
    def verify_regions(expected, phase):
        for rva, data in expected.items():
            if process.read(process.base + rva, len(data)) != bytes(data):
                raise ValueError(f"Full {len(data)}-byte interaction function differs {phase}")
    try:
        for rva, index in sites:
            if before[rva][index] == targets[rva][index]:
                continue
            verify_regions(expected_functions, "before the next write")
            attempted.append((rva, index))  # A writer can mutate then report cleanup failure.
            process.write_byte(process.base + rva + index, before[rva][index], targets[rva][index])
            expected_functions[rva][index] = targets[rva][index]
            verify_regions(expected_functions, "after a reviewed write")
        if inspect_state(process) != wanted:
            raise ValueError("Full function readback did not match the intended interaction version")
    except (OSError, ValueError) as error:
        rollback_errors = []
        for rva, index in reversed(attempted):
            try:
                location = process.base + rva + index
                current = process.read(location, 1)[0]
                if current == targets[rva][index]:
                    process.write_byte(location, current, before[rva][index])
                elif current != before[rva][index]:
                    raise ValueError(f"Reviewed opcode at index {index} changed externally; refusing rollback")
            except (OSError, ValueError) as rollback_error:
                rollback_errors.append(str(rollback_error))
        try:
            verify_regions(before, "from pre-operation bytes after rollback")
        except (OSError, ValueError) as rollback_error:
            rollback_errors.append(str(rollback_error))
        if rollback_errors:
            raise ValueError(f"{error}; rollback incomplete: {'; '.join(rollback_errors)}") from error
        raise
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--apply", action="store_true")
    mode.add_argument("--restore", action="store_true")
    parser.add_argument("--wait-seconds", type=float, default=180)
    parser.add_argument("--log", type=Path, help="Append-only helper status log")
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
        process = LiveProcess(args.pid)
        digest = verify_disk(process.path)
        state = inspect_state(process)
        emit("verified", diskSha256=digest, liveFunction=state, base=hex(process.base))
        if args.apply:
            emit("waiting for initialized world", maximumSeconds=args.wait_seconds)
            wait_ready(process, args.wait_seconds, process.path.parent / "Logs" / CLIENT_LOG)
            # Recheck both the original disk image and all function bytes after waiting.
            verify_disk(process.path)
            changed = change_verified(process)
            emit("live patch applied" if changed else "live patch already present", version=3,
                 byteAddresses=[hex(process.base + PATCH_RVA), hex(process.base + ACK_PATCH_RVA),
                                hex(process.base + throttle.PATCH_RVA)])
        elif args.restore:
            changed = change_verified(process, restore=True)
            emit("live patch restored" if changed else "live patch already original",
                 byteAddresses=[hex(process.base + PATCH_RVA), hex(process.base + ACK_PATCH_RVA),
                                hex(process.base + throttle.PATCH_RVA)])
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
