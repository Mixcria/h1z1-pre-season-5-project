"""Bounds-checked structural 1315 WeaponDefinitions reader.

Unknown fixed spans remain opaque and have no asserted runtime field names.
The measured two counted nine-byte arrays in list 2 and changed list-3/list-6
widths are consumed explicitly. This is NOT a protocol-1148 packet writer.
"""
from bounded import Cursor, DecodeError


def unique(rows, label):
    ids = [r["id"] for r in rows]
    if len(ids) != len(set(ids)):
        raise DecodeError(f"duplicate {label} keys")


def keyed(c):
    start = c.pos
    key, body_key = c.u32(), c.u32()
    if key != body_key:
        raise DecodeError(f"key/body mismatch at {start}")
    return {"id": key, "offset": start}


def curve(c):
    start = c.pos
    count = c.count(256)
    points = []
    for _ in range(count):
        at = c.pos
        points.append({"offset": at, "scalar0": c.f32(), "scalar1": c.f32(), "byte8": c.u8()})
    return {"offset": start, "count": count, "points": points}


def parse(body):
    c = Cursor(body)
    lists, spans = [], []
    for number in range(8):
        start = c.pos
        rows = []
        count = c.count(4096)
        for _ in range(count):
            if number == 0:
                r = keyed(c)
                c.take(4 + 1 + 19 * 4)  # rest of fixed pre-string prefix
                r["animation"] = c.string()
                c.take(7 * 4 + 1 + 2 * 4)
                slots = []
                for _ in range(c.count(64)):
                    slots.append({"ammoId": c.u32(), "clipSize": c.u32()})
                    c.take(4 + 1 + 3 * 4)
                    for _ in range(3): c.string()
                r["slots"] = slots
                r["groups"] = [c.u32() for _ in range(c.count(64))]
            elif number == 1:
                r = keyed(c)
                r["modes"] = [c.u32() for _ in range(c.count(64))]
                r["flags"] = c.u8()
                r["words"] = [c.u32() for _ in range(10)]
            elif number == 2:
                r = keyed(c)
                r["fixedPrefix"] = c.take(165)  # including flags; key pair is separate
                r["array0"] = curve(c)
                r["fixedMiddle"] = c.take(41)
                r["array1"] = curve(c)
                r["fixedSuffix"] = c.take(779)
            elif number == 3:
                r = keyed(c)
                states = []
                for _ in range(c.count(64)):
                    at = c.pos
                    state, body_id, flags = c.u32(), c.u32(), c.u8()
                    states.append({"id": state, "bodyId": body_id, "flags": flags,
                                   "offset": at, "words": [c.u32() for _ in range(17)]})
                unique(states, "list-3 state")
                r["states"] = states
            elif number == 4:
                r = keyed(c)
                r["ammoId"], r["projectileId"] = c.u32(), c.u32()
            elif number == 5:
                r = {"offset": c.pos, "id": c.u32(), "words": [c.u32() for _ in range(23)]}
            elif number == 6:
                r = keyed(c)
                entries = []
                for _ in range(c.count(256)):
                    entry = keyed(c)
                    entry.update(scalar0=c.f32(), scalar1=c.f32())
                    entries.append(entry)
                unique(entries, "list-6 member")
                r["entries"], r["trailingByte"] = entries, c.u8()
            else:
                r = keyed(c)
                r["values"] = [c.u32() for _ in range(c.count(256))]
            r["bytes"] = c.pos - r["offset"]
            rows.append(r)
        unique(rows, f"list-{number}")
        lists.append(rows)
        spans.append({"list": number, "offset": start, "end": c.pos, "count": count})
    c.finish()
    ids = [{r["id"] for r in rows} for rows in lists]
    joins = {
        "weaponToGroup": [g for r in lists[0] for g in r["groups"] if g not in ids[1]],
        "groupToMode": [m for r in lists[1] for m in r["modes"] if m not in ids[2]],
        "projectileMappingToMode": [r["id"] for r in lists[4] if r["id"] not in ids[2]],
        "list7ToList6": [v for r in lists[7] for v in r["values"] if v not in ids[6]],
    }
    if any(joins.values()):
        raise DecodeError(f"unresolved structural joins: {joins}")
    return {"lists": lists, "spans": spans, "joins": joins, "consumed": c.pos}
