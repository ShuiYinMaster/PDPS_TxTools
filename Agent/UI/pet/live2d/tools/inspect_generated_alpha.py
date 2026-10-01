from pathlib import Path
import sys,json
ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/'.deps'))
import numpy as np
from PIL import Image
g=Path('C:/Users/A1875/.codex/generated_images/01a0dcc1-4b82-76b2-a640-954b2e3d5445')
for n in ['exec-8f3aeb2c-a482-4cb9-9a04-60210aab5ea4.png','exec-ac78743f-4917-4615-ae7b-e545d3a02a85.png']:
    im=Image.open(g/n).convert('RGBA');p=np.array(im);a=p[:,:,3]
    print(json.dumps({'file':n,'size':im.size,'alpha_min':int(a.min()),'zero_fraction':float((a==0).mean()),'opaque_fraction':float((a>=250).mean()),'corners':p[[0,0,-1,-1],[0,-1,0,-1]].tolist()}))
