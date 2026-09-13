#!/usr/bin/env python3
r"""
gen-opcode-map.py - join the Z1 client's packet-id registrations (ClientProtocol_1087) against
the August client's (ClientProtocol_1148) to produce the opcode translation map that lets Z1
server code be re-expressed against the August build.

Inputs
  C:\Project\out\registrations.json        1087 rows: baseOpcode / subOpcode / member / baseName
  C:\Aug2017\out\registrations-1148.json   1148 rows: levels[] / member / family

Join key
  The member name. `baseName` is null on most 1087 rows so it cannot be part of the key; the
  handful of members a registrar registers twice under different bases (e.g.
  cCommandPacketIdShowDialog under CommandBase 9/1 and AdminBase 10/1) are paired positionally
  after sorting each build's duplicates by opcode - safe because the renumbering is monotonic.

Arity normalisation
  1087 encodes a base/leaf packet as (base, sub=0) - 237 such rows; 1148 encodes the same thing
  as a single-element levels[] - 244 such rows. sub==0 is therefore collapsed to [base] so the
  two builds are compared like for like.

Verdicts
  identical   both pairs equal
  sub-shift   base equal, sub moved
  base-shift  sub equal, base moved (the dominant case: base delta is -1 for every single one)
  both-shift  both moved
  reshaped    arity changed (a family became a leaf, or a leaf gained a level)
  deleted     present in 1087, absent from 1148  <- the only hard losses
  new         present in 1148 only

Usage: python tools/bridge/gen-opcode-map.py [out.json]
"""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

SRC_1087 = Path(r"C:\Project\out\registrations.json")
SRC_1148 = Path(r"C:\Aug2017\out\registrations-1148.json")
DST = Path(sys.argv[1] if len(sys.argv) > 1
           else r"C:\Aug2017\out\wave9-bridge\opcode-map-1087-to-1148.json")


def load_1087():
    """member -> [levels, ...] sorted by opcode. sub==0 collapses to a single-level base."""
    out = defaultdict(list)
    for r in json.load(open(SRC_1087, encoding="utf-8")):
        base, sub = r["baseOpcode"], r["subOpcode"]
        out[r["member"]].append([base] if sub == 0 else [base, sub])
    for v in out.values():
        v.sort()
    return out


def load_1148():
    out = defaultdict(list)
    for r in json.load(open(SRC_1148, encoding="utf-8")):
        out[r["member"]].append(list(r["levels"]))
    for v in out.values():
        v.sort()
    return out


def family_of(member, levels, fam_by_base):
    return fam_by_base.get(levels[0]) if len(levels) > 1 else None


def verdict(a, b):
    if a is None:
        return "new"
    if b is None:
        return "deleted"
    if a == b:
        return "identical"
    if len(a) != len(b):
        return "reshaped"
    if len(a) == 1:
        return "base-shift"
    if a[0] == b[0]:
        return "sub-shift"
    if a[1] == b[1]:
        return "base-shift"
    return "both-shift"


def hexpair(levels):
    return " ".join(f"0x{x:02x}" for x in levels) if levels else None


def main():
    z1, aug = load_1087(), load_1148()

    # base opcode -> family name, from each build's own single-level registrations
    fam1148 = {r["levels"][0]: r["member"]
               for r in json.load(open(SRC_1148, encoding="utf-8")) if len(r["levels"]) == 1}

    rows = []
    for member in sorted(set(z1) | set(aug)):
        za, ab = z1.get(member, []), aug.get(member, [])
        # pair positionally; the renumbering is monotonic so sorted order is preserved
        for i in range(max(len(za), len(ab))):
            a = za[i] if i < len(za) else None
            b = ab[i] if i < len(ab) else None
            v = verdict(a, b)
            row = {
                "member": member,
                "family": family_of(member, b, fam1148) if b else None,
                "verdict": v,
                "z1_1087": a,
                "aug_1148": b,
                "z1_hex": hexpair(a),
                "aug_hex": hexpair(b),
            }
            if a and b and len(a) == len(b):
                row["base_delta"] = b[0] - a[0]
                if len(a) > 1:
                    row["sub_delta"] = b[1] - a[1]
            rows.append(row)

    counts = Counter(r["verdict"] for r in rows)
    base_deltas = Counter(r["base_delta"] for r in rows
                          if r["verdict"] == "base-shift" and "base_delta" in r)
    sub_deltas = Counter(r["sub_delta"] for r in rows
                         if r["verdict"] == "sub-shift" and "sub_delta" in r)
    deleted = sorted(r["member"] for r in rows if r["verdict"] == "deleted")
    doc = {
        "generatedBy": "tools/bridge/gen-opcode-map.py",
        "source1087": str(SRC_1087),
        "source1148": str(SRC_1148),
        "summary": {
            "z1Members": len(z1),
            "augMembers": len(aug),
            "z1Registrations": sum(len(v) for v in z1.values()),
            "augRegistrations": sum(len(v) for v in aug.values()),
            "survivingRegistrations": sum(1 for r in rows
                                          if r["verdict"] not in ("deleted", "new")),
            "verdicts": dict(sorted(counts.items())),
            "baseShiftHistogram": dict(sorted(base_deltas.items())),
            "subShiftHistogram": dict(sorted(sub_deltas.items())),
            "deletedMembers": deleted,
        },
        "rows": rows,
    }
    DST.parent.mkdir(parents=True, exist_ok=True)
    DST.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    print(f"wrote {DST}  ({len(rows)} rows)")
    for k, v in sorted(counts.items()):
        print(f"  {k:11s} {v}")
    print(f"  base-shift deltas: {dict(sorted(base_deltas.items()))}")
    print(f"  sub-shift deltas:  {dict(sorted(sub_deltas.items()))}")
    print(f"  deleted ({len(deleted)}): {deleted}")


if __name__ == "__main__":
    main()
