#!/usr/bin/env python3
"""Build the August vehicle roster and the approved whole-map retail spawn set.

Retail Z2.zone supplies every X/Z, orientation and marker family. A complete,
hashed geometry audit supplies August parking heights and explicit exclusions.
The old reference scanner below is retained only to reproduce historical audits;
it is never called by this generator. See docs/vehicle-spawns-retail-20260912.md.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from vehicle_geometry import STATIC_MODELS, DRIVABLE_MODELS, box, load_bounds, overlaps
from vehicle_terrain import VehicleTerrain
from vehicle_stations import load_stations, station_anchors
from vehicle_structures import load_obstructions, obstructed_anchors
from vehicle_captured import apply_captured
from vehicle_retail import load_anchors as load_retail_anchors
from vehicle_terrain import DEFAULT_TERRAIN
from vehicle_geometry import DEFAULT_BOUNDS

# --- inputs -----------------------------------------------------------------------------------

OUT_ROOT = r"C:\Aug2017\out"
OBJECTS = os.path.join(OUT_ROOT, "world_aug", "z2-objects.jsonl")
AREAS = os.path.join(OUT_ROOT, "world_aug", "z2-areas.json")
VEHICLES = os.path.join(OUT_ROOT, "data_aug", "derived", "vehicles.json")
MODELS = os.path.join(OUT_ROOT, "data_aug", "Models.txt")
LOCATIONS = os.path.join(os.path.dirname(__file__), "data", "z2-vehicle-locations.json")

# Includes abandoned trucks/SUVs/vans and semi cabs, missed by the former six-name
# radius filter. Bounds come from shipped meshes, not made-up vehicle clearances.
STATIC_VEHICLES = STATIC_MODELS

# The four drivable land vehicles.  15/16 are the *Ignition match-mode* variants and 13/1337 are the
# parachute and the observer, so none of them belongs in a BR car park (docs/43 sec.2.1).
DRIVABLE_IDS = (1, 2, 3, 5)

# Invisible DPO markers, not scenery: their shipped ADR has Invisible=1. Only two
# PoliceCar proxies survive in August Z2. One moved 6.19 m since the reference map.
# A nearby same-name reference row supplies identity, never the final transform.
VEHICLE_PROXIES = {
    "Common_DPO_Vehicle_Offroader_proxy.adr": 1,
    "Common_DPO_Vehicle_PickupTruck_proxy.adr": 2,
    "Common_DPO_Vehicle_PoliceCar01_proxy.adr": 3,
    "Common_DPO_Vehicle_ATV_proxy.adr": 5,
}
PROXY_REFERENCE_MATCH_METRES = 10.0

MODEL_FILE_BY_VEHICLE = {
    1: "Common_OffRoader_Tintable.adr",
    2: "Common_PickupTruck.adr",
    3: "Common_Vehicle_PoliceCar01.adr",
    5: "Vehicle_Common_ATV01.adr",
}


def load_areas():
    """The named ``Loot.<Place>.NN`` boxes, as (name, minX, minZ, maxX, maxZ)."""
    with open(AREAS, "r", encoding="utf-8") as handle:
        areas = json.load(handle)

    boxes = []
    for area in areas:
        name = area.get("name", "")
        if not name.startswith("Loot."):
            continue
        low, high = area["min"], area["max"]
        # "Loot.PVResidential.03" -> "PVResidential": the planner caps per *place*, not per box.
        parts = name.split(".")
        place = parts[1] if len(parts) > 1 else name
        boxes.append((place, low[0], low[2], high[0], high[2]))
    return boxes


def load_models():
    """``Models.txt`` row id by ``.adr`` file name."""
    ids = {}
    with open(MODELS, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if line.startswith("#"):
                continue
            cells = line.rstrip("\r\n").split("^")
            if len(cells) > 1 and cells[0].isdigit():
                ids[cells[1]] = int(cells[0])
    return ids


def scan_anchors(boxes, *, terrain=None, stations=None, obstructions=None, captured=False):
    """Retired reference path: used only for historical regression/audit reproduction."""
    with open(LOCATIONS, encoding="utf-8") as handle:
        locations = json.load(handle)
    obstacles = []
    proxies = []
    parking = []
    parking_ids = {s["parkingInstanceId"] for s in stations["spawns"]} if stations else set()
    obstruction_objects = []
    obstruction_ids = {r["witness"]["object"]["id"] for r in obstructions["obstructions"]} if obstructions else set()
    obstruction_models = {r["witness"]["object"]["model"] for r in obstructions["obstructions"]} if obstructions else set()
    bounds = load_bounds()
    with open(OBJECTS, encoding="utf-8") as handle:
        for line in handle:
            if not any(model in line for model in (*STATIC_VEHICLES, *VEHICLE_PROXIES,
                                                   *obstruction_models,
                                                   *((stations["parkingModel"],) if stations else ()))):
                continue
            record = json.loads(line)
            if record.get("id") in parking_ids:
                parking.append(record)
            if record.get("id") in obstruction_ids:
                obstruction_objects.append(record)
            if record["model"] not in STATIC_VEHICLES and record["model"] not in VEHICLE_PROXIES:
                continue
            if record["model"] in VEHICLE_PROXIES:
                proxies.append(record)
                continue
            obstacles.append(box(bounds[record["model"]], record["pos"],
                                 record.get("rot", (0, 0, 0)), record.get("scale", (1, 1, 1))))
    overrides = {}
    for proxy in proxies:
        candidates = [record for record in locations
                      if record["name"] + ".adr" == proxy["model"]
                      and record["vehicleId"] == VEHICLE_PROXIES[proxy["model"]]
                      and sum((a-b)**2 for a, b in zip(record["position"][:3], proxy["pos"]))
                      < PROXY_REFERENCE_MATCH_METRES**2]
        if len(candidates) != 1 or candidates[0]["id"] in overrides:
            raise ValueError(f"August proxy {proxy['id']} cannot be uniquely joined to a reference location")
        overrides[candidates[0]["id"]] = proxy
    anchors = []
    rejected = []
    buried = []
    for record in locations:
        proxy = overrides.get(record["id"])
        x, y, z = proxy["pos"] if proxy else record["position"][:3]
        yaw = proxy["rot"][0] if proxy else record["orientation"]
        candidate = box(bounds[DRIVABLE_MODELS[record["vehicleId"]]], (x, y, z), (yaw, 0, 0))
        if any(overlaps(candidate, obstacle) for obstacle in obstacles):
            rejected.append(record["id"])
            continue
        if terrain is not None and terrain.is_buried(
                bounds[DRIVABLE_MODELS[record["vehicleId"]]], (x, y, z), yaw):
            buried.append(record["id"])
            continue
        place = next((name for name, x0, z0, x1, z1 in boxes
                      if x0 <= x <= x1 and z0 <= z <= z1), None)
        anchor = dict(id=record["id"] + 1, x=x, y=y, z=z,
                      yaw=yaw, spaces=1, area=place, vehicleId=record["vehicleId"])
        if proxy:
            anchor["augustProxyInstanceId"] = proxy["id"]
        anchors.append(anchor)
    print(f"Authored vehicle points: {len(locations)}; static-prop exclusions: {len(rejected)} {rejected}")
    print(f"Exact August proxy transforms: {len(overrides)} {sorted(overrides)}")
    print(f"Fully buried under solid August terrain: {len(buried)} {buried}")
    if obstructions:
        blocked = obstructed_anchors(anchors, obstruction_objects, obstructions, bounds)
        anchors = [a for a in anchors if a["id"] not in blocked]
        print(f"August structural triangle obstructions: {len(blocked)} {sorted(blocked)}")
    if stations:
        corrections = station_anchors(stations, parking, boxes, obstacles, bounds, terrain)
        if {a["id"] for a in corrections} & {r["id"] + 1 for r in locations}:
            raise ValueError("Police-station corrections reuse a reference vehicle id")
        anchors.extend(corrections)
        print(f"August police-station omissions filled: {len(corrections)}")
    if captured:
        anchors = apply_captured(anchors, boxes, obstacles, bounds, terrain, OBJECTS)
        print('Later-retail Z2 capture adoption: one West Peaks jeep moved, one ATV added')
    anchors.sort(key=lambda a: a["id"])
    return anchors, len(anchors), sum(a["area"] is not None for a in anchors)


def build_roster(model_ids):
    with open(VEHICLES, "r", encoding="utf-8") as handle:
        derived = json.load(handle)

    constants = derived["shared"]["constants"]["server"]
    roster = []

    for entry in derived["vehicles"]:
        if entry["id"] not in DRIVABLE_IDS:
            continue

        model_file = MODEL_FILE_BY_VEHICLE[entry["id"]]
        seats = []
        by_seat_info = {seat["seatInfoId"]: seat["index"] for seat in entry["seats"]}
        for seat in entry["seats"]:
            loop = seat.get("loopNextSeatInfoId")
            seats.append(
                {
                    "index": seat["index"],
                    "seatInfoId": seat["seatInfoId"],
                    "isDriver": bool(seat["isDriver"]),
                    "canFire": bool(seat["canFire"]),
                    "canBail": bool(seat["canBail"]),
                    "enclosed": bool(seat["enclosed"]),
                    "kickCorpse": bool(seat["kickCorpse"]),
                    "loopNextSeatIndex": by_seat_info.get(loop, -1) if loop else -1,
                }
            )
        seats.sort(key=lambda s: s["index"])

        modes = []
        for mode in entry["drive"]["modes"]:
            modes.append(
                {
                    "ordinal": mode["ordinal"],
                    "moveInfoId": mode["moveInfoId"],
                    "movementMode": mode["movementMode"],
                    "maxForward": mode["maxForward"],
                    "estimatedMaxSpeed": mode["estimatedMaxSpeed"],
                }
            )
        modes.sort(key=lambda m: m["ordinal"])

        world = entry["world"]
        roster.append(
            {
                "id": entry["id"],
                "name": entry["name"],
                "nameId": entry["nameStringId"],
                "modelId": model_ids[model_file],
                "modelFile": model_file,
                "destroyedModelId": entry["destruction"]["destroyedModelId"],
                "physicsFamily": entry["physicsFamily"]["base"],
                "decaySeconds": world["decaySeconds"],
                # 0 on all four land rows, so Vehicle.DefaultMaxDismountSpeed applies (docs/43 4.6).
                "maxDismountSpeed": world["maxDismountSpeedOverride"] or constants[
                    "Vehicle.DefaultMaxDismountSpeed"
                ],
                "seats": seats,
                "driveModes": modes,
            }
        )

    roster.sort(key=lambda v: v["id"])
    return roster, constants


def main() -> int:
    default_out = os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "src",
        "Cranberry.Zone",
        "Data",
        "Vehicles",
    )
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", default=default_out)
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)

    model_ids = load_models()
    boxes = load_areas()
    anchors, provenance = load_retail_anchors(boxes, OBJECTS, DEFAULT_TERRAIN, DEFAULT_BOUNDS)
    spaces = len(anchors)
    inside = sum(a['area'] is not None for a in anchors)
    roster, constants = build_roster(model_ids)

    anchor_doc = {
        "schema": "cranberry/vehicle-anchors/1",
        "note": (
            "550 of 591 playable retail Z2.zone v7 vehicle markers, covering the whole map. "
            "All X/Z coordinates, orientations and marker families are exact source values. "
            "41 locations are explicitly excluded for August terrain/object conflicts. "
            "Y is an August adaptation: downward solid-terrain/visual-mesh support plus 0.1 m; "
            "it is not a recovered retail settled Y or a full PhysX simulation. "
            "All 591 markers and individual decisions are retained in tools/world/data. "
            "This completely replaces the old reference and server-selected station additions. "
            "Retail family substitution and occupancy are separate from marker locations; "
            "the existing policy occupies every accepted marker with its authored family. "
            "See docs/vehicle-spawns-retail-20260912.md."
        ),
        "source": provenance,
        "generator": "Server/tools/world/vehicle_anchors.py",
        "counts": {
            "anchors": len(anchors),
            "spaces": spaces,
            "spacesInNamedAreas": inside,
            "namedAreas": len({a["area"] for a in anchors if a["area"]}),
        },
        "anchors": anchors,
    }

    roster_doc = {
        "schema": "cranberry/vehicle-roster/1",
        "note": (
            "The four drivable land vehicles of a BR match and their seats, from the client's own "
            "datasheets via out/data_aug/derived/vehicles.json plus Models.txt. Vehicles 15/16 are "
            "the Ignition match-mode variants and are excluded (docs/43 sec.2.1)."
        ),
        "generator": "Server/tools/world/vehicle_anchors.py",
        "constants": {
            "interactionCooldownMs": constants["VehicleInteractionCooldownMs"],
            "seatSwapCooldownMs": constants["VehicleSeatSwapCooldownMs"],
            "defaultMaxDismountSpeed": constants["Vehicle.DefaultMaxDismountSpeed"],
            "defaultMinDismountDamageSpeed": constants["Vehicle.DefaultMinDismountDamageSpeed"],
        },
        "vehicles": roster,
    }

    for name, doc in (("z2-vehicle-anchors.json", anchor_doc), ("vehicle-roster.json", roster_doc)):
        path = os.path.join(args.out, name)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(doc, handle, indent=1)
            handle.write("\n")
        print(f"{path}: {os.path.getsize(path):,} bytes")

    print(
        f"anchors={len(anchors):,} spaces={spaces:,} inNamedAreas={inside:,} "
        f"areas={anchor_doc['counts']['namedAreas']} vehicles={len(roster)}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
