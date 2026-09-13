#!/usr/bin/env python3
r"""
z1-diff.py - diff the owner's WORKING Z1 server's wire against Cranberry's, per packet.

WHY THIS EXISTS
    docs/84 proved an id survives 1087 -> 1148. It says nothing about whether we SEND it. A tap
    of the owner's OWN Z1 server does the things Cranberry cannot yet do - it puts a gun in a
    hand, it shoots, it opens doors - and every one of those behaviours is visible as packets.
    This tool reads such a tap, translates every packet id through the generated 1087 -> 1148
    map, reads Cranberry's own capture, and prints the two side by side.

PROVENANCE - 2026-09-02, wave 11 lane 0A. READ THIS BEFORE RUNNING IT.
    This tool used to default to C:\Project\out\refsessions. Those files are tap logs of a
    third-party emulator (h1z1-server V0.49.1 - the banner is on line 1 of every
    zone-console.log), which docs/00 lists as a FORBIDDEN source. --z1 is therefore mandatory
    and any path containing "refsessions" is refused, here and in every future run.

    A legal input is a tap this project made itself, with its own tools, of a server whose
    source the owner owns (docs/00 rule 4 + D53). If you have no such tap on disk, this tool
    has no input and that is the correct outcome, not an inconvenience.

EVIDENCE GRADING - read this before quoting a number
    Z1 s2c        the owner's OWN Z1 SERVER wrote these. Evidence of what his server sends, and
                  adoptable as his design under D53. NOT evidence about the August client,
                  which never saw them, and never a substitute for a Ghidra derivation.
    Z1 c2s        the 1087 CLIENT wrote these. A different build from ours; a hint about what a
                  client expects to answer, never a 1148 layout proof.
    Cran s2c      CRANBERRY wrote these. What we send. Not proof the August client liked it.
    Cran c2s      the AUGUST CLIENT wrote these - the only genuine 1148 evidence in the table,
                  and channel 2 (~82% of it) is undecodable, so a zero here is weak.

    So the useful reading is: Z1 s2c vs Cran s2c. Both columns are server intent, on comparable
    footing, and their difference is exactly "what a working server does that we do not".

INPUTS
    --z1        REQUIRED. One or more Z1 tap logs, or directories holding packets.log files.
                There is no default: see PROVENANCE. Lines look like
                    <epoch> <hh:mm:ss.mmm> +<delta> ZONE s2c sess=.. ch=0 len=.. op=9502
                    ref=Equipment.SetCharacterEquipmentSlot hex=..
    --cran      one or more Cranberry captures (default: every C:\Aug2017\captures\wire-*.txt)
    --map       the generated opcode map (default: C:\Aug2017\out\wave9-bridge\
                opcode-map-1087-to-1148.json)

MODES
    (default)   the side-by-side census, "Z1 only" rows first
    --around REF [--window N]
                print Z1's ordered s2c burst around every occurrence of a named packet. This is
                how you read a SEQUENCE rather than a count - e.g. what Z1 sends around
                Equipment.SetCharacterEquipmentSlot when a weapon enters the hand.

Usage:
    python tools/bridge/z1-diff.py --z1 <own-tap-dir>
    python tools/bridge/z1-diff.py --z1 <own-tap-dir> --around Equipment.SetCharacterEquipmentSlot
    python tools/bridge/z1-diff.py --z1 <own-tap.log> --json out/z1-vs-cranberry.json
"""
import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "capture"))
from opcodes import OpcodeTable  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
_census = __import__("capture-census")
zone_payloads = _census.zone_payloads

DEFAULT_MAP = Path(r"C:\Aug2017\out\wave9-bridge\opcode-map-1087-to-1148.json")
# No default Z1 root. docs/00 forbids C:\Project\out\refsessions (h1emu tap logs); a legal tap
# is one this project made itself of a server the owner owns, and the caller must name it.
FORBIDDEN_Z1_PATH_TOKEN = "refsessions"
DEFAULT_CRAN = Path(r"C:\Aug2017\captures")

# <epoch> <time> +<delta> <SERVICE> <dir> sess=.. ch=.. len=.. op=<hex> ref=<name> hex=<bytes>
Z1_LINE = re.compile(
    r"^\d+\s+(?P<time>[\d:.]+)\s+\S+\s+(?P<service>LOGIN|ZONE)\s+(?P<dir>s2c|c2s)\s+"
    r".*?\bop=(?P<op>[0-9a-fA-F]+)\s+ref=(?P<ref>\S+)"
)


def load_map(path):
    """(1087 base, sub) -> (1148 base, sub), plus the leaf-only table and the deletions."""
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    pairs, leaves, deleted = {}, {}, {}
    for row in doc["rows"]:
        z1 = row.get("z1_1087")
        aug = row.get("aug_1148")
        if not z1:
            continue
        key = (z1[0], z1[1] if len(z1) > 1 else 0)
        if row["verdict"] == "deleted" or not aug:
            deleted[key] = row["member"]
            if key[1] == 0:
                deleted[(key[0], None)] = row["member"]
            continue
        value = (aug[0], aug[1] if len(aug) > 1 else 0)
        pairs[key] = value
        if key[1] == 0:
            leaves[key[0]] = value
    return pairs, leaves, deleted


def translate(op_hex, pairs, leaves, deleted):
    """A Z1 op field -> ('1148 hex', note). The op field is the packet's first bytes, so for a
    LEAF packet its second byte is payload, not a sub-opcode: try the pair first, then the leaf."""
    raw = bytes.fromhex(op_hex if len(op_hex) % 2 == 0 else "0" + op_hex)
    base = raw[0]
    sub = raw[1] if len(raw) > 1 else 0
    if (base, sub) in pairs:
        b, s = pairs[(base, sub)]
        return f"{b:02x} {s:02x}", ""
    if base in leaves:
        b, _ = leaves[base]
        return f"{b:02x}", "leaf"
    if (base, sub) in deleted or (base, None) in deleted:
        return None, "DELETED at 1148"
    return None, "unmapped"


def read_z1(paths, pairs, leaves, deleted):
    counts = defaultdict(lambda: defaultdict(int))   # (aug_id, ref) -> dir -> n
    notes = {}
    for path in paths:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                m = Z1_LINE.match(line)
                if not m or m.group("service") != "ZONE":
                    continue
                aug, note = translate(m.group("op"), pairs, leaves, deleted)
                key = (aug or f"({note})", m.group("ref"))
                counts[key][m.group("dir")] += 1
                if note:
                    notes[key] = note
    return counts, notes


def read_cranberry(paths):
    table = OpcodeTable()
    counts = defaultdict(lambda: defaultdict(int))   # aug_id -> dir -> n
    names = {}
    for path in paths:
        for direction, payload in zone_payloads(path):
            op = table.resolve(payload)
            key = op.hex.replace("0x", "").strip()
            counts[key][direction] += 1
            names.setdefault(key, op.name)
    return counts, names


def normalise(hexid):
    """'0x94 0x02' / '94 02' / '9402' -> '94 02', so the two sides key alike."""
    parts = re.findall(r"[0-9a-fA-F]{2}", hexid.replace("0x", ""))
    return " ".join(p.lower() for p in parts)


def around(paths, ref, window):
    """Z1's ordered ZONE burst around each occurrence of ref - the SEQUENCE, not the count."""
    for path in paths:
        rows = []
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                m = Z1_LINE.match(line)
                if m and m.group("service") == "ZONE":
                    rows.append((m.group("time"), m.group("dir"), m.group("op"), m.group("ref")))
        hits = [i for i, r in enumerate(rows) if r[3] == ref]
        if not hits:
            continue
        print(f"\n=== {path}  -  {len(hits)} occurrence(s) of {ref}")
        for n, i in enumerate(hits, 1):
            print(f"\n  --- occurrence {n} @ {rows[i][0]}")
            for j in range(max(0, i - window), min(len(rows), i + window + 1)):
                t, d, op, r = rows[j]
                mark = ">>" if j == i else "  "
                print(f"  {mark} {t}  {d}  op={op:<6} {r}")


def expand_z1(paths):
    """Resolve --z1 into packets.log files, refusing every forbidden third-party artefact.

    docs/00: C:\\Project\\out\\refsessions\\* are tap logs of h1emu's h1z1-server V0.49.1, not the
    owner's own sessions. The check is on the whole resolved path, so neither a relative path nor
    a symlink into that tree gets through, and it fails loudly rather than skipping the file.
    """
    resolved = []
    for path in paths:
        full = Path(path).expanduser().resolve()
        if FORBIDDEN_Z1_PATH_TOKEN in str(full).lower():
            raise SystemExit(
                f"refused: {full}\n"
                "  That tree is a forbidden third-party artefact (h1emu h1z1-server V0.49.1 tap\n"
                "  logs), named in docs/00-clean-room-rules.md. Point --z1 at a tap this project\n"
                "  made itself, of a server the owner owns."
            )
        if full.is_dir():
            found = sorted(full.glob("*/packets.log")) + sorted(full.glob("packets.log"))
            if not found:
                raise SystemExit(f"no packets.log under {full}")
            resolved.extend(found)
        elif full.is_file():
            resolved.append(full)
        else:
            raise SystemExit(f"no such path: {full}")
    return resolved


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--z1", nargs="+", type=Path, required=True,
                    help="own Z1 tap log(s) or directories holding packets.log; see PROVENANCE")
    ap.add_argument("--cran", nargs="*", type=Path)
    ap.add_argument("--map", type=Path, default=DEFAULT_MAP)
    ap.add_argument("--around", help="print Z1's ordered burst around this ref instead")
    ap.add_argument("--window", type=int, default=6)
    ap.add_argument("--json", type=Path)
    args = ap.parse_args()

    z1_paths = expand_z1(args.z1)
    cran_paths = args.cran or sorted(DEFAULT_CRAN.glob("wire-*.txt"))
    if not z1_paths:
        raise SystemExit("--z1 matched no packets.log")

    if args.around:
        around(z1_paths, args.around, args.window)
        return 0

    pairs, leaves, deleted = load_map(args.map)
    z1_counts, notes = read_z1(z1_paths, pairs, leaves, deleted)
    cran_counts, cran_names = read_cranberry(cran_paths)

    print(f"Z1 tap logs      {len(z1_paths):>4}   ({', '.join(str(p) for p in args.z1)})")
    print(f"Cranberry wire   {len(cran_paths):>4}   ({DEFAULT_CRAN})")
    print(f"opcode map       {len(pairs):>4} translations, {len(deleted)} deleted\n")

    cran_by_id = {normalise(k): v for k, v in cran_counts.items()}
    rows = []
    for (aug_id, ref), dirs in z1_counts.items():
        key = normalise(aug_id)
        cran = cran_by_id.get(key, {})
        rows.append({
            "packet": ref,
            "aug1148": aug_id,
            "z1_s2c": dirs.get("s2c", 0),
            "z1_c2s": dirs.get("c2s", 0),
            "cran_s2c": cran.get("s2c", 0),
            "cran_c2s": cran.get("c2s", 0),
            "note": notes.get((aug_id, ref), ""),
        })

    # "Z1 sends it and we never have" first, by how often Z1 sends it. That ordering IS the
    # work list: the top row is the biggest thing a working server does that we do not.
    def rank(r):
        z1_only = r["z1_s2c"] > 0 and r["cran_s2c"] == 0
        return (0 if z1_only else 1, -r["z1_s2c"], -r["z1_c2s"])

    rows.sort(key=rank)

    print(f"{'1148 id':<9} {'Z1 s2c':>7} {'Cran s2c':>9} {'Z1 c2s':>7} {'Cran c2s':>9}  packet")
    print("-" * 78)
    for r in rows:
        flag = "  <== Z1 ONLY" if r["z1_s2c"] > 0 and r["cran_s2c"] == 0 else ""
        note = f"  [{r['note']}]" if r["note"] else ""
        print(f"{r['aug1148']:<9} {r['z1_s2c']:>7} {r['cran_s2c']:>9} "
              f"{r['z1_c2s']:>7} {r['cran_c2s']:>9}  {r['packet']}{note}{flag}")

    gap = [r for r in rows if r["z1_s2c"] > 0 and r["cran_s2c"] == 0]
    print(f"\n{len(rows)} packet kinds in Z1's log; "
          f"{len(gap)} of them Cranberry has NEVER sent "
          f"({sum(r['z1_s2c'] for r in gap):,} Z1 sends in total).")

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(rows, indent=1), encoding="utf-8")
        print(f"wrote {args.json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
