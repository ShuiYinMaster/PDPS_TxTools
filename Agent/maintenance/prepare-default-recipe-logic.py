from pathlib import Path
import re,json
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/default-recipes-regression'
text='using System;using System.Collections.Generic;using System.Linq;using Tecnomatix.Engineering;\npublic static class DefaultScripts {\n'
for path in sorted((root/'recipes/defaults').glob('*.md')):
    md=path.read_text(encoding='utf8')
    params=json.loads(re.search(r'## 参数\s+```json\s*\n([\s\S]*?)\n```',md)[1])
    code=re.search(r'## 代码\s+```csharp\s*\n([\s\S]*?)\n```',md)[1]
    args=['Action<string> log']
    for p in params:
        kind={'objects':'TxObjectList<ITxObject>','number':'double','text':'string','bool':'bool','object':p.get('TypeHint','ITxObject')}[p['Kind']]
        args.append(kind+' '+p['Name'])
    text+='public static object '+path.stem+'('+','.join(args)+') {\n'+code+'\n}\n'
text+='}\n'
(out/'DefaultScripts.cs').write_text(text,encoding='utf8')
print(out/'DefaultScripts.cs')
