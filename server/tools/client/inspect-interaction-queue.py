"""Bounded read-only snapshots of the August client's F-action queue and weapons.

No input is sent and no process memory is written. Only canonical H1Z1.exe is
opened using the existing read-only LiveProcess constructor. Useful while the
owner reproduces a delayed pickup. JSON lines are emitted on state changes.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import struct
import time

from loot_reload_live_process import LiveProcess


def snapshot(process):
    def q(address):
        return struct.unpack("<Q", process.read(address, 8))[0]
    def d(address):
        return struct.unpack("<I", process.read(address, 4))[0]
    base = process.base
    manager = q(base + 0x3f69430)
    inventory = q(base + 0x3f69f60)
    player = q(manager + 0x1948)
    if q(player) != base + 0x31dddc0:
        raise ValueError("Local player is not the reviewed August class")
    result = {"player": hex(player), "queueCount": d(player + 0x38c8),
              "busyCount": d(player + 0x38d0), "busyFlag": process.read(player + 0x54b9, 1)[0],
              "playerFlags": hex(q(player + 0x18d8)), "adsState": d(player + 0x908),
              "equipState": d(player + 0xef8), "queue": [], "weapons": []}
    node = q(player + 0x38b8)
    visited = set()
    while node and node not in visited and len(visited) < 16:
        visited.add(node)
        result["queue"].append({"node": hex(node), "kind": d(node + 0x18),
                                "attempts": d(node + 0x1c), "argument": hex(q(node + 0x28))})
        node = q(node + 0x10)
    # Native 140dc07d0 resolves ACK targets in these inventory GUID buckets.
    count, buckets = d(inventory + 0xbde8), q(inventory + 0xbe08)
    if count > 4096:
        raise ValueError("Inventory bucket count exceeds diagnostic bound")
    visited = set()
    for bucket in range(count):
        node = q(buckets + 8 * bucket)
        depth = 0
        while node and node not in visited and depth < 128:
            visited.add(node)
            depth += 1
            # Reviewed weapon Item virtual+0x40 returns this (141487c50).
            if q(node) == base + 0x3254c98:
                result["weapons"].append({
                    "object": hex(node), "guid": hex(q(node + 0x88)), "definition": d(node + 8),
                    "state": d(node + 0xec), "outstanding": process.read(node + 0x291, 1)[0],
                    "acknowledged": q(node + 0x180), "expected": q(node + 0x188),
                })
            node = q(node + 0x90)
    result["weapons"].sort(key=lambda value: value["guid"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--seconds", type=float, default=30)
    parser.add_argument("--interval", type=float, default=0.05)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not 0 < args.seconds <= 120 or not 0.02 <= args.interval <= 1:
        parser.error("seconds must be in (0,120], interval in [0.02,1]")
    output = args.out.open("x", encoding="utf-8") if args.out else None
    try:
        with LiveProcess(args.pid) as process:
            deadline, previous = time.monotonic() + args.seconds, None
            while True:
                try:
                    state = snapshot(process)
                except (OSError, ValueError, struct.error) as error:
                    state = {"readError": str(error)}
                encoded = json.dumps(state, sort_keys=True)
                if encoded != previous:
                    previous = encoded
                    line = json.dumps({"timeUtc": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
                                       "pid": args.pid, **state})
                    print(line, flush=True)
                    if output:
                        output.write(line + "\n")
                        output.flush()
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                time.sleep(min(args.interval, remaining))
    finally:
        if output:
            output.close()


if __name__ == "__main__":
    main()
