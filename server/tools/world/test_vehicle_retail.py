import copy
import json
import math
from pathlib import Path
import sys
import unittest

from vehicle_geometry import axes_for
from vehicle_retail import CATALOGUE, PLACEMENTS, sha, validate


class WholeRetailCatalogueTests(unittest.TestCase):
    def setUp(self):
        self.catalogue = json.loads(CATALOGUE.read_text())
        self.audit = json.loads(PLACEMENTS.read_text())

    def check(self):
        return validate(self.catalogue, self.audit, sha(CATALOGUE))

    def test_complete_retail_map_and_every_exclusion_are_accounted_for(self):
        accepted = self.check()
        self.assertEqual(550, len(accepted))
        self.assertEqual(591, len(self.audit['decisions']))
        self.assertEqual(601, len(self.catalogue['records']))
        self.assertEqual(4, len({(r['position'][0] >= 0, r['position'][2] >= 0) for r in accepted}))
        self.assertFalse({r['instanceId'] for r in accepted} & set(range(1, 407)))

    def test_partial_duplicate_or_lobby_decisions_are_rejected(self):
        original = copy.deepcopy(self.audit['decisions'])
        for changed in (original[:-1], original + [original[0]], original + [dict(original[0], instanceId=1)]):
            self.audit['decisions'] = changed
            with self.assertRaisesRegex(ValueError, 'exactly one'):
                self.check()

    def test_moving_or_retyping_a_marker_cannot_masquerade_as_grounding(self):
        for key, index in (('position', 0), ('position', 2), ('rotation', 0), ('rotation', 1), ('rotation', 2)):
            self.setUp()
            self.audit['decisions'][0][key][index] += 0.01
            with self.assertRaisesRegex(ValueError, 'changed retail'):
                self.check()
        self.setUp()
        self.audit['decisions'][0]['vehicleId'] = 99
        with self.assertRaisesRegex(ValueError, 'changed retail'):
            self.check()

    def test_changed_source_or_support_requires_a_new_review(self):
        self.catalogue['source']['sha256'] = '0'*64
        with self.assertRaisesRegex(ValueError, 'Unreviewed'):
            self.check()
        self.setUp()
        self.audit['catalogueSha256'] = '0'*64
        with self.assertRaisesRegex(ValueError, 'changed since'):
            self.check()
        self.setUp()
        next(r for r in self.audit['decisions'] if not r['reasons'])['position'][1] += 0.1
        with self.assertRaisesRegex(ValueError, 'support height'):
            self.check()

    def test_all_37_recorded_supported_poses_match_source_xz_and_orientation(self):
        observed = json.loads(CATALOGUE.with_name('z1br-retail-vehicle-recording.json').read_text())
        by_id = {r['instanceId']: r for r in self.catalogue['records']}
        frames = {r['frame']: r for r in observed['vehicles']}
        self.assertEqual(observed['source']['sha256'], self.catalogue['captureComparison']['sha256'])
        comparisons = self.catalogue['captureComparison']['records']
        self.assertEqual(37, len(comparisons))
        for comparison in comparisons:
            marker, seen = by_id[comparison['instanceId']], frames[comparison['frame']]
            self.assertTrue(seen['verifiedLayout'])
            self.assertEqual('Z2', seen['zone'])
            self.assertEqual(marker['position'][::2], seen['position'][::2])
            x, y, z, w = seen['rotation']
            axes = ((1-2*(y*y+z*z), 2*(x*y+z*w), 2*(x*z-y*w)),
                    (2*(x*y-z*w), 1-2*(x*x+z*z), 2*(y*z+x*w)),
                    (2*(x*z+y*w), 2*(y*z-x*w), 1-2*(x*x+y*y)))
            self.assertLess(max(abs(a-b) for left,right in zip(axes,axes_for(marker['rotation']))
                                for a,b in zip(left,right)), 0.000001)

    def test_pipeline_cached_classification_does_not_replace_a_fresh_source_digest(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'pipeline'))
        import pipeline
        grades = {pipeline.rel(CATALOGUE): dict(grade='CLIENT', sha256='stale', size=0, thirdParty=False)}
        current = pipeline.input_meta(CATALOGUE, grades)
        current.update(pipeline.grade_for_generated_input(CATALOGUE, grades))
        self.assertEqual(sha(CATALOGUE), current['sha256'])
        self.assertEqual(CATALOGUE.stat().st_size, current['size'])


if __name__ == '__main__':
    unittest.main()
