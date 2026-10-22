from pathlib import Path
import shutil
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/shared-pet-tests'
out.mkdir(parents=True,exist_ok=True)
source=(root/'TxAgentCommand.cs').read_text(encoding='utf8')
controller=source[source.index('    internal sealed class DshPetController'):source.index('    /// <summary>Owns dsh-pet')]
(out/'Controller.cs').write_text('using System;using System.Diagnostics;using System.Threading;using System.Windows.Forms;using Newtonsoft.Json.Linq;using TxTools.Agent.Core;namespace TxTools.Agent {\n'+controller+'\n}',encoding='utf8')
client=(root/'Core/Multi/PsRpcClient.cs').read_text(encoding='utf8')
client=client[:client.index('    // ══════════════════════════════════════════════════════════════')]
(out/'Client.cs').write_text(client+'\n}',encoding='utf8')
json_dll=root.parent/'packages/Newtonsoft.Json.13.0.4/lib/net45/Newtonsoft.Json.dll'
shutil.copy2(json_dll,out/'Newtonsoft.Json.dll')
files=[root/'maintenance/SharedPetRegression.cs',root/'Core/Multi/DesktopPetSession.cs',root/'Core/Multi/PsRpcServer.cs',out/'Controller.cs',out/'Client.cs']
args=['/nologo','/out:'+str(out/'SharedPetRegression.exe'),'/r:System.dll','/r:System.Core.dll','/r:System.Windows.Forms.dll','/r:'+str(out/'Newtonsoft.Json.dll'),*[str(f) for f in files]]
(out/'compile.rsp').write_text('\n'.join('"'+arg+'"' for arg in args),encoding='utf8')
print(out/'compile.rsp')
