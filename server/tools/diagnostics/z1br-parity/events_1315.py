"""Narrow bounded event shapes; identities stay private to in-memory joins."""
from bounded import Cursor, DecodeError


def weapon_leaves(data, offset=1, depth=0):
    """Yield (gateway-relative header offset, time, sub, body) with exact nesting."""
    if depth > 8: raise DecodeError("weapon nesting limit")
    c = Cursor(data)
    if c.u8() != 0x72: raise DecodeError("weapon family")
    time, sub = c.u32(), c.u8()
    if sub != 0x1f:
        yield offset, time, sub, c.take(len(data) - c.pos)
        return
    count = c.count(64)
    for _ in range(count):
        size = c.count(1 << 20)
        if size < 6: raise DecodeError("weapon member too small")
        at = c.pos
        yield from weapon_leaves(c.take(size), offset + at, depth + 1)
    c.finish()


def fire(body, hint=False):
    c = Cursor(body)
    identity = c.take(8)
    if hint: c.u8()
    position = [c.f32() for _ in range(3)]
    if any(abs(p) >= 100000 for p in position): raise DecodeError("muzzle bounds")
    count = c.count(32)
    if not count: raise DecodeError("empty shot array")
    projectiles = []
    for _ in range(count):
        projectiles.append(c.u32())
        c.take(16)
    c.finish()
    return {"identity": identity, "projectiles": projectiles, "position": position}


def dto(data, direction):
    c = Cursor(data)
    if c.u8() != {"c2s": 6, "s2c": 5}.get(direction) or c.u8() != 0xa9:
        raise DecodeError("DTO family/direction")
    sub = c.u16()
    if (direction, sub) not in (("c2s", 1), ("s2c", 2)):
        raise DecodeError("unsupported DTO subtype")
    identity = c.take(8)
    model = c.string()
    if not model or len(model) > 256: raise DecodeError("DTO model")
    result = {"sub": sub, "identity": identity, "model": model, "tailOffset": c.pos}
    if sub == 1:
        result["projectileId"] = c.u32()
        result["sourceIdentity"] = c.take(8)
    else:
        result.update(effectId=c.u32(), scalar=c.f32(), byte0=c.u8(), word=c.u32(),
                      flags=[c.u8() for _ in range(4)])
        if result["byte0"] > 1 or any(v > 1 for v in result["flags"]):
            raise DecodeError("DTO Boolean-shaped field")
    c.finish()
    return result


def bounce(body):
    c = Cursor(body)
    result = {"projectileId": c.u32(), "effectCandidate": c.u32(), "identity": c.take(8)}
    c.finish()
    return result


def appearance(data):
    c = Cursor(data)
    if c.take(3) != b"\x05\xde\x01": raise DecodeError("appearance family")
    result = {"vehicleIdentity": c.take(8), "driverIdentity": c.take(8), "shaderCandidate": c.u32()}
    c.finish()
    return result
