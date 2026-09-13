"""Small placement regressions, independent of the large installed world extraction."""
import contextlib
import io
import json
import math
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import vehicle_anchors
from vehicle_geometry import DRIVABLE_MODELS, box, load_bounds, overlaps
from vehicle_stations import load_stations, station_anchors


class PlacementTests(unittest.TestCase):
    def test_police_station_omissions_use_individual_bays_in_the_august_map(self):
        document = load_stations()
        parking = [dict(id=s["parkingInstanceId"], model=document["parkingModel"],
                        pos=s["parkingPosition"], rot=s["parkingRotation"], scale=s["parkingScale"])
                   for s in document["spawns"]]
        anchors = station_anchors(document, parking, [], [], load_bounds(), None)
        self.assertEqual([404, 405], [a["id"] for a in anchors])
        self.assertEqual([3, 3], [a["vehicleId"] for a in anchors])
        for anchor, expected in zip(anchors, [(-99.2823, 33.22, 252.19951), (1681.80049, 16.25, 1862.2616)]):
            self.assertEqual(expected, tuple(anchor[k] for k in ("x", "y", "z")))
        parking[0]["pos"] = [0, 0, 0]
        with self.assertRaisesRegex(ValueError, "transform changed"):
            station_anchors(document, parking, [], [], load_bounds(), None)

    def test_police_station_correction_cannot_silently_disappear_or_overlap_scenery(self):
        document = load_stations()
        with self.assertRaisesRegex(ValueError, "Missing August parking"):
            station_anchors(document, [], [], [], load_bounds(), None)
        parking = [dict(id=s["parkingInstanceId"], model=document["parkingModel"],
                        pos=s["parkingPosition"], rot=s["parkingRotation"], scale=s["parkingScale"])
                   for s in document["spawns"]]
        bounds = load_bounds()
        obstacle = box(bounds["Common_Props_AbandonedSUV.adr"], [-99.2823, 33.22, 252.19951])
        with self.assertRaisesRegex(ValueError, "overlaps a static vehicle"):
            station_anchors(document, parking, [], [obstacle], bounds, None)

    def test_all_shipped_pads_can_be_populated_together_without_vehicle_mesh_overlap(self):
        # The default now populates every pad. A density-limited sample could hide
        # pairs of intersecting vehicles, so check the complete shipped layout.
        path = Path(__file__).resolve().parents[2] / "src/Cranberry.Zone/Data/Vehicles/z2-vehicle-anchors.json"
        document = json.loads(path.read_text(encoding="utf-8"))
        bounds = load_bounds()
        vehicles = [(anchor["id"], box(bounds[DRIVABLE_MODELS[anchor["vehicleId"]]],
                     [anchor[k] for k in ("x", "y", "z")], [anchor["yaw"], 0, 0]))
                    for anchor in document["anchors"]]
        self.assertTrue(vehicles, "The shipped layout must contain vehicle pads")
        for index, (left_id, left) in enumerate(vehicles):
            for right_id, right in vehicles[index + 1:]:
                self.assertFalse(overlaps(left, right),
                                 f"Vehicle pads {left_id} and {right_id} overlap when both spawn")

    def scan(self, points, objects):
        with tempfile.TemporaryDirectory(prefix="cranberry-spawns-") as root:
            locations = Path(root) / "locations.json"
            world = Path(root) / "objects.jsonl"
            locations.write_text(json.dumps(points))
            world.write_text("\n".join(json.dumps(o) for o in objects))
            with patch.object(vehicle_anchors, "LOCATIONS", str(locations)), patch.object(vehicle_anchors, "OBJECTS", str(world)), contextlib.redirect_stdout(io.StringIO()):
                return vehicle_anchors.scan_anchors([("Town", 20, -10, 40, 10)])

    def test_authored_height_and_heading_survive_and_wrecks_are_excluded(self):
        points = [dict(id=i, position=[x, y, 0, 1], orientation=.75, vehicleId=3)
                  for i, x, y in [(0, 0, 10), (1, 30, 12), (2, 60, 14)]]
        # The first point overlaps a static sedan, the second is beside a misplaced
        # parking-mesh origin. That origin must never replace the authored Y=12.
        objects = [dict(model="Common_Props_AbandonedSedan.adr", pos=[0, 10, 0], scale=[1, 1, 1]),
                   dict(model="Z2_Parking_6Spaces.adr", pos=[30, 100, 0], scale=[1, 1, 1])]
        anchors, spaces, inside = self.scan(points, objects)
        self.assertEqual([2, 3], [a["id"] for a in anchors])
        self.assertEqual([12, 14], [a["y"] for a in anchors])
        self.assertEqual([.75, .75], [a["yaw"] for a in anchors])
        self.assertEqual(2, spaces)
        self.assertEqual(1, inside)

    def test_surviving_august_proxy_replaces_old_transform_without_moving_other_reference_rows(self):
        name = "Common_DPO_Vehicle_PoliceCar01_proxy"
        points = [dict(id=1, name=name, position=[2991.3967, 89.189, -515.0989, 1],
                       orientation=2.3552, vehicleId=3),
                  dict(id=2, name=name, position=[100, 20, 30, 1], orientation=.75, vehicleId=3)]
        objects = [dict(model=name + ".adr", id=4082834563,
                        pos=[2987.0159, 89.189, -510.7259], rot=[2.355217, 0, 0])]
        anchors, _, _ = self.scan(points, objects)
        self.assertEqual([2987.0159, 89.189, -510.7259], [anchors[0][k] for k in ("x", "y", "z")])
        self.assertEqual(2.355217, anchors[0]["yaw"])
        self.assertEqual(4082834563, anchors[0]["augustProxyInstanceId"])
        self.assertEqual([100, 20, 30], [anchors[1][k] for k in ("x", "y", "z")])
        self.assertNotIn("augustProxyInstanceId", anchors[1])

    def test_proxy_with_ambiguous_reference_identity_is_rejected(self):
        name = "Common_DPO_Vehicle_PoliceCar01_proxy"
        points = [dict(id=i, name=name, position=[i, 0, 0, 1], orientation=0, vehicleId=3) for i in range(2)]
        objects = [dict(model=name + ".adr", id=42, pos=[0, 0, 0], rot=[0, 0, 0])]
        with self.assertRaisesRegex(ValueError, "uniquely joined"):
            self.scan(points, objects)

    def test_parallel_bay_beside_abandoned_truck_survives_but_head_on_overlap_does_not(self):
        points = [dict(id=0, position=[4, 0, 0, 1], orientation=0, vehicleId=3),
                  dict(id=1, position=[0, 0, 5.2, 1], orientation=0, vehicleId=3)]
        objects = [dict(model="Common_Props_AbandonedTruck.adr", pos=[0, 0, 0], rot=[0, 0, 0])]
        anchors, _, _ = self.scan(points, objects)
        self.assertEqual([1], [a["id"] for a in anchors])

    def test_candidate_type_heading_and_vertical_separation_control_overlap(self):
        bounds = load_bounds()
        truck = box(bounds["Common_Props_AbandonedTruck.adr"], [0, 0, 0])
        cop = bounds["Common_Vehicle_PoliceCar01.adr"]
        atv = bounds["Vehicle_Common_ATV01.adr"]
        self.assertTrue(overlaps(truck, box(cop, [0, 0, 4.5])))
        self.assertFalse(overlaps(truck, box(atv, [0, 0, 4.5])))
        self.assertFalse(overlaps(truck, box(cop, [0, 0, 4.5], [math.pi/2, 0, 0])))
        self.assertFalse(overlaps(truck, box(cop, [0, 4, 0])))

    def test_scaled_static_vehicle_uses_its_authored_size(self):
        bounds = load_bounds()
        sedan = bounds["Common_Props_AbandonedSedan.adr"]
        cop = box(bounds["Common_Vehicle_PoliceCar01.adr"], [2.5, 0, 0])
        self.assertFalse(overlaps(cop, box(sedan, [0, 0, 0])))
        self.assertTrue(overlaps(cop, box(sedan, [0, 0, 0], scale=[2, 1, 1])))


if __name__ == "__main__":
    unittest.main()
