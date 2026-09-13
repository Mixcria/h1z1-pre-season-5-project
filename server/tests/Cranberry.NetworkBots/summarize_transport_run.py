"""Summarize an isolated NetworkBots run without exporting endpoint/ticket/player data.

Usage: python summarize_transport_run.py SCENARIO_ROOT --output SUMMARY.json
Timing quantiles are merged histogram upper bounds, not subtracted percentiles.
"""
from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
import math
import pathlib


def read(path: pathlib.Path, default=None):
    if not path.exists():
        return default
    return json.loads(path.read_text(encoding="utf-8-sig"))


def merge_histogram(rows: list[dict], timing: bool = True) -> dict:
    bound_key = "BucketUpperBoundsMs" if timing else "BucketUpperBounds"
    max_key = "MaxMs" if timing else "Max"
    sum_key = "SumMs" if timing else "Sum"
    bounds = rows[0][bound_key]
    buckets = [0] * (len(bounds) + 1)
    count, total, maximum = 0, 0, 0
    for row in rows:
        if row[bound_key] != bounds or len(row["BucketCounts"]) != len(buckets):
            raise ValueError("Histogram schema changed within the run")
        if sum(row["BucketCounts"]) != row["Count"]:
            raise ValueError("Histogram count mismatch")
        count += row["Count"]
        total += row[sum_key]
        maximum = max(maximum, row[max_key])
        buckets = [a + b for a, b in zip(buckets, row["BucketCounts"])]

    def quantile(q):
        if count == 0:
            return None
        target, cumulative = math.ceil(count * q), 0
        for index, n in enumerate(buckets):
            cumulative += n
            if cumulative >= target:
                return min(maximum, bounds[index]) if index < len(bounds) else maximum
        raise ValueError("Missing histogram samples")

    return dict(samples=count, sum=total, average=total / count if count else None,
                max=maximum, p50Upper=quantile(.50), p95Upper=quantile(.95),
                p99Upper=quantile(.99), units="ms" if timing else "field units",
                bucketUpperBounds=bounds, bucketCounts=buckets,
                quantilesAreUpperBounds=True)


def summarize(root: pathlib.Path) -> dict:
    inputs = read(root / "run-input.json", {})
    result = read(root / "clients/result.json", {})
    profile = read(root / "server/server-profile.json", {})
    metrics_path = root / "server/production-metrics.jsonl"
    metrics, incomplete_lines = [], 0
    if metrics_path.exists():
        for line in metrics_path.read_text(encoding="utf-8-sig").splitlines():
            try:
                metrics.append(json.loads(line))
            except json.JSONDecodeError:
                incomplete_lines += 1
    timing_rows, distribution_rows = collections.defaultdict(list), collections.defaultdict(list)
    counters, queue_peaks = collections.Counter(), collections.Counter()
    windows, seconds, configurations, send_windows = 0, 0, [], set()
    for sample in metrics:
        transport = sample.get("productionDiagnostics", {}).get("Transport", {})
        if not transport.get("enabled"):
            continue
        windows += 1
        seconds += transport["windowSeconds"]
        counters.update(transport.get("counters", {}))
        for name, value in transport.get("queues", {}).items():
            queue_peaks[name] = max(queue_peaks[name], value)
        for name, row in transport.get("timings", {}).items():
            timing_rows[name].append(row)
        for name, row in transport.get("distributions", {}).items():
            distribution_rows[name].append(row)
        config = transport.get("configuration")
        if config is not None and config not in configurations:
            configurations.append(config)
        for session in transport.get("sampledSessions", []):
            if "SendWindow" in session:
                send_windows.add(session["SendWindow"])

    process = profile.get("process", {})
    process_delta = None
    if len(metrics) >= 2:
        first, last = metrics[0], metrics[-1]
        elapsed = (dt.datetime.fromisoformat(last["utc"]) - dt.datetime.fromisoformat(first["utc"])).total_seconds()
        cpu = last["processCpuSeconds"] - first["processCpuSeconds"]
        process_delta = dict(sampledWallSeconds=elapsed, cpuSeconds=cpu,
                             averageCores=cpu / elapsed if elapsed > 0 else None,
                             gcPauseMs=last["gcPauseMs"] - first["gcPauseMs"],
                             allocatedBytes=last["allocatedBytes"] - first["allocatedBytes"])
    checks = result.get("checks", [])
    failed = [item["name"] for item in checks if not item.get("pass", False)]
    movement = []
    for row in result.get("movementWindows", []):
        movement.append({key: row[key] for key in (
            "name", "airborne", "clientGcPauseMs", "worstP50Ms", "worstP95Ms", "worstP99Ms",
            "maxMs", "outOfOrderTimestamps", "minimumPairHz", "missingPeers", "stalestPeerMs", "olderRecords"
        ) if key in row})
    return dict(
        scenario=str(root.resolve()),
        workload={key: inputs[key] for key in (
            "bots", "menuBots", "seconds", "combatSeconds", "matchCycles", "tls", "serverGcMode",
            "clientGcMode", "delayMs", "jitterMs", "loss", "slowClient", "reliableMovement"
        ) if key in inputs},
        seeds=dict(match=20170907, impairmentPerBot="3100 + zero-based bot index",
                   harnessPerBot="7000 + zero-based bot index"),
        completion=read(root / "completion.json"), checks=len(checks), failedChecks=failed,
        resultPresent=bool(result), serverErrorCount=len(profile.get("errors", [])),
        movementWindows=movement, serverProcess=process, clientProcess=result.get("clientProcess"),
        impairment=result.get("impairment"), sampledProcessDelta=process_delta,
        observedTransportWindows=windows, observedTransportWindowSeconds=seconds,
        malformedMetricLines=incomplete_lines, effectiveTransportConfigurations=configurations,
        sampledSendWindows=sorted(send_windows), counters=dict(counters), queuePeaks=dict(queue_peaks),
        egressBytesPerSecond=counters["SentBytes"] / seconds if seconds > 0 else None,
        timings={name: merge_histogram(rows) for name, rows in timing_rows.items()},
        distributions={name: merge_histogram(rows, False) for name, rows in distribution_rows.items()},
        serverBinaries=inputs.get("serverBinaries", []), clientBinaries=inputs.get("clientBinaries", []),
        limitations=["Same-machine isolated endpoint; not Internet or native-animation acceptance.",
                     "SOE-edge impairment is not underlying TCP loss emulation.",
                     "Whole scenario server histograms are separate from client movement windows.",
                     "Histogram quantiles are bucket upper bounds. Stages overlap; do not add or subtract percentiles."])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scenario", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    summary = summarize(args.scenario)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(dict(output=str(args.output), failedChecks=summary["failedChecks"],
                          transportWindows=summary["observedTransportWindows"])))


if __name__ == "__main__":
    main()
