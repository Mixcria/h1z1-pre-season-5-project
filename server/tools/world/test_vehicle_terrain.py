"""Vehicle placement terrain regressions, including the real buried Vultures Nest pad."""
import gzip
from pathlib import Path
import struct
import tempfile
import unittest

from vehicle_geometry import load_bounds
from vehicle_terrain import VehicleTerrain


class TerrainPlacementTests(unittest.TestCase):
    bounds = (-1, 0, -2, 1, 2, 2)

    def terrain(self, height=10, change=None):
        samples = bytearray(struct.pack("<hBB", height * 32, 3, 3) * (16 * 65 * 65))
        if change is not None:
            change(samples)
        packed = gzip.compress(samples, mtime=0)
        data = (b"CBAHT01\0" + struct.pack("<IiiII", 1, 0, 256, 256, 1)
                + struct.pack("<iiI", 0, 0, len(packed)) + packed)
        with tempfile.TemporaryDirectory(prefix="cranberry-vehicle-terrain-") as directory:
            path = Path(directory) / "terrain.bin"
            path.write_bytes(data)
            return VehicleTerrain(path)

    def test_fully_buried_vehicle_is_rejected_but_ground_and_bridge_heights_survive(self):
        terrain = self.terrain()
        self.assertTrue(terrain.is_buried(self.bounds, (30, 0, 30), .6))
        self.assertFalse(terrain.is_buried(self.bounds, (30, 10, 30), .6))
        self.assertFalse(terrain.is_buried(self.bounds, (30, 16, 30), .6))

    def test_even_one_heightfield_hole_prevents_underground_rejection(self):
        for material in (2, 3):
            def hole(samples):
                samples[(30 * 65 + 30) * 4 + material] = 127
            self.assertFalse(self.terrain(change=hole).is_buried(self.bounds, (30, 0, 30), 0))

    def test_low_footprint_corner_prevents_rejecting_vehicle_on_slope(self):
        def low_corner(samples):
            struct.pack_into("<h", samples, (29 * 65 + 28) * 4, 0)
        terrain = self.terrain(change=low_corner)
        self.assertFalse(terrain.is_buried(self.bounds, (30, 0, 30), 0))

    def test_outside_terrain_and_roof_touching_surface_are_not_proven_buried(self):
        terrain = self.terrain()
        self.assertFalse(terrain.is_buried(self.bounds, (0, 0, 30), 0))
        self.assertFalse(terrain.is_buried(self.bounds, (30, 8, 30), 0))

    def test_queries_cross_tile_edges_and_include_final_chunk_vertex(self):
        terrain = self.terrain()
        self.assertTrue(terrain.is_buried(self.bounds, (64, 0, 64), 0))
        self.assertTrue(terrain.is_buried(self.bounds, (255, 0, 254), 0))

    def test_shipped_august_terrain_confirms_vultures_nest_burial_but_not_elevated_pad(self):
        terrain = VehicleTerrain()
        bounds = load_bounds()
        self.assertTrue(terrain.is_buried(bounds["Common_OffRoader_Tintable.adr"],
                                         (2343.9423, -4, -1180.471), .4618))
        self.assertFalse(terrain.is_buried(bounds["Vehicle_Common_ATV01.adr"],
                                          (-2460.9317, -30.8258, -1143.3993), -.5236))


if __name__ == "__main__":
    unittest.main()
