"""Placement geometry regressions, including the real August exclusion witnesses."""
import struct
import unittest

from vehicle_geometry import DRIVABLE_MODELS, box, load_bounds
from vehicle_structures import (blocking_triangle, load_obstructions, mesh_triangles,
                                obstructed_anchors, triangle_intersects_box)


class StructureTests(unittest.TestCase):
    def test_wall_and_ceiling_intersect_but_hollow_garage_and_ground_support_do_not(self):
        car = box([-1, 0, -2, 1, 1.8, 2], (0, 0, 0))
        self.assertTrue(blocking_triangle(((0, 0, -3), (0, 3, -3), (0, 1, 3)), car, 0))
        self.assertTrue(blocking_triangle(((-3, 1.4, -3), (3, 1.4, -3), (0, 1.4, 3)), car, 0))
        self.assertFalse(blocking_triangle(((-3, .02, -3), (3, .02, -3), (0, .02, 3)), car, 0))
        self.assertFalse(blocking_triangle(((-3, .25, -3), (3, .25, -3), (0, .25, 3)), car, 0))
        self.assertFalse(blocking_triangle(((4, 0, -4), (4, 5, -4), (4, 0, 4)), car, 0))
        self.assertFalse(blocking_triangle(((-4, 3, -4), (4, 3, -4), (0, 3, 4)), car, 0))

    def test_triangle_enclosing_bounds_alone_and_boundary_contact_are_insufficient(self):
        car = box([-.5, -.5, -.5, .5, .5, .5], (0, 0, 0))
        self.assertFalse(triangle_intersects_box(((2, 2, 0), (2, -.1, 0), (-.1, 2, 0)), car))
        self.assertFalse(triangle_intersects_box(((.5, -2, -2), (.5, 2, -2), (.5, 0, 2)), car))
        self.assertFalse(triangle_intersects_box(((0, 0, 0),) * 3, car))

    def test_static_position_stream_and_indices_reject_corrupt_meshes(self):
        triangle = ((0., 0., 0.), (1., 0., 0.), (0., 1., 0.))
        data = b"DMOD" + struct.pack("<II6fI8II", 4, 0, 0, 0, 0, 1, 1, 1, 1,
                                       0, 1, 0, 0xffffffff, 1, 2, 3, 3, 12)
        data += b"".join(struct.pack("<3f", *p) for p in triangle) + struct.pack("<3H", 0, 1, 2)
        self.assertEqual([triangle], list(mesh_triangles(data)))
        with self.assertRaisesRegex(ValueError, "index outside"):
            list(mesh_triangles(data[:-2] + struct.pack("<H", 3)))
        with self.assertRaises((ValueError, struct.error)):
            list(mesh_triangles(data[:-10]))

    def test_all_saved_exclusions_have_a_reproducible_blocking_triangle(self):
        evidence = load_obstructions()
        self.assertEqual(59, len(evidence["obstructions"]))
        anchors = [r["anchor"] for r in evidence["obstructions"]]
        objects = [r["witness"]["object"] for r in evidence["obstructions"]]
        rejected = obstructed_anchors(anchors, objects, evidence, load_bounds())
        self.assertEqual({a["id"] for a in anchors}, rejected)
        self.assertTrue({8, 283, 304, 359, 374, 380}.issubset(evidence["enclosingBoundsOnlyRetained"]))
        self.assertFalse(rejected.intersection(evidence["enclosingBoundsOnlyRetained"]))
        changed = [dict(anchors[0], x=anchors[0]["x"] + 1), *anchors[1:]]
        with self.assertRaisesRegex(ValueError, "needs regeneration"):
            obstructed_anchors(changed, objects, evidence, load_bounds())


if __name__ == "__main__":
    unittest.main()
