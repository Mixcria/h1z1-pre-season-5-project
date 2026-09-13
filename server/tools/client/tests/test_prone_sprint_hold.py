import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('sprint', Path(__file__).parents[1] / 'install-prone-sprint-hold.py')
sprint = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sprint)


class SprintSettingsTests(unittest.TestCase):
    def test_missing_setting_is_added_only_to_general(self):
        raw = b'[Controls]\r\nSprintToggle=1\r\n\r\n[General]\r\nMouseSensitivity=0.17\r\n[UI]\r\nLegacyHitmarker=1\r\n'
        changed, previous = sprint.sprint_hold(raw)
        self.assertIsNone(previous)
        self.assertEqual(changed, raw.replace(b'[General]\r\n', b'[General]\r\nSprintToggle=0\r\n'))
        self.assertEqual(sprint.sprint_hold(changed), (changed, '0'))

    def test_existing_setting_retains_format_and_other_settings(self):
        raw = b'[General]\nMouseSensitivity=0.17\nSprintToggle = 1\n[UI]\nFirstTimeEventEnabled=0\n'
        changed, previous = sprint.sprint_hold(raw)
        self.assertEqual(previous, '1')
        self.assertEqual(changed, raw.replace(b'SprintToggle = 1', b'SprintToggle = 0'))

    def test_ambiguous_or_unknown_settings_are_refused(self):
        for raw in (b'[General]\nSprintToggle=0\nSprintToggle=1\n', b'[General]\n[General]\n',
                    b'[General]\nSprintToggle=broken\n', b'[UI]\n'):
            with self.assertRaises(ValueError):
                sprint.sprint_hold(raw)


if __name__ == '__main__':
    unittest.main()
