"""Executable sanitized evidence audit; output contains no generic player payloads."""
from __future__ import annotations
import argparse
import collections
import datetime
import json
import pathlib
import struct
import sys

sys.dont_write_bytecode = True
from bounded import Cursor, DecodeError
from capture_source import load_capture, REPORTS, REFERENCE, sha
from movement_1315 import parse as movement, FIELDS
from weapon_1315 import parse as weapons


def utc(t):
    return datetime.datetime.fromtimestamp(t, datetime.timezone.utc).isoformat()


def definitions(decoded, lz4):
    for stream, frame, time, direction, data, reliable in decoded:
        if direction != "s2c" or data[:2] != b"\x05\x15":
            continue
        c = Cursor(data, 2)
        flags = c.u16()
        if flags & 0x8000 or flags & 0x1fff > 128:
            raise DecodeError("unexpected definition name encoding")
        name = c.take(flags & 0x1fff).decode("ascii")
        if flags & 0x2000 and c.u8() != 0:
            raise DecodeError("name NUL")
        if flags & 0x4000: c.u32()
        unpacked, packed = c.u32(), c.u32()
        if unpacked > 32 * 1024 * 1024 or packed > 32 * 1024 * 1024:
            raise DecodeError("definition size limit")
        payload_offset = c.pos
        payload = c.take(packed)
        c.finish()
        body = payload if unpacked == packed else lz4(payload, unpacked)
        if len(body) != unpacked: raise DecodeError("inflated size mismatch")
        yield {"stream": stream, "frame": frame, "utc": utc(time), "direction": direction,
               "name": name, "gatewayBodyOffset": payload_offset, "inflatedBytes": len(body),
               "sha256": sha(body)}, body


def movement_audit(decoded, stats):
    grouped = collections.defaultdict(list)
    aliases = {}
    flags = collections.Counter()
    fields = collections.defaultdict(list)
    failure = []
    for stream, frame, time, direction, data, reliable in sorted(decoded, key=lambda r: r[1]):
        if data[:1] not in (b"\x45", b"\x46"):
            continue
        try:
            r = movement(data, direction)
        except DecodeError as exc:
            failure.append({"frame": frame, "error": str(exc)})
            continue
        key = (stream, direction, r["identity"])
        alias = aliases.setdefault(key, f"entity-{len(aliases) + 1}")
        grouped[key].append((frame, time, reliable, r))
        if 1 in r["fields"]: flags[r["fields"][1]["values"][0]] += 1
        for bit, field in r["fields"].items():
            fields[bit].append((frame, field["offset"], field["bytes"]))
    summary = []
    for key, rows in grouped.items():
        deltas = [((b[3]["timestamp"] - a[3]["timestamp"] + 2**31) % 2**32) - 2**31
                  for a, b in zip(rows, rows[1:])]
        summary.append({"stream": key[0], "direction": key[1], "anonymousEntity": aliases[key],
                        "records": len(rows), "reliable": sum(r[2] for r in rows),
                        "firstFrame": rows[0][0], "lastFrame": rows[-1][0],
                        "firstUtc": utc(rows[0][1]), "lastUtc": utc(rows[-1][1]),
                        "timestampDelta": stats(deltas), "backwardTimestampCount": sum(d < 0 for d in deltas),
                        "equalTimestampCount": sum(d == 0 for d in deltas),
                        "captureArrivalDeltaMs": stats([(b[1] - a[1]) * 1000 for a, b in zip(rows, rows[1:])]),
                        "versionCounts": dict(collections.Counter(str(r[3]["version"]) for r in rows)),
                        "maskCounts": dict(collections.Counter(hex(r[3]["mask"]) for r in rows))})
    return {"sourceProtocol": 1315, "targetProtocol": 1148, "failures": failure,
            "records": sum(len(r) for r in grouped.values()), "groups": summary,
            "fields": [{"mask": hex(bit), "primitive": kind, "components": count,
                        "observations": len(fields[bit]),
                        "firstFrameAndByteOffsetAndWidth": fields[bit][0] if fields[bit] else None}
                       for bit, kind, count in FIELDS],
            "stateFlagValues": {hex(k): v for k, v in flags.items()},
            "limits": ["Arrival intervals are not simulation tick, one-way latency or physical input latency.",
                       "Identity is eight fixed bytes in 1315 s2c, not a decoded August transient id.",
                       "Field order/width is structural; body/look ownership, half-angle units and state-bit meanings need native registration/controlled input evidence.",
                       "Bits1000/4000 occur only together in full masks; their 11/1 split is an inferred grouping of the observed 15-component tail, with independently observed bit2000=3."]}


def weapon_audit(current, prior, provenance):
    cur, prev = weapons(current), weapons(prior)
    if cur["spans"] != prev["spans"]: raise DecodeError("prior/current structural span difference")
    result = {"sourceProtocol": 1315, "targetProtocol": 1148,
              "provenance": provenance, "currentSha256": sha(current), "priorSha256": sha(prior),
              "consumedBytes": cur["consumed"], "lists": cur["spans"], "joins": cur["joins"],
              "changedByteCount": sum(a != b for a, b in zip(current, prior)),
              "modeArrayCountPairs": dict(collections.Counter(f"{r['array0']['count']}/{r['array1']['count']}" for r in cur["lists"][2])),
              "commonWeapons": [], "changedModes": []}
    groups = {r["id"]: r for r in cur["lists"][1]}
    modes = {r["id"]: r for r in cur["lists"][2]}
    for weapon in cur["lists"][0]:
        if weapon["id"] not in (6, 1405, 1384, 1385): continue
        row = {"weaponDefinitionId": weapon["id"], "bodyOffset": weapon["offset"],
               "recordBytes": weapon["bytes"], "groups": []}
        for gid in weapon["groups"]:
            group = {"id": gid, "bodyOffset": groups[gid]["offset"], "modes": []}
            for mid in groups[gid]["modes"]:
                mode = modes[mid]; at = mode["offset"]; end = at + mode["bytes"]
                group["modes"].append({"id": mid, "bodyOffset": at, "bytes": mode["bytes"],
                                       "sha256": sha(current[at:end]), "sameAsPrior": current[at:end] == prior[at:end],
                                       "flagsAtRecord8": list(current[at+8:at+12]), "typeCandidateAtRecord12": current[at+12],
                                       "arrayCounts": [mode["array0"]["count"], mode["array1"]["count"]]})
            row["groups"].append(group)
        result["commonWeapons"].append(row)
    for r in cur["lists"][2]:
        at, end = r["offset"], r["offset"] + r["bytes"]
        changed = [i for i in range(at, end) if current[i] != prior[i]]
        if changed: result["changedModes"].append({"id": r["id"], "offset": at, "changedOffsets": changed})
    result["limits"] = ["Full structural consumption does not name opaque prefix/middle/suffix fields in list2.",
                         "Four AR and AK modes are linked; the two-mode August join cannot silently discard alternate modes.",
                         "Current official and prior ROTK are separate sources. Final August values require the weapons owner's post-overlay export.",
                         "No entire 1315 table or unknown field is authorized for 1148 replay."]
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=pathlib.Path, default=REPORTS)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    audit, transport, decoded = load_capture()
    blobs = [(p, b) for p, b in definitions(decoded, audit.TABLE.lz4_block_decompress) if p["name"] == "WeaponDefinitions"]
    if len(blobs) != 2 or blobs[0][1] != blobs[1][1]: raise DecodeError("repeated weapon payload disagreement")
    prior = pathlib.Path("C:/Aug2017/out/rotk-shooting-20260911/reference/weapon-table-decompressed.bin").read_bytes()
    w = weapon_audit(blobs[0][1], prior, [p for p, b in blobs])
    m = movement_audit(decoded, audit.stats)
    m["captureSha256"] = transport["captureSha256"]
    m["additionalRawCrcChecks"] = len(transport["additionalRawMovementCrcFrames"])
    for filename, value in (("weapon-structure.json", w), ("movement-contract.json", m)):
        (args.out / filename).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    if m["failures"]: raise DecodeError("movement failures")
    print(json.dumps({"weaponsExactBytes": w["consumedBytes"], "listCounts": [r["count"] for r in w["lists"]],
                      "movementExactRecords": m["records"], "additionalRawCrcChecks": m["additionalRawCrcChecks"]}))


if __name__ == "__main__": main()
