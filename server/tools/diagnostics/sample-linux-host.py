#!/usr/bin/env python3
"""Read-only /proc sampler for an existing Linux game host. No external packages.

Example: python3 sample-linux-host.py --pid 1234 --output /tmp/cranberry-os.jsonl
Writes a NEW private JSONL file. Does not read credentials, command lines or payloads.
Network statistics belong to the sampler's network namespace, not to the process.
Application queues, GC and end-to-end latency need separate instrumentation.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import sys
import time


def stat_record(text: str) -> dict:
    # comm may contain spaces and ')'; fields after its LAST ')' start at field 3.
    end = text.rfind(')')
    fields = text[end + 2:].split()
    if end < 0 or len(fields) < 22:
        raise ValueError('Incomplete proc stat record')
    return {
        'cpu_ticks': int(fields[11]) + int(fields[12]),
        'start_ticks': int(fields[19]),
        'major_faults': int(fields[9]),
        'rss_pages': int(fields[21]),
        'last_cpu': int(fields[36]) if len(fields) > 36 else None,
    }


def colon_values(path: Path) -> dict:
    values = {}
    for line in path.read_text().splitlines():
        key, _, value = line.partition(':')
        parts = value.split()
        if parts and parts[0].isdigit():
            values[key] = int(parts[0]) * (1024 if len(parts) > 1 and parts[1] == 'kB' else 1)
    return values


def network_values(proc: Path) -> dict:
    devices = {}
    for line in (proc / 'net/dev').read_text().splitlines()[2:]:
        name, _, body = line.partition(':')
        v = [int(x) for x in body.split()]
        if len(v) >= 16:
            devices[name.strip()] = dict(zip(
                ('rx_bytes', 'rx_packets', 'rx_errors', 'rx_drops',
                 'tx_bytes', 'tx_packets', 'tx_errors', 'tx_drops'),
                v[:4] + v[8:12]))
    protocols = {}
    lines = (proc / 'net/snmp').read_text().splitlines()
    for first, second in zip(lines[::2], lines[1::2]):
        keys, vals = first.split(), second.split()
        if not keys or keys[0] != vals[0]:
            continue
        if keys[0] in ('Tcp:', 'Udp:'):
            protocols[keys[0][:-1]] = dict(zip(keys[1:], map(int, vals[1:])))
    return {'interfaces': devices, 'protocols_ipv4': protocols}


def read_sample(proc: Path, pid: int) -> dict:
    process = proc / str(pid)
    identity = stat_record((process / 'stat').read_text())
    status = colon_values(process / 'status')
    cpu = {}
    host_context_switches = None
    for line in (proc / 'stat').read_text().splitlines():
        parts = line.split()
        if parts and parts[0] == 'ctxt': host_context_switches = int(parts[1])
        if parts and (parts[0] == 'cpu' or parts[0][3:].isdigit() and parts[0].startswith('cpu')):
            # Exclude guest/guest_nice, already included in user/nice.
            v = [int(x) for x in parts[1:9]]
            cpu[parts[0]] = {'total': sum(v), 'idle': v[3],
                            'iowait': v[4], 'steal': v[7]}
    threads = {}
    for task in (process / 'task').iterdir():
        try:
            threads[task.name] = stat_record((task / 'stat').read_text())
            task_status = colon_values(task / 'status')
            threads[task.name]['context_switches'] = {k: task_status.get(k, 0) for k in
                ('voluntary_ctxt_switches', 'nonvoluntary_ctxt_switches')}
        except (OSError, ValueError):
            pass  # Threads can exit while enumerating.
    try:
        io = colon_values(process / 'io')
    except PermissionError:
        io = None  # May require the service UID or root; do not elevate automatically.
    # Detect PID reuse during a sample rather than mixing two processes.
    if stat_record((process / 'stat').read_text())['start_ticks'] != identity['start_ticks']:
        raise ProcessLookupError('Target PID changed while sampling')
    return {
        'utc': datetime.now(timezone.utc).isoformat(), 'monotonic_seconds': time.monotonic(),
        'pid': pid, 'process': identity,
        'memory_bytes': {k: status.get(k) for k in ('VmRSS', 'VmHWM', 'RssAnon', 'VmSwap')},
        'thread_count': status.get('Threads'), 'threads': threads, 'io': io,
        'host_context_switches': host_context_switches,
        'host_cpu': cpu, 'host_memory_bytes': colon_values(proc / 'meminfo'),
        'host_loadavg': (proc / 'loadavg').read_text().strip(),
        'network_namespace': network_values(proc),
    }


def counter_rates(current: dict, previous: dict, seconds: float) -> dict:
    # A reset/hotplug produces null, not an invented negative rate or a huge spike.
    return {k: ((v - previous[k]) / seconds if k in previous and v >= previous[k] else None)
            for k, v in current.items()}


def add_rates(current: dict, previous: dict, hz: int) -> None:
    dt = current['monotonic_seconds'] - previous['monotonic_seconds']
    if dt <= 0:
        return
    if current['process']['start_ticks'] != previous['process']['start_ticks']:
        raise ProcessLookupError('Target PID was reused; refusing to combine measurements')
    current['interval_seconds'] = dt
    current['process_cpu_percent_one_core'] = max(0, current['process']['cpu_ticks']
                                                 - previous['process']['cpu_ticks']) / hz / dt * 100
    current['major_faults_per_second'] = counter_rates(
        {'major_faults': current['process']['major_faults']}, previous['process'], dt)['major_faults']
    busy = []
    for tid, task in current['threads'].items():
        old = previous['threads'].get(tid)
        if old and old['start_ticks'] == task['start_ticks']:
            busy.append({'tid': int(tid), 'last_cpu': task['last_cpu'],
                         'context_switches_per_second': counter_rates(task['context_switches'], old['context_switches'], dt),
                         'cpu_percent_one_core':
                         max(0, task['cpu_ticks'] - old['cpu_ticks']) / hz / dt * 100})
    current['busiest_threads'] = sorted(busy, key=lambda x: x['cpu_percent_one_core'], reverse=True)[:10]
    if current['host_context_switches'] is not None and previous['host_context_switches'] is not None:
        current['host_context_switches_per_second'] = max(0, current['host_context_switches'] - previous['host_context_switches']) / dt
    current['host_cpu_percent'] = {}
    for name, v in current['host_cpu'].items():
        old = previous['host_cpu'].get(name)
        if not old or v['total'] <= old['total']:
            continue
        total = v['total'] - old['total']
        idle, wait, steal = [max(0, v[k] - old[k]) / total * 100 for k in ('idle', 'iowait', 'steal')]
        current['host_cpu_percent'][name] = {'busy': max(0, 100 - idle - wait - steal),
                                           'iowait': wait, 'steal': steal}
    current['network_rates_per_second'] = {}
    for name, v in current['network_namespace']['interfaces'].items():
        old = previous['network_namespace']['interfaces'].get(name, {})
        current['network_rates_per_second'][name] = counter_rates(v, old, dt)
    if current['io'] is not None and previous['io'] is not None:
        current['process_io_rates_per_second'] = counter_rates(current['io'], previous['io'], dt)
    # Select counters only: TCP CurrEstab is a gauge, not a monotonic counter.
    current['network_error_rates_per_second_ipv4'] = {}
    for protocol, keys in [('Tcp', ('RetransSegs', 'InErrs', 'OutRsts')),
                           ('Udp', ('InErrors', 'RcvbufErrors', 'SndbufErrors', 'NoPorts'))]:
        now = current['network_namespace']['protocols_ipv4'].get(protocol, {})
        old = previous['network_namespace']['protocols_ipv4'].get(protocol, {})
        current['network_error_rates_per_second_ipv4'][protocol] = counter_rates(
            {k: now[k] for k in keys if k in now}, old, dt)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', required=True, type=int)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--interval', type=float, default=5)
    parser.add_argument('--samples', type=int, default=120, help='Number of samples; default 10 minutes')
    args = parser.parse_args()
    if sys.platform != 'linux':
        parser.error('Run on the Linux server; this collector reads Linux /proc.')
    if args.pid <= 0 or not math.isfinite(args.interval) or args.interval < 0.5 or args.samples < 2:
        parser.error('Use a positive PID, finite interval >= 0.5 seconds, and samples >= 2.')
    proc = Path('/proc')
    hz = os.sysconf('SC_CLK_TCK')
    # O_EXCL refuses overwrite and symlinks. Private mode even with a permissive umask.
    fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    previous = None
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as output:
            for index in range(args.samples):
                start = time.monotonic()
                try:
                    sample = read_sample(proc, args.pid)
                    if previous is not None:
                        add_rates(sample, previous, hz)
                except (OSError, ValueError) as ex:
                    output.write(json.dumps({'utc': datetime.now(timezone.utc).isoformat(),
                                             'collection_error': str(ex)}) + '\n')
                    return 1
                sample['clock_ticks_per_second'] = hz
                output.write(json.dumps(sample, separators=(',', ':')) + '\n')
                output.flush()
                previous = sample
                if index + 1 < args.samples:
                    time.sleep(max(0, args.interval - (time.monotonic() - start)))
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
