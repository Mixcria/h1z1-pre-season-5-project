#!/usr/bin/env python3
r"""
localedat.py - read the August client's locale archives and resolve datasheet string ids.

INPUT   C:\Aug2017\Client\Locale\<lang>_data.dat  +  <lang>_data.dir   (9 languages)
OUTPUT  id -> string listings / JSON (e.g. C:\Aug2017\out\data_aug\locale-en_us.json)

=====================================================================================
FORMAT (verified against en_us, 9,330 records, and cross-checked on de_de)
=====================================================================================

Both halves are UTF-8 text with a BOM and CRLF line endings.

``<lang>_data.dir`` - the index.
    Leading metadata lines start with ``##``:
        ## CidLength: 191        ## Count: 9330       ## Date: Mon Jul 31 14:16:57 2017
        ## Game: PSNX            ##Locale: en_us      ## MD5Checksum: B05149F19C9D...
        ## T4Version: Unknown    ## TextLength: 815   ## Version: 2.1.886508
    (``Count`` is the record count and ``TextLength`` is the longest text in characters -
    both confirmed exactly: 9,330 records parsed, longest text 815 characters.)
    Every following line is four TAB-separated fields:
        key <TAB> offset <TAB> length <TAB> 'd'
    ``offset``/``length`` are BYTE positions into the ``.dat`` file **counting the 3-byte
    UTF-8 BOM**: the first record is offset 3, length 26, and ``dat[3:29]`` is exactly
    ``274368\tucdt\tCooked Chicken``. The fourth field is the literal letter ``d`` on all
    9,330 rows (a record-kind column with one observed value).

``<lang>_data.dat`` - the payload.
    One record per ``.dir`` slice, three TAB-separated fields:
        key <TAB> kind <TAB> text
    ``kind`` is ``ucdt`` (7,469), ``ugdt`` (1,847) or ``ucdn`` (14) in en_us.
    Records are separated by CRLF, **but the text itself may contain CRLF**: 87 en_us
    records are multi-line (e.g. key 18811314, the MRE description). Splitting the ``.dat``
    on newlines therefore corrupts 87 strings and produces 9,474 "lines" instead of 9,330
    records. The ``.dir`` byte ranges are the only correct way to cut the file, and this
    reader always uses them. Each slice is followed by CRLF or by end-of-file, which this
    reader asserts on every record as a self-check.
    Text carries markup the UI expands: ``<br>``, ``[[*key*]]``, ``[*target*]``,
    ``#count(...)``. It is passed through untouched.

=====================================================================================
THE NAME_ID -> LOCALE KEY MAPPING  (the open problem from the stage-1 inventory: SOLVED)
=====================================================================================

Datasheet columns that end in ``_ID`` and name a string (``NAME_ID``, ``DESCRIPTION_ID``,
``TITLE_STRING_ID``, ``STRING_ID``, ``HINT_STRING_ID``, ...) hold SMALL integers - 24, 97,
12302, 17815 - while locale keys are spread across the whole 32-bit range (min 274,368,
max 4,294,289,198). They are related by a hash of a formatted key string:

    locale_key(n) = jenkins_lookup2( ascii("Global.Text." + str(n)), initval = 0 )

``Global.Text.%d`` is a literal in the client at VA 0x143118638 (file offset 0x3117038),
referenced from twelve sites in the 0x140af9830 / 0x140b06392 / 0x140b98540-0x140b9d1a4
range - the locale-lookup helpers. The hash is Bob Jenkins' 1996 ``lookup2`` ``hash()``
(public algorithm; implemented from its published description below, not copied), taken
over the plain ASCII bytes with no NUL terminator and initval 0.

Evidence:
  * Brute-force fit: hashing ``Global.Text.<n>`` for n in 0..40,000 with each of murmur2,
    murmur3, lookup2, CRC-32, FNV-1, FNV-1a, djb2, sdbm, ELF and the client's own
    StringHashToValue hash (FUN_140b80030, see tools/data/gen-string-hash-values.py), over
    ASCII / UTF-16LE / NUL-terminated encodings, produced exactly one function with more
    than two hits into the 9,330-key set: lookup2/ASCII, with 7,463 hits. Every other
    combination scored 0. 7,463 accidental 32-bit collisions is not a coincidence.
  * Semantic spot checks (``verify`` reproduces all of these):
        ClientItemDefinitions ID 2423 (Common_Props_Bandages_BandageRoll_3P.adr)
            NAME_ID 12302        -> key   382397961 -> "Field Bandage"
            DESCRIPTION_ID 12299 -> key  3064977557 -> "Reduces bleeding. Minor heal over time."
        ClientItemDefinitions ID 2230 (Weapon_M16A4_3p.adr)
            NAME_ID 11955        -> key  1703629405 -> "Steyr Aug"
        CodeStringMappings BR.ChickenDinner 11121
                                 -> key  1997658942 -> "Winner, winner, chicken dinner!"
        CodeStringMappings BR.ChokedOnChickenDinner 13396
                                 -> key  3327660662 -> "choked on their chicken dinner!"
        CodeStringMappings AccessTarget 31
                                 -> key  1748077556 -> "[[*key*]] Open [*target*]"
        GameModeDefinitions ID 20 (BR.Z2 "Skirmish") TITLE_STRING_ID 17815
                                 -> key  3221546367 -> "Pleasant Valley Nightmare"

``CodeStringMappings.txt`` (MESSAGE_NAME -> STRING_ID, 1,844 rows) is a *second* consumer of
the same small-id space, not the mapping itself: it names the ids the client's code uses.

Usage:
    python tools/locale/localedat.py header
    python tools/locale/localedat.py dump --out C:\Aug2017\out\data_aug\locale-en_us.json
    python tools/locale/localedat.py text 12302            # NAME_ID -> string
    python tools/locale/localedat.py key 12302             # NAME_ID -> locale key
    python tools/locale/localedat.py get 382397961         # raw locale key -> string
    python tools/locale/localedat.py find 'chicken dinner'
    python tools/locale/localedat.py verify [--scan 200000]

Import:
    from localedat import LocaleData, text_key
    loc = LocaleData()          # en_us by default
    loc.text(12302)             # 'Field Bandage'
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Iterator, NamedTuple

DEFAULT_LOCALE_DIR = Path(r"C:\Aug2017\Client\Locale")
DEFAULT_LANG = "en_us"

#: Format the client uses to build a locale key from a datasheet string id.
#: Literal at VA 0x143118638 in H1Z1.exe 0.0.118.208059.
TEXT_KEY_FORMAT = "Global.Text.{}"

_MASK = 0xFFFFFFFF


# -- Bob Jenkins lookup2 (1996) ------------------------------------------------------
#
# Public algorithm, implemented here from its published description: three 32-bit
# accumulators a, b, c; a and b seeded with the golden ratio 0x9e3779b9 and c with the
# initval; the input consumed 12 bytes at a time as three little-endian words through the
# mix() avalanche; the tail folded in with the length added to c; hash is the final c.


def _mix(a: int, b: int, c: int) -> tuple[int, int, int]:
    a = (a - b - c) & _MASK; a ^= c >> 13
    b = (b - c - a) & _MASK; b ^= (a << 8) & _MASK
    c = (c - a - b) & _MASK; c ^= b >> 13
    a = (a - b - c) & _MASK; a ^= c >> 12
    b = (b - c - a) & _MASK; b ^= (a << 16) & _MASK
    c = (c - a - b) & _MASK; c ^= b >> 5
    a = (a - b - c) & _MASK; a ^= c >> 3
    b = (b - c - a) & _MASK; b ^= (a << 10) & _MASK
    c = (c - a - b) & _MASK; c ^= b >> 15
    return a, b, c


def jenkins_lookup2(data: bytes, initval: int = 0) -> int:
    """Bob Jenkins' 1996 ``lookup2`` 32-bit hash. Returns the final ``c``."""
    a = b = 0x9E3779B9
    c = initval & _MASK
    total = len(data)
    i = 0
    remaining = total
    while remaining >= 12:
        a = (a + int.from_bytes(data[i:i + 4], "little")) & _MASK
        b = (b + int.from_bytes(data[i + 4:i + 8], "little")) & _MASK
        c = (c + int.from_bytes(data[i + 8:i + 12], "little")) & _MASK
        a, b, c = _mix(a, b, c)
        i += 12
        remaining -= 12
    c = (c + total) & _MASK
    t = data[i:]
    n = len(t)
    if n >= 11: c = (c + (t[10] << 24)) & _MASK
    if n >= 10: c = (c + (t[9] << 16)) & _MASK
    if n >= 9:  c = (c + (t[8] << 8)) & _MASK
    if n >= 8:  b = (b + (t[7] << 24)) & _MASK
    if n >= 7:  b = (b + (t[6] << 16)) & _MASK
    if n >= 6:  b = (b + (t[5] << 8)) & _MASK
    if n >= 5:  b = (b + t[4]) & _MASK
    if n >= 4:  a = (a + (t[3] << 24)) & _MASK
    if n >= 3:  a = (a + (t[2] << 16)) & _MASK
    if n >= 2:  a = (a + (t[1] << 8)) & _MASK
    if n >= 1:  a = (a + t[0]) & _MASK
    return _mix(a, b, c)[2]


def text_key(string_id: int) -> int:
    """Datasheet string id (NAME_ID, DESCRIPTION_ID, ...) -> locale key."""
    return jenkins_lookup2(TEXT_KEY_FORMAT.format(string_id).encode("ascii"))


# -- the archive ---------------------------------------------------------------------


class Record(NamedTuple):
    key: int
    kind: str  # 'ucdt' | 'ugdt' | 'ucdn'
    text: str


class LocaleData:
    """One language's ``.dat``/``.dir`` pair, fully parsed via the ``.dir`` byte ranges."""

    def __init__(self, lang: str = DEFAULT_LANG, locale_dir: str | Path = DEFAULT_LOCALE_DIR) -> None:
        self.lang = lang
        self.dir_path = Path(locale_dir) / f"{lang}_data.dir"
        self.dat_path = Path(locale_dir) / f"{lang}_data.dat"
        for p in (self.dir_path, self.dat_path):
            if not p.is_file():
                sys.exit(f"no such locale file: {p}")
        self.meta: dict[str, str] = {}
        self.records: dict[int, Record] = {}
        self._load()

    def _load(self) -> None:
        # The .dat is sliced by byte offset, so it is read as bytes; offsets include the BOM.
        dat = self.dat_path.read_bytes()
        index = self.dir_path.read_text(encoding="utf-8-sig", newline="")

        for line in index.split("\r\n"):
            if not line:
                continue
            if line.startswith("##"):
                name, _, value = line.lstrip("#").strip().partition(":")
                self.meta[name.strip()] = value.strip()
                continue
            parts = line.split("\t")
            if len(parts) != 4:
                raise ValueError(f"{self.dir_path}: expected 4 fields, got {len(parts)}: {line!r}")
            key, offset, length, kind_flag = int(parts[0]), int(parts[1]), int(parts[2]), parts[3]
            blob = dat[offset:offset + length]
            if len(blob) != length:
                raise ValueError(f"{self.dir_path}: record {key} runs past the end of the .dat")
            # Self-check: a correct slice is always followed by the record separator or EOF.
            trailer = dat[offset + length:offset + length + 2]
            if trailer not in (b"\r\n", b""):
                raise ValueError(
                    f"{self.dir_path}: record {key} at {offset}+{length} is not followed by CRLF "
                    f"(got {trailer!r}) - offsets are not being interpreted correctly"
                )
            fields = blob.decode("utf-8").split("\t", 2)
            if len(fields) != 3:
                raise ValueError(f"record {key}: expected 'key<TAB>kind<TAB>text', got {blob!r}")
            if int(fields[0]) != key:
                raise ValueError(f"record at {offset}: .dir says key {key}, .dat says {fields[0]}")
            self.records[key] = Record(key, fields[1], fields[2])
            del kind_flag

        declared = self.meta.get("Count")
        if declared is not None and int(declared) != len(self.records):
            raise ValueError(
                f"{self.dir_path}: header declares Count {declared} but {len(self.records)} "
                f"records were read"
            )

    # -- lookups ---------------------------------------------------------------------

    def get(self, key: int) -> str | None:
        """By raw 32-bit locale key."""
        rec = self.records.get(key)
        return rec.text if rec else None

    def text(self, string_id: int) -> str | None:
        """By datasheet string id (NAME_ID / DESCRIPTION_ID / ...)."""
        return self.get(text_key(string_id))

    def find(self, pattern: str) -> Iterator[Record]:
        rx = re.compile(pattern, re.IGNORECASE)
        for rec in self.records.values():
            if rx.search(rec.text):
                yield rec

    def reverse_index(self, scan: int = 200_000) -> dict[int, int]:
        """
        locale key -> datasheet string id, by hashing ``Global.Text.<n>`` for n in [0, scan).

        Only keys that actually exist in this language are kept, so the result is exactly the
        set of records reachable from a datasheet.
        """
        rev: dict[int, int] = {}
        for n in range(scan):
            k = text_key(n)
            if k in self.records and k not in rev:
                rev[k] = n
        return rev


# -- subcommands ---------------------------------------------------------------------


def _load(args: argparse.Namespace) -> LocaleData:
    return LocaleData(args.lang, args.locale_dir)


def cmd_header(args: argparse.Namespace) -> int:
    loc = _load(args)
    for k, v in loc.meta.items():
        print(f"{k:<14} {v}")
    kinds: dict[str, int] = {}
    multiline = 0
    for rec in loc.records.values():
        kinds[rec.kind] = kinds.get(rec.kind, 0) + 1
        if "\n" in rec.text:
            multiline += 1
    print(f"{'records':<14} {len(loc.records)}")
    print(f"{'kinds':<14} " + ", ".join(f"{k}={n}" for k, n in sorted(kinds.items())))
    print(f"{'multi-line':<14} {multiline}")
    print(f"{'key range':<14} {min(loc.records)} .. {max(loc.records)}")
    return 0


def cmd_dump(args: argparse.Namespace) -> int:
    loc = _load(args)
    payload: dict[str, object]
    if args.with_kind:
        payload = {str(k): {"kind": r.kind, "text": r.text} for k, r in sorted(loc.records.items())}
    else:
        payload = {str(k): r.text for k, r in sorted(loc.records.items())}
    text = json.dumps(payload, ensure_ascii=False, indent=1)
    if args.out:
        Path(args.out).write_text(text + "\n", encoding="utf-8", newline="\n")
        print(f"{len(loc.records)} string(s) -> {args.out}", file=sys.stderr)
    else:
        print(text)
    return 0


def cmd_text(args: argparse.Namespace) -> int:
    loc = _load(args)
    for n in args.string_id:
        k = text_key(n)
        v = loc.get(k)
        print(f"{n}\t{k}\t" + ("(no such string)" if v is None else v.replace("\r\n", "\\n")))
    return 0


def cmd_key(args: argparse.Namespace) -> int:
    for n in args.string_id:
        print(f"{n}\t{text_key(n)}")
    return 0


def cmd_get(args: argparse.Namespace) -> int:
    loc = _load(args)
    for k in args.key:
        rec = loc.records.get(k)
        print(f"{k}\t" + ("(no such key)" if rec is None else f"{rec.kind}\t{rec.text}"))
    return 0


def cmd_find(args: argparse.Namespace) -> int:
    loc = _load(args)
    rev = loc.reverse_index(args.scan) if args.ids else {}
    n = 0
    for rec in loc.find(args.pattern):
        sid = rev.get(rec.key)
        prefix = f"{sid if sid is not None else '?':>8}\t" if args.ids else ""
        print(f"{prefix}{rec.key}\t{rec.kind}\t{rec.text}".replace("\r\n", "\\n"))
        n += 1
        if args.limit and n >= args.limit:
            break
    print(f"{n} match(es)", file=sys.stderr)
    return 0


#: (string id, expected text) pairs, each traceable to a datasheet row or code-string row.
VERIFY_CASES = [
    (12302, "Field Bandage", "ClientItemDefinitions ID 2423 NAME_ID"),
    (12299, "Reduces bleeding. Minor heal over time.", "ClientItemDefinitions ID 2423 DESCRIPTION_ID"),
    (11955, "Steyr Aug", "ClientItemDefinitions ID 2230 NAME_ID (Weapon_M16A4_3p.adr)"),
    (11121, "Winner, winner, chicken dinner!", "CodeStringMappings BR.ChickenDinner"),
    (13396, "choked on their chicken dinner!", "CodeStringMappings BR.ChokedOnChickenDinner"),
    (31, "[[*key*]] Open [*target*]", "CodeStringMappings AccessTarget"),
    (17815, "Pleasant Valley Nightmare", "GameModeDefinitions ID 20 TITLE_STRING_ID"),
]


def cmd_verify(args: argparse.Namespace) -> int:
    loc = _load(args)
    # The expected texts are English. In any other language the same keys must still
    # RESOLVE (the key is language-independent) but the text is of course translated.
    english = loc.lang == "en_us"
    failures = 0
    for sid, expected, source in VERIFY_CASES:
        actual = loc.text(sid)
        ok = actual == expected if english else actual is not None
        failures += not ok
        print(f"{'ok ' if ok else 'FAIL'}  {sid:>6} -> {text_key(sid):>10}  {actual!r}   [{source}]")
    if not english:
        print(f"({loc.lang}: checking that the key resolves, not the English text)")
    rev = loc.reverse_index(args.scan)
    unresolved = len(loc.records) - len(rev)
    highest = max(rev.values()) if rev else 0
    print(
        f"\ncoverage: {len(rev)}/{len(loc.records)} keys resolve to a Global.Text.<n> with "
        f"n < {args.scan} ({unresolved} unresolved); highest n used = {highest}"
    )
    if failures:
        print(f"{failures} case(s) FAILED", file=sys.stderr)
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Read the August client's locale .dat/.dir archives and resolve "
                    "datasheet NAME_ID/DESCRIPTION_ID values to strings.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--lang", default=DEFAULT_LANG,
                    help="language code (en_us, de_de, es_es, fr_fr, ja_jp, ko_kr, pt_br, ru_ru, zh_cn)")
    ap.add_argument("--locale-dir", default=str(DEFAULT_LOCALE_DIR))
    sub = ap.add_subparsers(dest="cmd", required=True)

    h = sub.add_parser("header", help="print the .dir metadata and a record summary")
    h.set_defaults(fn=cmd_header)

    d = sub.add_parser("dump", help="write every locale key -> string as JSON")
    d.add_argument("--out", help="output file (default stdout)")
    d.add_argument("--with-kind", action="store_true", help="emit {kind, text} objects")
    d.set_defaults(fn=cmd_dump)

    t = sub.add_parser("text", help="datasheet string id -> string")
    t.add_argument("string_id", type=int, nargs="+")
    t.set_defaults(fn=cmd_text)

    k = sub.add_parser("key", help="datasheet string id -> locale key (no archive needed)")
    k.add_argument("string_id", type=int, nargs="+")
    k.set_defaults(fn=cmd_key)

    g = sub.add_parser("get", help="raw locale key -> string")
    g.add_argument("key", type=int, nargs="+")
    g.set_defaults(fn=cmd_get)

    f = sub.add_parser("find", help="regex search over the text")
    f.add_argument("pattern")
    f.add_argument("--limit", type=int, default=0)
    f.add_argument("--ids", action="store_true", help="also show the datasheet string id")
    f.add_argument("--scan", type=int, default=200_000, help="Global.Text.<n> range for --ids")
    f.set_defaults(fn=cmd_find)

    v = sub.add_parser("verify", help="re-prove the NAME_ID -> locale key mapping")
    v.add_argument("--scan", type=int, default=200_000, help="how far to scan Global.Text.<n>")
    v.set_defaults(fn=cmd_verify)

    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
