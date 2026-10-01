from pathlib import Path
root = Path(__file__).resolve().parents[3]
source = (root/'TxAgentCommand.cs').read_text(encoding='utf8')
start = source.index('    internal sealed class DshPetHost : IDisposable')
host = source[start:source.rfind('\n}')]
head = '''using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
namespace TxTools.Agent {
internal class TxAgentCommand { }
'''
test = '''
internal class DshPetHostSmoke {
    [STAThread] static int Main(string[] args) {
        bool crashTest = args.Contains("--crash");
        Application.EnableVisualStyles();
        Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Debug.AutoFlush = true;
        using (var dispatcher = new Control()) {
            var handle = dispatcher.Handle;
            var host = new DshPetHost(dispatcher);
            var field = typeof(DshPetHost).GetField("_stateFile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            string file = (string)field.GetValue(host);
            host.SetThinking(true);
            Check(file, "thinking");
            host.SetState("working", "Smoke tool");
            Check(file, "working");
            host.Celebrate();
            host.SetThinking(false);
            Check(file, "success");
            host.SetState(null);
            Check(file, null);
            var context = new ApplicationContext();
            bool exited = false;
            bool ready = false;
            bool requestedClose = false;
            string failure = null;
            host.Failed += message => {
                failure = message;
                if (!crashTest) { host.Dispose(); context.ExitThread(); }
            };
            host.Closed += (s,e) => {
                exited = true;
                if (!requestedClose && !crashTest) failure = "Helper exited before an intentional close";
                context.ExitThread();
            };
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            var deadline = DateTime.UtcNow.AddSeconds(65);
            int phase = 0;
            timer.Tick += (s,e) => {
                if (!ready && host.IsReady) {
                    ready = true;
                    Console.WriteLine("READY: visible, on-screen video advancing; log=" + host.LogPath);
                }
                if (ready && !requestedClose && ++phase >= 12) {
                    requestedClose = true;
                    if (crashTest) {
                        var processField = typeof(DshPetHost).GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        ((Process)processField.GetValue(host)).Kill();
                    } else host.Dispose();
                }
                if (DateTime.UtcNow > deadline) {
                    failure = "Timed out waiting for visible video and helper cleanup: " + host.LogPath;
                    context.ExitThread();
                }
            };
            timer.Start();
            Application.Run(context);
            timer.Dispose();
            host.Dispose();
            if ((crashTest ? failure == null : failure != null) || !ready || !exited || System.IO.File.Exists(file)) {
                Console.Error.WriteLine("FAIL: " + (failure ?? "Helper readiness/cleanup failed") );
                return 1;
            }
            Console.WriteLine(crashTest
                ? "PASS: unexpected helper exit reports a failure with durable log and cleans state"
                : "PASS: actual .NET host visible video handshake, status writes, terminal retention, cancellation and helper cleanup");
        }
        return 0;
    }
    static void Check(string file, string expected) {
        var value = JObject.Parse(System.IO.File.ReadAllText(file));
        if ((string)value["state"] != expected) throw new Exception("Unexpected pet state");
    }
}
}
'''
out = root/'artifacts/dsh-pet-smoke/DshPetHostSmoke.cs'
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(head+host+test, encoding='utf8')
print(out)
