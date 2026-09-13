"""Ensure measurement windows and old-protocol evidence cannot overstate native results."""
import importlib.util
import json
import pathlib
import struct
import tempfile
import unittest


def module(name):
    spec = importlib.util.spec_from_file_location(name, pathlib.Path(__file__).with_name(name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


linux = module('summarize-linux-multiplayer')
reference = module('summarize-reference-peer-capture')


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = pathlib.Path(self.directory.name)

    def test_menu_steady_window_excludes_account_setup_and_partial_samples(self):
        def sample(second, cpu):
            return {'utc': f'2026-09-10T12:00:{second:02d}Z', 'interval_seconds': 1,
                    'process_cpu_percent_one_core': cpu, 'busiest_threads': [],
                    'host_cpu_percent': {'cpu': {'busy': cpu / 20}}}
        result = linux.steady_window(self.root, {'menuPopulation': 2000,
            'completedUtc': '2026-09-10T12:00:30Z', 'idleSeconds': 10}, {},
            [sample(19, 999), sample(20, 999), sample(21, 10), sample(30, 20), sample(31, 999)])
        self.assertEqual(2, result['osSamples'])
        self.assertEqual(20, result['processCpuPercentOneCoreIs100']['max'])
        self.assertEqual({}, result['histogramTimings'])  # Missing data is not zero delay.

    def test_no_steady_window_is_inferred_for_unmarked_single_match(self):
        self.assertIsNone(linux.steady_window(self.root, {'menuPopulation': 0}, {}, []))

    def test_reference_keeps_sparse_records_and_separates_capture_gaps_from_client_time(self):
        def line(at, data):
            return f'{at} event ZONE s2c packet ch=0 hex={data.hex()}\n'
        spawn = b'\xd6' + b'12345678' + b'\x40'
        move = lambda tick: b'\x79\x40' + struct.pack('<HIB', 0x200, tick, 5) + bytes(4)
        path = self.root / 'reference.log'
        path.write_text(line(999, move(10)) + line(1000, spawn) + line(1040, move(100))
                        + line(1080, move(101)) + line(1100, b'\x78\x40' + move(102)[2:]), encoding='utf-8')
        result = reference.summarize(path)
        peer = result['peerMovement'][0]
        self.assertEqual(2, peer['records'])  # Ignore pre-spawn poses and August opcode 0x78.
        self.assertEqual(40, peer['arrivalGapMs']['p50'])
        self.assertEqual(1, peer['clientTimestampDeltaMs']['p50'])
        self.assertEqual({'0x200': 2}, peer['maskCounts'])
        self.assertNotIn('12345678', json.dumps(result))

    def test_reference_reports_truncation_instead_of_silently_counting_valid_peers(self):
        path = self.root / 'broken.log'
        path.write_text('100 event ZONE s2c packet ch=0 hex=d6\n'
                        '101 event ZONE c2s packet ch=2 hex=00\n', encoding='utf-8')
        result = reference.summarize(path)
        self.assertEqual(2, result['malformedRelevantPackets'])
        self.assertEqual(0, result['spawnedPcs'])


if __name__ == '__main__':
    unittest.main()
