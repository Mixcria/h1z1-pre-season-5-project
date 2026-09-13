#!/usr/bin/env python3
"""Summarize one bounded production-metrics run without exporting session data."""
import argparse
import json
import math
import os
from pathlib import Path

MAX_LINE = 2 * 1024 * 1024
TIMINGS = {
    "listener_work": "gateway.data.transport.timings.listenerWork",
    "transport_tick_work": "gateway.data.transport.timings.transportTickWork",
    "transport_tick_interval": "gateway.data.transport.timings.transportTickInterval",
    "posted_queue_wait": "gateway.data.transport.timings.postedQueueWait",
    "local_input_queue_wait": "gateway.data.transport.timings.localInputQueueWait",
    "local_output_queue_wait": "gateway.data.transport.timings.localOutputQueueWait",
    "latest_newest_offer_to_commit": "gateway.data.transport.timings.latestNewestOfferToCommit",
    "reliable_held_wait": "gateway.data.transport.timings.reliableHeldWait",
    "reliable_assembly_wait": "gateway.data.transport.timings.reliableAssemblyWait",
    "reliable_buffer_wait": "gateway.data.transport.timings.reliableBufferWait",
    "reliable_first_transmit_wait": "gateway.data.transport.timings.reliableFirstTransmitWait",
    "encrypt_work": "gateway.data.transport.timings.encryptWork",
    "decrypt_work": "gateway.data.transport.timings.decryptWork",
    "player_handler": "gateway.data.gameplay.playerMovement.handlerDuration",
    "managed_handler": "gateway.data.gameplay.managedMovement.handlerDuration",
    "movement_arrival_interval": "gateway.data.gameplay.playerMovement.arrivalIntervals",
    "timer_continuation_lateness": "gateway.data.gameplay.timers.continuationLateness",
    "timer_owner_queue_wait": "gateway.data.gameplay.timers.continuationToCallback",
    "launcher_gate_wait": "launcher.gateWait",
    "launcher_gate_hold": "launcher.gateHold",
    "wss_send_wait": "launcher.tunnel.sendSemaphoreWait",
    "wss_send_completion": "launcher.tunnel.sendCompleted",
}
RATES = {
    "transport_timer_calls_per_second": ("gateway.data.transport.timings.transportTickWork.count", "gateway.data.transport.windowSeconds"),
    "gateway_received_datagrams_per_second": ("gateway.data.transport.counters.receivedDatagrams", "gateway.data.transport.windowSeconds"),
    "gateway_sent_datagrams_per_second": ("gateway.data.transport.counters.sentDatagrams", "gateway.data.transport.windowSeconds"),
    "gateway_received_bytes_per_second": ("gateway.data.transport.counters.receivedBytes", "gateway.data.transport.windowSeconds"),
    "gateway_sent_bytes_per_second": ("gateway.data.transport.counters.sentBytes", "gateway.data.transport.windowSeconds"),
    "player_movement_received_per_second": ("gateway.data.gameplay.playerMovement.counts.received", "gateway.data.gameplay.windowSeconds"),
    "player_movement_applied_per_second": ("gateway.data.gameplay.playerMovement.counts.applied", "gateway.data.gameplay.windowSeconds"),
    "managed_movement_received_per_second": ("gateway.data.gameplay.managedMovement.counts.received", "gateway.data.gameplay.windowSeconds"),
    "peer_pose_offers_per_second": ("gateway.data.gameplay.replication.peerPoseOffers", "gateway.data.gameplay.windowSeconds"),
    "vehicle_pose_offers_per_second": ("gateway.data.gameplay.replication.vehiclePoseOffers", "gateway.data.gameplay.windowSeconds"),
}
GAUGES = {
    "rss_bytes": "process.rssBytes",
    "process_cpu_percent_one_logical_cpu_is_100": "process.cpuPercent",
    "thread_pool_pending_work": "process.threadPoolPendingWork",
    "gateway_posted_depth": "gateway.data.transport.queues.postedDepth",
    "gateway_oldest_posted_ms": "gateway.data.transport.queues.oldestPostedMs",
    "gateway_reliable_unsent_datagrams": "gateway.data.transport.queues.reliableUnsentDatagrams",
    "gateway_reliable_pending_bytes": "gateway.data.transport.queues.reliablePendingBytes",
    "authenticated_game_sessions": "gateway.data.gameplay.population.authenticatedOpenSessions",
    "admitted_matches": "gateway.data.gameplay.population.admittedMatches",
    "active_matches": "gateway.data.gameplay.population.activeMatches",
    "exporter_dropped_samples": "exporter.droppedSamples",
}


def at(row, path):
    for key in path.split("."):
        if not isinstance(row, dict):
            return None
        row = row.get(key)
    return row


def number(value):
    try:
        return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0
    except OverflowError:
        return False


class Histogram:
    def __init__(self):
        self.count = 0
        self.total = self.maximum = 0.0
        self.bounds = self.buckets = None

    def add(self, sample):
        if not isinstance(sample, dict):
            return
        count, total, maximum = (sample.get(k) for k in ("count", "sumMs", "maxMs"))
        bounds, buckets = sample.get("bucketUpperBoundsMs"), sample.get("bucketCounts")
        if not all(number(v) for v in (count, total, maximum)):
            raise ValueError("Invalid timing totals")
        if (not isinstance(bounds, list) or not isinstance(buckets, list) or len(bounds) > 64
                or len(buckets) != len(bounds) + 1 or not all(number(v) for v in bounds + buckets)
                or bounds != sorted(set(bounds)) or sum(buckets) != count):
            raise ValueError("Invalid timing histogram")
        if self.bounds is None:
            self.bounds, self.buckets = bounds, [0] * len(buckets)
        if self.bounds != bounds:
            raise ValueError("Timing bucket schema changed within one run")
        self.count += count
        self.total += total
        self.maximum = max(self.maximum, maximum)
        self.buckets = [a + b for a, b in zip(self.buckets, buckets)]

    def percentile_bound(self, fraction):
        if not self.count:
            return None
        wanted, cumulative = math.ceil(self.count * fraction), 0
        for index, count in enumerate(self.buckets):
            cumulative += count
            if cumulative >= wanted:
                # Overflow has no finite histogram bound; never misreport 5000 ms as its cap.
                return self.bounds[index] if index < len(self.bounds) else None
        return None

    def result(self):
        return {"count": self.count, "average_ms": self.total / self.count if self.count else None,
                "true_max_ms": self.maximum if self.count else None,
                "p50_bucket_upper_ms": self.percentile_bound(.50),
                "p95_bucket_upper_ms": self.percentile_bound(.95),
                "p99_bucket_upper_ms": self.percentile_bound(.99),
                "overflow_samples": self.buckets[-1] if self.buckets else 0}


def summarize(paths):
    if not 1 <= len(paths) <= 64:
        raise ValueError("Supply 1 to 64 capture files from one run")
    timings = {key: Histogram() for key in TIMINGS}
    rates = {key: [0.0, 0.0] for key in RATES}
    maxima = {key: None for key in GAUGES}
    rows = gaps = previous = 0
    run_id = None
    pending = {"login": 0, "gateway": 0}
    cpu_seconds = process_seconds = gc_pause_ms = 0.0
    first_utc = last_utc = None
    for path in sorted(map(Path, paths)):
        with path.open("rb") as source:
            while line := source.readline(MAX_LINE + 1):
                if len(line) > MAX_LINE:
                    raise ValueError("Capture line exceeds 2 MiB")
                row = json.loads(line)
                if not isinstance(row, dict) or row.get("schemaVersion") != 1 or row.get("type") != "production-window":
                    raise ValueError("Unsupported capture schema")
                candidate = at(row, "identity.runId")
                if not isinstance(candidate, str) or not candidate:
                    raise ValueError("Missing run identity")
                if run_id is not None and candidate != run_id:
                    raise ValueError("Use separate summaries for separate process runs")
                run_id = candidate
                sequence = row.get("sequence")
                if not isinstance(sequence, int) or isinstance(sequence, bool) or sequence <= previous:
                    raise ValueError("Duplicate or out-of-order sequence; check input files")
                gaps += sequence - previous - 1
                previous = sequence
                rows += 1
                if first_utc is None:
                    first_utc = row.get("utc")
                last_utc = row.get("utc")
                for owner in pending:
                    if at(row, f"{owner}.status") != "ok":
                        pending[owner] += 1
                for key, path in TIMINGS.items():
                    timings[key].add(at(row, path))
                for key, (count_path, seconds_path) in RATES.items():
                    count, seconds = at(row, count_path), at(row, seconds_path)
                    if number(count) and number(seconds) and seconds > 0:
                        rates[key][0] += count
                        rates[key][1] += seconds
                for key, path in GAUGES.items():
                    value = at(row, path)
                    if number(value):
                        maxima[key] = value if maxima[key] is None else max(maxima[key], value)
                seconds, percent = at(row, "process.windowSeconds"), at(row, "process.cpuPercent")
                if number(seconds) and number(percent):
                    process_seconds += seconds
                    cpu_seconds += seconds * percent / 100
                pause = at(row, "process.gcPauseMs")
                if number(pause):
                    gc_pause_ms += pause
    if not rows:
        raise ValueError("No production windows found")
    return {"schema_version": 1, "windows": rows, "sequence_gaps_including_omitted_prefix": gaps,
            "first_utc": first_utc, "last_utc": last_utc, "owner_unavailable_windows": pending,
            "measured_process_seconds": process_seconds,
            "average_process_cpu_percent_one_logical_cpu_is_100": cpu_seconds / process_seconds * 100 if process_seconds else None,
            "gc_pause_ms": gc_pause_ms, "maximum_gauges": maxima,
            "rates": {key: {"per_second": count / seconds if seconds else None, "count": count,
                            "covered_seconds": seconds} for key, (count, seconds) in rates.items()},
            "timings": {key: value.result() for key, value in timings.items()},
            "interpretation": "Histogram percentile bounds, not exact percentiles. Null percentile can mean no samples or overflow. Rates use each source's own windows; gaps are not zero traffic. No NIC or client-display latency is inferred."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", help="JSONL capture files from one process run")
    parser.add_argument("--output", type=Path, help="Create a new summary file; never overwrite")
    args = parser.parse_args()
    try:
        text = json.dumps(summarize(args.paths), indent=2, allow_nan=False) + "\n"
        if args.output:
            descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as output:
                output.write(text)
        else:
            print(text, end="")
    except (OSError, ValueError, TypeError) as error:
        parser.exit(1, f"Metrics summary failed: {type(error).__name__}: {error}\n")


if __name__ == "__main__":
    main()
