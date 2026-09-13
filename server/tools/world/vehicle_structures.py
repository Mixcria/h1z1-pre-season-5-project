"""August static-mesh triangle checks for reference vehicle placements.

Use the shipped collision actor's visual LOD0 triangles to distinguish solid walls
from empty space inside a building's enclosing bounds. This is an offline placement
check, not a replacement for the client's PhysX collision simulation.
"""
from __future__ import annotations

import math
import struct
import json
from pathlib import Path

from vehicle_geometry import axes_for, box, cross, dot, DRIVABLE_MODELS

DEFAULT_OBSTRUCTIONS = Path(__file__).with_name("data") / "august-vehicle-obstructions.json"


def load_obstructions(path=DEFAULT_OBSTRUCTIONS):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def obstructed_anchors(anchors, objects, evidence, bounds):
    """Recheck each saved witness triangle against this exact map and vehicle pose."""
    by_id = {record["id"]: record for record in objects}
    by_anchor = {a["id"]: a for a in anchors}
    rejected = set()
    for row in evidence["obstructions"]:
        anchor = by_anchor.get(row["anchor"]["id"])
        if anchor != row["anchor"]:
            raise ValueError(f"Vehicle obstruction evidence needs regeneration: anchor {row['anchor']['id']}")
        witness = row["witness"]
        if by_id.get(witness["object"]["id"]) != witness["object"]:
            raise ValueError(f"August obstruction moved for anchor {anchor['id']}; recheck its triangles")
        car = box(bounds[DRIVABLE_MODELS[anchor["vehicleId"]]],
                  tuple(anchor[k] for k in ("x", "y", "z")), (anchor["yaw"], 0, 0))
        triangle = transform_triangle(witness["localTriangle"], witness["object"])
        if not blocking_triangle(triangle, car, anchor["y"]):
            raise ValueError(f"Obstruction witness no longer intersects vehicle {anchor['id']}")
        rejected.add(anchor["id"])
    return rejected


def mesh_triangles(data):
    """Read static DMOD v4 position streams and triangle indices, validating sizes."""
    if data[:4] != b"DMOD" or struct.unpack_from("<I", data, 4)[0] != 4:
        raise ValueError("Expected static DMOD v4")
    offset = 12 + struct.unpack_from("<I", data, 8)[0] + 24
    count = struct.unpack_from("<I", data, offset)[0]
    offset += 4
    if not 0 < count < 1024:
        raise ValueError("Invalid mesh count")
    for _ in range(count):
        _, _, _, _, streams, index_size, index_count, vertex_count = struct.unpack_from("<8I", data, offset)
        offset += 32
        if not 0 < streams < 10 or index_size not in (2, 4) or index_count % 3:
            raise ValueError("Invalid static mesh streams/indices")
        positions = None
        for stream in range(streams):
            stride = struct.unpack_from("<I", data, offset)[0]
            offset += 4
            if offset + stride * vertex_count > len(data) or stride < 1:
                raise ValueError("Truncated vertex stream")
            if stream == 0:
                if stride != 12:
                    raise ValueError("Expected static float3 position stream")
                positions = [struct.unpack_from("<3f", data, offset + i * stride) for i in range(vertex_count)]
                if not all(math.isfinite(v) for p in positions for v in p):
                    raise ValueError("Non-finite mesh position")
            offset += stride * vertex_count
        indices = struct.unpack_from("<" + str(index_count) + ("H" if index_size == 2 else "I"), data, offset)
        offset += index_count * index_size
        if any(i >= vertex_count for i in indices):
            raise ValueError("Mesh index outside the position stream")
        for i in range(0, index_count, 3):
            yield tuple(positions[index] for index in indices[i:i + 3])


def transform_triangle(triangle, record):
    axes = axes_for(record["rot"])
    return tuple(tuple(record["pos"][i] + sum(axes[j][i] * p[j] * record["scale"][j]
                                             for j in range(3)) for i in range(3)) for p in triangle)


def triangle_intersects_box(triangle, vehicle_box):
    """Strict triangle/OBB SAT: surrounding bounds and boundary contact are insufficient."""
    centre, axes, extents = vehicle_box
    points = [tuple(dot(tuple(p[i] - centre[i] for i in range(3)), axis) for axis in axes)
              for p in triangle]
    unit = ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    edges = [tuple(points[(i + 1) % 3][j] - points[i][j] for j in range(3)) for i in range(3)]
    normal = cross(edges[0], edges[1])
    if dot(normal, normal) < 1e-16:
        return False
    for axis in (*unit, normal, *(cross(edge, basis) for edge in edges for basis in unit)):
        if dot(axis, axis) < 1e-16:
            continue
        values = [dot(p, axis) for p in points]
        radius = sum(extents[i] * abs(axis[i]) for i in range(3))
        if min(values) >= radius or max(values) <= -radius:
            return False
    return True


def blocking_triangle(triangle, vehicle_box, ground_y):
    # Pavement/floors are legitimate support. Ignore geometry wholly at/below the
    # authored ground origin; wheels' mesh bounds extend a few millimetres below it.
    if max(p[1] for p in triangle) <= ground_y + 0.03:
        return False
    normal = cross(tuple(triangle[1][i] - triangle[0][i] for i in range(3)),
                   tuple(triangle[2][i] - triangle[0][i] for i in range(3)))
    # Sloping pavement, garage ramps and the lower floor can support tyres. They
    # do not prove a blocked pad. A wall/post/rail or a ceiling through the body does.
    if normal[1] * normal[1] > 0.25 * dot(normal, normal) and max(p[1] for p in triangle) < vehicle_box[0][1]:
        return False
    return triangle_intersects_box(triangle, vehicle_box)


def main():
    """Regenerate obstruction witnesses from the installed August world and meshes."""
    import collections
    import sys
    import xml.etree.ElementTree as ET
    import vehicle_anchors
    from vehicle_geometry import load_bounds, overlaps
    from vehicle_stations import load_stations
    from vehicle_terrain import VehicleTerrain
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "pack"))
    import packread

    anchors, _, _ = vehicle_anchors.scan_anchors(vehicle_anchors.load_areas(), terrain=VehicleTerrain(),
                                                stations=load_stations())
    grid = collections.defaultdict(list)
    for anchor in anchors:
        grid[int(anchor["x"] // 64), int(anchor["z"] // 64)].append(anchor)
    neighbours = collections.defaultdict(list)
    with open(vehicle_anchors.OBJECTS, encoding="utf-8") as handle:
        for line in handle:
            record = json.loads(line)
            if not any(part in record["model"] for part in ("Structures", "Combined_Meshes", "Fence", "ModularWall", "ConcreteBarrier", "Rock")):
                continue
            x, y, z = record["pos"]
            cx, cz = int(x // 64), int(z // 64)
            for dx in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for anchor in grid[cx + dx, cz + dz]:
                        if math.hypot(anchor["x"] - x, anchor["z"] - z) < 50 and abs(anchor["y"] - y) < 20:
                            neighbours[anchor["id"]].append(record)
    entries, _ = packread.build_index(packread.find_packs(packread.DEFAULT_ASSETS_DIR))
    index = {entry.name: entry for entry in entries}
    bounds = load_bounds()
    actors = {}
    meshes = {}
    facts = {}
    for name in sorted({r["model"] for rows in neighbours.values() for r in rows}):
        actor = ET.fromstring(packread.read_asset_bytes(index[name]))
        base = actor.find("Base")
        mesh = base.get("fileName") if base is not None else None
        if mesh not in index or actor.find("CollisionData") is None:
            continue
        data = packread.read_asset_bytes(index[mesh])
        bounds[name] = struct.unpack_from("<6f", data, 12 + struct.unpack_from("<I", data, 8)[0])
        actors[name] = data
        facts[name] = dict(mesh=mesh, crc32=f"{index[mesh].crc32:08x}")
    confirmed, clear = [], []
    for anchor in anchors:
        car = box(bounds[DRIVABLE_MODELS[anchor["vehicleId"]]],
                  tuple(anchor[k] for k in ("x", "y", "z")), (anchor["yaw"], 0, 0))
        candidates = [r for r in neighbours[anchor["id"]] if r["model"] in actors
                      and not any(k in r["model"] for k in ("Road_", "Sidewalk_", "Parking_", "Bridge_", "Floor", "Ceiling", "Stairs"))
                      and overlaps(car, box(bounds[r["model"]], r["pos"], r["rot"], r["scale"]))]
        witness = None
        for record in candidates:
            name = record["model"]
            if name not in meshes:
                meshes[name] = list(mesh_triangles(actors[name]))
                facts[name]["triangles"] = len(meshes[name])
            for triangle_index, triangle in enumerate(meshes[name]):
                if blocking_triangle(transform_triangle(triangle, record), car, anchor["y"]):
                    witness = dict(object=record, mesh=facts[name], triangleIndex=triangle_index, localTriangle=triangle)
                    break
            if witness:
                break
        if witness:
            confirmed.append(dict(anchor=anchor, witness=witness))
        elif candidates:
            clear.append(anchor["id"])
    document = dict(schema="cranberry/august-vehicle-obstructions/1",
                    note="Retained reference pads whose vehicle mesh envelope intersects actual August collision-actor visual LOD0 wall/fence/post/roof triangles. A witness triangle, mesh CRC and exact world instance/vehicle transforms make each exclusion reproducible. Empty building bounding boxes and lower support surfaces are insufficient. This is conservative visual-mesh placement validation, not a PhysX collision-mesh simulation.",
                    obstructions=confirmed, enclosingBoundsOnlyRetained=clear)
    DEFAULT_OBSTRUCTIONS.write_text(json.dumps(document, indent=1) + "\n", encoding="utf-8")
    print(f"Structural obstructions: {len(confirmed)}; enclosing bounds/support only retained: {len(clear)}")


if __name__ == "__main__":
    main()
