// Exercise the actual command entry/factory with a fake PS manager and window hosts.
const fs = require('fs'), path = require('path');
const base = path.resolve(__dirname, '..');
const src = fs.readFileSync(path.join(base, 'TxAgentCommand.cs'), 'utf8');
function method(signature) {
    const start = src.indexOf(signature);
    if (start < 0) throw Error('Missing method: ' + signature);
    return src.slice(start, src.indexOf('\n        }', start) + '\n        }'.length);
}
const harness = `using System;
using System.Threading;
using System.Windows.Forms;
namespace TxTools.Agent {
internal sealed class WindowsFormsSynchronizationContext : SynchronizationContext {}
internal abstract class CommandBase { public abstract void Execute(object value); }
internal sealed class ToolRegistry {}
internal sealed class PsContext { internal static PsContext Current;internal static int Captures;
 internal PsContext(SynchronizationContext context){} internal static void CaptureFromMainThread(){Captures++;} }
internal static class PsInstanceRegistry {internal static bool Occupied;internal static int Acquires,Releases;
 internal static bool TryAcquireWindow(){Acquires++;return !Occupied;}internal static void ReleaseWindow(){Releases++;} }
internal static class RecipeUiActions { internal static bool IsRunning; }
internal static class TxApplication {internal static Manager ViewersManager=new Manager();internal static string StatusBarMessage;}
internal sealed class Manager {
 internal bool Registered,Single=true;internal object Control;internal int Shows;
 internal bool IsViewer(string name){return Registered;}
 internal bool IsNonOpenWithViewer(string name){return Single;}
 internal TxViewerControl ShowViewer(string name){Shows++;var dock=Control as TxAgentDockViewer;dock?.EnsureContent();return new TxViewerControl{Control=Control};}
}
internal sealed class TxViewerControl {internal object Control;internal object GetViewerControl(){return Control;}}
internal sealed class TxAgentControl : IDisposable {
 internal static int Created;internal bool IsDisposed;internal event EventHandler Disposed;
 internal TxAgentControl(SynchronizationContext context,ToolRegistry tools){Created++;}
 internal void ReportPetError(string message){} public void Dispose(){if(IsDisposed)return;IsDisposed=true;Disposed?.Invoke(this,EventArgs.Empty);}
}
internal sealed class TxAgentForm : IDisposable {
 internal static int Created;internal static bool FailShow;internal bool IsDisposed;internal string Name;
 internal FormWindowState WindowState;internal TxAgentControl Content;internal event EventHandler FormClosed;
 internal TxAgentForm(TxAgentControl content){Created++;Content=content;}
 internal void BringToFront(){}internal void Activate(){}internal void Focus(){}
 internal void Show(IWin32Window owner){Show();} internal void Show(){if(FailShow)throw new Exception("show failed");}
 public void Dispose(){if(IsDisposed)return;IsDisposed=true;FormClosed?.Invoke(this,EventArgs.Empty);Content.Dispose();}
}
internal sealed class TxAgentDockViewer {
 internal const string RegisteredName="TxAgentDockViewer";internal TxAgentControl Content;
 internal bool EnsureContent(){if(Content!=null&&!Content.IsDisposed)return true;Content=TxAgentCommand.CreateViewerContent();return Content!=null;}
 internal void Focus(){} internal void Uninitialize(){Content?.Dispose();Content=null;}
}
internal sealed class TxAgentCommand : CommandBase {
 internal static TxAgentForm _form;internal static TxAgentControl _content;private static string _petError;
 internal static int OccupiedHints;internal static bool FailTools;
 private static ToolRegistry BuildToolRegistry(){if(FailTools)throw new Exception("tools failed");return new ToolRegistry();}
 private static void ShowOccupiedHint(){OccupiedHints++;}private static IWin32Window TryGetPsMainWindow(){return null;}
 private static void SetPetState(string value){}
 ${method('public override void Execute(object cmdParams)')}
 ${method('internal static TxAgentControl CreateViewerContent()')}
}
internal static class DockingEntryRegression {
 static int checks;static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
 static void ExpectError(Action action,string text){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Check(failed,text);}
 [STAThread] static int Main(){try{
  var command=new TxAgentCommand();var manager=TxApplication.ViewersManager;
  command.Execute(null);var content=TxAgentCommand._content;var form=TxAgentCommand._form;
  Check(form!=null&&form.Content==content&&TxAgentControl.Created==1,"unregistered environment opens the shared content in a standalone host");
  command.Execute(null);Check(TxAgentControl.Created==1&&TxAgentForm.Created==1,"repeated standalone launch activates without creating another session");
  form.Dispose();Check(TxAgentCommand._content==null&&PsInstanceRegistry.Releases==1,"standalone close releases the global session slot");
  manager.Registered=true;var dock=new TxAgentDockViewer();manager.Control=dock;
  command.Execute(null);content=TxAgentCommand._content;
  Check(dock.Content==content&&TxAgentForm.Created==1&&TxAgentControl.Created==2,"registered environment opens the PS Viewer without a separate form");
  command.Execute(null);Check(TxAgentControl.Created==2&&manager.Shows==2,"repeated Viewer launch reuses its live session");
  dock.Uninitialize();Check(TxAgentCommand._content==null&&PsInstanceRegistry.Releases==2,"Viewer uninitialize releases the global session slot");
  dock.EnsureContent();Check(TxAgentControl.Created==3&&PsContext.Captures>0,"restoring a Viewer can initialize content without a command click");dock.Uninitialize();
  PsInstanceRegistry.Occupied=true;command.Execute(null);
  Check(TxAgentCommand._content==null&&TxAgentControl.Created==3,"occupied Viewer does not create a competing conversation");
  manager.Registered=false;command.Execute(null);Check(TxAgentCommand.OccupiedHints==1&&TxAgentForm.Created==1,"occupied fallback reports the owner instead of opening a second window");
  PsInstanceRegistry.Occupied=false;manager.Registered=true;manager.Single=false;
  ExpectError(()=>command.Execute(null),"multi-instance Viewer registration is rejected");manager.Single=true;manager.Control=new object();
  ExpectError(()=>command.Execute(null),"wrong Viewer class registration is rejected");
  Check(TxAgentForm.Created==1,"invalid registration does not silently open another host");
  manager.Registered=false;TxAgentForm.FailShow=true;int releases=PsInstanceRegistry.Releases;
  try{command.Execute(null);}catch(Exception){}
  Check(TxAgentCommand._content==null&&TxAgentCommand._form==null&&PsInstanceRegistry.Releases==releases+1,"failed standalone Show disposes content and releases ownership");
  TxAgentForm.FailShow=false;TxAgentCommand.FailTools=true;releases=PsInstanceRegistry.Releases;
  try{command.Execute(null);}catch(Exception){}
  Check(TxAgentCommand._content==null&&PsInstanceRegistry.Releases==releases+1,"tool construction failure releases ownership");
  Console.WriteLine("PASS: docking entry ("+checks+" checks)");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
}
`;
const output = path.join(base, 'artifacts/docking-tests');
fs.mkdirSync(output, { recursive: true });
fs.writeFileSync(path.join(output, 'DockingEntryRegression.cs'), harness);
console.log('Generated PS Viewer command routing regression harness.');
