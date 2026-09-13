"""Load the whole-map retail catalogue with the separately audited August placements.

The public generator has no fallback to the retired 403-point reference, guessed
parking bays, or route-only capture replacements. Every retail marker must have
exactly one explicit accept/exclude decision before this loader can emit a map.
"""
from __future__ import annotations

from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import struct

DATA = Path(__file__).with_name('data')
CATALOGUE = DATA / 'z1br-retail-vehicle-markers.json'
PLACEMENTS = DATA / 'z1br-retail-august-placements.json'
RETAIL_ZONE_SHA256 = '035d568bd6ad820dc2c7001541426a87cb6ac674f024e2be8423961490a972ca'


def sha(path):
    with Path(path).open('rb') as handle:
        return hashlib.file_digest(handle, 'sha256').hexdigest()


def f32(value):
    return struct.unpack('<f', struct.pack('<f', value))[0]


def validate(catalogue, audit, catalogue_sha):
    if (catalogue['schema'] != 'cranberry/retail-vehicle-proxies/1'
            or catalogue['source']['sha256'] != RETAIL_ZONE_SHA256
            or catalogue['source']['zoneVersion'] != 7
            or not catalogue['parse']['fullyConsumed']):
        raise ValueError('Unreviewed retail zone source or incomplete extraction')
    if (audit['schema'] != 'cranberry/retail-august-placement-audit/1'
            or audit['catalogueSha256'] != catalogue_sha):
        raise ValueError('Retail catalogue changed since the August geometry audit')
    all_rows = catalogue['records']
    if len({r['instanceId'] for r in all_rows}) != len(all_rows):
        raise ValueError('Duplicate retail instance identity')
    rows = {r['instanceId']: r for r in all_rows
            if all(abs(r['position'][i]) <= 4096 for i in (0, 2))}
    if any(r['vehicleId'] not in (1, 2, 3, 5) for r in rows.values()):
        raise ValueError('Unsupported playable retail vehicle marker')
    if (catalogue['counts']['allVehicleMarkers'] != len(all_rows)
            or catalogue['counts']['playableMarkers'] != len(rows)
            or {str(k): v for k, v in Counter(r['vehicleId'] for r in rows.values()).items()}
            != catalogue['counts']['byVehicleId']):
        raise ValueError('Retail catalogue count mismatch')
    decisions = audit['decisions']
    if (len({r['instanceId'] for r in decisions}) != len(decisions)
            or {r['instanceId'] for r in decisions} != set(rows)):
        raise ValueError('Every playable retail marker needs exactly one geometry decision')
    for decision in decisions:
        marker = rows[decision['instanceId']]
        position, rotation = decision['position'], decision['rotation']
        if (not 0 < marker['instanceId'] <= 0xffffffff
                or marker['scale'] != [1, 1, 1]
                or any(marker[k] for k in ('dwordParams', 'scalarParams', 'vectorParams'))):
            raise ValueError('Unsupported retail instance transform or parameter overrides')
        if (len(position) != 3 or len(rotation) != 3
                or not all(math.isfinite(v) for v in (*position, *rotation))
                or decision['editorPosition'] != marker['position']
                or rotation != marker['rotation']
                or decision['vehicleId'] != marker['vehicleId']
                or position[::2] != marker['position'][::2]):
            raise ValueError('A geometry decision changed retail X/Z, orientation or family')
        if not decision['reasons']:
            support = decision['support']
            if (support['kind'] not in ('solid-terrain', 'August visual mesh')
                    or support['height'] is None or not math.isfinite(support['height'])
                    or position[1] != f32(support['height'] + 0.1)):
                raise ValueError('Accepted placement lacks its audited August support height')
    accepted = [r for r in decisions if not r['reasons']]
    expected = {'source': len(rows), 'accepted': len(accepted), 'excluded': len(rows)-len(accepted)}
    if audit['counts'] != expected or not accepted or audit['interVehicleConflicts']:
        raise ValueError('Invalid geometry counts or unresolved inter-vehicle conflicts')
    return sorted(accepted, key=lambda r: r['instanceId'])


def load_anchors(boxes, world, terrain, bounds, catalogue_path=CATALOGUE, audit_path=PLACEMENTS):
    catalogue = json.loads(Path(catalogue_path).read_text(encoding='utf-8'))
    audit = json.loads(Path(audit_path).read_text(encoding='utf-8'))
    for field, path in (('augustWorldSha256', world), ('terrainSha256', terrain), ('boundsSha256', bounds)):
        if sha(path) != audit[field]:
            raise ValueError(f'{field}: August geometry changed; regenerate and review the audit')
    decisions = validate(catalogue, audit, sha(catalogue_path))
    anchors = []
    for row in decisions:
        x, y, z = row['position']
        area = next((name for name, min_x, min_z, max_x, max_z in boxes
                     if min_x <= x <= max_x and min_z <= z <= max_z), None)
        anchors.append(dict(id=row['instanceId'], x=x, y=y, z=z,
                            yaw=row['rotation'][0], pitch=row['rotation'][1], roll=row['rotation'][2],
                            spaces=1, area=area, vehicleId=row['vehicleId']))
    provenance = dict(retailSource=catalogue['source'], retailCounts=catalogue['counts'],
                      augustCompatibility=audit['counts'], catalogueSha256=sha(catalogue_path),
                      placementAuditSha256=sha(audit_path))
    return anchors, provenance
