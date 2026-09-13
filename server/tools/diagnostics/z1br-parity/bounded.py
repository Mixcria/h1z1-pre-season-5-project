"""Small strict primitives shared by the offline parity readers."""
import math
import struct


class DecodeError(ValueError):
    pass


class Cursor:
    def __init__(self, data, offset=0):
        self.data = data
        self.pos = offset
        self.need(0)

    def need(self, size):
        if size < 0 or self.pos < 0 or self.pos + size > len(self.data):
            raise DecodeError(f"bounds at {self.pos}: need {size}, total {len(self.data)}")

    def take(self, size):
        self.need(size)
        result = self.data[self.pos:self.pos + size]
        self.pos += size
        return result

    def number(self, kind):
        return struct.unpack("<" + kind, self.take(struct.calcsize("<" + kind)))[0]

    def u8(self): return self.number("B")
    def u16(self): return self.number("H")
    def u32(self): return self.number("I")
    def f32(self):
        value = self.number("f")
        if not math.isfinite(value):
            raise DecodeError(f"nonfinite f32 at {self.pos - 4}")
        return value

    def count(self, maximum=4096):
        count = self.u32()
        if count > maximum:
            raise DecodeError(f"count {count} exceeds {maximum} at {self.pos - 4}")
        return count

    def string(self):
        try:
            return self.take(self.count()).decode("ascii")
        except UnicodeDecodeError as exc:
            raise DecodeError("non-ASCII definition string") from exc

    def packed(self, signed=False):
        self.need(1)
        first = self.data[self.pos]
        size = 1 + ((first >> 1) & 3) if signed else 1 + (first & 3)
        raw = int.from_bytes(self.take(size), "little")
        if signed:
            return -(raw >> 3) if raw & 1 else raw >> 3
        return raw >> 2

    def finish(self):
        if self.pos != len(self.data):
            raise DecodeError(f"trailing {len(self.data) - self.pos} bytes at {self.pos}")
