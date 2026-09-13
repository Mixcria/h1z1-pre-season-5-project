"""Create an August compatibility candidate at unchanged retail X/Z locations.

Uses downward visual-mesh/solid-terrain surface queries, not a PhysX simulation.
Ambiguous, buried and obstructed placements are reported and never relocated.
"""
import argparse, collections, hashlib, json, math, pathlib, struct, sys
import xml.etree.ElementTree as ET
import numpy as np

REPO=pathlib.Path(__file__).resolve().parents[2]
sys.path[:0]=[str(REPO/'tools/world'),str(REPO/'tools/pack')]
import packread
from vehicle_geometry import axes_for,box,overlaps,load_bounds,DRIVABLE_MODELS,STATIC_MODELS
from vehicle_structures import mesh_triangles,blocking_triangle
from vehicle_terrain import VehicleTerrain

def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def f32(value):return struct.unpack('<f',struct.pack('<f',value))[0]

def terrain_height(terrain,x,z):
    ix,iz=math.floor(x),math.floor(z);u,v=x-ix,z-iz
    h00,m0,m1=terrain.sample(ix,iz)
    if 127 in (m0,m1):return None
    h10=terrain.sample(ix+1,iz)[0];h01=terrain.sample(ix,iz+1)[0];h11=terrain.sample(ix+1,iz+1)[0]
    cx,cz=ix//256*256,iz//256*256;lx,lz=ix-cx,iz-cz
    index=((lz//64*4+lx//64)*65*65+lx%64*65+lz%64)*4
    diagonal=bool(terrain.cache[cx,cz][index+2]&0x80)
    if diagonal:
        return h00+u*(h10-h00)+v*(h11-h10) if u>=v else h00+v*(h01-h00)+u*(h11-h01)
    return h00+u*(h10-h00)+v*(h01-h00) if u+v<=1 else h11+(1-u)*(h01-h11)+(1-v)*(h10-h11)

def triangle_heights(triangles,x,z,ceiling):
    p=triangles[:,0,:];a=triangles[:,1,:]-p;b=triangles[:,2,:]-p
    det=a[:,0]*b[:,2]-a[:,2]*b[:,0]
    mask=np.abs(det)>1e-10
    safe=np.where(mask,det,1.)
    u=((x-p[:,0])*b[:,2]-(z-p[:,2])*b[:,0])/safe
    v=(a[:,0]*(z-p[:,2])-a[:,2]*(x-p[:,0]))/safe
    h=p[:,1]+u*a[:,1]+v*b[:,1]
    normal=np.cross(a,b)
    mask&=(u>=-1e-7)&(v>=-1e-7)&(u+v<=1+1e-7)&(h<=ceiling+1e-5)
    mask&=normal[:,1]**2>0.25*np.sum(normal**2,axis=1)
    ids=np.flatnonzero(mask)
    return [(int(i),float(h[i])) for i in ids]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalogue', type=pathlib.Path, default=REPO/'tools/world/data/z1br-retail-vehicle-markers.json')
    parser.add_argument('--world', type=pathlib.Path, default=pathlib.Path('C:/Aug2017/out/world_aug/z2-objects.jsonl'))
    parser.add_argument('--out', type=pathlib.Path, required=True)
    args=parser.parse_args()
    catalogue=json.loads(args.catalogue.read_text())
    candidates=sorted((r for r in catalogue['records'] if r['vehicleId'] and all(abs(r['position'][i])<=4096 for i in (0,2))),key=lambda r:r['instanceId'])
    terrain=VehicleTerrain();bounds=load_bounds()
    entries,_=packread.build_index(packread.find_packs(packread.DEFAULT_ASSETS_DIR))
    index={e.name.casefold():e for e in entries}
    world=args.world
    objects=[json.loads(line) for line in world.open()]
    models={};missing=[]
    for name in sorted({o['model'] for o in objects}):
        entry=index.get(name.casefold())
        if not entry:missing.append(name);continue
        actor=ET.fromstring(packread.read_asset_bytes(entry));base=actor.find('Base')
        if base is None or actor.find('CollisionData') is None:continue
        mesh=index.get(base.get('fileName','').casefold())
        if mesh is None:continue
        data=packread.read_asset_bytes(mesh)
        if data[:4]!=b'DMOD':continue
        bb=struct.unpack_from('<6f',data,12+struct.unpack_from('<I',data,8)[0])
        models[name]={'bounds':bb,'data':data,'mesh':mesh.name,'crc32':f'{mesh.crc32:08x}','triangles':None}
    grid={};colliders=[]
    for o in objects:
        m=models.get(o['model'])
        if m is None:continue
        ob=box(m['bounds'],o['pos'],o['rot'],o['scale']);centre,axes,ext=ob
        reach=[sum(abs(axes[j][i])*ext[j] for j in range(3)) for i in range(3)]
        low=[centre[i]-reach[i] for i in range(3)];high=[centre[i]+reach[i] for i in range(3)]
        if low[0]>4096 or low[2]>4096 or high[0]<-4096 or high[2]<-4096:continue
        item=(o,m,ob,low,high);colliders.append(item)
        for gx in range(max(-32,math.floor(low[0]/128)),min(32,math.floor(high[0]/128))+1):
            for gz in range(max(-32,math.floor(low[2]/128)),min(32,math.floor(high[2]/128))+1):
                grid.setdefault((gx,gz),[]).append(item)
    print('World colliders',len(colliders),'models',len(models),flush=True)
    def triangles(item):
        o,m,*_=item
        if m['triangles'] is None:m['triangles']=np.asarray(list(mesh_triangles(m['data'])),dtype=np.float64)
        a=m['triangles']
        if not a.size:return a.reshape((0,3,3))
        return (a*np.asarray(o['scale']))@np.asarray(axes_for(o['rot']))+np.asarray(o['pos'])
    accepted=[];decisions=[]
    for row in candidates:
        x,editor_y,z=row['position'];rotation=row['rotation'];family=row['vehicleId'];reasons=[];witnesses=[]
        near=grid.get((math.floor(x/128),math.floor(z/128)),[])
        ground=terrain_height(terrain,x,z);support={'kind':'solid-terrain','height':ground}
        if terrain.is_buried(bounds[DRIVABLE_MODELS[family]],row['position'],rotation[0]):
            reasons.append('retail marker fully buried in August terrain')
        for item in near:
            o,m,ob,low,high=item
            if not(low[0]<=x<=high[0] and low[2]<=z<=high[2]) or low[1]>editor_y:continue
            # Furniture, logs and scenery cars cannot become replacement car platforms.
            if not any(s in o['model'].lower() for s in ('structur','road','parking','bridge','res_house','combined_meshes')):continue
            try:hits=triangle_heights(triangles(item),x,z,editor_y)
            except ValueError:
                reasons.append('unsupported potential support mesh');continue
            if hits:
                triangle,height=max(hits,key=lambda r:r[1])
                if ground is None or height>ground:
                    ground=height;support={'kind':'August visual mesh','height':height,'instanceId':o['id'],'model':o['model'],'mesh':m['mesh'],'crc32':m['crc32'],'triangle':triangle}
        if ground is None:reasons.append('no verified solid support')
        # The recorded untouched parked poses use ground + 0.1 m on ordinary flat
        # terrain. This is an explicit August grounding adaptation, not recovered
        # retail settled Y outside the captured route.
        position=[x,f32(ground+0.1) if ground is not None else editor_y,z]
        car=box(bounds[DRIVABLE_MODELS[family]],position,rotation)
        centre,axes,ext=car;rx=sum(abs(axes[j][0])*ext[j] for j in range(3));rz=sum(abs(axes[j][2])*ext[j] for j in range(3))
        nearby={}
        for gx in range(math.floor((centre[0]-rx)/128),math.floor((centre[0]+rx)/128)+1):
            for gz in range(math.floor((centre[2]-rz)/128),math.floor((centre[2]+rz)/128)+1):
                for item in grid.get((gx,gz),[]):nearby[item[0]['id']]=item
        for item in nearby.values():
            o,m,ob,low,high=item
            if not overlaps(car,ob):continue
            if o['model'] in STATIC_MODELS:
                reasons.append('overlap with static August vehicle');witnesses.append({'model':o['model'],'instanceId':o['id']});continue
            try:
                tri=triangles(item)
                for i,t in enumerate(tri):
                    if blocking_triangle(t,car,position[1]):
                        reasons.append('obstructed by August mesh');witnesses.append({'model':o['model'],'instanceId':o['id'],'mesh':m['mesh'],'crc32':m['crc32'],'triangle':i,'worldVertices':t.tolist()});break
            except ValueError:reasons.append('unsupported intersecting mesh')
        decision={'instanceId':row['instanceId'],'position':position,'rotation':rotation,'vehicleId':family,'editorPosition':row['position'],'support':support,'reasons':sorted(set(reasons)),'witnesses':witnesses}
        decisions.append(decision)
        if not reasons:accepted.append(decision)
        if len(decisions)%100==0:print('Grounded',len(decisions),'accepted',len(accepted),flush=True)
    collisions=[]
    for i,a in enumerate(accepted):
        ab=box(bounds[DRIVABLE_MODELS[a['vehicleId']]],a['position'],a['rotation'])
        for b in accepted[i+1:]:
            if overlaps(ab,box(bounds[DRIVABLE_MODELS[b['vehicleId']]],b['position'],b['rotation'])):
                collisions.append([a['instanceId'],b['instanceId']]);a['reasons'].append('overlap with another retail marker');b['reasons'].append('overlap with another retail marker')
    accepted=[r for r in decisions if not r['reasons']]
    report={'schema':'cranberry/retail-august-placement-audit/1','catalogueSha256':sha(args.catalogue),'augustWorldSha256':sha(world),'terrainSha256':sha(REPO/'src/Cranberry.Zone/Data/Loot/z2-terrain.bin'),'boundsSha256':sha(REPO/'tools/world/data/august-vehicle-mesh-bounds.json'),'method':__doc__,'collisionObjects':len(colliders),'missingModels':missing,'counts':{'source':len(candidates),'accepted':len(accepted),'excluded':len(candidates)-len(accepted)},'interVehicleConflicts':collisions,'decisions':decisions}
    args.out.write_text(json.dumps(report,indent=1)+'\n')
    print(json.dumps(report['counts']),flush=True)

if __name__=='__main__':main()
