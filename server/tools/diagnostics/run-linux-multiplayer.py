#!/usr/bin/env python3
"""Run disposable Linux SOE/WSS fixtures with separate server/client PIDs and /proc evidence.
Never uses production accounts, ports or state. Output must be new. Loopback timing is not WAN/native rendering.
"""
import argparse, hashlib, json, os, pathlib, subprocess, sys, time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--bots', type=int, default=2)
    parser.add_argument('--menus', type=int, default=0)
    parser.add_argument('--seconds', type=int, default=30)
    parser.add_argument('--menu-only', action='store_true')
    parser.add_argument('--match-cycles', type=int, default=1)
    parser.add_argument('--matches', type=int, default=1)
    parser.add_argument('--mixed-modes', action='store_true')
    parser.add_argument('--server-cpus', help='Optional measured CPU list, e.g. 0-7; never inferred from CPU numbers')
    parser.add_argument('--client-cpus')
    args = parser.parse_args()
    if sys.platform != 'linux': parser.error('Run on Linux')
    binary = args.binary.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    os.chmod(output, 0o700)
    server_dir, client_dir = output / 'server', output / 'clients'
    server_dir.mkdir(); client_dir.mkdir()
    command = [str(binary), '--bots', str(args.bots), '--menu-bots', str(args.menus), '--seconds', str(args.seconds)]
    server_cmd = command + ['--server-only', '--tls-fixture', '--output', str(server_dir)]
    client_cmd = command + ['--fixture', str(server_dir / 'endpoint.json'), '--unreliable-movement', '--output', str(client_dir)]
    if args.menu_only: client_cmd += ['--menu-only']
    if args.match_cycles != 1: client_cmd += ['--match-cycles', str(args.match_cycles)]
    if args.matches > 1:
        server_cmd += ['--matches', str(args.matches)]
        client_cmd += ['--multi-match', '--matches', str(args.matches)]
    if args.mixed_modes: client_cmd += ['--mixed-modes']
    if args.server_cpus: server_cmd = ['taskset', '-c', args.server_cpus] + server_cmd
    if args.client_cpus: client_cmd = ['taskset', '-c', args.client_cpus] + client_cmd
    identity = {'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()), 'binarySha256':hashlib.sha256(binary.read_bytes()).hexdigest(),
                'serverCommand':server_cmd, 'clientCommand':client_cmd, 'transport':'loopback authenticated WSS',
                'nativeClients':0, 'cpuCount':os.cpu_count(), 'serverCpus':args.server_cpus, 'clientCpus':args.client_cpus,
                'assemblySha256': {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in binary.parent.glob('Cranberry*.dll')}}
    (output / 'identity.json').write_text(json.dumps(identity, indent=2))
    processes = []
    server = client = None
    code = 1
    try:
        with (output / 'server-console.log').open('w') as slog, (output / 'client-console.log').open('w') as clog, (output / 'sampler.log').open('w') as mlog:
            env = dict(os.environ, DOTNET_gcServer='1')
            server = subprocess.Popen(server_cmd, stdout=slog, stderr=subprocess.STDOUT, env=env); processes.append(server)
            sampler = pathlib.Path(__file__).with_name('sample-linux-host.py')
            if sampler.exists():
                processes.append(subprocess.Popen([sys.executable,str(sampler),'--pid',str(server.pid),'--interval','1','--samples','3600','--output',str(output/'server-os.jsonl')],stdout=mlog,stderr=subprocess.STDOUT))
            expires = time.monotonic() + 900
            while not (server_dir / 'endpoint.json').exists():
                if server.poll() is not None or time.monotonic() > expires: raise RuntimeError('Fixture failed to start; inspect server-console.log')
                time.sleep(.25)
            client = subprocess.Popen(client_cmd, stdout=clog, stderr=subprocess.STDOUT, env=env); processes.append(client)
            if sampler.exists():
                processes.append(subprocess.Popen([sys.executable,str(sampler),'--pid',str(client.pid),'--interval','1','--samples','3600','--output',str(output/'client-os.jsonl')],stdout=mlog,stderr=subprocess.STDOUT))
            expires = time.monotonic() + 1800 + args.seconds
            while client.poll() is None:
                if time.monotonic() > expires: raise TimeoutError('Bounded scenario deadline exceeded')
                memory = {k: int(v.split()[0])*1024 for k,v in (line.split(':',1) for line in pathlib.Path('/proc/meminfo').read_text().splitlines()) if k in ('MemTotal','MemAvailable')}
                if memory['MemAvailable'] < memory['MemTotal']*.20: raise RuntimeError('Stopped at 20% host memory headroom')
                time.sleep(1)
            code = client.returncode
    finally:
        (server_dir / 'stop.request').write_text('stop')
        if server is not None:
            try: server.wait(timeout=15)
            except subprocess.TimeoutExpired: server.terminate()
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
        (output / 'completion.json').write_text(json.dumps({'clientExit':None if client is None else client.returncode,
            'serverExit':None if server is None else server.returncode, 'success':code == 0}, indent=2))
    return code


if __name__ == '__main__': raise SystemExit(main())
