#!/usr/bin/env python3
r"""areasread - Cranberry's reader for the August-2017 H1Z1 client's ``<Zone>Areas.xml``.

INPUT
    ``C:\Aug2017\out\world_aug\Z2Areas.xml`` - 5,478,706 B, extracted from
    ``Assets_070.pack`` by ``tools/pack/packread.py``. 12,374 ``<AreaDefinition>`` elements.

OUTPUT
    ``list``     one line per area: name, shape, centre, size.
    ``named``    only the designer-named areas (everything that is not the auto-generated
                 ``Box_<id>`` / ``Sphere_<id>`` filler), which is where the points of
                 interest live.
    ``json``     every selected area as JSON, with centre / half-extent / axis-aligned
                 bounds precomputed for the server.

FORMAT FACTS THIS TOOL RELIES ON
    Verified 2026-08-29 by reading ``Z2Areas.xml`` itself.

    A1  The file is an XML **fragment**: a bare sequence of sibling ``<AreaDefinition>``
        elements with no document root, so it must be wrapped before parsing. (First byte
        of the file is ``<AreaDefinition``; there is no ``<?xml`` declaration and no
        enclosing element - the last line closes an ``AreaDefinition``.)
    A2  ``shape`` is ``box`` (9,952) or ``sphere`` (2,422) - the only two values in the file -
        and **the two shapes carry different attributes**. Counting the attribute set of
        every one of the 12,374 elements gives exactly two shapes of element:
            box     ``id name shape x1 y1 z1 x2 y2 z2 rotX rotY rotZ``   (all 9,952)
            sphere  ``id name shape x1 y1 z1 radius``                    (all 2,422)
        For a box, (x1,y1,z1)-(x2,y2,z2) is the axis-aligned extent **before** the ``rotY``
        yaw is applied about the box centre; rotations are radians. For a sphere there is
        **no x2/y2/z2 and no rotation**: (x1,y1,z1) is the centre itself and ``radius`` is
        an explicit attribute, e.g.
            ``<AreaDefinition id="2160258836" name="BoxOfDestinyFX_Fire_Tires_Huge"
              shape="sphere" x1="-288.533081" y1="507.582642" z1="-4920.833008"
              radius="400.000000">``
        Reading a sphere as though it had a second corner (defaulting x2/y2/z2 to 0) puts
        its centre halfway to the world origin and makes its size ``|x1|,|y1|,|z1|`` - so
        the two shapes are decoded by separate branches here.
    A3  Y is the up axis, matching the ``.zone`` placements (Z9 in ``zoneread.py``).
    A4  Optional child ``<Property type=...>`` elements carry presentation data only:
        ``SoundEmitter`` (7,734), ``CompositeEffect`` (3,954), ``RandomEffect`` (315),
        ``ObjectTerrainData`` (22). A ``MultiPositionSoundArea`` property holds
        ``<MultiPosition>`` children (1,697 in total).
    A5  Designer names use dotted namespaces. In Z2 those are:
        ``Loot.<PoiName>.<nn>``      118 - the loot regions per point of interest
        ``GasWeightArea.<PoiName>``    9 - gas-circle weighting volumes
        ``Z2.KotK.POI.<PoiName>``      1 - an explicit KotK POI volume
        ``KotK.SkySpawn``              1 - the drop-plane volume, y 845..855
        plus FX names (``BoxOfDestiny*``, ``Fireflies_*``, ``CampFire_*``, ...).
        Everything else is named ``Box_<id>`` or ``Sphere_<id>`` and is FX/sound filler.

USAGE
    python areasread.py C:\Aug2017\out\world_aug\Z2Areas.xml named
    python areasread.py C:\Aug2017\out\world_aug\Z2Areas.xml json --prefix Loot. -o areas.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Iterator, Sequence

#: A2: the only two shape values in the file.
SHAPES = ("box", "sphere")

#: A5: names the editor generates for unnamed FX/sound volumes.
_AUTO_NAME = re.compile(r"^(Box|Sphere)_\d+$")


def iter_areas(path: Path) -> Iterator[ET.Element]:
    """Stream ``<AreaDefinition>`` elements out of the root-less fragment (A1).

    ``ET.XMLPullParser`` is fed a synthetic root so the fragment parses, and elements are
    cleared as they complete, so the 5.5 MB file never becomes a 5.5 MB DOM.
    """
    parser = ET.XMLPullParser(events=("end",))
    parser.feed(b"<Areas>")
    with path.open("rb") as fh:
        while True:
            chunk = fh.read(1 << 20)
            if not chunk:
                break
            parser.feed(chunk)
            for _, elem in parser.read_events():
                if elem.tag == "AreaDefinition":
                    yield elem
                    elem.clear()
    parser.feed(b"</Areas>")
    for _, elem in parser.read_events():
        if elem.tag == "AreaDefinition":
            yield elem
            elem.clear()


def area_to_dict(elem: ET.Element) -> dict:
    """A2/A3: normalise one area into (bounds, centre, size) plus its properties (A4).

    Boxes and spheres are decoded by separate branches because they carry different
    attributes (A2): a sphere has no second corner and no rotation, and gives its ``radius``
    explicitly. ``min``/``max`` are the axis-aligned bounds in both cases - for a sphere,
    centre +/- radius - so a consumer can broad-phase either shape the same way.
    """
    g = elem.get
    shape = g("shape", "")
    x1, y1, z1 = float(g("x1", 0)), float(g("y1", 0)), float(g("z1", 0))

    if shape == "sphere":
        radius = float(g("radius", 0))
        centre = (x1, y1, z1)
        lo = tuple(c - radius for c in centre)
        hi = tuple(c + radius for c in centre)
        rot = (0.0, 0.0, 0.0)
    else:
        x2, y2, z2 = float(g("x2", 0)), float(g("y2", 0)), float(g("z2", 0))
        lo = (min(x1, x2), min(y1, y2), min(z1, z2))
        hi = (max(x1, x2), max(y1, y2), max(z1, z2))
        centre = tuple((a + b) / 2.0 for a, b in zip(lo, hi))
        radius = None
        rot = (float(g("rotX", 0)), float(g("rotY", 0)), float(g("rotZ", 0)))

    d = {
        "id": int(g("id", 0)),
        "name": g("name", ""),
        "shape": shape,
        "min": [round(v, 4) for v in lo],
        "max": [round(v, 4) for v in hi],
        "center": [round(v, 4) for v in centre],
        "size": [round(b - a, 4) for a, b in zip(lo, hi)],
        "rot": [round(v, 6) for v in rot],
    }
    if radius is not None:
        d["radius"] = round(radius, 4)
    props = [p.get("type", "") for p in elem.findall("Property")]
    if props:
        d["properties"] = props
    return d


def is_named(name: str) -> bool:
    """A5: True for a designer-given name, False for editor filler."""
    return bool(name) and not _AUTO_NAME.match(name)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="areasread",
        description=(
            "Read the August-2017 H1Z1 client's <Zone>Areas.xml area definitions: named "
            "points of interest, loot regions, gas-weight volumes and FX volumes."
        ),
        epilog=(
            "Format facts (verified 2026-08-29 against Z2Areas.xml, see "
            "docs/29-z2-world-data.md): the file is a root-less XML fragment of "
            "<AreaDefinition id name shape x1 y1 z1 x2 y2 z2 rotX rotY rotZ> siblings; "
            "shape is box or sphere; Y is up; optional <Property> children are sound/FX "
            "only. Designer names are dotted (Loot.*, GasWeightArea.*, Z2.KotK.POI.*, "
            "KotK.SkySpawn); Box_<id>/Sphere_<id> are editor filler."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("file", type=Path, help="the <Zone>Areas.xml file")
    sub = p.add_subparsers(dest="cmd", required=True)
    for name, helptext in (
        ("list", "one line per area"),
        ("named", "only designer-named areas (points of interest)"),
        ("json", "emit the selected areas as JSON"),
    ):
        s = sub.add_parser(name, help=helptext)
        s.add_argument("--prefix", action="append", default=[], metavar="STR",
                       help="only areas whose name starts with STR (repeatable)")
        s.add_argument("--shape", choices=SHAPES, help="only this shape")
        if name == "json":
            s.add_argument("-o", "--out", type=Path, help="write here instead of stdout")
    return p


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if not args.file.is_file():
        print(f"areasread: no such file: {args.file}", file=sys.stderr)
        return 2
    rows = []
    total = 0
    for elem in iter_areas(args.file):
        total += 1
        name = elem.get("name", "")
        if args.cmd == "named" and not is_named(name):
            continue
        if args.prefix and not any(name.startswith(p) for p in args.prefix):
            continue
        if args.shape and elem.get("shape") != args.shape:
            continue
        rows.append(area_to_dict(elem))
    if args.cmd == "json":
        text = json.dumps(rows, indent=1)
        if args.out:
            args.out.parent.mkdir(parents=True, exist_ok=True)
            args.out.write_text(text + "\n", encoding="utf-8")
            print(f"wrote {len(rows):,} area(s) -> {args.out}", file=sys.stderr)
        else:
            print(text)
    else:
        for a in sorted(rows, key=lambda r: r["name"]):
            c, s = a["center"], a["size"]
            print(
                f"{a['name']:<48} {a['shape']:<7} "
                f"center=({c[0]:9.1f},{c[1]:8.1f},{c[2]:9.1f}) "
                f"size=({s[0]:8.1f},{s[1]:7.1f},{s[2]:8.1f}) "
                f"{','.join(a.get('properties', [])) }"
            )
    print(f"# {len(rows):,} of {total:,} areas", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
