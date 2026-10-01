"""Semantic layers v2. No nearest-part flood fill or copied edge extensions.

Generated hidden plates + tightly bounded original feature masks. Every layer
is independently inspected; renderer/rigging is deliberately outside this tool.
"""
from pathlib import Path
from collections import OrderedDict, deque
import sys, json, shutil
ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/'.deps'))
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from psd_tools import PSDImage
OUT=ROOT/'repair_v2'; OUT.mkdir(exist_ok=True)
LAYERS=OUT/'layers'; LAYERS.mkdir(exist_ok=True)
src=Image.open(ROOT/'whale_master_clean.png').convert('RGBA')
arr=np.array(src);rgb=arr[:,:,:3].astype(np.int16)
W,H=src.size
parts=OrderedDict()
genroot=Path('C:/Users/A1875/.codex/generated_images/01a0dcc1-4b82-76b2-a640-954b2e3d5445')
GENERATED={
 'FrontHair':'exec-a5eb1d91-9b64-4338-91c6-d464736ca7f5.png',
 'FaceBase':'exec-ab478b57-4a5c-447c-8610-f396f3a0d0f3.png',
 'RearHair':'exec-8f3aeb2c-a482-4cb9-9a04-60210aab5ea4.png',
 'BodyDress':'exec-3a2b0f44-8062-4f76-bb67-50af36c31b52.png',
 'SleeveL':'exec-ac78743f-4917-4615-ae7b-e545d3a02a85.png',
 'EyeLWhite':'exec-177dc5a0-b092-44a0-861e-6ef407be61bd.png',
 'EyeLIris':'exec-367103da-0b52-4c51-a265-4bea16782d76.png',
 'EarL':'exec-fc5336b0-8482-4e0b-9e40-8e18d1c20b5d.png',
 'Tail':'exec-46e3118e-48b8-4472-8ade-b09fe1fcc14e.png',
 'SkirtBowL':'exec-96e86086-35c2-4c80-ad0f-35a318d11cc1.png',
 'HairBow':'exec-80d9959d-cad5-4239-b185-349a89ff2809.png',
 'NeckBow':'exec-b9e1f976-fc01-4569-8e0c-6f990e7f1e74.png',
}

def poly(points):
    im=Image.new('L',src.size);ImageDraw.Draw(im).polygon(points,fill=255)
    return np.array(im)>0

def rect(bounds):
    x0,y0,x1,y1=bounds
    return poly([(x0,y0),(x1,y0),(x1,y1),(x0,y1)])

def ellipse(bounds):
    im=Image.new('L',src.size);ImageDraw.Draw(im).ellipse(bounds,fill=255)
    return np.array(im)>0

def largest(mask):
    """Only discard disconnected foreign fragments; never expand semantics."""
    rows,cols=np.where(mask)
    if not len(rows):raise ValueError('Empty mask')
    x0,x1=cols.min(),cols.max()+1;y0,y1=rows.min(),rows.max()+1
    m=mask[y0:y1,x0:x1].copy();seen=np.zeros_like(m);best=[]
    for y,x in zip(*np.where(m)):
        if seen[y,x]:continue
        seen[y,x]=True;q=deque([(y,x)]);comp=[]
        while q:
            py,px=q.popleft();comp.append((py,px))
            for dy,dx in ((-1,0),(1,0),(0,-1),(0,1),(-1,-1),(-1,1),(1,-1),(1,1)):
                ny,nx=py+dy,px+dx
                if 0<=ny<m.shape[0] and 0<=nx<m.shape[1] and m[ny,nx] and not seen[ny,nx]:
                    seen[ny,nx]=True;q.append((ny,nx))
        if len(comp)>len(best):best=comp
    out=np.zeros_like(mask)
    for y,x in best:out[y+y0,x+x0]=True
    return out

def take(name,shape,condition=None,connected=False):
    m=shape&(arr[:,:,3]>0)
    if condition is not None:m &=condition
    if connected:m=largest(m)
    pixels=np.zeros_like(arr);pixels[m]=arr[m]
    parts[name]=Image.fromarray(pixels)
    return parts[name]

def generated(name,bounds):
    path=genroot/GENERATED[name]
    shutil.copy2(path,OUT/(name+'_generated.png'))
    im=Image.open(path).convert('RGBA')
    if name=='RearHair':
        # The generated rear plate included an ahoge. Remove it along the
        # true crown contour; that accessory has its own independent layer.
        clip=Image.new('L',im.size)
        ImageDraw.Draw(clip).polygon([(0,370),(270,370),(320,287),(373,211),
           (419,168),(467,141),(510,133),(565,145),(626,176),(697,244),
           (748,325),(1023,390),(1023,1536),(0,1536)],fill=255)
        p=np.array(im);p[np.array(clip)==0]=0;im=Image.fromarray(p)
    # Alpha noise is excluded by the largest opaque silhouette; retain edge
    # alpha around that silhouette only. This does not erase an opaque backdrop.
    a=np.array(im.getchannel('A'));core=largest(a>=160)
    near=np.array(Image.fromarray(core.astype('uint8')*255).filter(ImageFilter.MaxFilter(3)))>0
    p=np.array(im);p[~near]=0
    im=Image.fromarray(p)
    box=im.getbbox();im=im.crop(box)
    x0,y0,x1,y1=bounds
    im=im.resize((x1-x0,y1-y0),Image.Resampling.LANCZOS)
    plate=Image.new('RGBA',src.size);plate.alpha_composite(im,(x0,y0));parts[name]=plate
    return {'original_size':a.shape[::-1],'alpha_opaque_fraction':float((a>=250).mean()),'crop':box,'placement':bounds}

skin=(rgb[:,:,0]>rgb[:,:,1]+5)&(rgb[:,:,0]>rgb[:,:,2]+8)
dark=(rgb[:,:,0]<230)&(rgb[:,:,1]<155)&(rgb[:,:,2]<175)&(rgb[:,:,2]-rgb[:,:,0]<48)&(rgb[:,:,0]>=rgb[:,:,1]-2)
blue=(rgb[:,:,2]>rgb[:,:,0]+3)

# Wrist/fingers: color exclusion is bounded to the hand; never grows into cuffs.
take('HandL',rect((181,852,273,968)),skin,True)
take('HandR',rect((748,852,841,969)),skin,True)

# Neck bow silhouette including jewel, excluding neighboring chest/frills.
bow=poly([(455,508),(468,503),(492,505),(510,506),(530,505),(551,502),(570,506),
 (568,519),(565,537),(560,547),(550,550),(529,540),(553,578),(553,582),
 (530,591),(522,581),(511,548),(500,577),(491,594),(467,585),(462,579),
 (484,540),(469,549),(461,545),(457,534)])
take('NeckBow',bow,blue|ellipse((495,503,531,538)),True)

# Tight outer contours of the skirt ribbons, not arbitrary quadrilateral crops.
take('SkirtBowL',poly([(319,1027),(327,1030),(342,1049),(353,1044),(367,1044),
 (370,1050),(365,1063),(358,1079),(351,1081),(339,1071),(349,1099),
 (328,1109),(326,1080),(308,1105),(297,1094),(313,1070),(298,1070),
 (300,1052),(307,1035)]))
take('SkirtBowR',poly([(696,1034),(703,1035),(714,1053),(721,1070),
 (707,1072),(730,1095),(719,1110),(703,1086),(702,1108),(678,1106),
 (682,1076),(671,1082),(665,1075),(660,1060),(659,1049),(665,1046),(680,1047)]))
for bowname in ('SkirtBowL','SkirtBowR'):
    p=np.array(parts[bowname]);bgblue=(rgb[:,:,1]>rgb[:,:,0]+15)&(rgb[:,:,2]>rgb[:,:,1]+35)
    p[bgblue]=0;parts[bowname]=Image.fromarray(p)
take('HairBow',poly([(677,271),(686,270),(694,275),(701,283),(708,297),
 (716,286),(729,279),(736,281),(740,296),(740,318),(735,333),(720,328),
 (710,321),(701,331),(685,342),(679,335),(677,319)]))

# Upper lash bands: both thickness and lash spikes belong to this ONE layer.
EYES={
 'L':{'upper':[(383,371),(392,359),(388,353),(400,349),(407,334),(416,340),
   (429,338),(444,339),(455,343),(464,349),(473,355),(479,364),
   (469,359),(457,352),(443,350),(430,351),(419,356),(409,366),
   (402,379),(399,388),(392,382)],
   'opening':[(398,379),(409,362),(426,352),(444,351),(460,353),(475,364),
   (472,379),(465,393),(447,398),(431,400),(414,396),(402,389)],
   'iris':(416,346,467,400),
   'lower':[(402,390),(413,394),(431,398),(448,396),(465,392),
   (465,395),(449,400),(431,403),(413,400),(402,394)]},
 'R':{'upper':[(522,359),(532,346),(544,337),(559,332),(578,327),(582,327),
   (585,333),(597,337),(604,337),(609,341),(621,338),(615,347),(620,353),
   (627,359),(620,377),(611,388),(611,374),(605,359),(595,350),
   (582,345),(570,342),(554,344),(541,350),(532,358)],
   'opening':[(531,359),(544,349),(559,343),(577,341),(596,349),(610,365),
   (605,380),(593,390),(572,392),(552,390),(539,380)],
   'iris':(541,335,594,394),
   'lower':[(539,382),(553,388),(573,390),(594,388),(604,383),
   (604,386),(594,393),(574,395),(552,393),(539,385)]}
}
for side,spec in EYES.items():
    take('Eye'+side+'UpperLash',poly(spec['upper']),dark,True)
    # Lower line is reddish and very thin; no iris pixels or upper arc permitted.
    m=poly(spec['lower'])&skin&(rgb[:,:,0]<248)&(rgb[:,:,1]<195)
    take('Eye'+side+'LowerLash',m)
    # Preserve original iris/highlights within a tight ellipse, excluding red skin.
    take('Eye'+side+'Iris',ellipse(spec['iris']),~skin)
    # Original white/shadow and original iris underneath it are legitimate eye
    # pixels; the opaque opening plate is the clipping mask for the iris above.
    take('Eye'+side+'White',poly(spec['opening']))

# Existing eyebrows are blue strokes; bound to the visible skin gap only.
take('BrowL',poly([(394,305),(409,299),(426,295),(438,295),(441,300),
 (428,300),(413,303),(399,309)]),blue,True)
take('BrowR',poly([(537,293),(549,289),(564,289),(577,292),(581,298),
 (567,295),(552,295),(539,298)]),blue,True)
take('Mouth',rect((486,422,531,438)),skin&(rgb[:,:,0]-rgb[:,:,1]>55))
take('Nose',rect((497,391,511,406)),(rgb[:,:,0]>245)&(rgb[:,:,1]>237)&(rgb[:,:,2]>232),True)

# Complete isolated ahoge, with a strict mask excluding the white headband.
take('Ahoge',poly([(300,5),(500,5),(493,84),(475,88),(450,88),(450,100),
 (385,139),(300,139)]),(rgb[:,:,2]>rgb[:,:,0]+18)&(rgb[:,:,2]>rgb[:,:,1]+3),True)

# Ruffled headband: boundary follows the INNER curved edge of the white band.
band=poly([(302,281),(301,243),(306,208),(315,171),(335,140),(350,112),
 (380,93),(412,81),(459,74),(488,81),(512,75),(549,78),(563,91),
 (589,87),(623,96),(650,112),(661,126),(680,128),(699,149),(710,171),
 (704,192),(714,207),(715,230),(706,246),(710,263),(699,276),
 (691,277),(680,240),(662,210),(642,187),(615,169),(585,156),
 (557,147),(530,139),(505,135),(482,137),(463,144),(446,142),
 (426,147),(397,158),(368,177),(346,201),(331,223),(315,254)])
whiteband=(rgb[:,:,0]>100)&(rgb[:,:,1]>105)&(rgb[:,:,2]-rgb[:,:,0]<55)
bandcore=band&whiteband
nearband=np.array(Image.fromarray(bandcore.astype('uint8')*255).filter(ImageFilter.MaxFilter(3)))>0
take('Headband',band&nearband)

# Legs have no dress pixels. Tops end under the new dress plate.
take('LegL',rect((373,1255,509,1520)))
take('LegR',rect((510,1255,636,1520)))

metadata={}
metadata['FrontHair']=generated('FrontHair',(284,140,706,578))
metadata['FaceBase']=generated('FaceBase',(373,205,641,478))
metadata['RearHair']=generated('RearHair',(77,150,905,929))
metadata['BodyDress']=generated('BodyDress',(156,477,884,1259))
metadata['SleeveL']=generated('SleeveL',(207,546,426,895))
parts['SleeveR']=Image.new('RGBA',src.size)
left=parts['SleeveL'].crop((207,546,426,895)).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
parts['SleeveR'].alpha_composite(left,(598,546))
metadata['EyeLWhite']=generated('EyeLWhite',(397,350,477,401))
parts['EyeRWhite']=Image.new('RGBA',src.size)
white=parts['EyeLWhite'].crop((397,350,477,401)).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
parts['EyeRWhite'].alpha_composite(white,(530,343))
metadata['EyeLIris']=generated('EyeLIris',(416,344,467,400))
parts['EyeRIris']=Image.new('RGBA',src.size)
iris=parts['EyeLIris'].crop((416,344,467,400)).resize((53,59),Image.Resampling.LANCZOS)
parts['EyeRIris'].alpha_composite(iris,(541,335))
metadata['EarL']=generated('EarL',(211,330,352,445))
parts['EarR']=Image.new('RGBA',src.size)
ear=parts['EarL'].crop((211,330,352,445)).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
parts['EarR'].alpha_composite(ear,(674,327))
metadata['Tail']=generated('Tail',(557,829,1002,1136))
metadata['HairBow']=generated('HairBow',(658,270,726,341))
metadata['NeckBow']=generated('NeckBow',(455,502,572,593))
metadata['SkirtBowL']=generated('SkirtBowL',(298,1030,372,1109))
parts['SkirtBowR']=Image.new('RGBA',src.size)
skirtbow=parts['SkirtBowL'].crop((298,1030,372,1109)).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
parts['SkirtBowR'].alpha_composite(skirtbow,(655,1031))

# A separate complete skin neck plate, cropped from the repainted skin asset.
# Its top overlaps the face by 31 px; its bottom lies behind the collar.
face_asset=Image.open(genroot/GENERATED['FaceBase']).convert('RGBA')
fb=face_asset.getbbox();fx,fy,fr,fbottom=fb
skin_sample=face_asset.crop((fx+(fr-fx)*.36,fy+(fbottom-fy)*.35,
                            fx+(fr-fx)*.64,fy+(fbottom-fy)*.62))
skin_sample=skin_sample.resize((88,76),Image.Resampling.LANCZOS)
neck=Image.new('RGBA',src.size);neck.alpha_composite(skin_sample,(469,441))
neckmask=poly([(478,441),(543,441),(542,481),(550,502),(557,512),
              (535,517),(490,517),(470,511),(478,493)])
npix=np.array(neck);npix[~neckmask]=0;parts['Neck']=Image.fromarray(npix)

def write_outputs(stack,complete=False):
    neutral=Image.new('RGBA',src.size); manifest=[]
    psd=PSDImage.new(mode='RGB',size=src.size,color=(0,0,0),depth=8)
    for i,name in enumerate(stack):
        im=parts[name];im.save(LAYERS/(name+'.png'),optimize=True)
        neutral.alpha_composite(im);psd.create_pixel_layer(im,name=name)
        manifest.append({'name':name,'draw_order':i+1,'bbox':im.getbbox(),
                         'opaque_pixels':int((np.array(im.getchannel('A'))>160).sum())})
    psd.save(OUT/'whale_source_v2.psd')
    neutral.save(OUT/'composite.png',optimize=True)
    bg=Image.new('RGBA',src.size,(236,236,243,255));bg.alpha_composite(neutral)
    bg.convert('RGB').save(OUT/'composite_preview.jpg',quality=96)
    columns=4;cellw=320;cellh=360
    sheet=Image.new('RGB',(columns*cellw,((len(stack)+columns-1)//columns)*cellh),(235,235,242))
    dr=ImageDraw.Draw(sheet)
    for i,name in enumerate(stack):
        im=parts[name].crop(parts[name].getbbox());im.thumbnail((cellw-20,cellh-45))
        x=(i%columns)*cellw;y=(i//columns)*cellh
        sheet.paste(im,(x+(cellw-im.width)//2,y+35),im)
        dr.text((x+10,y+10),name,fill='black')
    sheet.save(OUT/'layer_audit.jpg',quality=95)
    for label,names in {
      'eyes':['EyeLWhite','EyeRWhite','EyeLIris','EyeRIris','EyeLUpperLash','EyeRUpperLash','EyeLLowerLash','EyeRLowerLash'],
      'accessories':['HandL','HandR','HairBow','NeckBow','SkirtBowL','SkirtBowR','Headband','Ahoge'],
      'plates':['FrontHair','RearHair','FaceBase','Neck','BodyDress','SleeveL','EarL','Tail']
    }.items():
        board=Image.new('RGB',(1280,720),(235,235,242));bd=ImageDraw.Draw(board)
        for i,name in enumerate(names):
            im=parts[name].crop(parts[name].getbbox())
            im.thumbnail((295,305))
            # Enlarge small native feature crops for meaningful edge inspection.
            if max(im.size)<120:
                factor=min(4,295/im.width,305/im.height)
                im=im.resize((int(im.width*factor),int(im.height*factor)),Image.Resampling.NEAREST)
            x=(i%4)*320;y=(i//4)*360
            board.paste(im,(x+(320-im.width)//2,y+38),im)
            bd.text((x+10,y+10),name,fill='black')
        board.save(OUT/('audit_'+label+'.jpg'),quality=96)
    (OUT/'manifest.json').write_text(json.dumps({'status':'assembled_unbound' if complete else 'repair_in_progress',
      'canvas':src.size,'parts':manifest,'generated_plates':metadata,
      'method':'semantic object masks + separately repainted plates; no global nearest assignment'},indent=2),encoding='utf8')
    print(json.dumps({'layers':len(stack),'psd':str(OUT/'whale_source_v2.psd'),'complete':complete}))

if __name__=='__main__':
    write_outputs(['RearHair','Tail','LegL','LegR','Neck','BodyDress','SleeveL','SleeveR',
      'HandL','HandR','NeckBow','SkirtBowL','SkirtBowR',
      'EarL','EarR','FaceBase','EyeLWhite','EyeRWhite','EyeLIris','EyeRIris',
      'EyeLLowerLash','EyeRLowerLash','EyeLUpperLash','EyeRUpperLash',
      'BrowL','BrowR','Nose','Mouth','FrontHair','Headband','HairBow','Ahoge'],complete=True)
