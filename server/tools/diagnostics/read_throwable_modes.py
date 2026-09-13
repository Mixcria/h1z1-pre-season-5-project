"""Read one bounded snapshot of August throwable definitions; never writes client memory.

Usage: python read_throwable_modes.py PID OUTPUT.json

Uses read_reticle_state.Reader (QUERY_INFORMATION | VM_READ only), including its
exact H1Z1.exe path check and bounded hash traversal. Output is created exclusively.
The snapshot is asynchronous: the client can change its tables between reads.

Native provenance, August build 0.0.118.208059:
* DAT_143f6a030: weapon/reference table singleton, rebased from the live module.
* FUN_14147f040: fire modes, count +a0, buckets +c0, key +378, next +380.
* WeaponListLayouts.cs: named fire-mode record offsets and native scalar types.
* FUN_14147f4c0: weapons, count +40, buckets +60, key +110, next +118.
* FUN_1421e5e90: ammo slot count +e0, pointer +d8, stride 68.
* FUN_14228de50 / FUN_14228df60: slot ammo ID +0 / clip size +4.
* AugustWeaponFacts.g.cs: item-to-weapon and fire-group identities below.
"""

import argparse
import json
from pathlib import Path
import struct

from read_reticle_state import Reader, address, timestamp


THROWABLES = (
    (14, 7, "Molotov Cocktail", (14, 15)),
    (65, 1404, "M67 Frag Grenade", (2808, 2809)),
    (2235, 1410, "Stun Grenade", (84, 85)),
    (2236, 1411, "Smoke Grenade", (86, 87)),
    (2237, 1412, "Gas Grenade", (88, 89)),
)


def mode_snapshot(reader, table, mode_id):
    record = reader.lookup(table, 0xa0, 0xc0, mode_id, 0x378, 0x380)
    result = {"modeId": mode_id, "record": address(record)}
    if not record:
        return result
    data = reader.read(record, 0x19c)
    result.update(
        type=struct.unpack_from("<i", data, 0x24)[0],
        flagsAt20=data[0x20:0x23].hex(),
        ammoItemId=struct.unpack_from("<I", data, 0x2c)[0],
        ammoSlot=struct.unpack_from("<i", data, 0x30)[0],
        fireDurationMs=struct.unpack_from("<i", data, 0x38)[0],
        range=struct.unpack_from("<f", data, 0x58)[0],
        ammoPerShot=struct.unpack_from("<i", data, 0x5c)[0],
        launchPitchAdditiveDegrees=struct.unpack_from("<f", data, 0x198)[0],
    )
    return result


def weapon_snapshot(reader, table, weapon_id):
    record = reader.lookup(table, 0x40, 0x60, weapon_id, 0x110, 0x118)
    result = {"weaponId": weapon_id, "record": address(record)}
    if not record:
        return result
    count = reader.u32(record + 0xe0)
    if count > 32:
        raise ValueError("Unexpected throwable ammo slot count")
    slots = reader.u64(record + 0xd8)
    result.update(ammoSlotCount=count, ammoSlots=[])
    for index in range(count):
        ammo_id, clip_size = struct.unpack("<II", reader.read(slots + index * 0x68, 8))
        result["ammoSlots"].append({"index": index, "ammoId": ammo_id, "clipSize": clip_size})
    return result


def snapshot(pid):
    result = {"pid": pid, "timeUtc": timestamp(), "readOnly": True}
    reader = None
    try:
        reader = Reader(pid)
        result.update(module=str(reader.path), base=address(reader.base))
        table = reader.u64(reader.base + 0x3f6a030)
        result["table"] = address(table)
        if not table:
            result["error"] = "Weapon definition table is not initialized"
            return result
        result["throwables"] = []
        for item_id, weapon_id, name, modes in THROWABLES:
            entry = {"itemId": item_id, "name": name}
            try:
                entry["weapon"] = weapon_snapshot(reader, table, weapon_id)
                entry["modes"] = [mode_snapshot(reader, table, mode_id) for mode_id in modes]
            except (OSError, ValueError, struct.error) as error:
                entry["error"] = str(error)
            result["throwables"].append(entry)
    except (OSError, ValueError, struct.error) as error:
        result["error"] = str(error)
    finally:
        if reader is not None:
            reader.close()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pid", type=int)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.pid <= 0:
        parser.error("PID must be positive")
    with args.output.open("x", encoding="utf-8") as output:
        result = snapshot(args.pid)
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")
    print(args.output)
    return 1 if "error" in result or any("error" in row for row in result.get("throwables", [])) else 0


if __name__ == "__main__":
    raise SystemExit(main())
