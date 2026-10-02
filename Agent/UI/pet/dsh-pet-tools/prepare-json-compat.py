"""Stage a regression against Json.NET 12 without changing the host installation."""
from pathlib import Path
from zipfile import ZipFile

root = Path(__file__).resolve().parents[3]
work = root / 'artifacts/dsh-pet-smoke'
build = root / 'artifacts/dsh-pet-build'
legacy = work / 'newtonsoft-12.0.3.dll'
with ZipFile(work / 'newtonsoft-12.0.3.nupkg') as package:
    legacy.write_bytes(package.read('lib/net45/Newtonsoft.Json.dll'))

source = (work / 'DshPetHostSmoke.cs').read_text(encoding='utf8')
source = source.replace('[STAThread] static int Main(string[] args)', 'public static int Run(string[] args)')
source += '''
class JsonCompatibilityMain {
    [System.STAThread] static int Main(string[] args) {
        try {
            System.Console.WriteLine("JSON runtime: " + typeof(Newtonsoft.Json.Linq.JToken).Assembly.FullName);
            System.Console.WriteLine("JSON path: " + typeof(Newtonsoft.Json.Linq.JToken).Assembly.Location);
            return TxTools.Agent.DshPetHostSmoke.Run(args);
        } catch (System.Exception ex) { System.Console.Error.WriteLine(ex); return 1; }
    }
}
'''
config = f'''<?xml version="1.0" encoding="utf-8"?>
<configuration><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
<dependentAssembly><assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral"/>
<bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="12.0.0.0"/>
<codeBase version="12.0.0.0" href="{legacy.as_uri()}"/>
</dependentAssembly></assemblyBinding></runtime></configuration>'''
for name, content in (
    ('DshPetHostLegacyBefore', source.replace('value.ToString(), new System.Text.UTF8Encoding(false)',
                                            'value.ToString(Newtonsoft.Json.Formatting.None), new System.Text.UTF8Encoding(false)')),
    ('DshPetHostLegacyAfter', source),
):
    (work / (name + '.cs')).write_text(content, encoding='utf8')
    (build / (name + '.exe.config')).write_text(config, encoding='utf8')
    print(name)
