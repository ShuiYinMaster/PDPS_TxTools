from pathlib import Path
import shutil

root=Path(__file__).resolve().parents[1]
out=root/'artifacts/default-recipes-regression'
out.mkdir(parents=True,exist_ok=True)
shutil.copy2(root.parent/'packages/Newtonsoft.Json.13.0.4/lib/net45/Newtonsoft.Json.dll',out/'Newtonsoft.Json.dll')
args=['/nologo','/target:exe','/out:'+str(out/'DefaultRecipesRegression.exe'),'/r:System.dll','/r:System.Core.dll','/r:Microsoft.CSharp.dll',
      '/r:'+str(out/'Newtonsoft.Json.dll'),'/r:'+str(root.parents[1]/'2402dll/Tecnomatix.Engineering.dll')]
for file in (root/'recipes/defaults').glob('*.md'):
    args.append('/resource:'+str(file)+',TxTools.Agent.DefaultRecipes.'+file.name)
for file in (root/'maintenance/recipe-migrations').glob('*.md'):
    args.append('/resource:'+str(file)+',TxTools.Agent.LegacyRecipes.'+file.name)
for file in ['maintenance/DefaultRecipesRegression.cs','Core/RecipeStore.cs','Core/RecipeRunner.cs','Core/MdStore.cs','Core/MarkdownDoc.cs','Ps/CSharpRunner.cs']:
    args.append(str(root/file))
(out/'compile.rsp').write_text('\n'.join('"'+a+'"' for a in args),encoding='utf8')
print(out/'compile.rsp')
