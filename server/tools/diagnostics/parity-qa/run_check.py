"""Run an isolated QA command and retain commit, config, timing and TRX evidence.

Heavy commands should specify --team-root and --build-slot (repeatable) so a
failed shared-slot claim prevents execution. Acquired slots release on exit.
Results must use a fresh directory so earlier binaries/results cannot be mistaken
for a new run. Do not pass credentials in command arguments.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET


SAFE_CONFIGURATION_KEYS = (
    "CRANBERRY_WEAPON_TABLE", "CRANBERRY_PROJECTILE_TABLE",
    "CRANBERRY_FAST_LONG_GUN_DRAW", "CRANBERRY_RELOAD_TIMED",
    "CRANBERRY_WIELD_FIRST_PICKUP", "CRANBERRY_PEER_RELAY",
    "CRANBERRY_PEER_COALESCE", "CRANBERRY_MOVE_PRESET",
    "CranberryDataCheck", "DOTNET_ROLL_FORWARD",
)


def utc() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def git(repo: Path, *args: str) -> str:
    return subprocess.check_output(
        ["git", "-C", str(repo), *args], text=True, encoding="utf-8"
    ).strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--config-note", required=True)
    parser.add_argument("--candidate-commit", help="Source commit of a separately supplied binary under test")
    parser.add_argument("--team-root", type=Path)
    parser.add_argument("--build-slot", choices=("1", "2"), action="append", default=[])
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("a command after -- is required")
    if args.build_slot and args.team_root is None:
        parser.error("--build-slot requires --team-root")
    acquired = []
    team = [os.sys.executable, str(args.team_root / "team.py")] if args.team_root else []
    try:
        for slot in sorted(set(args.build_slot)):
            resource = "build/slot-" + slot
            claim = subprocess.run([*team, "claim", "--role", "qa", "--resource", resource])
            if claim.returncode:
                return claim.returncode
            acquired.append(resource)
        return run_command(args, command)
    finally:
        for resource in reversed(acquired):
            subprocess.run([*team, "release", "--role", "qa", "--resource", resource], check=True)


def run_command(args: argparse.Namespace, command: list[str]) -> int:
    repo = args.repo.resolve()
    results = args.results.resolve()
    results.mkdir(parents=True, exist_ok=False)
    record = {
        "startedUtc": utc(), "repository": str(repo),
        "commit": git(repo, "rev-parse", "HEAD"),
        "candidateCommit": args.candidate_commit or git(repo, "rev-parse", "HEAD"),
        "branch": git(repo, "branch", "--show-current"),
        "worktreeStatusBefore": git(repo, "status", "--porcelain=v1"),
        "command": command, "commandWindows": subprocess.list2cmdline(command),
        "configurationNote": args.config_note,
        "safeEnvironment": {key: os.environ.get(key) for key in SAFE_CONFIGURATION_KEYS},
        "resultsDirectory": str(results),
    }
    record_path = results / "run.json"
    record_path.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    started = time.monotonic()
    log_path = results / "output.log"
    with log_path.open("wb") as log:
        process = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT)
    record.update(endedUtc=utc(), durationSeconds=round(time.monotonic() - started, 3),
                  exitCode=process.returncode)
    record["outputSha256"] = hashlib.sha256(log_path.read_bytes()).hexdigest()
    record["trx"] = []
    for trx in sorted(results.rglob("*.trx")):
        tree = ET.parse(trx)
        ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
        counters = tree.find("t:ResultSummary/t:Counters", ns)
        failed = []
        for result in tree.findall("t:Results/t:UnitTestResult", ns):
            if result.get("outcome") == "Failed":
                failed.append({"name": result.get("testName"),
                               "message": result.findtext("t:Output/t:ErrorInfo/t:Message", namespaces=ns)})
        record["trx"].append({"file": str(trx),
                              "counters": counters.attrib if counters is not None else None,
                              "failed": failed})
    record_path.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"commit": record["commit"], "exitCode": process.returncode,
                      "durationSeconds": record["durationSeconds"],
                      "resultsDirectory": str(results),
                      "trxCounters": [item["counters"] for item in record["trx"]]}, indent=2))
    return process.returncode


if __name__ == "__main__":
    raise SystemExit(main())
