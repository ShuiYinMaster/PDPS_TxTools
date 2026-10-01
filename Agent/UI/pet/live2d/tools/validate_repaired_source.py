"""Validate saved layer identities/canvas and PSD composition, not just a preview."""
from pathlib import Path
import sys,json
ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/'.deps'))
import numpy as np
from PIL import Image
from psd_tools import PSDImage
OUT=ROOT/'repair_v2'
m=json.loads((OUT/'manifest.json').read_text(encoding='utf8'))
layers={p['name']:Image.open(OUT/'layers'/(p['name']+'.png')).convert('RGBA') for p in m['parts']}
assert len(layers)==32
assert all(im.size==(1024,1536) for im in layers.values())
checks=[]
for name in ['HandL','HandR']:
    p=np.array(layers[name]).astype(np.int16);opaque=p[:,:,3]>160
    foreign=opaque&(p[:,:,2]>p[:,:,0]+15)
    assert not foreign.any(),name+' contains blue material'
    checks.append(name+': no blue cloth/hair pixels')
for name in ['EyeLWhite','EyeRWhite']:
    p=np.array(layers[name]);opaque=p[:,:,3]>160
    assert p[:,:,:3][opaque].min()>150,name+' contains iris/lash pixels'
    checks.append(name+': no iris/lash pixels')
for name in ['FrontHair','RearHair']:
    p=np.array(layers[name]).astype(np.int16);opaque=p[:,:,3]>160
    foreign=opaque&(p[:,:,0]>p[:,:,2]+12)&(p[:,:,0]>p[:,:,1]+12)
    assert not foreign.any(),name+' contains skin pixels'
    checks.append(name+': no warm skin pixels')
psd=PSDImage.open(OUT/'whale_source_v2.psd')
assert [l.name for l in psd]==list(layers)
assert psd.size==(1024,1536)
saved=psd.composite(force=True).convert('RGBA')
png=Image.open(OUT/'composite.png').convert('RGBA')
def flat(im):
    bg=Image.new('RGBA',im.size,(236,236,243,255));bg.alpha_composite(im)
    return np.array(bg).astype(np.int16)
err=np.abs(flat(saved)-flat(png))[:,:,:3]
report={'layers':len(layers),'canvas':psd.size,'checks':checks,
        'psd_vs_png_mean_error':float(err.mean()),'psd_vs_png_max_error':int(err.max()),
        'rig_status':'unbound','visual_review':'separate plates, features and accessories reviewed'}
assert report['psd_vs_png_mean_error']<.5,report
(OUT/'validation.json').write_text(json.dumps(report,indent=2),encoding='utf8')
print(json.dumps(report,indent=2))
