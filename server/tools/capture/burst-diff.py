#!/usr/bin/env python3
"""Dump the s2c zone burst from a wire capture, with opcode labels and lengths.

    python tools/capture/burst-diff.py <capture> [s2c|c2s|both] [firstLine] [lastLine]

One line per gateway message, so two runs' bursts can be diffed against each other - which is
how the G10 zoning burst was cut down to the shape the August client accepts (docs/108, D211).
Written for the G10 lane as out/g10/burst.py on 2026-09-03; kept here because the burst will be
compared again.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from opcodes import OpcodeTable

def rows(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for ln, line in enumerate(fh, 1):
            parts = [p.strip() for p in line.split("|")]
            if len(parts) < 6:
                continue
            yield ln, parts

def main():
    path = sys.argv[1]
    want_dir = sys.argv[2] if len(sys.argv) > 2 else "s2c"
    start = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    end = int(sys.argv[4]) if len(sys.argv) > 4 else 10**9
    table = OpcodeTable()
    for ln, p in rows(path):
        if ln < start or ln > end:
            continue
        t, remote, proto, direction, length, blob = p[0], p[1], p[2], p[3], p[4], p[5]
        if proto != "ExternalGatewayApi_3":
            continue
        if want_dir != "both" and direction != want_dir:
            continue
        if direction not in ("c2s", "s2c"):
            continue
        try:
            data = bytes.fromhex(blob)
        except ValueError:
            continue
        if not data:
            continue
        op = data[0] & 0x1F
        ch = data[0] >> 5
        if op not in (5, 6) or len(data) < 2:
            print(f"{ln:6} {t} {direction:4} gw op={op} ch={ch} len={len(data)}")
            continue
        payload = data[1:]
        r = table.resolve(payload)
        print(f"{ln:6} {t} {direction:4} ch{ch} {r.hex:<8} {len(payload):6} {r.name[:44]:<44} {payload[:24].hex()}")

main()
