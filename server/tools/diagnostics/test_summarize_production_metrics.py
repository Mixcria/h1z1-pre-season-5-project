"""Synthetic checks for production JSONL summary interpretation and bounds."""
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("metrics_summary", Path(__file__).with_name("summarize-production-metrics.py"))
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


def row(sequence=1, seconds=2, count=50, cpu=50, run="run-one"):
    return {"schemaVersion": 1, "type": "production-window", "sequence": sequence,
            "identity": {"runId": run, "private": "sensitive-canary"},
            "utc": "2026-09-09T12:00:00Z",
            "process": {"windowSeconds": seconds, "cpuPercent": cpu, "rssBytes": 123},
            "gateway": {"status": "ok", "data": {"transport": {"windowSeconds": seconds,
                "counters": {"receivedDatagrams": count}, "sampledSessions": [{"payload": "sensitive-canary"}]}}}}


class SummaryTests(unittest.TestCase):
    def captures(self, *rows):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name, "metrics.000.jsonl")
        path.write_text("".join(json.dumps(item) + "\n" for item in rows), encoding="utf-8")
        return [path]

    def test_rates_and_cpu_are_weighted_by_each_actual_window(self):
        result = summary.summarize(self.captures(row(), row(2, seconds=8, count=50, cpu=150)))
        rate = result["rates"]["gateway_received_datagrams_per_second"]
        self.assertEqual(10, rate["per_second"])
        self.assertEqual(10, rate["covered_seconds"])
        self.assertEqual(130, result["average_process_cpu_percent_one_logical_cpu_is_100"])
        self.assertNotIn("sensitive-canary", json.dumps(result))

    def test_pending_owner_is_missing_data_not_zero_traffic(self):
        missing = row(3, seconds=8)
        missing["gateway"] = {"status": "pending"}
        result = summary.summarize(self.captures(row(), missing))
        self.assertEqual(1, result["sequence_gaps_including_omitted_prefix"])
        self.assertEqual(1, result["owner_unavailable_windows"]["gateway"])
        self.assertEqual(25, result["rates"]["gateway_received_datagrams_per_second"]["per_second"])

    def test_overflow_never_claims_last_bucket_is_true_max_or_percentile_bound(self):
        histogram = summary.Histogram()
        histogram.add({"count": 2, "sumMs": 7100, "maxMs": 7000,
                       "bucketUpperBoundsMs": [100, 5000], "bucketCounts": [1, 0, 1]})
        result = histogram.result()
        self.assertEqual(7000, result["true_max_ms"])
        self.assertEqual(3550, result["average_ms"])
        self.assertIsNone(result["p99_bucket_upper_ms"])
        self.assertEqual(1, result["overflow_samples"])

    def test_mixed_runs_and_duplicate_sequences_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "separate summaries"):
            summary.summarize(self.captures(row(), row(2, run="run-two")))
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            summary.summarize(self.captures(row(), row()))

    def test_invalid_histogram_totals_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "histogram"):
            summary.Histogram().add({"count": 3, "sumMs": 1, "maxMs": 1,
                "bucketUpperBoundsMs": [100], "bucketCounts": [1, 1]})

    def test_oversize_input_is_rejected_before_json_parsing(self):
        paths = self.captures()
        paths[0].write_bytes(b" " * (summary.MAX_LINE + 1))
        with self.assertRaisesRegex(ValueError, "2 MiB"):
            summary.summarize(paths)


if __name__ == "__main__":
    unittest.main()
