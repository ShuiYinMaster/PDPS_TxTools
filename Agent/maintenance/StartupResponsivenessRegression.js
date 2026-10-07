const fs=require('fs'),path=require('path');const root=path.resolve(__dirname,'../..');
const src=fs.readFileSync(path.join(root,'Agent/TxAgentCommand.cs'),'utf8');
const controller=src.slice(src.indexOf('    internal sealed class DshPetController'),src.indexOf('    /// <summary>Owns dsh-pet'));
const harness=`using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
namespace TxTools.Agent {
${controller}
internal sealed class DshPetHost : IDisposable {
 public static int Created, Disposed; public static bool ThrowNext; public static ManualResetEventSlim Entered=new ManualResetEventSlim();
 public string LogPath=>"test-helper.log"; public bool IsDisposed; public DshPetHost(Control dispatcher){Interlocked.Increment(ref Created);Entered.Set();Thread.Sleep(650);if(ThrowNext){ThrowNext=false;throw new Exception("slow start failed");}}
 public event EventHandler OpenAssistantRequested, HideRequested, Closed; public event Action<JObject> OpenRecipesRequested; public event Action<string> Failed;
 public void SetState(string state,string task){} public void Dispose(){if(!IsDisposed){IsDisposed=true;Interlocked.Increment(ref Disposed);}}
}
class StartupResponsivenessRegression {
 static int checks; static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
 static void Pump(int ms){var elapsed=Stopwatch.StartNew();while(elapsed.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
 [STAThread] static int Main(){try{
  var controller=new DshPetController(true,_=>{},()=>{});int beats=0;var timer=new System.Windows.Forms.Timer{Interval=20};timer.Tick+=(s,e)=>beats++;timer.Start();
  var elapsed=Stopwatch.StartNew();Application.DoEvents();Check(elapsed.ElapsedMilliseconds<250,"queued pet startup returns without waiting for slow Process.Start");
  Check(DshPetHost.Entered.Wait(1000),"pet initialization runs on a worker");Pump(350);Check(beats>=5,"UI message loop remains responsive during 650 ms pet initialization");
  controller.SetEnabled(false);Pump(500);Check(DshPetHost.Disposed==1,"disabling during startup disposes the completed helper");controller.Dispose();
  int before=DshPetHost.Created;var hidden=new DshPetController(false,_=>{},()=>{});Pump(100);Check(DshPetHost.Created==before,"hidden pets do not launch a helper");hidden.SetEnabled(true);hidden.SetEnabled(true);Pump(850);Check(DshPetHost.Created==before+1,"repeated enable requests launch only one helper");hidden.Dispose();Check(DshPetHost.Disposed==2,"controller disposal releases the active helper");
  DshPetHost.ThrowNext=true;int uiThread=Thread.CurrentThread.ManagedThreadId,callbackThread=0;var failing=new DshPetController(true,_=>{},()=>{});failing.Failed+=message=>callbackThread=Thread.CurrentThread.ManagedThreadId;Pump(850);Check(callbackThread==uiThread,"worker startup failure returns to the UI thread");failing.Dispose();timer.Dispose();Console.WriteLine("PASS: startup responsiveness ("+checks+" checks)");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
}
`;
fs.mkdirSync(path.join(root,'artifacts/startup-review'),{recursive:true});fs.writeFileSync(path.join(root,'artifacts/startup-review/StartupResponsivenessRegression.cs'),harness);
// Guard the registration path separately from the worker/controller timing checks.
const cctor=src.slice(src.indexOf('        static TxAgentCommand()'),src.indexOf('        private static void OnStartupDelay'));
if(/BuildToolRegistry|RecipeStore|InitializePet|TxAgentService\.Start|WaitOne|Process\.Start|UserPrefsStore/.test(cctor))throw Error('Heavy work returned to static initialization');
const service=fs.readFileSync(path.join(root,'Agent/Core/Multi/TxAgentService.cs'),'utf8');
if(service.includes('PsContext.CaptureFromMainThread();'))throw Error('Worker must not replace PS UI context');
console.log('PASS: static command initialization only schedules delayed startup without recipes, services, locks, preferences or Electron; worker keeps the UI context');
