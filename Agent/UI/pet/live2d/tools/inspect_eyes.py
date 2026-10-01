from pathlib import Path
import sys
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / '.deps'))
from PIL import Image, ImageDraw
names = ['whale_master_clean.png','source_composite.png']
names += ['source_layers/'+n+'.png' for n in ['EyeLUpperLash','EyeLLowerLash','EyeLIris','EyeLWhite','Face']]
out = Image.new('RGB',(1200, len(names)*340),(240,240,240))
d = ImageDraw.Draw(out)
for i,n in enumerate(names):
    im=Image.open(ROOT/n).convert('RGBA').crop((365,320,500,410))
    bg=Image.new('RGBA',im.size,'white'); bg.alpha_composite(im)
    out.paste(bg.convert('RGB').resize((540,360)),(230,i*340))
    d.text((10,i*340+10),n,fill='black')
    for x in range(370,500,10):
        sx=230+(x-365)*4
        d.line((sx,i*340,sx,i*340+330),fill=(190,190,190),width=1)
        d.text((sx,i*340+20),str(x),fill=(200,10,10))
    for y in range(330,410,10):
        sy=i*340+(y-320)*4
        d.line((230,sy,770,sy),fill=(190,190,190),width=1)
        d.text((780,sy),str(y),fill=(200,10,10))
out.save(ROOT/'eye_layer_inspection.jpg')
