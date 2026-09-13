"""Read-only, numeric gas evidence from the immutable September 12 Z1BR capture.

1315's observed BB/01 body has an extra word compared with August CE/01. This
reader is an evidence tool, never a packet translator or retail population formula.
"""
import argparse
import json
import math
from pathlib import Path
import struct

from capture_source import load_capture


def gas_record(data):
    if len(data) < 4 or data[0] & 31 not in (5, 6) or data[0] >> 5 == 2 or data[1] != 0xbb:
        return None
    sub = int.from_bytes(data[2:4], 'little')
    if sub in (1, 2):
        expected = 36 if sub == 1 else 24
        if len(data) != expected:
            raise ValueError(f'Unexpected BB/{sub:02x} length {len(data)}')
        x, y, z, w, radius = struct.unpack_from('<5f', data, 4)
        if not all(math.isfinite(v) for v in (x, y, z, w, radius)) or w != 1 or not 0 < radius <= 12000:
            raise ValueError('Invalid gas geometry')
        row = dict(kind='boundary' if sub == 1 else 'target', centre=[x, y, z], radius=radius)
        if sub == 1:
            row['trailingWords'] = list(struct.unpack_from('<3I', data, 24))
        return row
    if sub == 11 and len(data) == 12:
        return dict(kind='population', counts=list(struct.unpack_from('<2I', data, 4)))
    if sub == 16 and len(data) >= 20:
        # Only the fixed numeric timer prefix. Do not export strings or identities.
        return dict(kind='countdown', milliseconds=struct.unpack_from('<I', data, 8)[0])
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    audit, integrity, decoded = load_capture()
    rows = []
    for stream, frame, time, direction, data, reliable in decoded:
        if direction != 's2c' or stream != 11:
            continue
        row = gas_record(data)
        if row is not None:
            rows.append(dict(frame=frame, utc=audit.utc(time), timestamp=time, **row))
    result = dict(captureSha256=integrity['captureSha256'], sourceProtocol=1315,
        targetProtocol=1148, truncatedFrames=integrity['truncatedFrames'], events=rows,
        limits=['One two-player match; no measured ten-player or full-population formula.',
                'Opening initialization updates are not elapsed gas phases.',
                'The extra 1315 boundary word has not been assigned August semantics.'])
    args.out.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(dict(events=len(rows), targets=[r['radius'] for r in rows if r['kind']=='target'],
        populations=[r['counts'] for r in rows if r['kind']=='population'])))


if __name__ == '__main__':
    main()
