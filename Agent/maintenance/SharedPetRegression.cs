// Runs actual session election, controller and named-pipe routing in three isolated processes.
// Helpers and PS tools are fakes; no engineering scene is opened or changed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TxTools.Agent;
using TxTools.Agent.Core;

public static class SharedPetRegression
{
    internal static string Root => Environment.GetEnvironmentVariable("TXAGENT_PET_TEST_ROOT");
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); Console.WriteLine("PASS: " + reason); }
    static void WaitFor(Func<bool> condition, string reason)
    {
        var limit=DateTime.UtcNow.AddSeconds(12);
        while(DateTime.UtcNow<limit){if(condition())return;Thread.Sleep(30);}
        throw new Exception(reason);
    }
    static void Foreground(int pid){File.WriteAllText(Path.Combine(Root,"foreground"),pid.ToString());}
    static string Command(int pid)=>Path.Combine(Root,pid+".command");
    static DesktopPetSnapshot Snapshot()
    {
        // Inspect with the same lock as production readers, including during atomic replacement.
        using(var gate=new Mutex(false,@"Local\TxAgent_DesktopPet_"+DesktopPetSession.SessionKey))
        {
            bool acquired=false;
            try
            {
                try{acquired=gate.WaitOne(2000);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired)throw new Exception("Snapshot lock timeout");
                return JsonConvert.DeserializeObject<DesktopPetSnapshot>(File.ReadAllText(Path.Combine(DesktopPetSession.DirectoryPath,"session.json")));
            }
            finally{if(acquired)gate.ReleaseMutex();}
        }
    }
    static Process StartWorker(string executable)
    {
        return Process.Start(new ProcessStartInfo(executable,"worker") {UseShellExecute=false,CreateNoWindow=true});
    }
    [STAThread] public static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="worker")return Worker();
        var workers=new List<Process>();
        try
        {
            string key="regression-"+Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable("TXAGENT_PET_TEST_SESSION",key);
            Environment.SetEnvironmentVariable("TXAGENT_PET_TEST_ROOT",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,key));
            Environment.SetEnvironmentVariable("TXAGENT_PET_TEST_PARENT",Process.GetCurrentProcess().Id.ToString());
            Directory.CreateDirectory(Root);Foreground(0);
            DesktopPetSession.ForegroundPidGetter=ReadForeground;
            string executable=Process.GetCurrentProcess().MainModule.FileName;
            for(int i=0;i<3;i++)workers.Add(StartWorker(executable));
            WaitFor(()=>Directory.GetFiles(Root,"*.instance").Length==3&&Directory.GetFiles(Root,"*.host").Length==1,"Initial election failed");
            int owner=Snapshot().OwnerPid;
            Check(workers.Any(p=>p.Id==owner),"three processes elect one pet host");
            Thread.Sleep(1400);
            Check(Directory.GetFiles(Root,"*.host").Length==1,"concurrent polling keeps one pet");
            int target=workers.First(p=>p.Id!=owner).Id;
            Foreground(target);
            WaitFor(()=>Snapshot().ForegroundPid==target,"Foreground not observed");
            Check(DesktopPetSession.ForegroundTarget().Pid==target,"foreground PS wins even when it does not own the pet");
            var reply=PsRpcClient.OpenPetUi(DesktopPetSession.ForegroundTarget(),new JObject { ["kind"]="open-recipes" });
            Check(reply.Ok&&File.Exists(Path.Combine(Root,target+".opened")),"actual named pipe opens recipes in foreground process");
            Foreground(owner);
            var stale=PsRpcClient.OpenPetUi(new PsInstanceInfo {Pid=target},new JObject());
            Check(!stale.Ok,"destination rejects stale routing when foreground changes");
            Foreground(target);DesktopPetSession.ForegroundTarget();
            // The fake helper is the live parent process, excluded from the PS registry.
            Foreground(Process.GetCurrentProcess().Id);
            Check(DesktopPetSession.ForegroundTarget().Pid==target,"focusing pet retains the immediately preceding PS");
            // Activation events retain a PS switched to immediately before the pet gains focus.
            Foreground(owner);DesktopPetSession.ForegroundTarget();
            Foreground(Process.GetCurrentProcess().Id);
            File.WriteAllText(Command(owner),"foreground:"+target);
            WaitFor(()=>Snapshot().ForegroundPid==target,"Fast activation observation not recorded");
            Check(DesktopPetSession.ForegroundTarget().Pid==target,"activation event resolves fast PS-to-pet focus before polling");
            Foreground(int.MaxValue);
            Check(DesktopPetSession.ForegroundTarget()==null,"unrelated foreground app has no recipe target");
            Foreground(target);
            File.WriteAllText(Command(target),"hide");
            WaitFor(()=>!Snapshot().Visible&&Directory.GetFiles(Root,"*.host").Length==0,"Global hide failed");
            Check(!Snapshot().Visible,"hide from a non-owner hides the shared pet");
            File.WriteAllText(Command(target),"show");
            WaitFor(()=>Snapshot().Visible&&Directory.GetFiles(Root,"*.host").Length==1,"Global show failed");
            Check(Directory.GetFiles(Root,"*.host").Length==1,"show from a non-owner creates one shared pet");
            owner=Snapshot().OwnerPid;
            workers.Single(p=>p.Id==owner).Kill();workers.Single(p=>p.Id==owner).WaitForExit();
            // Killed fake helpers cannot delete their own marker; remove just that known test marker.
            File.Delete(Path.Combine(Root,owner+".host"));
            WaitFor(()=>Snapshot().OwnerPid!=owner&&Directory.GetFiles(Root,"*.host").Length==1,"Crash takeover failed");
            Check(workers.Any(p=>!p.HasExited&&p.Id==Snapshot().OwnerPid),"remaining PS automatically takes over after owner crash");
            foreach(var worker in workers.Where(p=>!p.HasExited))File.WriteAllText(Command(worker.Id),"stop");
            foreach(var worker in workers)worker.WaitForExit(6000);
            Check(workers.All(p=>p.HasExited)&&Directory.GetFiles(Root,"*.host").Length==0,"last PS exit closes pet");
            Check(Snapshot().Visible,"software exit preserves visibility preference");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{foreach(var worker in workers){try{if(!worker.HasExited){worker.Kill();worker.WaitForExit();}}catch{}worker.Dispose();}}
    }
    internal static int ReadForeground(){int pid;try{return int.TryParse(File.ReadAllText(Path.Combine(Root,"foreground")),out pid)?pid:0;}catch{return 0;}}
    static int Worker()
    {
        try
        {
            File.WriteAllText(Path.Combine(Root,PsInstanceRegistry.SelfPid+".instance"),"same-study");
            DesktopPetSession.ForegroundPidGetter=ReadForeground;
            PsContext.Context=new WindowsFormsSynchronizationContext();
            PsRpcServer.PetUiHandler=request=>{
                string error=DesktopPetSession.ValidateSelf();
                if(error!=null)return new JObject{["ok"]=false,["error"]=error};
                File.WriteAllText(Path.Combine(Root,PsInstanceRegistry.SelfPid+".opened"),"opened");
                return new JObject{["ok"]=true};
            };
            var server=new PsRpcServer(new ToolRegistry(),()=>"same-study");server.Start();
            var controller=new DshPetController(true,_=>{},()=>{});
            var context=new ApplicationContext();
            var timer=new System.Windows.Forms.Timer{Interval=40};
            timer.Tick+=(s,e)=>{
                string path=Command(PsInstanceRegistry.SelfPid);if(!File.Exists(path))return;
                string command=File.ReadAllText(path);File.Delete(path);
                if(command=="hide")controller.SetEnabled(false);
                if(command=="show")controller.SetEnabled(true);
                if(command.StartsWith("foreground:"))DesktopPetSession.ObserveForeground(int.Parse(command.Substring(11)));
                if(command=="stop"){controller.Dispose();server.Dispose();context.ExitThread();}
            };
            timer.Start();Application.Run(context);timer.Dispose();return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}

namespace TxTools.Agent
{
    internal sealed class DshPetHost : IDisposable
    {
        public string LogPath=>"fake-helper";
        public bool IsDisposed {get;private set;}
        public bool SingletonBusy=>false;
        public int HelperPid=>Process.GetProcessById(GetParentPid()).Id;
        static int GetParentPid()=>int.Parse(Environment.GetEnvironmentVariable("TXAGENT_PET_TEST_PARENT"));
        public DshPetHost(Control dispatcher){Thread.Sleep(150);File.WriteAllText(Path.Combine(SharedPetRegression.Root,PsInstanceRegistry.SelfPid+".host"),"host");}
        public event EventHandler OpenAssistantRequested, HideRequested, Closed;
        public event Action<JObject> OpenRecipesRequested;
        public event Action<string> Failed;
        public void SetState(string state,string task){}
        public void Dispose(){IsDisposed=true;File.Delete(Path.Combine(SharedPetRegression.Root,PsInstanceRegistry.SelfPid+".host"));}
    }
}
namespace TxTools.Agent.Core
{
    public sealed class PsInstanceInfo
    {
        public int Pid;public string Name=>"same-study";public bool IsSelf=>Pid==PsInstanceRegistry.SelfPid;
        public string PipeName=>PsInstanceRegistry.PipeNameFor(Pid);
        public bool IsAlive{get{try{using(var process=Process.GetProcessById(Pid))return !process.HasExited;}catch{return false;}}}
    }
    public static class PsInstanceRegistry
    {
        public static int SelfPid=Process.GetCurrentProcess().Id;
        public static string PipeNameFor(int pid)=>"TxAgent_Test_"+Environment.GetEnvironmentVariable("TXAGENT_PET_TEST_SESSION")+"_"+pid;
        public static List<PsInstanceInfo> All()=>Directory.GetFiles(SharedPetRegression.Root,"*.instance").Select(p=>new PsInstanceInfo{Pid=int.Parse(Path.GetFileNameWithoutExtension(p))}).ToList();
    }
    public sealed class ToolRegistry{public int Count=>0;public IEnumerable<ITxAgentTool> Tools=>new ITxAgentTool[0];public bool TryGet(string name,out ITxAgentTool tool){tool=null;return false;}}
    public interface ITxAgentTool{string Name{get;}bool IsReadOnly{get;}string Description{get;}JObject InputSchema{get;}string Execute(JObject input);}
    public static class AuditLog{public static void Write(string line){}}
    public class PsContext
    {
        public static PsContext Current=new PsContext();public static SynchronizationContext Context;
        public void Run(Action action){Exception error=null;Context.Send(_=>{try{action();}catch(Exception ex){error=ex;}},null);if(error!=null)throw error;}
        public T Run<T>(Func<T> action){T result=default(T);Run(()=>{result=action();});return result;}
    }
}
