"""Two later-retail Z2 observations adopted into the August server's layout.

This is a limited server placement decision, not recovery of the full August
retail catalogue. See docs/vehicle-spawns-capture-20260911.md.
"""
import hashlib
import json
import math
from pathlib import Path
from vehicle_geometry import box, overlaps, DRIVABLE_MODELS

DATA = Path(__file__).with_name('data')


def load_captured():
    observations = json.loads((DATA / 'z1br-vehicle-observations.json').read_text(encoding='utf-8'))
    adoption = json.loads((DATA / 'z1br-vehicle-august-adoption.json').read_text(encoding='utf-8'))
    return observations, adoption


def apply_captured(anchors, boxes, obstacles, bounds, terrain, world_file, evidence=None):
    observations, adoption = evidence or load_captured()
    if observations['schema'] != 'cranberry/captured-vehicle-observations/1':
        raise ValueError('Unsupported captured vehicle observations')
    if observations['source']['sha256'] != adoption['captureSha256']:
        raise ValueError('Vehicle capture changed; review its adoption')
    # Negative geometry checks are specific to this world. A changed world must
    # be re-audited, including the farmhouse and barn's actual triangles.
    if hashlib.sha256(Path(world_file).read_bytes()).hexdigest() != adoption['augustWorldSha256']:
        raise ValueError('August world changed; repeat captured vehicle geometry audit')
    result = list(anchors)
    for rule in adoption['placements']:
        matches = [r for r in observations['vehicles'] if r['frame'] == rule['frame']]
        if len(matches) != 1:
            raise ValueError('Captured vehicle frame must identify one observation')
        record = matches[0]
        p, q = record['position'], record['rotation']
        if (record['zone'] != 'Z2' or len(p) != 3 or len(q) != 4
                or not all(math.isfinite(v) for v in (*p, *q))
                or not all(-4096 <= p[i] <= 4096 for i in (0, 2))):
            raise ValueError('Captured vehicle is not on the main Z2 map')
        if record != rule['observation']:
            raise ValueError('Captured pose changed; repeat August geometry audit')
        if abs(q[0]) > 1e-6 or abs(q[2]) > 1e-6 or abs(sum(v*v for v in q)-1) > 1e-5:
            raise ValueError('Captured vehicle requires more than a yaw-only anchor')
        # The separate vector in the 1315 lightweight record is NOT authoritative
        # here: it contains zero on the main-map ATV despite a 120-degree quaternion.
        yaw = 2 * math.atan2(q[1], q[3])
        vehicle_id = record['vehicleId']
        if {1:10060, 2:10084, 3:10119, 5:9588}.get(vehicle_id) != record['modelId']:
            raise ValueError('Captured vehicle family/model mismatch')
        car = box(bounds[DRIVABLE_MODELS[vehicle_id]], p, (yaw, 0, 0))
        if any(overlaps(car, obstacle) for obstacle in obstacles):
            raise ValueError('Captured vehicle overlaps static August scenery')
        if terrain is not None and terrain.is_buried(bounds[DRIVABLE_MODELS[vehicle_id]], p, yaw):
            raise ValueError('Captured vehicle is buried in August terrain')
        if 'replaces' in rule:
            old = [a for a in result if a['id'] == rule['id']]
            if old != [rule['replaces']]:
                raise ValueError('Legacy vehicle selected for replacement changed')
            result = [a for a in result if a['id'] != rule['id']]
        elif any(a['id'] == rule['id'] for a in result):
            raise ValueError('Captured vehicle reuses an existing anchor id')
        for other in result:
            if overlaps(car, box(bounds[DRIVABLE_MODELS[other['vehicleId']]],
                                 [other[k] for k in ('x','y','z')], (other['yaw'],0,0))):
                raise ValueError('Captured vehicle overlaps another planned vehicle')
        area = next((name for name,x0,z0,x1,z1 in boxes if x0 <= p[0] <= x1 and z0 <= p[2] <= z1), None)
        result.append(dict(id=rule['id'], x=p[0], y=p[1], z=p[2], yaw=yaw, spaces=1,
                           area=area, vehicleId=vehicle_id, capturedFrame=record['frame'],
                           placementSource='Z1BR-1315-observed-August-adoption'))
    return sorted(result, key=lambda a:a['id'])
