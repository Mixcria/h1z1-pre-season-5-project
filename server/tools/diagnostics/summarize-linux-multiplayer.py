#!/usr/bin/env python3
"""Summarize disposable fixture evidence. No native-rendering or public capacity claim is inferred."""
import argparse, datetime, importlib.util, json, math, pathlib

spec=importlib.util.spec_from_file_location('metrics',pathlib.Path(__file__).with_name('summarize-production-metrics.py'))
metrics=importlib.util.module_from_spec(spec); spec.loader.exec_module(metrics)

def lower(value):
    if isinstance(value,dict): return {k[:1].lower()+k[1:]:lower(v) for k,v in value.items()}
    if isinstance(value,list): return [lower(v) for v in value]
    return value

def rows(path):
    if path.exists():
        for line in path.read_text(encoding='utf-8-sig').splitlines():
            try: yield lower(json.loads(line))
            except json.JSONDecodeError: pass # last line can be an in-progress append

def quantile(values,q):
    values=sorted(v for v in values if isinstance(v,(int,float)) and math.isfinite(v))
    return values[min(len(values)-1,math.ceil(len(values)*q)-1)] if values else None

def distribution(values):
    values=list(values)
    return {'p50':quantile(values,.5),'p95':quantile(values,.95),'p99':quantile(values,.99),'max':max(values) if values else None}

def instant(text):
    return datetime.datetime.fromisoformat(text.replace('Z','+00:00')).timestamp()

def steady_window(root,result,identity,osdata):
    """Only label windows whose bounds the client recorded; exclude provisioning CPU."""
    if 'idleSeconds' in result and 'menuPopulation' in result:
        end=instant(result['completedUtc']); start=end-result['idleSeconds']; name='menu-steady'
    elif (root/'clients/phase.json').exists():
        phase=json.loads((root/'clients/phase.json').read_text(encoding='utf-8-sig'))
        if phase.get('name')!='simultaneous-ground': return None
        command=identity['clientCommand']; seconds=int(command[command.index('--seconds')+1])
        start=instant(phase['utc']); end=start+seconds; name=phase['name']
    else: return None
    samples=[r for r in osdata if start <= instant(r['utc'])-r['interval_seconds'] and instant(r['utc']) <= end]
    histograms={}; queues={}; tx=[]
    for row in rows(root/'server/production-metrics.jsonl'):
        transport=row.get('productionDiagnostics',{}).get('transport',{})
        seconds=transport.get('windowSeconds',0)
        if not (start <= instant(row['utc'])-seconds and instant(row['utc']) <= end): continue
        def visit(value,path):
            if not isinstance(value,dict): return
            if 'bucketCounts' in value:
                histograms.setdefault(path,metrics.Histogram()).add(value); return
            for k,v in value.items():
                if k!='sessions': visit(v,path+'.'+k if path else k)
        visit(row.get('productionDiagnostics',{}),''); visit(row.get('launcher',{}),'launcher')
        if seconds>0: tx.append(transport.get('counters',{}).get('sentBytes',0)/seconds)
        for k,v in transport.get('queues',{}).items():
            if isinstance(v,(int,float)): queues[k]=max(queues.get(k,0),v)
    return {'name':name,'startUtc':datetime.datetime.fromtimestamp(start,datetime.timezone.utc).isoformat(),
        'endUtc':datetime.datetime.fromtimestamp(end,datetime.timezone.utc).isoformat(),'osSamples':len(samples),
        'processCpuPercentOneCoreIs100':distribution(r['process_cpu_percent_one_core'] for r in samples),
        'busiestThreadPercentOneCoreIs100':distribution(r['busiest_threads'][0]['cpu_percent_one_core'] for r in samples if r.get('busiest_threads')),
        'hostCpuPercent':distribution(r['host_cpu_percent']['cpu']['busy'] for r in samples),
        'gatewayTxPayloadBytesPerSecond':distribution(tx),'queueHighWaterSampled':queues,
        'histogramTimings':{k:v.result() for k,v in histograms.items()}}

def summarize(root):
    result=json.loads((root/'clients/result.json').read_text(encoding='utf-8-sig'))
    histograms={}; counters={}; queues={}; populations={}; per_cpu={}; osdata=[]
    gateway_tx=[]; gateway_rx=[]; gc_deltas=[]; previous_gc=None; phases=set()
    for row in rows(root/'server/production-metrics.jsonl'):
        diagnostic=row.get('productionDiagnostics',{})
        transport=diagnostic.get('transport',{}); window=transport.get('windowSeconds',0)
        if window>0:
            gateway_tx.append(transport.get('counters',{}).get('sentBytes',0)/window)
            gateway_rx.append(transport.get('counters',{}).get('receivedBytes',0)/window)
        gc=row.get('gcPauseMs')
        if gc is not None and previous_gc is not None: gc_deltas.append(max(0,gc-previous_gc))
        previous_gc=gc
        for match in diagnostic.get('zone',{}).get('population',{}).get('publicMatches',[]):
            phases.add(str(match.get('phase')))
        def visit(value,path):
            if not isinstance(value,dict): return
            if 'bucketCounts' in value:
                histograms.setdefault(path,metrics.Histogram()).add(value); return
            for k,v in value.items():
                if k != 'sessions': visit(v,path+'.'+k if path else k)
        visit(diagnostic,''); visit(row.get('launcher',{}),'launcher')
        for k,v in diagnostic.get('transport',{}).get('counters',{}).items():
            if isinstance(v,(float,int)): counters[k]=counters.get(k,0)+v
        for k,v in diagnostic.get('transport',{}).get('queues',{}).items():
            if isinstance(v,(float,int)): queues[k]=max(queues.get(k,0),v)
        for k,v in diagnostic.get('zone',{}).get('population',{}).items():
            if isinstance(v,(float,int)): populations[k]=max(populations.get(k,0),v)
    for row in rows(root/'server-os.jsonl'):
        if 'interval_seconds' not in row: continue
        osdata.append(row)
        for cpu,values in row.get('host_cpu_percent',{}).items(): per_cpu.setdefault(cpu,[]).append(values['busy'])
    profile=lower(json.loads((root/'server/server-profile.json').read_text()))
    identity=json.loads((root/'identity.json').read_text())
    return {'case':root.name,'success':json.loads((root/'completion.json').read_text())['success'],
        'identity':identity,'nativeClients':0,'steady':steady_window(root,result,identity,osdata),
        'failedChecks':[c for c in result.get('checks',[]) if not c.get('pass')],
        'movementWindows':[{k:v for k,v in w.items() if k not in ('bots','measured')} for w in result.get('movementWindows',[])],
        'menu':result if 'menu' in root.name else None,
        'histogramTimings':{k:v.result() for k,v in histograms.items()},'transportCounters':counters,
        'queueHighWaterSampled':queues,'populationHighWaterSampled':populations,
        'processCpuPercentOneCoreIs100':distribution([r['process_cpu_percent_one_core'] for r in osdata]),
        'busiestThreadPercentOneCoreIs100':distribution([r['busiest_threads'][0]['cpu_percent_one_core'] for r in osdata if r.get('busiest_threads')]),
        'perCpuPercent':{cpu:distribution(v) for cpu,v in per_cpu.items()},
        'gatewayTxPayloadBytesPerSecond':distribution(gateway_tx), 'gatewayRxPayloadBytesPerSecond':distribution(gateway_rx),
        'gcPauseMsPerSample':distribution(gc_deltas),'observedPublicPhases':sorted(phases),
        'hostContextSwitchesPerSecond':distribution(r['host_context_switches_per_second'] for r in osdata if 'host_context_switches_per_second' in r),
        'processDiskReadBytesPerSecond':distribution(r.get('process_io_rates_per_second',{}).get('read_bytes',0) for r in osdata),
        'processDiskWriteBytesPerSecond':distribution(r.get('process_io_rates_per_second',{}).get('write_bytes',0) for r in osdata),
        'hostLoad1':distribution(float(r['host_loadavg'].split()[0]) for r in osdata if r.get('host_loadavg')),
        'tcpRetransmits':sum((r.get('network_error_rates_per_second_ipv4',{}).get('tcp',{}).get('retransSegs') or 0)*r['interval_seconds'] for r in osdata) if osdata else None,
        'profile':profile,'limitations':'OS rates are sampled at 1s; NIC/TCP counters include the network namespace. Whole-scenario stage distributions include zoning. WSS loopback protocol clients do not measure native presentation or WAN.'}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('root',type=pathlib.Path); parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    args.output.write_text(json.dumps(summarize(args.root),indent=2),encoding='utf-8')
