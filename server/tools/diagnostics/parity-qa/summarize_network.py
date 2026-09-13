"""Summarize a completed QA loopback run without exporting session/account fields.

Preserves separate movement windows and per-snapshot transport percentiles.
Percentiles are never averaged or subtracted to infer an unmeasured stage.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import json
from pathlib import Path


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    root = args.run.resolve()
    run = read(root / "run.json")
    scenario = root / "scenario"
    completion = read(scenario / "completion.json")
    client = read(scenario / "clients/result.json")
    server = read(scenario / "server/server-profile.json")
    samples = [json.loads(line) for line in
               (scenario / "server/production-metrics.jsonl").read_text(encoding="utf-8-sig").splitlines()
               if line.strip()]
    windows = []
    counters = Counter()
    seconds = 0.0
    for sample in samples:
        transport = sample.get("productionDiagnostics", {}).get("Transport", {})
        if not transport.get("enabled"):
            continue
        counters.update(transport.get("counters", {}))
        seconds += transport["windowSeconds"]
        windows.append({"utc": sample["utc"], "windowSeconds": transport["windowSeconds"],
                        "connections": transport["connections"],
                        "queues": transport.get("queues"), "timings": transport.get("timings")})
    sampled_process = None
    if len(samples) > 1:
        first, last = samples[0], samples[-1]
        elapsed = (datetime.fromisoformat(last["utc"]) - datetime.fromisoformat(first["utc"])).total_seconds()
        if elapsed > 0:
            cpu = last["processCpuSeconds"] - first["processCpuSeconds"]
            sampled_process = {
                "elapsedSeconds": elapsed, "cpuSeconds": cpu, "meanCpuCores": cpu / elapsed,
                "meanMachineCpuPercent": cpu / elapsed / server["process"]["logicalProcessors"] * 100,
                "gcPauseMs": last["gcPauseMs"] - first["gcPauseMs"],
                "allocatedBytes": last["allocatedBytes"] - first["allocatedBytes"],
                "peakSampledRssBytes": max(sample["rss"] for sample in samples),
            }
    checks = [{"name": check["name"], "pass": check["pass"]} for check in client["checks"]]
    result = {
        "candidateCommit": run.get("candidateCommit", run["commit"]),
        "runnerCommit": run["commit"], "resultsDirectory": str(root),
        "configurationNote": run["configurationNote"],
        "completion": {key: value for key, value in completion.items() if key != "serverPid"},
        "population": client["population"], "clientDurationSeconds": client["durationSeconds"],
        "movementTransport": client["movementTransport"], "loopbackTls": client["loopbackTls"],
        "counts": {"total": len(checks), "passed": sum(check["pass"] for check in checks),
                   "failed": sum(not check["pass"] for check in checks)},
        "failedChecks": [check["name"] for check in checks if not check["pass"]],
        "serverErrors": len(server["errors"]),
        "serverProcess": {key: value for key, value in server["process"].items() if key != "pid"},
        "clientProcess": {key: value for key, value in client["clientProcess"].items() if key != "pid"},
        "sampledServerProcess": sampled_process,
        "gatewayWireCounters": dict(counters), "gatewayDiagnosticSeconds": seconds,
        "gatewayMeanEgressBytesPerSecond": counters.get("SentBytes", 0) / seconds if seconds else None,
        "gatewayMeanEgressBytesPerSecondPerConfiguredPeer":
            counters.get("SentBytes", 0) / seconds / client["population"] if seconds else None,
        "poseTimings": server["poseTimings"],
        "movementWindows": [{key: value for key, value in window.items() if key != "bots"}
                            for window in client["movementWindows"]],
        "transportWindows": windows,
        "limits": ["Protocol-only, own loopback, server and generator share physical hardware.",
                   "Gateway wire counters cover sampled diagnostics; server Bytes counts both application directions.",
                   "Movement quantiles are worst per observer within each named window, not pooled quantiles.",
                   "Transport quantiles remain per diagnostic snapshot, not averaged overall percentiles.",
                   "Process profile is the final periodic sample; sampled CPU uses first-to-last sample deltas."],
    }
    path = root / "network-metrics.json"
    with path.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
        stream.write("\n")
    print(json.dumps({"path": str(path), "counts": result["counts"], "failedChecks": result["failedChecks"],
                      "meanCpuCores": sampled_process["meanCpuCores"] if sampled_process else None,
                      "gatewayMeanEgressBytesPerSecond": result["gatewayMeanEgressBytesPerSecond"]}, indent=2))


if __name__ == "__main__":
    main()
