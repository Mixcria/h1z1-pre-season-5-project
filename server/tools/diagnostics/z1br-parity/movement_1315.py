"""1315 channel-2 structural investigation; no August replay and no key-state claims."""
from bounded import Cursor, DecodeError

# Order starts from the prior 1148 comparison, then is checked against every 1315 record.
FIELDS = ((0x1, "packed_u", 1), (0x2, "packed_s", 3),
          (0x20, "u16", 1), (0x40, "packed_s", 1), (0x80, "packed_s", 1),
          (0x4, "packed_s", 1), (0x8, "packed_s", 1), (0x10, "packed_s", 1),
          (0x100, "packed_s", 3), (0x200, "packed_s", 2),
          (0x400, "packed_s", 1), (0x800, "packed_s", 1),
          (0x1000, "packed_s", 11), (0x2000, "packed_s", 3),
          (0x4000, "packed_s", 1))


def parse(data, direction, exact=True):
    c = Cursor(data)
    expected = {"c2s": 0x46, "s2c": 0x45}
    if direction not in expected or c.u8() != expected[direction]:
        raise DecodeError("channel/direction mismatch")
    # Observed s2c prefix is a fixed eight-byte identity, NOT August packed transient.
    identity = c.take(8) if direction == "s2c" else None
    start = c.pos
    mask, timestamp, version = c.u16(), c.u32(), c.u8()
    if mask & ~0x7fff:
        raise DecodeError("unknown mask")
    fields = {}
    for bit, kind, count in FIELDS:
        if not mask & bit:
            continue
        at = c.pos
        values = [c.packed(kind == "packed_s") if kind.startswith("packed")
                  else getattr(c, kind)() for _ in range(count)]
        fields[bit] = {"offset": at, "bytes": c.pos - at, "values": values}
    if exact:
        c.finish()
    return {"identity": identity, "headerOffset": start, "mask": mask,
            "timestamp": timestamp, "version": version, "fields": fields,
            "consumed": c.pos, "remaining": len(data) - c.pos}
