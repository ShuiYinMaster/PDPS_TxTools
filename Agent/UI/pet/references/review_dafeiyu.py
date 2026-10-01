"""Read public repository sources and metadata for review; never execute them."""
from pathlib import Path
from urllib.request import Request,urlopen
from concurrent.futures import ThreadPoolExecutor
import json
OUT=Path(__file__).resolve().parent/'dsh-dafeiyu-review';OUT.mkdir(exist_ok=True)
REPO='QCYTSN/dsh-dafeiyu'
def get(url):
    with urlopen(Request(url,headers={'User-Agent':'Codex-repository-review'}),timeout=35) as r:
        return r.read()
commit=json.loads(get(f'https://api.github.com/repos/{REPO}/commits/main'))
sha=commit['sha']
tree=json.loads(get(f'https://api.github.com/repos/{REPO}/git/trees/{sha}?recursive=1'))
(OUT/'tree.json').write_text(json.dumps(tree,indent=2),encoding='utf8')
paths={i['path'] for i in tree['tree'] if i['type']=='blob'}
selected=['README.md','ASSET_LICENSE.md','LICENSE','requirements.txt','package.json',
 'assets/pet-manifest.json','runtime/animation_model.py','runtime/helper.py',
 'scripts/import_dshpet_webm.py','assets/dsh-pet-LICENSE.txt']
selected += sorted(p for p in paths if p.startswith('src/') and p.endswith('.ts'))
selected += sorted(p for p in paths if p.startswith('docs/') and p.endswith('.md'))
def fetch(p):
    if p not in paths:return {'missing':p}
    dest=OUT/p;dest.parent.mkdir(parents=True,exist_ok=True)
    dest.write_bytes(get(f'https://raw.githubusercontent.com/{REPO}/{sha}/{p}'))
    return {'path':p,'bytes':dest.stat().st_size}
with ThreadPoolExecutor(max_workers=6) as ex:downloaded=list(ex.map(fetch,selected))
manifest=json.loads((OUT/'assets/pet-manifest.json').read_text(encoding='utf8'))
summary={'repo':REPO,'commit':sha,'commit_date':commit['commit']['committer']['date'],
 'truncated':tree.get('truncated'),
 'model_files':sorted(p for p in paths if p.lower().endswith(('.moc3','.model3.json','.cmo3','.psd'))),
 'pet_asset_count':sum(p.startswith('assets/pet/') for p in paths),
 'manifest_meta':{k:v for k,v in manifest.items() if k!='clips'},
 'clips':{k:{'frames':len(v['frames']),'frameMs':v['frameMs'],'loop':v['loop'],'motion':v.get('motion')} for k,v in manifest['clips'].items()},
 'downloaded':downloaded}
(OUT/'review_snapshot.json').write_text(json.dumps(summary,indent=2,ensure_ascii=False),encoding='utf8')
print(json.dumps(summary,indent=2,ensure_ascii=False))
