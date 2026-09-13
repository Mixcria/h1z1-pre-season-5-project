#!/usr/bin/env python3
r"""
capture-census.py - census the real August client's own zone packets out of the wire captures.

Why this exists: the opcode map proves an id survived; it says nothing about the BODY. A capture
is the strongest body evidence available, and it is free - a real ClientProtocol_1148 client's
own bytes sitting on disk.

Evidence grading, and it matters:
  c2s  the AUGUST CLIENT wrote these bytes. Real evidence of the 1148 layout.
  s2c  CRANBERRY wrote these bytes. Evidence of what we send, NOT that the client liked it.
So c2s lengths pin a client-authored layout; s2c lengths only pin our own writer.

Usage: python tools/bridge/capture-census.py [capture.txt ...] [--json out.json]
"""
import json
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "capture"))
from opcodes import OpcodeTable  # noqa: E402

GATEWAY_TUNNEL = {5, 6}  # TunnelToClient / TunnelFromClient
UNDECODABLE_C2S_CHANNEL = 2  # MISNOMER since 2026-09-02: channel 2 IS decodable (it is the
#                              bit-packed movement record); it is skipped here only because it
#                              carries no opcode byte to census. See zone_payloads.


def zone_payloads(path):
    """Yield (direction, zone_payload_bytes) for gateway-tunnelled zone packets."""
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            parts = [p.strip() for p in line.split("|")]
            if len(parts) < 6 or parts[2] != "ExternalGatewayApi_3":
                continue
            direction = parts[3]
            if direction not in ("c2s", "s2c"):
                continue
            blob = parts[5].strip()
            if not blob or any(c not in "0123456789ABCDEFabcdef" for c in blob):
                continue
            try:
                data = bytes.fromhex(blob)
            except ValueError:
                continue
            if not data:
                continue
            # Gateway header byte packs a five-bit opcode below a three-bit channel
            # (0x06 / 0x26 / 0x46 / 0x66 are all TunnelFromClient on channels 0-3). The zone
            # payload follows IMMEDIATELY - there is no length prefix inside the tunnel.
            op = data[0] & 0x1F
            channel = data[0] >> 5
            if op not in GATEWAY_TUNNEL or len(data) < 2:
                continue
            # CORRECTION 2026-09-02: channel 2 c2s is NOT undecodable, and it is not encrypted.
            # This comment used to say it was high-entropy and "decodes to nonsense" at offsets
            # 0/2/4. It is the bit-packed ClientMovementUpdate record - `u16 mask; u32 clientTime;
            # u8 state;` then packed varints in FUN_140a3ca40's read order - which is exactly what
            # looks like entropy to a byte census that assumes a flat opcode prefix. Cranberry's own
            # HandlePlayerMovement (ZoneService.cs) parses every packet of it with malformed=0, and
            # the S5a lane decoded 13,067 of 13,067 channel-2 records across three captures with
            # zero errors (out\overhaul-20260901\S5a-freeze-wire-forensics.md section 1).
            # It is still skipped HERE, deliberately: this tool censuses base/sub OPCODE ids, and a
            # channel-2 record has no opcode byte to census - it would only add noise. Read it with
            # MovementPackets.cs instead. It carries ~82% of client traffic, so "absent from every
            # capture" remains a claim about the opcode-carrying channels 0, 1 and 3 only.
            if direction == "c2s" and channel == UNDECODABLE_C2S_CHANNEL:
                continue
            yield direction, data[1:]


def main():
    argv = sys.argv[1:]
    out_json = None
    if "--json" in argv:
        i = argv.index("--json")
        out_json = argv[i + 1]
        del argv[i:i + 2]
    args = [a for a in argv if not a.startswith("--")]
    paths = [Path(a) for a in args] or sorted(Path(r"C:\Aug2017\captures").glob("wire-*.txt"))

    table = OpcodeTable()
    stats = defaultdict(lambda: {"c2s": 0, "s2c": 0, "lens": defaultdict(int)})
    for p in paths:
        for direction, payload in zone_payloads(p):
            op = table.resolve(payload)
            key = (op.hex, op.name)
            stats[key][direction] += 1
            if direction == "c2s":
                stats[key]["lens"][len(payload)] += 1

    rows = []
    for (hexid, name), s in sorted(stats.items(), key=lambda kv: -(kv[1]["c2s"] + kv[1]["s2c"])):
        lens = sorted(s["lens"].items(), key=lambda kv: -kv[1])[:4]
        rows.append({
            "opcode": hexid, "name": name, "c2s": s["c2s"], "s2c": s["s2c"],
            "clientLengths": [{"len": l, "n": n} for l, n in lens],
        })

    print(f"{'opcode':<10} {'c2s':>7} {'s2c':>7}  name / client body lengths")
    for r in rows:
        lens = " ".join(f"{d['len']}x{d['n']}" for d in r["clientLengths"])
        print(f"{r['opcode']:<10} {r['c2s']:>7} {r['s2c']:>7}  {r['name']}   {lens}")
    print(f"\n{len(rows)} distinct opcodes over {len(paths)} capture(s)")
    if out_json:
        Path(out_json).write_text(json.dumps(rows, indent=1), encoding="utf-8")
        print(f"wrote {out_json}")


if __name__ == "__main__":
    main()
