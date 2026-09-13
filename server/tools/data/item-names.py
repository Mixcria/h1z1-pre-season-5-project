#!/usr/bin/env python3
r"""
item-names.py - resolve every ClientItemDefinitions row to its English display name and
description by joining the datasheet's NAME_ID / DESCRIPTION_ID through the locale archive.

INPUT   C:\Aug2017\out\data_aug\ClientItemDefinitions.txt   (tools/pack/packread.py extraction)
        C:\Aug2017\Client\Locale\en_us_data.dat + .dir
OUTPUT  C:\Aug2017\out\data_aug\item-names-en_us.json
            { "<item definition id>": { "name": ..., "description": ..., ... }, ... }

FORMAT FACTS RELIED ON
  * Datasheet dialect: see tools/data/sheet.py (``^``-delimited, ``#`` header, ``*`` key
    marker, trailing ``^`` on every line, no quoting or escaping).
  * Locale archive layout and the NAME_ID -> locale key mapping
        locale_key(n) = jenkins_lookup2(ascii("Global.Text." + str(n)), initval = 0)
    are derived and evidenced in tools/locale/localedat.py. ``Global.Text.%d`` is a literal
    in H1Z1.exe 0.0.118.208059 at VA 0x143118638.
  * A string id of 0 means "no string" (the column is unset, not a key into the locale).

Both tools live in sibling directories under Server/tools, so this script puts those two
directories on sys.path rather than requiring a package install.

Usage:
    python tools/data/item-names.py
    python tools/data/item-names.py --lang de_de --out C:\Aug2017\out\data_aug\item-names-de_de.json
    python tools/data/item-names.py --sheet Models.txt --name-col DESCRIPTION --out ...   # (not typical)
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

_TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_TOOLS / "data"))
sys.path.insert(0, str(_TOOLS / "locale"))

from localedat import LocaleData, text_key  # noqa: E402
from sheet import Sheet  # noqa: E402

DEFAULT_SHEET = Path(r"C:\Aug2017\out\data_aug\ClientItemDefinitions.txt")
DEFAULT_OUT = Path(r"C:\Aug2017\out\data_aug\item-names-en_us.json")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Join ClientItemDefinitions NAME_ID/DESCRIPTION_ID to locale strings.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--sheet", default=str(DEFAULT_SHEET))
    ap.add_argument("--lang", default="en_us")
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--id-col", default="ID")
    ap.add_argument("--name-col", default="NAME_ID")
    ap.add_argument("--desc-col", default="DESCRIPTION_ID")
    ap.add_argument("--extra", default="CODE_FACTORY_NAME,ITEM_CLASS,MODEL_NAME",
                    help="comma-separated extra columns to carry through ('' for none)")
    args = ap.parse_args(argv)

    loc = LocaleData(args.lang)
    sheet = Sheet(args.sheet)
    extra = [c for c in args.extra.split(",") if c]
    for col in [args.id_col, args.name_col, args.desc_col, *extra]:
        if col not in sheet.columns:
            sys.exit(f"{sheet.path} has no column {col}; have: {', '.join(sheet.columns)}")

    out: dict[str, dict[str, object]] = {}
    rows = named = described = unresolved_name = unresolved_desc = 0

    for row in sheet:
        rows += 1
        entry: dict[str, object] = {}
        for col, field in ((args.name_col, "name"), (args.desc_col, "description")):
            raw = row.get(col, "").strip()
            sid = int(raw) if raw.lstrip("-").isdigit() else 0
            if sid == 0:  # 0 = column unset, not a locale key
                continue
            entry[field + "_id"] = sid
            text = loc.get(text_key(sid))
            if text is None:
                if field == "name":
                    unresolved_name += 1
                else:
                    unresolved_desc += 1
                continue
            entry[field] = text
            if field == "name":
                named += 1
            else:
                described += 1
        for col in extra:
            value = row.get(col, "")
            if value:
                entry[col.lower()] = value
        out[row[args.id_col]] = entry

    Path(args.out).write_text(
        json.dumps(out, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n"
    )
    print(
        f"{rows} item rows -> {args.out}\n"
        f"  names resolved       {named}  (unresolved string ids: {unresolved_name})\n"
        f"  descriptions resolved {described}  (unresolved string ids: {unresolved_desc})",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
