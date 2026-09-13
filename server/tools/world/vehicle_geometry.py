"""Conservative vehicle-mesh overlap from August ADR/DME bounds.

This is an oriented bounding-box exclusion, not a replacement physics engine.
Bounding boxes may reject space inside a hollow mesh, but unlike the old radius
test they respect length, width, heading, height, scale and the candidate family.
Run this module to regenerate the small bounds input from the installed client.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET

DEFAULT_BOUNDS = Path(__file__).with_name("data") / "august-vehicle-mesh-bounds.json"
STATIC_MODELS = (
    "Common_Props_AbandonedSedan.adr", "Common_Props_AbandonedSUV.adr",
    "Common_Props_AbandonedTruck.adr", "Common_Props_AbandonedVan.adr",
    "Industrial_Props_CrushedSedan.adr", "Common_Props_WreckedCar01.adr",
    "Vehicles_Camper01.adr", "Hospital_Props_AmbulanceWrecked.adr",
    "Industrial_Props_Vehicles_Forklift01.adr", "Common_Props_SemiVolvo_Body.adr",
)
DRIVABLE_MODELS = {
    1: "Common_OffRoader_Tintable.adr", 2: "Common_PickupTruck.adr",
    3: "Common_Vehicle_PoliceCar01.adr", 5: "Vehicle_Common_ATV01.adr",
}


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def axes_for(rotation):
    # Same ZONE heading/pitch/roll conversion as the owner's ZoneWorldObjects.
    # Pure yaw is (0, sin(yaw/2), 0, cos(yaw/2)), also the live spawn quaternion.
    heading, attitude, bank = rotation[0], rotation[1], -rotation[2]
    c1, s1 = math.cos(heading/2), math.sin(heading/2)
    c2, s2 = math.cos(attitude/2), math.sin(attitude/2)
    c3, s3 = math.cos(bank/2), math.sin(bank/2)
    x = c1*s2*c3-s1*c2*s3
    y = s1*c2*c3+c1*s2*s3
    z = -(c1*c2*s3+s1*s2*c3)
    w = c1*c2*c3-s1*s2*s3
    return ((1-2*(y*y+z*z), 2*(x*y+z*w), 2*(x*z-y*w)),
            (2*(x*y-z*w), 1-2*(x*x+z*z), 2*(y*z+x*w)),
            (2*(x*z+y*w), 2*(y*z-x*w), 1-2*(x*x+y*y)))


def box(bounds, position, rotation=(0, 0, 0), scale=(1, 1, 1)):
    axes = axes_for(rotation)
    local_center = [(bounds[i]+bounds[i+3])/2*scale[i] for i in range(3)]
    extents = [(bounds[i+3]-bounds[i])/2*abs(scale[i]) for i in range(3)]
    center = tuple(position[i]+sum(axes[j][i]*local_center[j] for j in range(3)) for i in range(3))
    return center, axes, extents


def overlaps(left, right):
    """Separating-axis test for two OBBs; mere boundary contact is not overlap."""
    a, ax, ae = left
    b, bx, be = right
    delta = tuple(x-y for x, y in zip(b, a))
    # A cheap enclosing-sphere rejection keeps the 403-point offline join small.
    if dot(delta, delta) > (math.sqrt(dot(ae, ae))+math.sqrt(dot(be, be)))**2:
        return False
    for axis in (*ax, *bx, *(cross(x, y) for x in ax for y in bx)):
        if dot(axis, axis) < 1e-12:
            continue
        reach = sum(extent*abs(dot(local, axis)) for extent, local in zip(ae, ax))
        reach += sum(extent*abs(dot(local, axis)) for extent, local in zip(be, bx))
        if abs(dot(delta, axis)) >= reach:
            return False
    return True


def load_bounds(path=DEFAULT_BOUNDS):
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    return {name: row["bounds"] for name, row in doc["models"].items()}


def main():
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "pack"))
    import packread
    entries, _ = packread.build_index(packread.find_packs(packread.DEFAULT_ASSETS_DIR))
    index = {entry.name: entry for entry in entries}
    models = {}
    for name in sorted((*STATIC_MODELS, *DRIVABLE_MODELS.values())):
        actor = ET.fromstring(packread.read_asset_bytes(index[name]))
        mesh = actor.find("Base").get("fileName")
        collision = actor.find("CollisionData")
        if collision is None:
            raise ValueError(f"{name} is not an authored collision actor")
        data = packread.read_asset_bytes(index[mesh])
        if data[:4] != b"DMOD":
            raise ValueError(f"{mesh}: not DMOD")
        version, material_length = struct.unpack_from("<II", data, 4)
        bounds = struct.unpack_from("<6f", data, 12+material_length)
        if not all(math.isfinite(v) for v in bounds) or any(bounds[i] >= bounds[i+3] for i in range(3)):
            raise ValueError(f"{mesh}: invalid bounds")
        models[name] = dict(mesh=mesh, version=version, meshCrc32=f"{index[mesh].crc32:08x}",
                            bounds=bounds, collisionFile=collision.get("fileName"))
    doc = dict(schema="cranberry/august-vehicle-mesh-bounds/1",
               source="August client Resources/Assets: ADR Base -> DMOD header + material block -> six f32 bounds",
               generator="tools/world/vehicle_geometry.py", models=models)
    DEFAULT_BOUNDS.write_text(json.dumps(doc, indent=1)+"\n", encoding="utf-8")
    print(f"{DEFAULT_BOUNDS}: {len(models)} client vehicle meshes")


if __name__ == "__main__":
    main()
