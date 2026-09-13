#!/usr/bin/env python3
r"""
sheet.py - THE reader for the August client's ``^``-delimited datasheets.

This is the single copy. ``tools/data/sheet.py`` re-exports it so the five ``derive_*.py``
documents keep their ``from sheet import Sheet`` line working, and every ``gen-*.py`` under
``tools/data`` imports ``read_rows``/``read_table`` from here instead of carrying its own
splitter (S8 defect T3: three private ``read_sheet()`` copies and two hand ``^`` splits).

INPUT   any client datasheet, e.g. C:\Aug2017\out\data_aug\ClientItemDefinitions.txt
        (extracted from Assets_*.pack with tools/pack/packread.py)
OUTPUT  column listings, filtered rows, value statistics, or JSON on stdout / --out

DIALECT (verified, not assumed - see "Evidence" below)

  * Records are lines separated by CRLF (``\r\n``). A bare LF is accepted too so the
    reader survives a file that has been through a text-mode copy.
  * Fields are separated by a single ``^``. There is no multi-character delimiter.
  * EVERY line - header and data alike - ends with a trailing ``^``, so a naive
    ``split('^')`` yields one extra empty field at the end. This reader drops that one
    trailing empty field from the header and from each row, so a 74-column sheet reports
    74 columns and not 75.
  * The header is the FIRST line and is prefixed with ``#``. The ``#`` is not part of the
    first column's name.
  * A column name may be prefixed with ``*`` to mark it as the sheet's key
    (``#*ID^CODE_FACTORY_NAME^...``). The ``*`` is stripped from the reported name and the
    column is listed as a key by ``columns``. Some sheets put the key on the second column
    (``#TYPE_NAME^*ID^PARAM1^...`` in Datasheets.txt / ClientDatasheets.txt), so the key is
    never assumed to be column 0.
  * There is NO quoting and NO escaping. A field is exactly the bytes between two ``^``.
    Empty fields are common and mean "unset" (they are not NULL sentinels).
  * Text is 7-bit ASCII in every sheet checked; the reader decodes latin-1 so that a
    stray high byte round-trips instead of raising.
  * Rows may in principle be ragged. This reader is tolerant: a short row is padded with
    empty strings and a long row keeps its surplus fields under the synthetic column names
    ``_extra1``, ``_extra2``, ... Both cases are counted and reported by ``stats``.

  * A sheet may declare MORE THAN ONE key column: VehicleMoveInfoMappings.txt is
    ``#*VEHICLE_ID^*MOVE_INFO_ID^`` and GameModePlayerStatManagerMap.txt,
    VehicleSeatMappings.txt, VehicleSets.txt and VehicleSpawnMappings.txt are the same
    shape. ``keys`` is therefore a list, never a single column.

  Evidence for the dialect: all 36 datasheets under C:\Aug2017\out\data_aug were parsed by
  this reader and scanned byte-wise for ``"``, ``\``, non-ASCII bytes, lone LF and
  inconsistent field counts. Result across all 36 (ClientItemDefinitions.txt 2,643 rows x
  74 cols, Models.txt 1,173 x 14, CodeStringMappings.txt 1,844 x 2, Vehicles.txt 8 x 125,
  SeatInfo.txt 26 x 30, ClientDatasheets.txt / Datasheets.txt 13 and 7 x 22,
  ItemClasses.txt 97 x 12, languages.txt header-only with 0 rows, and the rest): zero quote
  characters, zero backslashes, zero non-ASCII bytes, CRLF throughout with no lone LF, and
  every data row in every file carrying exactly the header's field count - no ragged row
  anywhere. The quoting/escaping and ragged handling above are therefore documented
  tolerances, not observed behaviour, and the reader never silently "unquotes" a field that
  legitimately begins with a quote.

  The header ``#``/``*`` convention is also what the client's own generator emits: the
  same two markers appear on the first line of every one of the 293 ``.txt`` assets listed
  in pack1-index-aug.txt.

STREAMING: the file is read line by line and rows are yielded as they are parsed; nothing
but the current row is held, so a 45 MB sheet costs a few kilobytes of memory. ``tojson``
is the one subcommand that must materialise its output, and it streams the JSON array out
element by element rather than building a list first.

Usage:
    python tools/data/sheet.py columns ClientItemDefinitions.txt
    python tools/data/sheet.py rows ClientItemDefinitions.txt --where ID=2423
    python tools/data/sheet.py rows ClientItemDefinitions.txt --where CODE_FACTORY_NAME=Weapon \
                                    --cols ID,NAME_ID,MODEL_NAME --limit 20
    python tools/data/sheet.py stats ClientItemDefinitions.txt --by CODE_FACTORY_NAME
    python tools/data/sheet.py tojson Models.txt --out models.json

Import:
    from sheet import Sheet
    for row in Sheet(path):        # row is a dict {column: str}
        ...
"""

from __future__ import annotations

import argparse
import collections
import json
import re
import sys
from pathlib import Path
from typing import Iterator, TextIO

#: The one and only field separator in this format.
DELIM = "^"

#: Marks the header line.
HEADER_PREFIX = "#"

#: Marks a key column inside the header.
KEY_MARKER = "*"


class Sheet:
    """
    A client datasheet opened for streaming iteration.

    ``columns`` and ``keys`` are available immediately after construction; iterating the
    object yields one ``dict`` per data row. The instance can be iterated more than once
    (the file is reopened each time).
    """

    def __init__(
        self,
        path: str | Path,
        encoding: str = "latin-1",
        *,
        keep_key_markers: bool = False,
        require_header: bool = False,
    ) -> None:
        self.path = Path(path)
        self.encoding = encoding
        #: keep the ``*`` key marker on the reported column name. Two catalogue generators key
        #: their row dicts on the raw header text (``row["*ID"]``); they pass True.
        self.keep_key_markers = keep_key_markers
        #: True when the first line carried the format's ``#`` header marker.
        self.has_header = False
        self.columns: list[str] = []
        self.keys: list[str] = []
        #: counts filled in during iteration; ``stats`` reports them
        self.short_rows = 0
        self.long_rows = 0
        self.blank_lines = 0
        self._read_header()
        if require_header and not self.has_header:
            raise ValueError(f"{self.path}: missing datasheet header")

    # -- header ---------------------------------------------------------------------

    def _open(self) -> TextIO:
        # newline="" keeps the CRLF intact so we strip it ourselves and never let the
        # runtime turn a lone CR inside a field into a record break.
        return self.path.open("r", encoding=self.encoding, newline="")

    @staticmethod
    def _split(line: str) -> list[str]:
        """Split one record. Drops the single trailing empty field the format always emits."""
        fields = line.split(DELIM)
        if fields and fields[-1] == "":
            fields.pop()
        return fields

    def _read_header(self) -> None:
        with self._open() as fh:
            first = fh.readline()
        if not first:
            raise ValueError(f"{self.path}: empty file, no header")
        first = first.rstrip("\r\n")
        if first.startswith(HEADER_PREFIX):
            self.has_header = True
            first = first[len(HEADER_PREFIX):]
        raw = self._split(first)
        if not raw:
            raise ValueError(f"{self.path}: header line has no columns")
        for name in raw:
            if name.startswith(KEY_MARKER):
                stripped = name[len(KEY_MARKER):]
                self.keys.append(stripped)
                if not self.keep_key_markers:
                    name = stripped
            self.columns.append(name)

    # -- rows -----------------------------------------------------------------------

    def __iter__(self) -> Iterator[dict[str, str]]:
        ncol = len(self.columns)
        self.short_rows = self.long_rows = self.blank_lines = 0
        with self._open() as fh:
            fh.readline()  # header
            for line in fh:
                line = line.rstrip("\r\n")
                if not line:
                    self.blank_lines += 1
                    continue
                fields = self._split(line)
                if len(fields) < ncol:
                    self.short_rows += 1
                    fields += [""] * (ncol - len(fields))
                row = dict(zip(self.columns, fields))
                if len(fields) > ncol:
                    self.long_rows += 1
                    for i, extra in enumerate(fields[ncol:], 1):
                        row[f"_extra{i}"] = extra
                yield row


# -- the import surface every generator uses ------------------------------------------


def read_rows(
    path: str | Path,
    *,
    keep_key_markers: bool = False,
    require_header: bool = False,
    encoding: str = "latin-1",
) -> list[dict[str, str]]:
    """
    Materialise one datasheet as a list of ``{column: value}`` dicts.

    This is the call that replaces the private ``read_sheet()`` copies in
    ``gen-inventory-slots.py`` / ``gen-item-use-options.py`` / ``gen-skin-catalog.py`` and the
    hand ``line.split("^")`` loops in ``gen-weapon-firegroups.py``, ``gen-ability-facts.py``,
    ``gen-armour-facts.py``, ``gen-item-class-mappings.py``, ``gen-string-hash-values.py`` and
    ``gen-doors.py`` (S8 defect T3).

    It is byte-for-byte equivalent to all of them on the August corpus: every extracted sheet is
    rectangular, CRLF-terminated, trailing-``^`` on every line, quote-free and 7-bit (the
    DIALECT block above), which is exactly the region where the private copies and this reader
    agree. Where they could have differed - a ragged row, a line with no trailing ``^``, a quote
    character, a high byte - no August sheet has one, and this reader's tolerances are the
    documented ones rather than each copy's accident.

    ``keep_key_markers=True`` reports the header verbatim, so a key column stays ``*ID`` - the
    shape ``csv.DictReader(fieldnames=header)`` produced for the catalogue generators.
    """
    return list(Sheet(path, encoding, keep_key_markers=keep_key_markers,
                      require_header=require_header))


def read_table(path: str | Path, *, encoding: str = "latin-1") -> list[dict[str, str]]:
    """``read_rows`` in the catalogue generators' dialect: header verbatim, header required."""
    return read_rows(path, keep_key_markers=True, require_header=True, encoding=encoding)


def column_index(path: str | Path) -> dict[str, int]:
    """``{column name: ordinal}`` for a sheet, for code that still wants positional access."""
    return {name: i for i, name in enumerate(Sheet(path).columns)}


# -- helpers -------------------------------------------------------------------------


def _resolve(path_arg: str) -> Path:
    """Accept a bare sheet name and look it up in the default extraction directory."""
    p = Path(path_arg)
    if p.exists():
        return p
    fallback = Path(r"C:\Aug2017\out\data_aug") / path_arg
    if fallback.exists():
        return fallback
    sys.exit(f"no such sheet: {path_arg}")


def _parse_where(clauses: list[str]) -> list[tuple[str, re.Pattern[str] | str, bool]]:
    """
    ``COL=VAL`` is an exact, case-insensitive match. ``COL~REGEX`` is a search.
    Returns (column, matcher, is_regex).
    """
    out: list[tuple[str, re.Pattern[str] | str, bool]] = []
    for c in clauses:
        if "~" in c and ("=" not in c or c.index("~") < c.index("=")):
            col, _, pat = c.partition("~")
            out.append((col, re.compile(pat, re.IGNORECASE), True))
        elif "=" in c:
            col, _, val = c.partition("=")
            out.append((col, val.lower(), False))
        else:
            sys.exit(f"--where needs COL=VALUE or COL~REGEX, got {c!r}")
    return out


def _matches(row: dict[str, str], where: list[tuple[str, re.Pattern[str] | str, bool]]) -> bool:
    for col, matcher, is_regex in where:
        cell = row.get(col)
        if cell is None:
            return False
        if is_regex:
            if not matcher.search(cell):  # type: ignore[union-attr]
                return False
        elif cell.lower() != matcher:
            return False
    return True


def _check_columns(sheet: Sheet, names: list[str]) -> None:
    unknown = [n for n in names if n not in sheet.columns]
    if unknown:
        sys.exit(f"unknown column(s) {', '.join(unknown)}; have: {', '.join(sheet.columns)}")


# -- subcommands ---------------------------------------------------------------------


def cmd_columns(args: argparse.Namespace) -> int:
    sheet = Sheet(_resolve(args.file))
    print(f"{sheet.path}")
    print(f"{len(sheet.columns)} columns, key column(s): {', '.join(sheet.keys) or '(none)'}")
    for i, name in enumerate(sheet.columns):
        mark = " *" if name in sheet.keys else ""
        print(f"  {i:>3}  {name}{mark}")
    return 0


def cmd_rows(args: argparse.Namespace) -> int:
    sheet = Sheet(_resolve(args.file))
    where = _parse_where(args.where)
    _check_columns(sheet, [c for c, _, _ in where])
    cols = [c.strip() for c in args.cols.split(",")] if args.cols else sheet.columns
    _check_columns(sheet, cols)

    shown = 0
    if args.format == "tsv":
        print("\t".join(cols))
    for row in sheet:
        if not _matches(row, where):
            continue
        if args.format == "json":
            print(json.dumps({c: row.get(c, "") for c in cols}, ensure_ascii=False))
        else:
            print("\t".join(row.get(c, "") for c in cols))
        shown += 1
        if args.limit and shown >= args.limit:
            break
    print(f"{shown} row(s)", file=sys.stderr)
    return 0


def cmd_stats(args: argparse.Namespace) -> int:
    sheet = Sheet(_resolve(args.file))
    if args.by:
        _check_columns(sheet, [args.by])
        counter: collections.Counter[str] = collections.Counter()
        total = 0
        for row in sheet:
            counter[row.get(args.by, "")] += 1
            total += 1
        print(f"{sheet.path}: {total} rows, {len(counter)} distinct {args.by}")
        for value, n in counter.most_common(args.top):
            print(f"  {n:>7}  {value if value else '(empty)'}")
        if len(counter) > args.top:
            print(f"  ... {len(counter) - args.top} more distinct value(s)")
    else:
        # Per-column fill and cardinality in one streaming pass.
        distinct: dict[str, set[str]] = {c: set() for c in sheet.columns}
        filled: collections.Counter[str] = collections.Counter()
        total = 0
        for row in sheet:
            total += 1
            for c in sheet.columns:
                v = row.get(c, "")
                if v:
                    filled[c] += 1
                if len(distinct[c]) <= args.top:
                    distinct[c].add(v)
        print(f"{sheet.path}: {total} rows, {len(sheet.columns)} columns")
        print(f"  {'column':<32} {'filled':>8} {'distinct':>10}")
        for c in sheet.columns:
            n = len(distinct[c])
            card = f">{args.top}" if n > args.top else str(n)
            print(f"  {c:<32} {filled[c]:>8} {card:>10}")
    if sheet.short_rows or sheet.long_rows or sheet.blank_lines:
        print(
            f"  ragged: {sheet.short_rows} short, {sheet.long_rows} long, "
            f"{sheet.blank_lines} blank line(s)"
        )
    return 0


def cmd_tojson(args: argparse.Namespace) -> int:
    sheet = Sheet(_resolve(args.file))
    if args.key:
        _check_columns(sheet, [args.key])
    out = open(args.out, "w", encoding="utf-8", newline="\n") if args.out else sys.stdout
    try:
        n = 0
        if args.key:
            out.write("{\n")
            for row in sheet:
                if n:
                    out.write(",\n")
                out.write("  " + json.dumps(row[args.key], ensure_ascii=False) + ": ")
                out.write(json.dumps(row, ensure_ascii=False))
                n += 1
            out.write("\n}\n")
        else:
            out.write("[\n")
            for row in sheet:
                if n:
                    out.write(",\n")
                out.write("  " + json.dumps(row, ensure_ascii=False))
                n += 1
            out.write("\n]\n")
    finally:
        if out is not sys.stdout:
            out.close()
    print(f"{n} row(s) -> {args.out or 'stdout'}", file=sys.stderr)
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Read the August client's ^-delimited datasheets.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    sub = ap.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("columns", help="list the sheet's columns and key column(s)")
    c.add_argument("file")
    c.set_defaults(fn=cmd_columns)

    r = sub.add_parser("rows", help="print rows, optionally filtered and projected")
    r.add_argument("file")
    r.add_argument("--where", action="append", default=[],
                   help="COL=VALUE (exact, case-insensitive) or COL~REGEX; repeatable, ANDed")
    r.add_argument("--cols", help="comma-separated columns to print (default: all)")
    r.add_argument("--limit", type=int, default=0, help="stop after N rows")
    r.add_argument("--format", choices=["tsv", "json"], default="tsv")
    r.set_defaults(fn=cmd_rows)

    s = sub.add_parser("stats", help="row/column fill and cardinality, or counts by one column")
    s.add_argument("file")
    s.add_argument("--by", help="count rows grouped by this column")
    s.add_argument("--top", type=int, default=40, help="how many groups/distinct values to show")
    s.set_defaults(fn=cmd_stats)

    j = sub.add_parser("tojson", help="convert the whole sheet to JSON")
    j.add_argument("file")
    j.add_argument("--out", help="output file (default stdout)")
    j.add_argument("--key", help="emit an object keyed by this column instead of an array")
    j.set_defaults(fn=cmd_tojson)

    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    raise SystemExit(main())
