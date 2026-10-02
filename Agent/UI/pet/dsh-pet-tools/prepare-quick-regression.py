from pathlib import Path
import shutil, tempfile, runpy, difflib

root=Path(__file__).resolve().parents[3]
out=root/'artifacts/dsh-pet-smoke/quick-regression'
out.mkdir(parents=True,exist_ok=True)
recipes=(root/'Core/RecipeStore.cs').read_text(encoding='utf8')
models=recipes[recipes.index('    public sealed class RecipeParam'):recipes.index('    public static class RecipeStore')]
provider=(root/'Core/LlmProviders.cs').read_text(encoding='utf8')
provider=provider[provider.index('    public sealed class LlmProvider'):provider.index('    public static class LlmProviders')]
(out/'Models.cs').write_text('using System;using System.Collections.Generic;using System.Text;using Newtonsoft.Json;\nnamespace TxTools.Agent.Core {\n'+models+'\n'+provider+'\n}',encoding='utf8')
packages=root.parent/'packages'
for name in ['Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll']:
    shutil.copy2(packages/'Microsoft.Web.WebView2.1.0.4022.49/lib/net462'/name,out/name)
shutil.copy2(packages/'Microsoft.Web.WebView2.1.0.4022.49/runtimes/win-x64/native/WebView2Loader.dll',out/'WebView2Loader.dll')
shutil.copy2(packages/'Newtonsoft.Json.13.0.4/lib/net45/Newtonsoft.Json.dll',out/'Newtonsoft.Json.dll')
print(out)

# Verify the recorded upstream patch against the same pinned input used by vendor.py.
source=root/'UI/pet/references/dsh-pet-source/dsh-pet/runtime/electron-helper'
with tempfile.TemporaryDirectory(dir=out) as temp:
    staged=Path(temp)
    shutil.copy2(source/'preload.js',staged/'preload.js')
    text=(source/'sprite.js').read_text(encoding='utf8')
    start=text.index("    const tools = [{ label: '打开网站'")
    end=text.index('    const tree = tools.concat',start)
    text=text[:start]+"    const tools = [\n      { label: '打开助手', action: 'open-site' },\n      { label: '回到初始位置', action: 'home' },\n      { label: '隐藏桌宠', action: 'close-pet' },\n    ];\n"+text[end:]
    text=text.replace("    if (leaf.action === 'show-balance') {", "    if (leaf.action === 'close-pet') {\n      fetch(BASE + '/close', { method: 'POST' }).catch(console.error);\n      return;\n    }\n    if (leaf.action === 'show-balance') {")
    (staged/'sprite.js').write_text(text,encoding='utf8')
    runpy.run_path(str(Path(__file__).with_name('patch-quick-launch.py')),init_globals={'TARGET_UPSTREAM':staged})
    for name in ['sprite.js','preload.js']:
        generated=(staged/name).read_text(encoding='utf8')
        current=(root/'UI/pet/dsh-pet/upstream'/name).read_text(encoding='utf8')
        if generated!=current:
            print(''.join(difflib.unified_diff(generated.splitlines(True),current.splitlines(True),fromfile='regenerated',tofile='current')))
        assert generated==current,name+' regeneration differs'
print('PASS: recorded upstream recipe patch regenerates identical sprite/preload')
