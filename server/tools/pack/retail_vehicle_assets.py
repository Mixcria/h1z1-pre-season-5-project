"""Read-only inspection of installed retail pack2 indexes and named assets."""
import argparse
import hashlib
import json
import pathlib
import re
import struct
import zlib

ASSETS = pathlib.Path(r'C:\Program Files (x86)\Steam\steamapps\common\H1Z1\Resources\Assets')
NAMES = ('Z2.zone', 'Models.txt', 'Vehicles.txt', 'VehicleSpawnMappings.txt',
         'SpawnInfo.txt', 'VehicleSets.txt', 'Z2_vehicleLocations.json',
         'Z2VehicleSpawns.xml', 'Z2VehicleSpawners.xml', 'Z2Areas.xml',
         '{NAMELIST}', 'NAMELIST')

def name_hash(name):
    value = (1 << 64) - 1
    for char in name.upper().encode('ascii'):
        value ^= char
        for _ in range(8):
            value = (value >> 1) ^ (0x95AC9329AC4BC9B5 if value & 1 else 0)
    # Measured against all installed indexes: the reflected Jones variant
    # matches these names only WITH the final all-ones xor.
    return value ^ ((1 << 64) - 1)

def read_asset(handle, offset, size, compressed, crc):
    handle.seek(offset)
    data = handle.read(size)
    if len(data) != size:
        raise ValueError('Truncated asset')
    if zlib.crc32(data) != crc:
        raise ValueError('Stored asset CRC mismatch')
    if compressed:
        magic, expected = struct.unpack_from('>II', data)
        if magic != 0xA1B2C3D4:
            raise ValueError('Unsupported compression preamble')
        data = zlib.decompress(data[8:])
        if len(data) != expected:
            raise ValueError('Decompressed length mismatch')
    return data

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=pathlib.Path, default=ASSETS)
    parser.add_argument('--out', type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    wanted = {name_hash(n): n for n in NAMES}
    out = args.out/'retail-assets'
    out.mkdir(exist_ok=True)
    report = {'source': str(args.assets), 'hash': 'Jones reflected / uppercase / init and xor all-ones',
              'packsChecked': 0, 'entriesChecked': 0, 'assets': []}
    names = set()
    for path in sorted(args.assets.glob('*.pack2')):
        with path.open('rb') as handle:
            header = handle.read(64)
            if header[:4] != b'PAK\x01':
                raise ValueError(f'Unsupported pack: {path.name}')
            count, length, table = struct.unpack_from('<IQQ', header, 4)
            if length != path.stat().st_size or table + count * 32 != length:
                raise ValueError('Invalid pack index bounds')
            handle.seek(table)
            entries = list(struct.iter_unpack('<QQQII', handle.read(count*32)))
            report['packsChecked'] += 1
            report['entriesChecked'] += count
            for key, offset, size, compressed, crc in entries:
                if key not in wanted:
                    continue
                if offset + size > table or compressed not in (0,1):
                    raise ValueError('Invalid asset bounds/flags')
                data = read_asset(handle, offset, size, compressed, crc)
                name = wanted[key]
                if name == '{NAMELIST}':
                    names.update(line.strip() for line in data.decode('ascii').splitlines() if line.strip())
                    name = path.stem+'-namelist.txt'
                target = out/name
                if target.exists() and target.read_bytes() != data:
                    raise ValueError(f'Ambiguous named asset {name}')
                target.write_bytes(data)
                report['assets'].append({'name': name, 'pack': path.name, 'bytes':len(data),
                    'sha256':hashlib.sha256(data).hexdigest(), 'storedCrc32':f'{crc:08x}',
                    'storedCrcVerified':True, 'decodedCrc32':f'{zlib.crc32(data):08x}'})
    report['namesEnumerated'] = len(names)
    report['vehicleSpawnAndZoneNames'] = sorted(n for n in names if re.search(
        r'(vehicle.*(?:spawn|location)|z2.*\.(?:zone|xml|json)$|^SpawnInfo)', n, re.I))
    (args.out/'retail-asset-audit.json').write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k!='assets'}, indent=2))
    print(json.dumps([r for r in report['assets'] if not r['name'].endswith('-namelist.txt')],indent=2))

if __name__ == '__main__':
    main()
