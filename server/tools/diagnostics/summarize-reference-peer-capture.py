#!/usr/bin/env python3
"""Read an already decoded 1087 friend-server packets_*.log; never replay its bytes.
Reports capture arrival and embedded movement-time deltas separately. Different PC clocks
cannot establish one-way latency. Packet numbers here are explicitly 1087, not August 1148.
"""
import argparse, collections, hashlib, json, math, pathlib, re, struct

LINE = re.compile(r'^(\d+) .*?ZONE (s2c|c2s) .*?ch=(\d+) .*?hex=([0-9a-fA-F]+)$')

def uint2b(data, offset):
    size=1+(data[offset]&3)
    if offset+size>len(data): raise ValueError('truncated transient ID')
    return int.from_bytes(data[offset:offset+size],'little')>>2, offset+size

def stats(values):
    values=sorted(values)
    def q(p): return values[min(len(values)-1,math.ceil(len(values)*p)-1)] if values else None
    return {'n':len(values),'p50':q(.5),'p95':q(.95),'p99':q(.99),'max':max(values) if values else None}

def motion(data, offset):
    if offset+7>len(data): raise ValueError('truncated movement header')
    mask, tick, version=struct.unpack_from('<HIB',data,offset)
    flags=None
    if mask&1: flags,_=uint2b(data,offset+7)
    return {'mask':mask,'tick':tick,'version':version,'flags':flags}

def summarize(path):
    packets=[]; malformed=0
    with path.open(encoding='utf-8-sig') as stream:
        for number,line in enumerate(stream,1):
            match=LINE.match(line.strip())
            if match:
                try:
                    packets.append((number,int(match[1]),match[2],int(match[3]),bytes.fromhex(match[4])))
                except ValueError: malformed+=1
    pcs={}; versions=collections.Counter(); own=[]; peers=collections.defaultdict(list)
    remote_subs=collections.Counter(); updates=collections.Counter(); fire_payloads=collections.Counter()
    mode_indices=collections.Counter(); remote_times=[]
    for number,at,direction,channel,data in packets:
        if direction=='s2c' and channel==0 and data[0]==0xd6:
            try:
                transient,_=uint2b(data,9)
                pcs.setdefault(transient,{'spawnLine':number,'spawnMs':at})
            except (IndexError,ValueError): malformed+=1
    for number,at,direction,channel,data in packets:
        try:
            if direction=='c2s' and channel==2:
                own.append(dict(motion(data,0),at=at,line=number))
            if direction!='s2c' or channel!=0: continue
            if data[:2]==b'\x0f\x56' and len(data)==11: versions[data[10]]+=1
            if data[0]==0x79:
                transient,offset=uint2b(data,1)
                if transient in pcs and at>=pcs[transient]['spawnMs']:
                    peers[transient].append(dict(motion(data,offset),at=at,line=number))
            if data[:2]==b'\x83\x00' and len(data)>8 and data[6]==0x15:
                sub=data[7]; remote_subs[sub]+=1
                owner,offset=uint2b(data,8)
                remote_times.append(struct.unpack_from('<I',data,2)[0])
                if sub==4 and len(data)>=offset+9:
                    kind=data[offset]; updates[kind]+=1; payload=data[offset+9:]
                    if kind==1: fire_payloads[(len(payload),payload[0] if payload else -1)]+=1
                    if kind==6: mode_indices[payload.hex()]+=1
        except (IndexError,ValueError,struct.error): malformed+=1
    def describe(records):
        if not records: return None
        gaps=[b['at']-a['at'] for a,b in zip(records,records[1:])]
        deltas=[((b['tick']-a['tick']+2**31)%2**32)-2**31 for a,b in zip(records,records[1:])]
        elapsed=records[-1]['at']-records[0]['at']
        return {'records':len(records),'firstLine':records[0]['line'],'lastLine':records[-1]['line'],
            'spanMs':elapsed,'averageHzOverSpan':(len(records)-1)*1000/elapsed if elapsed else None,
            'arrivalGapMs':stats(gaps),'clientTimestampDeltaMs':stats(deltas),
            'sameArrivalMs':sum(g==0 for g in gaps),'backwardClientTimes':sum(d<0 for d in deltas),
            'versionCounts':dict(collections.Counter(r['version'] for r in records)),
            'maskCounts':{hex(k):v for k,v in collections.Counter(r['mask'] for r in records).items()},
            'flagCountsWhenPresent':{hex(k):v for k,v in collections.Counter(r['flags'] for r in records if r['flags'] is not None).items()}}
    return {'source':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
        'protocol':'1087; reference client 0.23.4.161178; NOT August packet numbering',
        'decodedZonePackets':len(packets),'malformedRelevantPackets':malformed,'spawnedPcs':len(pcs),
        'movementVersionAnnounces':dict(versions),'ownMovement':describe(own),
        'peerMovement':[dict(peer=i+1,spawnLine=pcs[transient]['spawnLine'],**describe(records))
            for i,(transient,records) in enumerate(peers.items())],
        'remoteWeaponSubs':dict(remote_subs),'remoteWeaponUpdates':dict(updates),
        'remoteFirePayloadLengthsAndFlags':{str(k):v for k,v in fire_payloads.items()},
        'switchFireModeIndexBytes':dict(mode_indices),'remoteWeaponGameTime':stats(remote_times),
        'limitations':'Already-decoded historical capture, not a new paired-client observation. Excludes remote movement before the PC spawn. Arrival gaps include capture/network batching; embedded timestamps belong to senders. No native FPS or one-way latency inferred.'}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('log',type=pathlib.Path)
    parser.add_argument('--output',type=pathlib.Path,required=True); args=parser.parse_args()
    args.output.write_text(json.dumps(summarize(args.log),indent=2),encoding='utf-8')
