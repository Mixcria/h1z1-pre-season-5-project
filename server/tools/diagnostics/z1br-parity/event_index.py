"""Sanitized anchor index and exact private-field joins; no absolute AV calibration."""
import collections
import csv
import datetime
import json
import pathlib
import struct
import sys

sys.dont_write_bytecode = True
from capture_source import load_capture, REFERENCE, REPORTS, sha
from events_1315 import weapon_leaves, fire, dto, bounce, appearance

ANCHORS = (("skin-entry", 0, 58.5, 62.017), ("wall-break", 1, 71.5, 74.25),
           ("green-crate", 2, 20., 45.516), ("throw-first", 2, 45.5, 49.25),
           ("throw-second", 2, 49.5, 54.016), ("later-pickups", 3, 25., 45.))


def main():
    au, report, decoded = load_capture()
    decoded.sort(key=lambda r: r[1])
    video = json.loads((REFERENCE / "video-session.json").read_text())
    base = datetime.datetime.fromisoformat(video["segments"][0]["invokedUtc"]).timestamp()
    segments = list(csv.reader((REFERENCE / "video-001.segments.csv").open()))
    historical = json.loads((REFERENCE / "review/weapon-events-sanitized.json").read_text())
    oldnames = {(r["frame"], r.get("direction", "c2s"), r["sub"]): r["weapon"]
                for r in historical["shots"] + historical["triggerAndReloadCandidateEvents"]}
    leaves = []
    private_aliases = {}
    for stream, frame, time, direction, data, reliable in decoded:
        if len(data) <= 7 or data[0] & 31 not in (5, 6) or data[0] >> 5 == 2 or data[1] != 0x72: continue
        for offset, game_time, sub, body in weapon_leaves(data[1:]):
            if (frame, direction, sub) in oldnames and len(body) >= 8:
                private_aliases[stream, body[:8]] = oldnames[frame, direction, sub]
            leaves.append((stream, frame, time, direction, offset, game_time, sub, body))
    events = []
    shots = []
    bounces = []
    for stream, frame, time, direction, offset, game_time, sub, body in leaves:
        if sub not in (1, 3, 7, 8, 9, 32, 38, 43): continue
        row = {"frame": frame, "stream": stream, "utc": au.utc(time), "direction": direction,
               "family": "0x72", "sub": sub, "gatewayHeaderOffset": offset,
               "gatewayBodyOffset": offset + 6, "bodyBytes": len(body),
               "embeddedTime": game_time, "_time": time}
        if sub in (3, 32):
            parsed = fire(body, sub == 32)
            row.update(weapon=private_aliases.get((stream, parsed["identity"]), "unjoined"),
                       projectileCount=len(parsed["projectiles"]))
            if sub == 3 and direction == "c2s": shots.append((row, parsed))
        elif sub == 38:
            parsed = bounce(body); bounces.append((row, parsed))
            row["effectCandidate"] = parsed["effectCandidate"]
        else:
            row["weapon"] = private_aliases.get((stream, body[:8]), "unjoined")
            if sub == 8 and len(body) == 28:
                row["magazineShapeValues"] = list(struct.unpack_from("<IIIQ", body, 8))
        events.append(row)
    for row, parsed in bounces:
        matched = [s for s, p in shots if s["stream"] == row["stream"] and parsed["projectileId"] in p["projectiles"]]
        row["matchedFireFrames"] = [s["frame"] for s in matched]
        row["captureAfterFireMs"] = [round((row["_time"] - s["_time"]) * 1000, 4) for s in matched]
        row["embeddedAfterFireMs"] = [(row["embeddedTime"] - s["embeddedTime"]) & 0xffffffff for s in matched]
    dtos = []
    paints = []
    object_aliases = {}
    for stream, frame, time, direction, data, reliable in decoded:
        if data[:2] in (b"\x06\xa9", b"\x05\xa9") and int.from_bytes(data[2:4], "little") in (1, 2):
            parsed = dto(data, direction)
            row = {"frame": frame, "direction": direction, "utc": au.utc(time), "family": "0xa9",
                   "sub": parsed["sub"], "bytes": len(data), "model": parsed["model"],
                   "object": object_aliases.setdefault(parsed["identity"], f"object-{len(object_aliases)+1}"),
                   "objectByteRange": [4, 12], "stringLengthOffset": 12, "tailOffset": parsed["tailOffset"]}
            if direction == "c2s":
                row["matchedFireFrames"] = [s["frame"] for s, p in shots if s["stream"] == stream and parsed["projectileId"] in p["projectiles"]]
            else:
                row.update({k: parsed[k] for k in ("effectId", "scalar", "byte0", "word", "flags")})
            dtos.append(row)
        if data[:3] == b"\x05\xde\x01":
            parsed = appearance(data)
            matches = [(f, b) for s, f, t, dr, b, re in decoded if s == stream and dr == "s2c"
                       and b[:2] == b"\x05\xc4" and b[2:10] == parsed["vehicleIdentity"]]
            model_joins = []
            for f, b in matches:
                join = {"frame": f, "identityOffset": 2, "modelOffsetCandidate": 19, "familyOffsetCandidate": 119}
                try:
                    record = au.SOE.vehicle_record(b, "unclassified", f)
                    join.update(prefixValidated=True, modelId=record["modelId"], vehicleFamily=record["vehicleId"])
                except ValueError as exc:
                    join.update(prefixValidated=False, reason=str(exc))
                model_joins.append(join)
            paints.append({"frame": frame, "utc": au.utc(time), "direction": direction, "family": "0xde/01",
                           "bytes": len(data), "shaderOffset": 19, "shaderCandidate": parsed["shaderCandidate"],
                           "lightweightPrefixJoins": model_joins,
                           "confidence": "exact appearance shape and private identity join; C4 prefix only, not full actor schema"})
    anchors = []
    for label, segment, low, high in ANCHORS:
        filename, origin, end = segments[segment]
        origin = float(origin)
        lo, hi = base + origin + low, base + origin + high
        counts = collections.defaultdict(list)
        for stream, frame, time, direction, data, reliable in decoded:
            if stream != 11 or not lo - 2 <= time <= hi + 2 or not data: continue
            family = data[:2].hex() if data[0] & 31 in (5, 6) and data[0] >> 5 != 2 else data[:1].hex()
            counts[direction, family, len(data), reliable].append(frame)
        selected = []
        for event in events:
            if lo - 2 <= event["_time"] <= hi + 2:
                selected.append({k: v for k, v in event.items() if not k.startswith("_")} |
                                {"nominalLocalSeconds": round(event["_time"] - base - origin, 6)})
        anchors.append({"label": label, "file": filename, "segmentCsvOrigin": origin,
                        "localBoundsSeconds": [low, high], "encoderBoundsSeconds": [origin + low, origin + high],
                        "nominalUtcBounds": [au.utc(lo), au.utc(hi)], "searchPaddingSeconds": 2,
                        "weaponCandidates": selected,
                        "packetMetadataGroups": [{"direction": dr, "headerPrefix": f, "bytes": n,
                                                   "reliable": re, "count": len(fs), "frames": fs}
                                                  for (dr, f, n, re), fs in counts.items()]})
    result = {"captureSha256": report["captureSha256"], "sourceProtocol": 1315, "targetProtocol": 1148,
              "timing": {"invocationUtc": au.utc(base), "source": "video-session.json plus video-001.segments.csv",
                         "uncertainty": "No calibrated absolute acquisition offset or drift bound. The +/-2s search padding is a chosen navigation window, not a confidence interval or input latency bound.",
                         "audio": "Late WAV callbacks begin06:36:21.584; first pickup and early indexed wall/throws predate audio.",
                         "messageTimesUsedAsActionTimes": False},
              "anchors": anchors, "dtoEvents": dtos, "appearanceCandidates": paints,
              "limits": ["DTO body/model/projectile/object joins are exact; initial health and physical traversal remain separate.",
                         "0x72/26 is bounce-shaped and matches Fire projectile ids; it has no position field and does not prove settled contact or plume activation.",
                         "Inventory schemas and automatic key state are not decoded; later HUD magazine transition and reload-shaped exchange do not classify the missed initial sound."]}
    (REPORTS / "event-index.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"anchors": len(anchors), "dtoEvents": len(dtos), "appearanceCandidates": len(paints),
                      "weaponEventsIndexed": len(events), "bounceEvents": len(bounces)}))


if __name__ == "__main__": main()
