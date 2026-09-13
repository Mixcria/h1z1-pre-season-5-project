#!/usr/bin/env python3
r"""zonespawners - derive Cranberry's loot/NPC/vehicle spawn-marker tables from a ``.zone``.

INPUT
    a Forgelight ZONE v5 file  (read through ``zoneread.py``, same directory)
    ``Models.txt``             the client's actor-model catalogue, for the marker model ids
    optionally ``<Zone>Areas.xml``  to label each marker with the named area it falls in

OUTPUT
    ``--out-spawners  z2-item-spawners.json``    every item/NPC spawn marker placement
    ``--out-vehicles  z2-vehicle-spawners.json`` vehicle spawn markers (see V1 below)
    ``--out-summary   z2-summary.md``            map extents, class counts, tier counts,
                                                 and the named areas from Areas.xml

FACTS THIS TOOL RELIES ON
    S1  Spawn markers are ordinary ZONE object placements whose actor file is one of the
        ``ItemSpawner*.adr`` / ``NPCSpawner*.adr`` rows in ``Models.txt``. The August
        ``Models.txt`` has **98 matching rows holding 97 distinct names** - the duplicate is
        ``ItemSpawnerHospital.adr``, listed as both model 9539 and 9602 (``load_models``
        takes the lower id and reports the clash). 97 is what this tool emits as
        ``markerModelsInModelsTxt``, and 6 placed + 91 not placed = 97. Most marker rows
        carry the DESCRIPTION "DO NOT DELETE!! Used to place objects in the world" or an
        NPC name, but not all: 3 of the 98 have an **empty** DESCRIPTION (models 9386
        ``NPCSpawner_ZombieWalker``, 10080 ``NPCSpawner_FireExtinguisher``, 10118
        ``NPCSpawner_Target_Round10cmRings``), so the selection here is on the file name
        and never on the description.
        The marker model has no visible mesh in play; the server reads its transform.
    S2  Of those 97 marker models, **only 6 are actually placed in Z2.zone**:
        ``ItemSpawner_KotK_Gear01``, ``ItemSpawner_KotK_Weapons01``,
        ``ItemSpawner_KotK_Backpack01``, ``ItemSpawner_KotK_FirstAidKit01``,
        ``NPCSpawner_FireExtinguisher`` and ``ItemSpawner_BattleRoyale_Ammo01``.
        The ``ItemSpawner<Category>_Tier<nn>`` family that the survivor-mode maps use is
        **not placed anywhere in the KotK Z2 map**, so "tier" for this build is carried by
        the marker class, not by a Tier00/01/02 suffix. This tool still parses a
        ``Tier<nn>`` suffix when one is present, so it works on other zones unchanged.
    S3  ``Models.txt`` is ``^``-delimited with a ``#``-prefixed header row; column 0 is the
        numeric model ID the wire protocol uses and column 1 is ``MODEL_FILE_NAME``.
    V1  There is **no vehicle spawn-marker model** in this client: no ``Models.txt`` row is
        named ``VehicleSpawner*``, and no ``.zone`` placement matches one. The vehicle
        assets that are placed in Z2 (``Vehicles_Camper01``, ``Vehicle_C130_Grounded``,
        ``Common_DPO_Vehicle_PoliceCar01_proxy``, ...) are static scenery, not spawn
        points. The vehicle-spawner output therefore records that finding together with
        every vehicle-named placement as evidence, rather than inventing spawn points.
    A6  Area containment uses the ``Areas.xml`` box after undoing its ``rotY`` yaw about the
        box centre. **The box is yawed by -rotY**, so a point is taken into box-local space
        by rotating it by *+rotY*, not -rotY. This is measured, not assumed: take the 14
        ``Loot.*``/``GasWeightArea.*`` boxes that are both elongated (long/short side > 2)
        and meaningfully rotated (|sin 2*rotY| > 0.3), and compare the principal axis (PCA)
        of the spawner point cloud around each with the box's long axis under each sign -
        mean angular error is **22.3 deg under -rotY against 62.7 deg under +rotY, and 11
        of the 14 areas fit -rotY better**. A pure convention choice would win some areas
        and lose others; this is one-sided, so it is a sign, not a preference. Containment
        counts agree (e.g. ``Loot.SchadeWoodsLoggingTrail.01`` 71 -> 287).
        ``rotX``/``rotZ`` are 0 on every ``Loot.*`` / POI area in Z2; the tool reports it if
        that stops being true.
    A7  Named areas OVERLAP: each POI's ``GasWeightArea.<Poi>`` volume is co-sited with, and
        precedes in file order, that POI's ``Loot.<Poi>`` box. Returning the first match in
        file order therefore let the gas volume shadow the loot region it sits inside, and
        smallest-volume-wins does not fix it either: the gas box is a 25-100 m-tall slab
        inside a 341-500 m-tall Loot box, so it is the *smaller* of the two at 8 of the 9
        gas POIs. The label is therefore chosen by ``--area-prefix`` PRIORITY first (``Loot.``
        before ``GasWeightArea.``) and only then by smallest volume, and every containing
        area is emitted as ``areas`` so nothing is hidden. ``countsByArea`` is consequently
        a loot-region table; before this it was neither that nor a partition.

USAGE
    python zonespawners.py C:\Aug2017\out\world_aug\Z2.zone ^
        --models C:\Aug2017\out\data_aug\Models.txt ^
        --areas  C:\Aug2017\out\world_aug\Z2Areas.xml ^
        --out-spawners C:\Aug2017\out\world_aug\z2-item-spawners.json ^
        --out-vehicles C:\Aug2017\out\world_aug\z2-vehicle-spawners.json ^
        --out-summary  C:\Aug2017\out\world_aug\z2-summary.md
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

import areasread  # noqa: E402  (same directory, this project's own tool)
import zoneread  # noqa: E402

#: S1: marker models carry ``ItemSpawner``/``NPCSpawner`` in their actor-file name. 97 of
#: the 98 matching ``Models.txt`` rows START with it; the odd one out is
#: ``Common_DPO_NPCSpawner_HospitalLady.adr``, so this is a search and not a prefix match.
MARKER_PREFIX = re.compile(r"(ItemSpawner|NPCSpawner)", re.IGNORECASE)

#: S2: ``ItemSpawner<Category>_Tier<nn>.adr`` - the survivor-mode tiered family.
_TIERED = re.compile(r"^ItemSpawner(?P<category>[A-Za-z]+)_Tier(?P<tier>\d+)$", re.IGNORECASE)
#: ``ItemSpawner_<Group>_<Kind>.adr`` where Group is a known set name.
_GROUPED = re.compile(
    r"^(?P<family>ItemSpawner|NPCSpawner)_(?P<group>KotK|BattleRoyale|Z1|Weapon|Clothes|"
    r"Bandages|IndustrialElements|Target)_(?P<kind>.+)$",
    re.IGNORECASE,
)
#: ``ItemSpawner_<Kind>.adr`` / ``NPCSpawner_<Kind>.adr``
_SIMPLE = re.compile(r"^(?P<family>ItemSpawner|NPCSpawner)_(?P<kind>.+)$", re.IGNORECASE)
#: ``ItemSpawner<Category>.adr`` (ItemSpawnerFarm, ItemSpawnerHospital)
_BARE = re.compile(r"^ItemSpawner(?P<category>[A-Za-z]+)$")

#: V1: placements whose model name mentions a vehicle, kept as evidence.
VEHICLE_HINT = re.compile(r"vehicle", re.IGNORECASE)
#: V1: what a vehicle spawn marker would be called if the client had one.
VEHICLE_MARKER = re.compile(r"^(VehicleSpawner|Spawner_Vehicle|ItemSpawner_Vehicle)", re.IGNORECASE)


def parse_marker(model_file: str) -> dict:
    """S2: split a marker's actor-file name into family / category / tier / kind."""
    stem = model_file[:-4] if model_file.lower().endswith(".adr") else model_file
    m = _TIERED.match(stem)
    if m:
        return {
            "family": "ItemSpawner",
            "group": None,
            "category": m.group("category"),
            "tier": int(m.group("tier")),
            "kind": None,
        }
    m = _GROUPED.match(stem)
    if m:
        # S2: for the KotK/BattleRoyale families the loot class IS the trailing kind
        # (Gear01, Weapons01, Backpack01, FirstAidKit01, Ammo01), so that is the category;
        # the leading token is kept separately as the marker set it belongs to.
        return {
            "family": m.group("family"),
            "group": m.group("group"),
            "category": m.group("kind"),
            "tier": None,
            "kind": m.group("kind"),
        }
    m = _SIMPLE.match(stem)
    if m:
        return {
            "family": m.group("family"),
            "group": None,
            "category": m.group("kind"),
            "tier": None,
            "kind": m.group("kind"),
        }
    m = _BARE.match(stem)
    if m:
        return {
            "family": "ItemSpawner",
            "group": None,
            "category": m.group("category"),
            "tier": None,
            "kind": None,
        }
    return {"family": None, "group": None, "category": None, "tier": None, "kind": None}


def load_models(path: Path) -> tuple[dict[str, int], dict[str, list[int]]]:
    """S3: MODEL_FILE_NAME (lower-cased) -> model ID, plus any duplicated names.

    ``Models.txt`` is not free of duplicates: ``ItemSpawnerHospital.adr`` appears twice,
    as model 9539 and 9602. The lowest id wins so the mapping is deterministic, and every
    duplicate is returned so callers can report it instead of silently picking one.
    """
    out: dict[str, int] = {}
    dupes: dict[str, list[int]] = {}
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if line.startswith("#") or "^" not in line:
                continue
            cols = line.rstrip("\n").split("^")
            if len(cols) < 2 or not cols[0].isdigit():
                continue
            name, model_id = cols[1].lower(), int(cols[0])
            if name in out:
                dupes.setdefault(name, [out[name]]).append(model_id)
                out[name] = min(out[name], model_id)
            else:
                out[name] = model_id
    return out, dupes


class AreaIndex:
    """A6/A7: point-in-named-area lookup over the ``Areas.xml`` volumes.

    Areas overlap, so a point can be inside several. ``find_all`` returns every containing
    area smallest-volume-first, and ``find`` returns the first of those - the tightest
    volume that contains the point, which is the one that describes it.
    """

    def __init__(self, path: Path, prefixes: Sequence[str]) -> None:
        self.areas = []
        self.tilted = []
        for elem in areasread.iter_areas(path):
            name = elem.get("name", "")
            # A7: the ORDER of --area-prefix is the label priority, most meaningful first.
            rank = next(
                (i for i, p in enumerate(prefixes) if name.startswith(p)), None
            )
            if rank is None:
                continue
            a = areasread.area_to_dict(elem)
            if abs(a["rot"][0]) > 1e-4 or abs(a["rot"][2]) > 1e-4:
                self.tilted.append(name)
            lo, hi = a["min"], a["max"]
            cx, cy, cz = a["center"]
            yaw = a["rot"][1]
            volume = max(0.0, hi[0] - lo[0]) * max(0.0, hi[1] - lo[1]) * \
                max(0.0, hi[2] - lo[2])
            # A6: the box is yawed by -rotY, so a world point enters box-local space by
            # rotating it by +rotY about the box centre.
            self.areas.append(
                (name, lo, hi, cx, cy, cz, math.cos(yaw), math.sin(yaw), a["shape"],
                 a.get("radius"), volume, rank)
            )
        # A7: prefix priority first, then smallest volume, then name for determinism.
        # Volume alone would NOT do it: a POI's GasWeightArea box is a thin slab (25-100 m
        # of Y) sitting inside the same POI's 341-500 m-tall Loot box, so it is the smaller
        # of the two at 8 of the 9 gas POIs and would shadow the loot region anyway.
        self.areas.sort(key=lambda r: (r[11], r[10], r[0]))

    def find_all(self, x: float, y: float, z: float) -> list[str]:
        """Every named area containing the point, tightest volume first (A7)."""
        hits: list[str] = []
        for (name, lo, hi, cx, cy, cz, cos_y, sin_y, shape, radius, _vol,
             _rank) in self.areas:
            if shape == "sphere":
                # A2: a sphere has no rotation, and its centre is the element's (x1,y1,z1),
                # which area_to_dict has already put in "center".
                if radius is None:
                    continue
                if (x - cx) ** 2 + (y - cy) ** 2 + (z - cz) ** 2 <= radius * radius:
                    hits.append(name)
                continue
            dx, dz = x - cx, z - cz
            rx = cx + dx * cos_y - dz * sin_y
            rz = cz + dx * sin_y + dz * cos_y
            if lo[0] <= rx <= hi[0] and lo[1] <= y <= hi[1] and lo[2] <= rz <= hi[2]:
                hits.append(name)
        return hits

    def find(self, x: float, y: float, z: float) -> str | None:
        hits = self.find_all(x, y, z)
        return hits[0] if hits else None


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="zonespawners",
        description=(
            "Derive the item/NPC spawn-marker table, the vehicle-spawner finding, and a "
            "world summary from a Forgelight ZONE v5 file plus the client's Models.txt "
            "and Areas.xml."
        ),
        epilog=(
            "Facts (verified 2026-08-29, see docs/29-z2-world-data.md): spawn markers are "
            "ordinary ZONE placements of the 97 distinct ItemSpawner*/NPCSpawner* actor "
            "files in Models.txt (98 rows; ItemSpawnerHospital.adr is listed twice); only "
            "6 of them are placed in Z2.zone and the ItemSpawner*_Tier* family is not "
            "among them; the client has no vehicle-spawner marker model. Named areas "
            "overlap, so each spawner is labelled with the SMALLEST containing area and "
            "every containing area is listed alongside it."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("zone", type=Path, help="the .zone file")
    p.add_argument("--models", type=Path, required=True, help="the client's Models.txt")
    p.add_argument("--areas", type=Path, help="the matching <Zone>Areas.xml (optional)")
    p.add_argument("--area-prefix", action="append",
                   default=["Loot.", "Z2.KotK.POI.", "KotK.", "GasWeightArea."],
                   help="area names to label spawners with, HIGHEST PRIORITY FIRST "
                        "(repeatable); areas overlap, so a spawner inside several is "
                        "labelled with the earliest-listed prefix and then the smallest "
                        "volume, and every containing area is emitted as 'areas'")
    p.add_argument("--out-spawners", type=Path, help="write the spawner table here (JSON)")
    p.add_argument("--out-vehicles", type=Path, help="write the vehicle-spawner finding here")
    p.add_argument("--out-summary", type=Path, help="write the Markdown summary here")
    return p


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for path in (args.zone, args.models):
        if not path.is_file():
            print(f"zonespawners: no such file: {path}", file=sys.stderr)
            return 2

    models, model_dupes = load_models(args.models)
    marker_models = {k: v for k, v in models.items() if MARKER_PREFIX.search(k)}
    for name, ids in sorted(model_dupes.items()):
        if MARKER_PREFIX.search(name):
            print(f"zonespawners: Models.txt lists {name} {len(ids)} times (ids {ids}); "
                  f"using {min(ids)}", file=sys.stderr)
    areas = AreaIndex(args.areas, args.area_prefix) if args.areas else None
    if areas and areas.tilted:
        print(f"zonespawners: {len(areas.tilted)} area(s) carry a non-zero rotX/rotZ; "
              f"containment for those is approximate: {areas.tilted[:5]}", file=sys.stderr)

    file_size = args.zone.stat().st_size
    fh, mm = zoneread._open(args.zone)
    try:
        header = zoneread.read_header(mm)
        model_counts: Counter[str] = Counter()
        spawners: list[dict] = []
        vehicle_placements: Counter[str] = Counter()
        vehicle_markers: list[dict] = []
        minx = miny = minz = math.inf
        maxx = maxy = maxz = -math.inf
        total = 0
        for inst in zoneread.iter_objects(mm, header, file_size):
            total += 1
            model_counts[inst.model] += 1
            x, y, z, _ = inst.position
            minx, maxx = min(minx, x), max(maxx, x)
            miny, maxy = min(miny, y), max(maxy, y)
            minz, maxz = min(minz, z), max(maxz, z)
            low = inst.model.lower()
            if VEHICLE_HINT.search(inst.model):
                vehicle_placements[inst.model] += 1
            if VEHICLE_MARKER.match(inst.model):
                vehicle_markers.append(
                    {"model": inst.model, "id": inst.instance_id,
                     "pos": [round(v, 4) for v in inst.position[:3]],
                     "rot": [round(v, 6) for v in inst.rotation[:3]]}
                )
            if low in marker_models or MARKER_PREFIX.search(inst.model):
                parsed = parse_marker(inst.model)
                row = {
                    "model": inst.model,
                    "modelId": marker_models.get(low),
                    "id": inst.instance_id,
                    "pos": [round(v, 4) for v in inst.position[:3]],
                    "rot": [round(v, 6) for v in inst.rotation[:3]],
                    # Z9 stores a full scale vector, and 12 spawner instances in Z2 are
                    # non-uniform, so all three components are kept rather than scale.x.
                    "scale": [round(v, 4) for v in inst.scale[:3]],
                    "family": parsed["family"],
                    "group": parsed["group"],
                    "category": parsed["category"],
                    "tier": parsed["tier"],
                }
                if areas:
                    # A7: areas overlap. "area" is the tightest containing volume; "areas"
                    # keeps every one of them so the label is never the whole story.
                    hits = areas.find_all(x, y, z)
                    row["area"] = hits[0] if hits else None
                    if len(hits) > 1:
                        row["areas"] = hits
                spawners.append(row)
    finally:
        mm.close()
        fh.close()

    def tier_key(s: dict) -> str:
        """The spawn tier this build actually expresses (S2): a Tier<nn> suffix when the
        marker has one, otherwise the marker set + loot class."""
        if s["tier"] is not None:
            return f"{s['category']}/Tier{s['tier']:02d}"
        if s["group"]:
            return f"{s['group']}/{s['category']}"
        return f"{s['family']}/{s['category']}"

    for s in spawners:
        s["tierKey"] = tier_key(s)
    by_model = Counter(s["model"] for s in spawners)
    by_category = Counter(s["category"] or "?" for s in spawners)
    by_tier = Counter(s["tierKey"] for s in spawners)
    by_area = Counter(s.get("area") or "(outside every named area)" for s in spawners)

    if args.out_spawners:
        args.out_spawners.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "zone": args.zone.name,
            "zoneVersion": header.version,
            "source": "Forgelight ZONE v5 objects section, parsed by tools/zone/zoneread.py",
            "generatedBy": "tools/zone/zonespawners.py",
            "spawnerCount": len(spawners),
            "countsByModel": dict(by_model.most_common()),
            "countsByCategory": dict(by_category.most_common()),
            "countsByTier": dict(by_tier.most_common()),
            "countsByArea": dict(by_area.most_common()),
            "markerModelsInModelsTxt": len(marker_models),
            # Both lists are lower-cased, which is the key space Models.txt is indexed in
            # here (S3/load_models); emitting one in original case and one lower-cased made
            # two adjacent fields of the same object incomparable.
            "markerModelsPlaced": sorted(m.lower() for m in by_model),
            "markerModelsNotPlaced": sorted(
                m for m in marker_models if m not in {k.lower() for k in by_model}
            ),
        }
        # The header is written with indent for readability; the 168k spawner rows are
        # written one compact object per line, which keeps the file a third of the size
        # of a fully indented dump and stays line-diffable.
        with args.out_spawners.open("w", encoding="utf-8", newline="\n") as out:
            head = json.dumps(payload, indent=1)
            out.write(head[: head.rfind("}")].rstrip().rstrip(",") + ",\n")
            out.write(' "spawners": [\n')
            for i, row in enumerate(spawners):
                out.write("  " + json.dumps(row, separators=(",", ":")))
                out.write(",\n" if i + 1 < len(spawners) else "\n")
            out.write(" ]\n}\n")
        print(f"wrote {len(spawners):,} spawner(s) -> {args.out_spawners}", file=sys.stderr)

    if args.out_vehicles:
        args.out_vehicles.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "zone": args.zone.name,
            "generatedBy": "tools/zone/zonespawners.py",
            "finding": (
                "This client has no vehicle spawn-marker model (fact V1): no Models.txt row "
                "is named VehicleSpawner*/Spawner_Vehicle*/ItemSpawner_Vehicle*, and no "
                "placement in this .zone matches one. Vehicle spawning is therefore "
                "server-authored, not read out of the world file."
            ),
            "vehicleMarkerModelsInModelsTxt": sorted(
                m for m in models if VEHICLE_MARKER.match(m)
            ),
            "vehicleSpawnMarkers": vehicle_markers,
            "vehicleNamedPlacements": dict(vehicle_placements.most_common()),
        }
        with args.out_vehicles.open("w", encoding="utf-8", newline="\n") as out:
            json.dump(payload, out, indent=1)
            out.write("\n")
        print(f"wrote vehicle-spawner finding -> {args.out_vehicles}", file=sys.stderr)

    if args.out_summary:
        named_areas = []
        if args.areas:
            for elem in areasread.iter_areas(args.areas):
                if areasread.is_named(elem.get("name", "")):
                    named_areas.append(areasread.area_to_dict(elem))
        _write_summary(
            args.out_summary, args.zone, header, total, model_counts,
            (minx, miny, minz, maxx, maxy, maxz), spawners, by_model, by_tier, by_area,
            named_areas, vehicle_placements,
        )
        print(f"wrote summary -> {args.out_summary}", file=sys.stderr)
    return 0


def _write_summary(path, zone, header, total, model_counts, extents, spawners,
                   by_model, by_tier, by_area, named_areas, vehicle_placements) -> None:
    minx, miny, minz, maxx, maxy, maxz = extents
    tminx, tminz, tmaxx, tmaxz = header.world_extent

    def class_of(model: str) -> str:
        return model.split("_", 1)[0] if "_" in model else model.rsplit(".", 1)[0]

    by_class: Counter[str] = Counter()
    for model, n in model_counts.items():
        by_class[class_of(model)] += n

    lines: list[str] = []
    add = lines.append
    add(f"# {zone.name} - world summary")
    add("")
    add(f"Generated by `tools/zone/zonespawners.py` from `{zone.name}` (ZONE v"
        f"{header.version}) and the client's `Models.txt` / `Areas.xml`. Every number below "
        f"is counted out of the file, not estimated.")
    add("")
    add("## Terrain grid (header)")
    add("")
    add("| field | value |")
    add("|---|---|")
    add(f"| version | {header.version} |")
    add(f"| quadsPerTile | {header.quads_per_tile} |")
    add(f"| tileSize | {header.tile_size} (unit unresolved - not metres per grid unit) |")
    add(f"| tileHeight | {header.tile_height} |")
    add(f"| verticesPerTile | {header.vertices_per_tile} |")
    add(f"| tilesPerChunk | {header.tiles_per_chunk} |")
    add(f"| start | ({header.start_x}, {header.start_y}) grid units |")
    add(f"| size | {header.chunks_x} x {header.chunks_y} grid units |")
    add(f"| nominal terrain extent | X {tminx:.0f}..{tmaxx:.0f} m, Z {tminz:.0f}..{tmaxz:.0f} m |")
    add("")
    add(f"The extent is `start` / `size` scaled by **{zoneread.GRID_UNIT_METRES:g} m per grid "
        f"unit**, which is *derived* - from the `<Zone>_<x>_<y>_0.cnk` grid in the pack index "
        f"(4 grid units per chunk file in both zones) and from the measured placement bounds "
        f"below - and is deliberately **not** `tileSize`. Reading `tileSize` (64) as the "
        f"metres-per-unit would double this figure, and `tileSize` x `tilesPerChunk` does not "
        f"reconcile with the chunk-file grid at all, so what those two fields count is left "
        f"unresolved. See `GRID_UNIT_METRES` in `tools/zone/zoneread.py`.")
    add("")
    add("## Placement extents (measured over every instance)")
    add("")
    add("| axis | min | max | span |")
    add("|---|---|---|---|")
    add(f"| X | {minx:.1f} | {maxx:.1f} | {maxx - minx:.1f} m |")
    add(f"| Y (up) | {miny:.1f} | {maxy:.1f} | {maxy - miny:.1f} m |")
    add(f"| Z | {minz:.1f} | {maxz:.1f} | {maxz - minz:.1f} m |")
    add("")
    add(f"{total:,} placed instances across {len(model_counts):,} distinct actor models.")
    add("")
    add("## Instance counts by model class")
    add("")
    add("Class = the actor file name up to its first `_` (the client's own naming scheme).")
    add("")
    add("| class | instances | models |")
    add("|---|---:|---:|")
    models_per_class: Counter[str] = Counter()
    for model in model_counts:
        models_per_class[class_of(model)] += 1
    for cls, n in by_class.most_common(30):
        add(f"| `{cls}` | {n:,} | {models_per_class[cls]} |")
    add("")
    add("## Spawn markers")
    add("")
    add(f"{len(spawners):,} spawn-marker placements.")
    add("")
    add("| marker model | instances |")
    add("|---|---:|")
    for model, n in by_model.most_common():
        add(f"| `{model}` | {n:,} |")
    add("")
    add("### By parsed tier / category")
    add("")
    add("| tier key | instances |")
    add("|---|---:|")
    for key, n in by_tier.most_common():
        add(f"| {key} | {n:,} |")
    add("")
    add("### By named area")
    add("")
    add("| area | spawners |")
    add("|---|---:|")
    for name, n in by_area.most_common(40):
        add(f"| {name} | {n:,} |")
    if len(by_area) > 40:
        add(f"| _(+{len(by_area) - 40} more areas)_ | |")
    add("")
    add("## Vehicle-named placements (scenery, not spawn points - fact V1)")
    add("")
    add("| model | instances |")
    add("|---|---:|")
    for model, n in vehicle_placements.most_common():
        add(f"| `{model}` | {n:,} |")
    add("")
    add("## Named areas (points of interest)")
    add("")
    add(f"{len(named_areas):,} designer-named areas in the companion `Areas.xml` "
        f"(everything not called `Box_<id>` / `Sphere_<id>`). Bounds are axis-aligned "
        f"before the area's `rotY` yaw.")
    add("")
    add("| name | shape | centre X,Y,Z | size X,Y,Z | rotY |")
    add("|---|---|---|---|---|")
    for a in sorted(named_areas, key=lambda r: r["name"]):
        c, s = a["center"], a["size"]
        add(f"| {a['name']} | {a['shape']} | {c[0]:.1f}, {c[1]:.1f}, {c[2]:.1f} | "
            f"{s[0]:.1f}, {s[1]:.1f}, {s[2]:.1f} | {a['rot'][1]:.4f} |")
    add("")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
