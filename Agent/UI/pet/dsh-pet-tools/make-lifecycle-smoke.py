from pathlib import Path

root = Path(__file__).resolve().parents[3]
source = (root/'TxAgentCommand.cs').read_text(encoding='utf8')
start = source.index('    internal sealed class DshPetController : IDisposable')
classes = source[start:source.rfind('\n}')]
head = '''using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
namespace TxTools.Agent { internal class TxAgentCommand { }
'''
test = r'''
internal class DshPetLifecycleSmoke {
    static object Field(object target, string name) {
        return target.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(target);
    }
    static DshPetHost Host(DshPetController c) { return (DshPetHost)Field(c, "_host"); }
    static int Pid(DshPetHost h) { return ((Process)Field(h, "_process")).Id; }
    static bool Alive(int pid) { try { using (var p=Process.GetProcessById(pid)) return !p.HasExited; } catch { return false; } }
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    [STAThread] static int Main() {
        string prefs=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"prefs.json");
        byte[] previous=System.IO.File.Exists(prefs) ? System.IO.File.ReadAllBytes(prefs) : null;
        DshPetController controller=null;
        string failure=null;
        int lastPid=0;
        try {
            // Exercise actual prefs persistence in this isolated artifact directory.
            System.IO.File.WriteAllText(prefs,"{\"ProviderId\":\"sentinel\",\"Model\":\"keep-model\",\"ApprovalMode\":\"ask\"}");
            Check(UserPrefsStore.Load().DesktopPetVisible,"Old prefs must default to visible");
            UserPrefsStore.UpdateDesktopPetVisible(false);
            Check(!UserPrefsStore.Load().DesktopPetVisible,"Hidden state was not saved");
            Check(UserPrefsStore.Load().Model=="keep-model","Saving visibility replaced another setting");
            Application.EnableVisualStyles();
            controller=new DshPetController(UserPrefsStore.Load().DesktopPetVisible,UserPrefsStore.UpdateDesktopPetVisible,()=>{});
            var context=new ApplicationContext();
            Action<string> failed=message=> { failure=message; context.ExitThread(); };
            controller.Failed += failed;
            int phase=0, ticks=0;
            DshPetHost first=null;
            var deadline=DateTime.UtcNow.AddSeconds(60);
            var timer=new System.Windows.Forms.Timer { Interval=150 };
            timer.Tick += (s,e)=> {
                try {
                    if(DateTime.UtcNow>deadline) throw new Exception("Lifecycle test timed out at phase "+phase);
                    switch(phase) {
                        case 0:
                            if(++ticks<4) break;
                            Check(Host(controller)==null && !controller.Enabled,"Hidden startup created a helper");
                            controller.SetState("thinking","Hidden task");
                            controller.SetEnabled(true);
                            Check(UserPrefsStore.Load().DesktopPetVisible,"GUI show was not saved");
                            phase=1; break;
                        case 1:
                            if(Host(controller)?.IsReady!=true) break;
                            first=Host(controller); lastPid=Pid(first);
                            var snapshot=JObject.Parse(System.IO.File.ReadAllText((string)Field(first,"_stateFile")));
                            Check((string)snapshot["state"]=="thinking","Showing lost the current task state");
                            using(var agent=new Form()) { agent.Show(); agent.Close(); }
                            ticks=0; phase=2; break;
                        case 2:
                            if(++ticks<5) break;
                            Check(ReferenceEquals(Host(controller),first) && first.IsReady && Alive(lastPid),"Closing an Agent window stopped the pet");
                            controller.SetEnabled(false);
                            Check(!UserPrefsStore.Load().DesktopPetVisible,"GUI hide was not saved");
                            UserPrefsStore.UpdateChoice("sentinel","updated-model");
                            Check(!UserPrefsStore.Load().DesktopPetVisible,"Model settings reset hidden state");
                            phase=3; break;
                        case 3:
                            if(Alive(lastPid)) break;
                            controller.Dispose();
                            controller=new DshPetController(UserPrefsStore.Load().DesktopPetVisible,UserPrefsStore.UpdateDesktopPetVisible,()=>{});
                            controller.Failed += failed;
                            ticks=0; phase=4; break;
                        case 4:
                            if(++ticks<4) break;
                            Check(Host(controller)==null && !controller.Enabled,"Restart forgot hidden state");
                            controller.SetEnabled(true); phase=5; break;
                        case 5:
                            if(Host(controller)?.IsReady!=true) break;
                            lastPid=Pid(Host(controller));
                            // Feed the exact user-hide protocol produced by the Electron route.
                            typeof(DshPetHost).GetMethod("HandleHelperMessage",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                                .Invoke(Host(controller),new object[]{"txagent-pet:{\"kind\":\"hidden\"}"});
                            phase=6; break;
                        case 6:
                            if(controller.Enabled || Alive(lastPid)) break;
                            Check(!UserPrefsStore.Load().DesktopPetVisible,"Desktop menu hide was not remembered");
                            controller.Dispose();
                            UserPrefsStore.UpdateDesktopPetVisible(true);
                            controller=new DshPetController(UserPrefsStore.Load().DesktopPetVisible,UserPrefsStore.UpdateDesktopPetVisible,()=>{});
                            controller.Failed += failed;
                            phase=7; break;
                        case 7:
                            if(Host(controller)?.IsReady!=true) break;
                            lastPid=Pid(Host(controller));
                            context.ExitThread(); break;
                    }
                } catch(Exception ex) { failed(ex.ToString()); }
            };
            timer.Start(); Application.Run(context); timer.Dispose();
            // ApplicationExit, rather than Agent.FormClosed, owns teardown.
            var stop=DateTime.UtcNow.AddSeconds(4);
            while(Alive(lastPid) && DateTime.UtcNow<stop) System.Threading.Thread.Sleep(50);
            Check(failure==null,failure);
            Check(!Alive(lastPid),"ApplicationExit left the helper running");
            Check(UserPrefsStore.Load().DesktopPetVisible,"Software exit changed the user's visibility preference");
            Console.WriteLine("PASS: hidden startup, show/hide persistence, Agent close independence, hide protocol, restart restore, application exit cleanup");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            controller?.Dispose();
            if(previous!=null) System.IO.File.WriteAllBytes(prefs,previous);
            else System.IO.File.Delete(prefs);
        }
    }
}
}
'''
# Preserve the actual provider data type without pulling the provider registry into this test.
provider = (root/'Core/LlmProviders.cs').read_text(encoding='utf8')
provider = provider[provider.index('    public sealed class LlmProvider'):provider.index('    public static class LlmProviders')]
recipes = (root/'Core/RecipeStore.cs').read_text(encoding='utf8')
models = recipes[recipes.index('    public sealed class RecipeParam'):recipes.index('    public static class RecipeStore')]
test += '\nnamespace TxTools.Agent.Core {\n' + provider + models + '\n}\n'
out=root/'artifacts/dsh-pet-smoke/DshPetLifecycleSmoke.cs'
out.write_text(head+classes+test,encoding='utf8')
print(out)
