"""Reject fully buried vehicle pads using the August CNK heightfield.

This is deliberately a one-sided proof: a pad above the terrain may stand on a
bridge or a separate road mesh, so its height must not be snapped to the terrain.
Only reject when the whole vehicle fits below solid terrain over its footprint.
"""
from __future__ import annotations

import gzip
import math
from pathlib import Path
import struct

from vehicle_geometry import box

DEFAULT_TERRAIN = Path(__file__).resolve().parents[2] / "src/Cranberry.Zone/Data/Loot/z2-terrain.bin"


class VehicleTerrain:
    def __init__(self, path=DEFAULT_TERRAIN):
        data = Path(path).read_bytes()
        if len(data) < 28 or data[:8] != b"CBAHT01\0":
            raise ValueError("Expected the August indexed terrain heightfield")
        version, self.minimum, self.maximum, size, count = struct.unpack_from("<IiiII", data, 8)
        if (version != 1 or size != 256 or self.maximum <= self.minimum
                or self.minimum % 256 or self.maximum % 256
                or count != ((self.maximum - self.minimum) // 256) ** 2):
            raise ValueError("Invalid terrain header")
        self.chunks = {}
        self.cache = {}
        cursor = 28
        for _ in range(count):
            x, z, length = struct.unpack_from("<iiI", data, cursor)
            cursor += 12
            if (x % 256 or z % 256 or not self.minimum <= x < self.maximum
                    or not self.minimum <= z < self.maximum or length < 18
                    or cursor + length > len(data) or (x, z) in self.chunks):
                raise ValueError("Invalid terrain chunk")
            self.chunks[x, z] = data[cursor:cursor + length]
            cursor += length
        if cursor != len(data):
            raise ValueError("Unexpected terrain trailing bytes")

    def sample(self, x, z):
        # A shared upper boundary belongs to the previous chunk's final sample.
        cell_x, cell_z = min(x, self.maximum - 1), min(z, self.maximum - 1)
        chunk = cell_x // 256 * 256, cell_z // 256 * 256
        if chunk not in self.cache:
            samples = gzip.decompress(self.chunks[chunk])
            if len(samples) != 16 * 65 * 65 * 4:
                raise ValueError("Invalid terrain sample length")
            self.cache[chunk] = samples
        local_x, local_z = cell_x - chunk[0], cell_z - chunk[1]
        tile_x, tile_z = local_x // 64, local_z // 64
        sample_x = x - chunk[0] - tile_x * 64
        sample_z = z - chunk[1] - tile_z * 64
        index = (tile_z * 4 + tile_x) * 65 * 65 + sample_x * 65 + sample_z
        height, material0, material1 = struct.unpack_from("<hBB", self.cache[chunk], index * 4)
        return height / 32, material0 & 0x7f, material1 & 0x7f

    def is_buried(self, bounds, position, yaw):
        center, axes, extents = box(bounds, position, (yaw, 0, 0))
        reach = [sum(extents[j] * abs(axes[j][i]) for j in range(3)) for i in range(3)]
        low_x, high_x = math.floor(center[0] - reach[0]), math.ceil(center[0] + reach[0])
        low_z, high_z = math.floor(center[2] - reach[2]), math.ceil(center[2] + reach[2])
        roof = center[1] + reach[1]
        if low_x < self.minimum or low_z < self.minimum or high_x > self.maximum or high_z > self.maximum:
            return False
        # A triangle is linear: its minimum is one of its vertices. Checking the
        # entire enclosing grid rectangle proves every covered triangle is above
        # the roof, independent of tessellation. A PhysX material 127 is a hole.
        for x in range(low_x, high_x + 1):
            for z in range(low_z, high_z + 1):
                height, material0, material1 = self.sample(x, z)
                if height <= roof or 127 in (material0, material1):
                    return False
        return True
