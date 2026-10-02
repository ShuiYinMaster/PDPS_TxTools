"""Versioned, little-endian transport for the .NET Framework plugin bridge."""
import struct

def write_mesh(path, model, digest):
    nv,nf=len(model['vertices']),len(model['faces'])
    if not nv or not nf or any(len(model[k])!=nf for k in ('colors','surfaces','face_normals')):
        raise ValueError('Incomplete mesh arrays')
    temp=path.with_suffix(path.suffix+'.tmp')
    with temp.open('wb') as f:
        f.write(struct.pack('<8sI32sII',b'JTMESH01',1,bytes.fromhex(digest),nv,nf))
        for xyz in model['vertices']: f.write(struct.pack('<3f',*xyz))
        for ids,rgb,surface,normal in zip(model['faces'],model['colors'],model['surfaces'],model['face_normals']):
            f.write(struct.pack('<3i3BI3f',*ids,*rgb,surface,*normal))
    temp.replace(path)
