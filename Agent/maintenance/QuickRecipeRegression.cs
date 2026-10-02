// Compile with the real RecipeUiActions, UserPrefsStore and RecipeQuickForm.
// Engineering services are fakes: this test never modifies a PS study.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.UI;

public class QuickRecipeRegression
{
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Backend()
    {
        var r = RecipeStore.Item;
        UserPrefsStore.Save(new UserPrefs { Model="keep-model", DesktopPetVisible=false });
        UserPrefsStore.UpdateRecipeFavorite(r.Id,true);
        UserPrefsStore.UpdateRecipeFavorite(r.Id,true);
        var args = new Dictionary<string,string> { ["gap"]="2.5", ["targets"]="3,57,2,1|3,57,2,2", ["unknown"]="drop" };
        UserPrefsStore.UpdateRecipeArguments(r,args);
        var prefs=UserPrefsStore.Load();
        Check(prefs.PinnedRecipeIds.Count==1,"Favorite duplicate");
        Check(prefs.RecipeArguments[r.Id].Count==1 && prefs.RecipeArguments[r.Id]["gap"]=="2.5","Object IDs or undeclared args persisted");
        Check(prefs.Model=="keep-model" && !prefs.DesktopPetVisible,"Other prefs lost");
        Check(!(bool)RecipeUiActions.PickSelection(false)["ok"],"Single selection accepted multiple objects");
        Check((string)RecipeUiActions.PickSelection(true)["id"]==args["targets"],"Object ID delimiter changed");
        var msg=new JObject { ["recipeId"]=r.Id,["study"]="研究 A",["args"]=JObject.FromObject(args) };
        JObject result=null;
        var done=new ManualResetEventSlim();
        TxTools.Agent.Ps.PsBridge.Hold=true;
        RecipeUiActions.Run(1,msg,(seq,data)=>{ result=data;done.Set(); });
        Check(TxTools.Agent.Ps.PsBridge.Entered.Wait(3000),"Run did not start");
        Check(RecipeUiActions.IsRunning && RecipeUiActions.RunningId==r.Id,"Shared gate not held");
        JObject duplicate=null;
        RecipeUiActions.Run(2,msg,(seq,data)=>duplicate=data);
        Check(duplicate!=null && !(bool)duplicate["ok"],"Second entry executed concurrently");
        Tecnomatix.Engineering.TxApplication.ActiveDocument.CurrentStudy.Name="研究 B";
        TxTools.Agent.Ps.PsBridge.Release.Set();
        Check(done.Wait(3000),"Completion missing");
        Check(!(bool)result["ok"] && TxTools.Agent.Ps.PsBridge.Executed==0,"Queued execution used stale study");
        Check(!RecipeUiActions.IsRunning,"Gate not released");
        JObject stale=null;RecipeUiActions.Run(3,msg,(seq,data)=>stale=data);
        Check(stale!=null && !(bool)stale["ok"],"Initially stale study accepted");
        Tecnomatix.Engineering.TxApplication.ActiveDocument.CurrentStudy.Name="研究 A";
        TxTools.Agent.Ps.PsBridge.Hold=false;done.Reset();
        RecipeUiActions.Run(4,msg,(seq,data)=>{result=data;done.Set();});
        Check(done.Wait(3000) && (bool)result["ok"] && TxTools.Agent.Ps.PsBridge.Executed==1,"Valid rerun failed");
        Check(RecipeStore.Item.RunCount==1 && RecipeStore.Item.FailCount==1,"Outcome not recorded");
        Check(((JArray)RecipeUiActions.List()["recent"]).Count==1,"Recent missing");
        // Negative-coordinate and small monitor placement, without relying on one screen.
        foreach(var area in new[]{new Rectangle(-1920,0,1920,1040),new Rectangle(0,0,800,600)}) {
            var target=RecipeQuickPlacement.Place(area,new Rectangle(area.Left+2,area.Bottom-90,80,90),new Size(340,510));
            Check(area.Contains(target),"Panel offscreen");
        }
        Console.WriteLine("PASS: actual prefs, no persisted object IDs, shared execution gate, queued/initial study rejection, rerun, outcomes, placement");
    }

    [STAThread] static int Main()
    {
        string prefs=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"prefs.json");
        byte[] previous=File.Exists(prefs)?File.ReadAllBytes(prefs):null;
        int exit=1;
        try {
            Backend();
            Application.EnableVisualStyles();
            bool managed=false;
            var form=new RecipeQuickForm(()=>managed=true);
            var initialWeb=form.Controls.OfType<WebView2>().Single();
            initialWeb.CoreWebView2InitializationCompleted+=(s,e)=>{
                Console.WriteLine("Native initialization: "+e.IsSuccess+" "+e.InitializationException);
                if(initialWeb.CoreWebView2==null)return;
                initialWeb.CoreWebView2.NavigationStarting+=(a,b)=>Console.WriteLine("Native navigation: "+b.Uri.Substring(0,Math.Min(64,b.Uri.Length))+" cancel="+b.Cancel);
                initialWeb.CoreWebView2.NavigationCompleted+=(a,b)=>Console.WriteLine("Native navigation complete: "+b.IsSuccess+" "+b.WebErrorStatus);
            };
            var context=new ApplicationContext();
            var timer=new System.Windows.Forms.Timer { Interval=100 };
            var deadline=DateTime.UtcNow.AddSeconds(25);
            bool started=false;
            timer.Tick+=async(s,e)=>{
                if(started)return;
                if(DateTime.UtcNow>deadline){started=true;timer.Stop();var w=form.Controls.OfType<WebView2>().Single();Console.Error.WriteLine("WebView ready timeout: "+form.Controls.OfType<Label>().First().Text+" source="+w.Source);if(w.CoreWebView2!=null)Console.Error.WriteLine(await w.CoreWebView2.ExecuteScriptAsync("({html:document.documentElement.outerHTML,bridge:typeof window.chrome.webview,ready:document.readyState})"));form.Dispose();context.ExitThread();return;}
                bool ready=(bool)typeof(RecipeQuickForm).GetField("_ready",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                if(!ready)return;
                started=true;timer.Stop();
                try {
                    var web=form.Controls.OfType<WebView2>().Single();
                    var body=await web.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                    Check(body.Contains("焊点分组"),"Native WebView recipe list missing");
                    Check(form.TopMost && Screen.FromControl(form).WorkingArea.Contains(form.Bounds),"Native panel not on-screen/topmost");
                    await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.card-main').click();true");
                    Check(await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.purpose strong').textContent")=="\"按焊枪\"","Native Markdown description missing");
                    await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.pick button').click();true");
                    await Task.Delay(250);
                    Check(await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('run').disabled")=="false","Native object selection bridge failed");
                    await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('run').click();true");
                    await Task.Delay(400);
                    Check((await web.CoreWebView2.ExecuteScriptAsync("document.body.innerText")).Contains("执行完成"),"Native run result bridge failed");
                    Check(await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.result-detail strong').textContent")=="\"2 个焊点\"","Native Markdown result missing");
                    using(var stream=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quick-native.png")))
                        await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream);
                    form.Toggle(null,null);Check(!form.Visible,"Toggle did not hide");
                    form.Toggle(new JObject { ["screenIndex"]=0,["rx"]=0.8,["ry"]=0.8,["rw"]=0.1,["rh"]=0.1 },null);
                    Check(form.Visible,"Toggle did not reopen");
                    await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('manage').click();true");
                    await Task.Delay(150);Check(managed && !form.Visible,"Manager callback missing");
                    Console.WriteLine("PASS: actual Windows Form + WebView2 ready, list, selection, execution/result bridge, toggle, manager callback");
                    exit=0;
                }catch(Exception ex){Console.Error.WriteLine(ex);}
                finally{form.Dispose();context.ExitThread();}
            };
            form.Toggle(new JObject { ["screenIndex"]=0,["rx"]=0.8,["ry"]=0.8,["rw"]=0.1,["rh"]=0.1 },null);
            timer.Start();Application.Run(context);timer.Dispose();
        }catch(Exception ex){Console.Error.WriteLine(ex);}
        finally { if(previous!=null)File.WriteAllBytes(prefs,previous);else File.Delete(prefs); }
        return exit;
    }
}

namespace Tecnomatix.Engineering {
    public class Study { public string Name {get;set;}="研究 A"; }
    public class Document { public Study CurrentStudy {get;}=new Study(); }
    public class Item {public string Id {get;set;} public string Name {get;set;} }
    public class Selection { public List<Item> GetItems()=>new List<Item>{new Item{Id="3,57,2,1",Name="焊点 01"},new Item{Id="3,57,2,2",Name="焊点 02"}}; }
    public static class TxApplication {public static Document ActiveDocument=new Document();public static Selection ActiveSelection=new Selection();}
}
namespace TxTools.Agent {
    public static class TxAgentCommand { public static void SetPetState(string state,string task=null){} }
}
namespace TxTools.Agent.Core {
    public static class RecipeStore {
        public static event Action RecipesChanged;
        public static Recipe Item=new Recipe { Id="weld",Name="焊点分组",Description="## 用途\n\n**按焊枪**整理选中的 `焊点`",Code="fake",
            Params=new List<RecipeParam>{new RecipeParam{Name="targets",Label="焊点",Kind="objects"},new RecipeParam{Name="gap",Label="间距",Kind="number",Default="1"}} };
        public static IEnumerable<Recipe> All()=>new[]{Item};
        public static Recipe Get(string id)=>id==Item.Id?Item:null;
        public static void RecordRun(string id,bool ok){if(ok)Item.RunCount++;else Item.FailCount++;Item.LastRunUtc=DateTime.UtcNow;RecipesChanged?.Invoke();}
    }
    public static class RecipeRunner {public static string BuildCode(Recipe r,IDictionary<string,string> args,out string error){error=null;return r.Code;} }
    public static class SnippetStore {public static string NormalizeLang(string lang)=>lang;}
    public class PsContext {public static PsContext Current=new PsContext();public void Run(Action run)=>run();}
    public static class AuditLog {public static void Write(string text){} }
    public static class TxToolsTemp {public static string DirectoryFor(string plugin,params string[] parts){string p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quick-native-cache");Directory.CreateDirectory(p);return p;} }
}
namespace TxTools.Agent.Ps {
    public static class PsBridge {
        public static readonly ManualResetEventSlim Entered=new ManualResetEventSlim(),Release=new ManualResetEventSlim();
        public static bool Hold;public static int Executed;
        public static string RunCSharp(string code,out bool ok,string label,Func<string> validate){Entered.Set();if(Hold)Release.Wait();var error=validate();ok=error==null;if(ok)Executed++;return error??"已整理 **2 个焊点**";}
    }
}
namespace TxTools.Agent.Scripting {
    public enum PythonRunMode {Execute}
    public class PythonResult {public bool Success=true;public string ToAgentText()=>"done";}
    public class PythonHostProvider {public static PythonHostProvider Instance=new PythonHostProvider();public PythonResult Run(string code,PythonRunMode mode,string label)=>new PythonResult();}
}
