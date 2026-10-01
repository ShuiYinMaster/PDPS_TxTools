from pathlib import Path
import sys
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/'.deps'))
from PIL import Image, ImageDraw
out=ROOT/'repair_v2'; out.mkdir(exist_ok=True)
master=Image.open(ROOT/'whale_master_clean.png').convert('RGBA')
master.crop((379,325,484,405)).save(out/'left_eye_reference.png')
for name,box in {'head_reference':(270,110,710,590),'body_reference':(140,450,890,1260)}.items():
    im=master.crop(box); bg=Image.new('RGBA',im.size,(235,235,242,255)); bg.alpha_composite(im)
    bg.save(out/(name+'.png'))
names=['BangCenter','BangL','BangR','HairFrontL','HairFrontR','SideLockL','SideLockR','HandL','HandR','HairBow','SkirtBowL','SkirtBowR','NeckRibbon','EyeLUpperLash','EyeLLowerLash','BrowL','Bodice','SleeveL','CuffL','RearHairL']
sheet=Image.new('RGB',(1200,((len(names)+3)//4)*320),(232,232,239)); d=ImageDraw.Draw(sheet)
for i,name in enumerate(names):
    im=Image.open(ROOT/'source_layers'/(name+'.png')).convert('RGBA'); box=im.getbbox()
    im=im.crop(box); im.thumbnail((280,275))
    x=(i%4)*300;y=(i//4)*320
    sheet.paste(im,(x+(300-im.width)//2,y+30),im)
    d.text((x+10,y+7),name,fill='black')
sheet.save(out/'old_layers_audit.jpg',quality=95)
for name,box in {'hands':(170,815,845,990),'bows':(290,1020,735,1120),'head_accessories':(295,60,745,355),'neck_bow':(440,475,590,605),'eyes':(375,315,630,410)}.items():
    im=master.crop(box); bg=Image.new('RGBA',im.size,(235,235,242,255)); bg.alpha_composite(im)
    bg=bg.resize((bg.width*3,bg.height*3))
    dr=ImageDraw.Draw(bg)
    for x in range(((box[0]+9)//10)*10,box[2],10):
        sx=(x-box[0])*3;dr.line((sx,0,sx,bg.height),fill=(150,180,150),width=1);dr.text((sx,1),str(x),fill=(180,20,20))
    for y in range(((box[1]+9)//10)*10,box[3],10):
        sy=(y-box[1])*3;dr.line((0,sy,bg.width,sy),fill=(150,180,150),width=1);dr.text((1,sy),str(y),fill=(180,20,20))
    bg.save(out/(name+'_guide.png'))
