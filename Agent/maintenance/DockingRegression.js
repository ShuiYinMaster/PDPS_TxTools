// Build a headless harness from the production lifecycle and geometry code.
// No PS process, WebView2 initialization or user data files are used.
const fs = require('fs'), path = require('path');
const base = path.resolve(__dirname, '..');
const src = fs.readFileSync(path.join(base, 'UI/TxAgentForm.cs'), 'utf8');
function method(signature) {
    const start = src.indexOf(signature);
    if (start < 0) throw Error('Missing production method: ' + signature);
    return src.slice(start, src.indexOf('\n        }', start) + '\n        }'.length);
}
const layoutStart = src.indexOf('    internal sealed class RecipeSidebarHostLayout');
const layouts = src.slice(layoutStart, src.lastIndexOf('\n}'));
const harness = `using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
namespace TxTools.Agent.UI {
${layouts}
internal class FakeTimer : IDisposable { public int Stops, Disposals; public void Stop(){Stops++;} public void Dispose(){Disposals++;} }
internal class BaseSession : IDisposable { public void Dispose(){Dispose(true);} protected virtual void Dispose(bool disposing){} }
internal static class AskUserBridge { internal static Func<string,string> Handler; }
internal static class RecipeStore { internal static event Action RecipesChanged; internal static void Raise(){RecipesChanged?.Invoke();} }
internal static class RecipeUiActions { internal static event Action Changed; internal static void Raise(){Changed?.Invoke();} }
internal static class UploadStore { internal static int Clears; internal static void ClearAll(){Clears++;} }
internal interface ITxAgentTool { string Name {get;} string Description {get;} }
internal class JObject { public object this[string key] {get{return null;}} }
internal sealed class Session : BaseSession {
 private volatile bool _sessionClosed; private bool _webViewReady=true, _profileCleaned;
 private readonly UnhandledExceptionEventHandler _unhandledException;
 internal Func<string,string> _askUserHandler; internal Func<bool,int,bool> SidebarLayout;
 internal event EventHandler CloseRequested;
 internal FakeTimer _loadingTimer=new FakeTimer(),_studyTimer=new FakeTimer();
 internal CancellationTokenSource _cts=new CancellationTokenSource();
 internal readonly object _pendingAskUserLock=new object(),_pendingApprovalLock=new object();
 internal TaskCompletionSource<bool> _pendingApproval=new TaskCompletionSource<bool>();
 internal TaskCompletionSource<string> _pendingAskUser=new TaskCompletionSource<string>();
 internal int Saves, SyncStops, Extracts, Changes; internal static int ProfileCleanups;
 internal Session(){_unhandledException=(s,e)=>{}; AppDomain.CurrentDomain.UnhandledException+=_unhandledException;
  _askUserHandler=p=>p; AskUserBridge.Handler=_askUserHandler; RecipeStore.RecipesChanged+=OnRecipesChanged; RecipeUiActions.Changed+=OnRecipesChanged;}
 private void SaveCurrent(){if(!_sessionClosed) Saves++;} private void StopProcessSync(){SyncStops++;}
 private void FireExtractLessons(){Extracts++;} private void OnRecipesChanged(){Changes++;}
 private void ReleasePendingApproval(bool value){lock(_pendingApprovalLock){_pendingApproval?.TrySetResult(value);}}
 private void ReleasePendingAskUser(string value){lock(_pendingAskUserLock){_pendingAskUser?.TrySetResult(value);}}
 private static void CleanupWebViewProfile(){ProfileCleanups++;} private void PostJs(object value){}
 private static string Compact(JObject value){return "";}
 internal bool LateApproval(){return AskApprovalHtml(null,null,false);}
 internal string LateAsk(){return AskUser("", "", null);}
 ${method('internal void ShutdownSession()')}
 ${method('protected override void Dispose(bool disposing)')}
 ${method('private bool AskApprovalHtml(')}
 ${method('private string AskUser(string question,')}
}
internal static class DockingRegression {
 static int checks; static void Check(bool value,string text){if(!value)throw new Exception(text); checks++; Console.WriteLine("PASS "+text);}
 [STAThread] static int Main(){try{
  var area=new Rectangle(0,0,1920,1080); var bounds=new Rectangle(120,80,560,720); int writes=0;
  RecipeSidebarHostLayout layout=null;
  layout=new RecipeSidebarHostLayout(()=>bounds, value=>{bounds=value;writes++; layout.Apply(true,300,true);},()=>420,()=>0,_=>area);
  Check(!layout.Apply(true,300,true)&&bounds.Width==860,"floating host expands without overlay");
  layout.Apply(true,300,true);Check(writes==1,"repeated open and reentrant resize add width only once");
  layout.Apply(false,300,true);Check(bounds==new Rectangle(120,80,560,720),"closing restores the original floating bounds");
  layout.Apply(true,300,true);bounds=new Rectangle(200,90,1000,750);layout.Apply(false,300,true);
  Check(bounds==new Rectangle(200,90,700,750),"closing preserves manual move, resize and height");
  bounds=new Rectangle(0,0,1800,720);int before=writes;
  Check(layout.Apply(true,300,true)&&bounds.Width==1800&&writes==before,"insufficient screen space uses overlay without resizing");
  layout.Apply(false,300,true);bounds=new Rectangle(100,80,560,720);
  Check(layout.Apply(true,300,false)&&bounds.Width==560,"maximized host does not resize");
  layout.Apply(true,300,true);Check(bounds.Width==860,"restoring a floating window applies the pending sidebar expansion");
  layout.Reset();bounds=new Rectangle(0,0,400,900);before=writes;layout.Apply(false,300,true);
  Check(writes==before&&bounds.Width==400,"docking discards floating geometry bookkeeping");
  Check(RecipeSidebarWindowLayout.NeedsOverlay(true,560,96),"narrow docked panel uses overlay");
  Check(!RecipeSidebarWindowLayout.NeedsOverlay(true,900,96),"wide docked panel can show both areas");
  Check(RecipeSidebarWindowLayout.NeedsOverlay(true,900,144),"docked width threshold follows DPI");
  Check(!RecipeSidebarWindowLayout.NeedsOverlay(false,300,144),"closed sidebar never requests overlay");
  var session=new Session(); var approval=session._pendingApproval.Task;var answer=session._pendingAskUser.Task;var token=session._cts.Token;
  var loading=session._loadingTimer;var study=session._studyTimer;int cleared=UploadStore.Clears;
  RecipeStore.Raise();RecipeUiActions.Raise();Check(session.Changes==2,"session receives recipe updates while active");
  session.ShutdownSession();Check(session.Saves==1&&session.SyncStops==1,"close saves the conversation and releases process synchronization");
  Check(approval.IsCompleted&&!approval.Result&&answer.IsCompleted&&answer.Result==null,"closing releases pending approval and user answer waits");
  Check(token.IsCancellationRequested,"closing cancels the in-flight model request");
  Check(AskUserBridge.Handler==null,"closing removes its own shared question handler");
  RecipeStore.Raise();RecipeUiActions.Raise();Check(session.Changes==2&&study.Disposals==1,"closing detaches recipe events and stops study polling");
  Check(!session.LateApproval()&&session.LateAsk()==null,"late requests cannot create new blocking waits after close");
  session.Dispose();session.Dispose();session.ShutdownSession();
  Check(session.Saves==1&&session.Extracts==1&&UploadStore.Clears==cleared+1&&loading.Disposals==1,"repeated uninitialize/dispose performs session cleanup only once");
  Check(Session.ProfileCleanups==1,"WebView profile cleanup runs once even when ShutdownSession precedes Dispose");
  var older=new Session();Func<string,string> replacement=p=>"new";AskUserBridge.Handler=replacement;older.Dispose();
  Check(AskUserBridge.Handler==replacement,"closing an older session does not remove a newer question handler");
  Console.WriteLine("PASS: docking and lifecycle ("+checks+" checks)");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
}
`;
const output = path.join(base, 'artifacts/docking-tests');
fs.mkdirSync(output, { recursive: true });
fs.writeFileSync(path.join(output, 'DockingRegression.cs'), harness);
console.log('Generated headless production lifecycle and layout regression harness.');
