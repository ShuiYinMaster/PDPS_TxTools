"""Headless pilot: a checked subset of JT 10.6 -> colored MeshIR for CGR writer.

No PS SDK needed. Coordinates remain in the JT resource frame, not PS world frame.
"""
import argparse, hashlib, json, math, struct, time
from pathlib import Path
from collections import Counter
from jt_analyze import analyze
from jt_lsg import parse_lsg
from jt_shape_inventory import inventory_shape
from jt_instance_inventory import build_report, transform_point, determinant3
from jt_batch_mesh import tri_shapes
from jt_shape_decode import decode_topology
from jt_to_mesh import decode_mesh
from jt_assemble_attributes import transform_normal, normalize, geometric_normal
from jt_mesh_binary import write_mesh


def rigid(matrix):
    rows = [matrix[i:i+3] for i in (0,4,8)]
    return all(abs(sum(a[k]*b[k] for k in range(3))-(1 if i==j else 0))<1e-5
               for i,a in enumerate(rows) for j,b in enumerate(rows)) and abs(determinant3(matrix)-1)<1e-5


def convert(source, target):
    started=time.monotonic()
    digest=hashlib.sha256(source.read_bytes()).hexdigest()
    info=analyze(source)
    if not info['header']['version'].startswith('Version 10.6 JT') or info['header']['byte_order_marker']!=0:
        raise ValueError('Pilot supports little-endian JT 10.6 only')
    lsg=parse_lsg(source)
    if lsg['parse'].get('error') or lsg['parse']['remaining_bytes']:
        raise ValueError('Incomplete LSG parse')
    shapes=inventory_shape(source)
    inventory=build_report(lsg,shapes)
    summary=inventory['summary']
    if not summary['graph_shape_bindings'] or summary['unresolved_shape_bindings']:
        raise ValueError('Missing Shape LOD binding')
    supported={b['shape_ordinal'] for b in inventory['bindings']}
    bodies={ordinal:body for ordinal,body in tri_shapes(source)}
    if supported != set(bodies):
        raise ValueError('Unreferenced/unsupported TriStrip LOD; RangeLOD selection needs validation')
    vs,fs,colors,surfaces,normals,blocks=[],[],[],[],[],[]
    normal_fallback=degenerate=0
    for surface,b in enumerate(sorted(inventory['bindings'],key=lambda x:x['shape_ordinal']),1):
        rgba=b.get('material')
        if rgba is None or rgba[3]!=1:
            raise ValueError(f'Missing/transparent material on Shape {b["shape_object_id"]}')
        matrix=b['transform_matrix']
        if not rigid(matrix) or any(abs(matrix[k])>1e-8 for k in (3,7,11)) or abs(matrix[15]-1)>1e-8:
            raise ValueError('Pilot accepts proper rigid instance transforms only')
        body=bodies[b['shape_ordinal']]
        _,pos=decode_topology(body)
        flags=struct.unpack_from('<Q',body,pos)[0]
        if flags!=0xA:
            raise ValueError(f'Unsupported vertex bindings {flags:#x}; pilot expects coordinates+normals')
        mesh=decode_mesh(body)
        if mesh['stats']['invalid_normal_indices']:
            raise ValueError('Invalid corner normal indices')
        rgb=[round(v*255) for v in rgba[:3]]
        offset=len(vs); face_start=len(fs)
        world=[transform_point(p,matrix) for p in mesh['vertices']]
        if any(not math.isfinite(v) for p in world for v in p):
            raise ValueError('Nonfinite transformed vertex')
        vs.extend(world)
        for i,face in enumerate(mesh['faces']):
            fs.append([offset+k for k in face]); colors.append(rgb); surfaces.append(surface)
            corners=mesh['corner_normals'][i]
            available=[transform_normal(n,matrix) for n in corners if n is not None] if corners else []
            n=normalize([sum(p[k] for p in available) for k in range(3)]) if available else [0,0,0]
            geo=geometric_normal(*(world[k] for k in face))
            if sum(n[k]*geo[k] for k in range(3))<0: n=[-v for v in n]
            if not any(n): normal_fallback+=1
            normals.append(n)
        degenerate+=mesh['stats']['degenerate_triangles']
        blocks.append(dict(shape_ordinal=b['shape_ordinal'],shape_object_id=b['shape_object_id'],
                           rgb=rgb,material_id=b['material_attribute_id'],surface=surface,
                           face_start=face_start,face_count=len(mesh['faces']),transform=matrix,
                           material_chain=b['material_resolution']['material_chain']))
        print(f'Shape {surface}/{len(inventory["bindings"])} triangles={len(mesh["faces"])} RGB={rgb}',flush=True)
    if digest!=hashlib.sha256(source.read_bytes()).hexdigest():
        raise ValueError('Source changed during conversion')
    report=dict(schema='TxTools.JtDirectPilot',version=1,source=str(source.resolve()),sha256=digest,
                coordinate_frame='JT resource local, including internal instance transforms',
                vertices=len(vs),triangles=len(fs),shape_instances=len(blocks),
                distinct_colors=len({tuple(c) for c in colors}),
                color_triangles=dict(Counter('#'+''.join(f'{v:02X}' for v in c) for c in colors)),
                degenerate_source_triangles=degenerate,normal_fallback_triangles=normal_fallback,
                bounds=dict(min=[min(p[k] for p in vs) for k in range(3)],max=[max(p[k] for p in vs) for k in range(3)]),
                skipped_nontriangle_segments=sum(not any(e['type']=='TriStripSetShapeLOD' for e in s['elements']) for s in shapes['segments']),
                elapsed_seconds=round(time.monotonic()-started,3),blocks=blocks)
    target.parent.mkdir(parents=True,exist_ok=True)
    model=dict(vertices=vs,faces=fs,colors=colors,surfaces=surfaces,face_normals=normals,
               source=report['source'],stats={k:v for k,v in report.items() if k!='blocks'})
    if target.suffix=='.jtmesh':
        write_mesh(target,model,digest)
    else:
        temp=target.with_suffix(target.suffix+'.tmp')
        temp.write_text(json.dumps(model,separators=(',',':')),encoding='utf-8')
        temp.replace(target)
    target.with_suffix('.manifest.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='blocks'},indent=2))

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('input',type=Path);parser.add_argument('output',type=Path)
    args=parser.parse_args()
    convert(args.input,args.output)
