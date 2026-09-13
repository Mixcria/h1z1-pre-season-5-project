"""Extract vehicle observations from the owner's offline Z1BR recording.

SOE framing/RC4 are re-expressed from this project's earlier C:/Project tools.
The old PcapDecode did not reassemble fragments, split bundles or deduplicate.
The known login key works; per-gateway keys come from the recorded login/transfer.
Never connects to the captured service. Keys, tickets and player data stay in
memory. Output is limited to map/vehicle poses, frame references and stream audit.
This is a ClientProtocol_1315 capture reader, not an August packet decoder.
"""
import base64, collections, csv, json, pathlib, re, struct, zlib, argparse, math
import hashlib, subprocess, tempfile

ROOT = pathlib.Path(r'C:\Aug2017\out\vehicle-spawns-20260911')
CSV = None
LOGIN_KEY = base64.b64decode('F70IaxuU8C/w7FPXY1ibXw==')

class RC4:
    def __init__(self, key):
        self.s = list(range(256)); self.i = self.j = 0
        j = 0
        for i in range(256):
            j = (j + self.s[i] + key[i % len(key)]) & 255
            self.s[i], self.s[j] = self.s[j], self.s[i]
    def apply(self, data):
        s=self.s; i=self.i; j=self.j; out=bytearray()
        for b in data:
            i=(i+1)&255; j=(j+s[i])&255; s[i],s[j]=s[j],s[i]
            out.append(b^s[(s[i]+s[j])&255])
        self.i=i; self.j=j
        return bytes(out)

def chunks(d, off=2):
    while off < len(d):
        size=d[off]; off+=1
        if size==255:
            size=int.from_bytes(d[off:off+2],'big');off+=2
            if size==65535:
                size=int.from_bytes(d[off:off+4],'big');off+=4
        if size<=0 or off+size>len(d): raise ValueError('invalid chunk length')
        yield d[off:off+size]
        off+=size

def reliable(d, crc, compression, nested=False):
    if len(d)<2:return
    op=int.from_bytes(d[:2],'big')
    if op not in (3,9,13):return
    if not nested:
        if crc:d=d[:-crc]
        if compression:
            flag=d[2]
            if flag not in (0,1):raise ValueError('compression flag')
            d=d[:2]+(zlib.decompress(d[3:]) if flag else d[3:])
    if op==3:
        for c in chunks(d):yield from reliable(c,crc,compression,True)
    else:yield int.from_bytes(d[2:4],'big'),op==13,d[4:]

def assemble(rows, direction, crc, compression):
    pending={}; repeats=0
    for frame,t,dr,d in rows:
        if dr!=direction:continue
        for seq,frag,payload in reliable(d,crc,compression):
            if seq in pending:
                if pending[seq][0:2]!=(frag,payload):raise ValueError('conflicting retransmission')
                repeats+=1
            else:pending[seq]=(frag,payload,frame,t)
    result=[];seq=0
    while seq in pending:
        frag,payload,frame,t=pending[seq];start=seq;seq+=1
        if frag:
            if len(payload)<4:raise ValueError('fragment head')
            total=int.from_bytes(payload[:4],'big');payload=payload[4:]
            if total>32*1024*1024:raise ValueError('fragment length')
            while len(payload)<total:
                if seq not in pending:return result,{'stopGap':seq,'duplicates':repeats,'reliable':len(pending)}
                nf,np,ff,tt=pending[seq]
                if not nf:raise ValueError('non-fragment continuation')
                payload+=np;seq+=1;frame=max(frame,ff);t=max(t,tt)
            if len(payload)!=total:raise ValueError('fragment size mismatch')
        result.append((frame,t,start,payload))
    return result,{'stopGap':seq if any(k>=seq for k in pending) else None,'duplicates':repeats,'reliable':len(pending)}

def decode(messages,key,gateway=False,direction='s2c'):
    cipher=RC4(key);out=[]
    for i,(frame,t,seq,d) in enumerate(messages):
        parts=list(chunks(d)) if d[:2]==b'\0\x19' else [d]
        for part in parts:
            if not(gateway and direction=='c2s' and i==0):
                if part[:2]==b'\0\0':part=part[1:]
                part=cipher.apply(part)
            out.append((frame,t,seq,part))
    return out

def read_blob(d,off):
    n=struct.unpack_from('<I',d,off)[0];off+=4
    if n>len(d)-off:raise ValueError('blob length')
    return d[off:off+n],off+n

def vehicle_record(d, zone, frame):
    """1315 c4 prefix measured across all 44 captures; no 1087/1148 naming."""
    if d[:2] != b'\x05\xc4': return None
    if len(d)<123 or d[10:18]!=bytes(8) or d[39:51]!=bytes(12):
        raise ValueError('Unsupported nonempty 1315 lightweight vehicle prefix')
    model=struct.unpack_from('<I',d,19)[0]
    family=struct.unpack_from('<I',d,119)[0]
    if {10060:1,10084:2,10119:3,9588:5}.get(model)!=family:
        raise ValueError('Unexpected 1315 vehicle model/family join')
    position=struct.unpack_from('<3f',d,51);rotation=struct.unpack_from('<4f',d,63)
    if not all(math.isfinite(v) for v in (*position,*rotation)) or abs(sum(v*v for v in rotation)-1)>1e-5:
        raise ValueError('Invalid vehicle transform')
    return dict(frame=frame,zone=zone,vehicleId=family,modelId=model,position=position,rotation=rotation)

def main_map_record(record):
    """The captured Z2 pregame platform lies outside the +/-4096 terrain bounds."""
    return record['zone']=='Z2' and all(-4096<=record['position'][i]<=4096 for i in (0,2))

def run():
    sessions=collections.defaultdict(list)
    for row in csv.reader(CSV.open()):
        if len(row)!=7:continue
        frame=int(row[0]);t=float(row[1]);src,sp,dst,dp=row[2],int(row[3]),row[4],int(row[5]);d=bytes.fromhex(row[6])
        c2s=src.startswith('192.168.');endpoint=(dst,dp,sp) if c2s else (src,sp,dp)
        sessions[endpoint].append((frame,t,'c2s' if c2s else 's2c',d))
    streams={};summary=[];keys=[]
    for endpoint,rows in sorted(sessions.items(),key=lambda p:p[1][0][0]):
        requests=[r for r in rows if r[3][:2]==b'\0\1'];replies=[r for r in rows if r[3][:2]==b'\0\2']
        if len(requests)!=1 or len(replies)!=1:continue
        protocol=requests[0][3][14:].rstrip(b'\0').decode();reply=replies[0][3]
        if protocol=='IpPingServiceUdp_1':continue
        crc=reply[10];compression=int.from_bytes(reply[11:13],'big')
        info={'endpoint':list(endpoint),'protocol':protocol,'crc':crc,'compression':compression}
        # SOE's CRC is the low word of IEEE CRC32 over the LE seed followed by
        # the datagram without its trailer. Verify every protected SOE datagram.
        seed=zlib.crc32(reply[6:10][::-1]);checked=0
        for frame,t,dr,d in rows:
            if d[:2] not in (b'\0\x03',b'\0\x09',b'\0\x0d',b'\0\x15',b'\0\x11'):continue
            if crc!=2 or (zlib.crc32(d[:-2],seed)&65535)!=int.from_bytes(d[-2:],'big'):
                raise ValueError(f'SOE CRC failure at frame {frame}')
            checked+=1
        info['crcVerifiedDatagrams']=checked
        dirs={}
        for direction in ('c2s','s2c'):
            dirs[direction],info[direction]=assemble(rows,direction,crc,compression)
        streams[endpoint]=(protocol,dirs);summary.append(info)
        if protocol.startswith('LoginUdp'):
            for frame,t,seq,d in decode(dirs['s2c'],LOGIN_KEY):
                if d[:1]!=b'\x08':continue
                for m in re.finditer(rb'\d+\.\d+\.\d+\.\d+:\d+',d):
                    address,off=read_blob(d,m.start()-4)
                    ticket,off=read_blob(d,off);key,off=read_blob(d,off)
                    if len(key)!=16 or struct.unpack_from('<I',d,off)[0]!=3:continue
                    keys.append((ticket,key));print('Recovered gateway key from login frame',frame,'for',address.decode())
    output=[]
    for endpoint,(protocol,dirs) in sorted(streams.items(),key=lambda p:p[1][1]['c2s'][0][0]):
        if not protocol.startswith('External'):continue
        first=dirs['c2s'][0][3]
        if b'ClientProtocol_1315' not in first:
            raise ValueError('Expected the captured ClientProtocol_1315 gateway')
        ticket,_=read_blob(first,9)
        matching={key for tk,key in keys if tk==ticket}
        if not matching:
            for record in output:
                d=bytes.fromhex(record['hex'])
                if record['direction']!='s2c' or d[:3]!=bytes.fromhex('05950d'):continue
                at=d.find(ticket)
                if at<4 or struct.unpack_from('<I',d,at-4)[0]!=len(ticket):continue
                candidate,off=read_blob(d,at+len(ticket))
                if len(candidate)==16:
                    matching.add(candidate)
                    print('Recovered transfer key from gateway frame',record['frame'])
        if not matching:
            probe=decode(dirs['s2c'][:1],LOGIN_KEY,True,'s2c')
            if probe and probe[0][3]==b'\x02\x01':
                matching.add(LOGIN_KEY)
                print('Validated legacy static gateway key for',endpoint)
            else:
                print('No login key match for gateway',endpoint);continue
        if len(matching)!=1:raise ValueError('ambiguous key')
        key=matching.pop()
        for direction in ('c2s','s2c'):
            rows=decode(dirs[direction],key,True,direction)
            for frame,t,seq,d in rows:
                output.append(dict(frame=frame,time=t,seq=seq,endpoint=list(endpoint),direction=direction,hex=d.hex()))
    zones={};vehicles=[]
    for r in sorted(output,key=lambda r:r['frame']):
        if r['direction']!='s2c':continue
        d=bytes.fromhex(r['hex']);endpoint=tuple(r['endpoint'])
        if d[:2]==b'\x05\x0b':
            zone,_=read_blob(d,2);zones[endpoint]=zone.decode('ascii')
        record=vehicle_record(d,zones.get(endpoint),r['frame'])
        if record is not None:
            if record['zone'] is None:raise ValueError('Vehicle before known zone')
            vehicles.append(record)
    document={'schema':'cranberry/captured-vehicle-observations/1',
              'source':{'file':str(PCAP),'sha256':hashlib.sha256(PCAP.read_bytes()).hexdigest(),
                        'protocol':'ClientProtocol_1315'},
              'note':'Observed poses, not a complete August retail spawn catalogue or spawn probability measurement.',
              'counts':{'vehicles':len(vehicles),'mainZ2':sum(map(main_map_record,vehicles)),
                        'byZone':dict(collections.Counter(r['zone'] for r in vehicles))},
              'streams':summary,'vehicles':vehicles}
    OUTPUT.write_text(json.dumps(document,indent=1)+'\n',encoding='utf-8')
    print(json.dumps(document['counts']))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pcap',type=pathlib.Path,default=pathlib.Path(r'C:\Project\z1br.pcapng'))
    parser.add_argument('--out',type=pathlib.Path,required=True)
    parser.add_argument('--tshark',default=r'C:\Program Files\Wireshark\tshark.exe')
    args=parser.parse_args();PCAP=args.pcap;OUTPUT=args.out
    with tempfile.TemporaryDirectory(prefix='cranberry-vehicle-capture-') as temporary:
        CSV=pathlib.Path(temporary)/'udp.csv'
        command=[args.tshark,'-r',str(PCAP),'-Y','udp','-T','fields','-E','separator=,']
        for field in ['frame.number','frame.time_epoch','ip.src','udp.srcport','ip.dst','udp.dstport','udp.payload']:
            command.extend(['-e',field])
        with CSV.open('wb') as handle:subprocess.run(command,stdout=handle,check=True)
        run()
