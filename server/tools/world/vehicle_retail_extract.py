"""Measure the retail v7 layout against full section boundaries and captured poses."""
import argparse
import collections
import hashlib
import json
import math
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]/'zone'))
import zoneread as zone

FAMILIES = {'Common_DPO_Vehicle_Offroader_proxy.adr':1,
            'Common_DPO_Vehicle_PickupTruck_proxy.adr':2,
            'Common_DPO_Vehicle_PoliceCar01_proxy.adr':3,
            'Common_DPO_Vehicle_ATV01_proxy.adr':5}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zone', type=pathlib.Path, required=True)
    parser.add_argument('--observations', type=pathlib.Path, required=True)
    parser.add_argument('--out', type=pathlib.Path, required=True)
    args = parser.parse_args()
    path = args.zone
    data = path.read_bytes()
    # This isolated process reuses the proven cursor/parameter-list reader.
    # V7 adds one u32 after model distance and expands instance identity to u64.
    zone.SUPPORTED_VERSIONS = (7,)
    zone._INSTANCE_FIXED = struct.Struct('<12fQBf')
    zone._INSTANCE_FIXED_SIZE = zone._INSTANCE_FIXED.size
    header = zone.read_header(data)
    start, end = {n:(a,b) for n,a,b in header.section_spans(len(data))}['objects']
    cursor = zone.Cursor(data,start,end)
    models = cursor.u32()
    extra = collections.Counter()
    records = []
    instances = 0
    for _ in range(models):
        model = cursor.string()
        distance = cursor.f32()
        extra[cursor.u32()] += 1
        for _ in range(cursor.u32()):
            instance = zone._read_instance(cursor, model, distance, True)
            instances += 1
            if 'DPO_Vehicle' not in model:
                continue
            records.append(dict(instanceId=instance.instance_id, model=model,
                vehicleId=FAMILIES.get(model), position=instance.position[:3],
                rotation=instance.rotation[:3], scale=instance.scale[:3],
                flags=instance.flag_byte, dwordParams=instance.dword_params,
                scalarParams=instance.scalar_params, vectorParams=instance.vector_params))
    zone._require_exhausted(cursor,'retail v7 objects')
    playable = [r for r in records if r['vehicleId'] is not None
                and all(abs(r['position'][i])<=4096 for i in (0,2))]
    assert len({r['instanceId'] for r in records}) == len(records)
    seen = set()
    comparisons = []
    observed = json.loads(args.observations.read_text())
    for row in observed['vehicles']:
        key = row['vehicleId'],tuple(row['position'])
        if key in seen or not row['verifiedLayout'] or not all(abs(row['position'][i])<=4096 for i in (0,2)):
            continue
        seen.add(key)
        distance, marker = min((math.dist(row['position'][::2],m['position'][::2]),m) for m in playable)
        comparisons.append(dict(frame=row['frame'],instanceId=marker['instanceId'],
            horizontalDistance=distance, heightDelta=row['position'][1]-marker['position'][1],
            observedVehicleId=row['vehicleId'], markerVehicleId=marker['vehicleId']))
    doc = dict(schema='cranberry/retail-vehicle-proxies/1',source=dict(
        file=str(path),sha256=hashlib.sha256(data).hexdigest(),zoneVersion=header.version,
        clientBuild='1.0.326.439939',steamBuildId=16699462),
        parse=dict(modelGroups=models,totalInstances=instances,objectSectionBytes=end-start,
                   fullyConsumed=True,objectHeaderExtraCounts=dict(extra)),
        counts=dict(allVehicleMarkers=len(records),playableMarkers=len(playable),
                    byVehicleId=dict(collections.Counter(r['vehicleId'] for r in playable))),
        captureComparison=dict(sha256=observed['source']['sha256'],observations=len(comparisons),
            maxHorizontalError=max(r['horizontalDistance'] for r in comparisons),records=comparisons),
        note='Official client authored markers. Marker Y is not the settled server pose; family can be substituted by retail policy. Not an August compatibility approval.',
        records=records)
    args.out.write_text(json.dumps(doc,indent=1)+'\n', encoding='utf-8')
    print(json.dumps(doc['counts']))
    print('Fully consumed',end-start,'bytes;',instances,'instances')
    print('Capture matches',len(comparisons),'max XZ error',doc['captureComparison']['maxHorizontalError'])

if __name__=='__main__':main()
