import copy
import hashlib
import json
import math
from pathlib import Path
import tempfile
import unittest
from vehicle_captured import apply_captured, load_captured
from vehicle_geometry import box, load_bounds, DRIVABLE_MODELS


class CapturePlacementTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cranberry-capture-test-')
        self.addCleanup(self.temp.cleanup)
        self.world = Path(self.temp.name)/'world.jsonl'
        self.world.write_text('')
        self.observations, self.adoption = load_captured()
        self.adoption['augustWorldSha256'] = hashlib.sha256(b'').hexdigest()
        self.old = [copy.deepcopy(self.adoption['placements'][0]['replaces'])]

    def apply(self, obstacles=()):
        return apply_captured(self.old, [], obstacles, load_bounds(), None, self.world,
                              (self.observations, self.adoption))

    def test_recorded_quaternion_preserves_atv_heading_and_replaces_only_selected_jeep(self):
        result = self.apply()
        self.assertEqual([194,406], [a['id'] for a in result])
        atv = result[1]
        self.assertAlmostEqual(2*math.pi/3, atv['yaw'], places=6)
        self.assertEqual((-524.2061157226562,237.38125610351562,-3721.393310546875),
                         tuple(atv[k] for k in ('x','y','z')))

    def test_practice_zone_and_outside_map_pregame_positions_are_never_adopted(self):
        original = copy.deepcopy(self.observations['vehicles'])
        for change in ('practice','pregame'):
            self.observations['vehicles'] = copy.deepcopy(original)
            r = next(r for r in self.observations['vehicles'] if r['frame']==7441)
            if change=='practice': r['zone']='PracticeZone'
            else: r['position'][2]=-4897.0
            with self.assertRaisesRegex(ValueError,'main Z2'):
                self.apply()

    def test_changed_capture_geometry_or_legacy_replacement_requires_review(self):
        self.world.write_text('changed')
        with self.assertRaisesRegex(ValueError,'world changed'):self.apply()
        self.world.write_text('')
        self.old[0]['x'] += 1
        with self.assertRaisesRegex(ValueError,'replacement changed'):self.apply()

    def test_new_static_vehicle_collision_fails_instead_of_silently_shipping_the_pad(self):
        obstacle=box(load_bounds()[DRIVABLE_MODELS[5]],[-524.2061157226562,237.38125610351562,-3721.393310546875])
        with self.assertRaisesRegex(ValueError,'static August scenery'):self.apply([obstacle])

    def test_missing_or_repeated_capture_record_is_rejected(self):
        self.observations['vehicles'] += [copy.deepcopy(next(r for r in self.observations['vehicles'] if r['frame']==7441))]
        with self.assertRaisesRegex(ValueError,'one observation'):self.apply()


if __name__=='__main__':unittest.main()
