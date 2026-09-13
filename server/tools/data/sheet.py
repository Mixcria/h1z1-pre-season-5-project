#!/usr/bin/env python3
r"""
sheet.py - compatibility shim. THE datasheet reader now lives in
``tools/pipeline/readers/sheet.py``.

Lane 2A of the overhaul (docs/96) collapsed the six copies of the ``^``-delimited datasheet
reader into one. This file stays because five ``derive_*.py`` documents sit next to it and open
with ``sys.path.insert(0, <this directory>)`` + ``from sheet import Sheet``; it re-exports the
one reader so those imports, and the ``python tools/data/sheet.py columns ...`` command line,
keep working unchanged.

New code should import from the pipeline package instead::

    from readers.sheet import Sheet, read_rows, read_table   # tools/pipeline on sys.path

Usage (unchanged):
    python tools/data/sheet.py columns ClientItemDefinitions.txt
    python tools/data/sheet.py rows ClientItemDefinitions.txt --where ID=2423
    python tools/data/sheet.py stats ClientItemDefinitions.txt --by CODE_FACTORY_NAME
    python tools/data/sheet.py tojson Models.txt --out models.json
"""

from __future__ import annotations

import sys
from pathlib import Path

_PIPELINE = Path(__file__).resolve().parents[1] / "pipeline"
if str(_PIPELINE) not in sys.path:
    sys.path.insert(0, str(_PIPELINE))

from readers.sheet import (  # noqa: E402  (tools/pipeline/readers/sheet.py - the one reader)
    DELIM,
    HEADER_PREFIX,
    KEY_MARKER,
    Sheet,
    column_index,
    main,
    read_rows,
    read_table,
)

__all__ = [
    "DELIM",
    "HEADER_PREFIX",
    "KEY_MARKER",
    "Sheet",
    "column_index",
    "main",
    "read_rows",
    "read_table",
]


if __name__ == "__main__":
    raise SystemExit(main())
