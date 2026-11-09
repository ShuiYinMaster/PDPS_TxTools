using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TxAgent.Core;
using TxTools.Agent.Harness;

// Run against an isolated TxTools build. No live model calls or scene changes.
public static class ApprovalRegression
{
    static int checks;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    sealed class Host : IAgentHost
    {
        public bool Saved;
        public int SaveCount, ConfirmCount;
        public Func<bool> Answer = () => true;
        public HostMode Mode { get { return HostMode.Standalone; } }
        public void Invoke(Action action) { action(); }
        public T Invoke<T>(Func<T> action) { return action(); }
        public bool Confirm(string title, string detail, bool destructive) { ConfirmCount++; return Answer(); }
        public RestorePoint CreateRestorePoint(string reason)
        {
            SaveCount++;
            return new RestorePoint { Created = Saved, HowToRollback = "test save unavailable" };
        }
        public void Log(string level, string text) { }
    }

    sealed class Tool : ITool
    {
        public string Name { get; set; }
        public string Description { get { return Name; } }
        public string ParametersJsonSchema { get { return "{}"; } }
        public bool IsWrite { get; set; }
        public bool IsDestructive { get; set; }
        public int Executions;
        public ToolResult Execute(string args, IAgentHost host) { Executions++; return ToolResult.Ok("done"); }
    }

    sealed class Client : ILlmClient
    {
        public int Turn;
        public string ModelId { get { return "mock"; } }
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Turn++;
            return Task.FromResult(Turn <= 2
                ? new LlmResponse { ToolCalls = new List<ToolCall> {
                    new ToolCall { Id = "write" + Turn, Name = "write", ArgumentsJson = "{\"step\":" + Turn + "}" },
                    new ToolCall { Id = "read" + Turn, Name = "read", ArgumentsJson = "{}" }
                } }
                : new LlmResponse { Content = "complete" });
        }
    }

    static AgentSession Run(Host host, Tool write, bool restore = true, CancellationToken ct = default(CancellationToken))
    {
        var registry = new ToolRegistry();
        registry.Register(write);
        registry.Register(new Tool { Name = "read" });
        var session = new AgentSession(null);
        new AgentLoop(new Client(), registry, host, new AgentLoopOptions { AutoRestorePoint = restore })
            .RunAsync(session, ct).GetAwaiter().GetResult();
        return session;
    }

    static void CheckLoop()
    {
        var host = new Host();
        var tool = new Tool { Name = "write", IsWrite = true };
        Run(host, tool);
        Check(tool.Executions == 2 && host.ConfirmCount == 1, "allow without restore point executes writes after one confirmation");
        Check(host.SaveCount == 1, "restore point attempted once per run");

        host = new Host { Answer = () => false };
        tool = new Tool { Name = "write", IsWrite = true };
        var session = Run(host, tool);
        Check(tool.Executions == 0 && host.ConfirmCount == 1, "denial persists across calls and model turns");
        Check(session.Messages.Count(m => m.Role == MessageRole.Tool && m.Content == "done") == 2, "read tools still run after write denial");
        Check(session.Messages.Count(m => m.Role == MessageRole.Tool && m.Content.Contains("用户拒绝")) == 2, "actual denial is reported consistently");

        host = new Host { Answer = () => { throw new InvalidOperationException("approval UI unavailable"); } };
        tool = new Tool { Name = "write", IsWrite = true };
        session = Run(host, tool);
        Check(tool.Executions == 0 && host.ConfirmCount == 1, "approval error blocks subsequent writes");
        Check(session.Messages.Any(m => m.Role == MessageRole.Tool && m.Content.Contains("approval UI unavailable")), "approval error preserves diagnostic cause");
        Check(!session.Messages.Any(m => m.Role == MessageRole.Tool && m.Content.StartsWith("用户拒绝")), "approval failure does not impersonate user denial");

        host = new Host { Saved = true };
        tool = new Tool { Name = "write", IsWrite = true };
        Run(host, tool);
        Check(tool.Executions == 2 && host.ConfirmCount == 0, "saved restore point needs no failure confirmation");

        tool = new Tool { Name = "write", IsWrite = true, IsDestructive = true };
        host = new Host { Saved = true, Answer = () => { throw new InvalidOperationException("tool approval broken"); } };
        session = Run(host, tool);
        Check(tool.Executions == 0 && session.Messages.Any(m => m.Role == MessageRole.Tool && m.Content.Contains("tool approval broken")), "destructive approval faults are surfaced without executing tools");

        using (var cts = new CancellationTokenSource())
        {
            host = new Host { Answer = () => { cts.Cancel(); return true; } };
            tool = new Tool { Name = "write", IsWrite = true };
            bool cancelled = false;
            try { Run(host, tool, true, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && tool.Executions == 0, "stop while confirming cannot execute an approved write");
        }
    }

    static void CheckRouting()
    {
        var host = new PsAgentHost(new SynchronizationContext());
        int tools = 0, generic = 0;
        host.ConfirmRequest = (name, input) => { tools++; return true; };
        host.HostConfirmRequest = (title, detail, destructive) => {
            generic++;
            Check(title == "无法建立回滚点" && detail.Contains("save failed") && destructive, "host approval retains title and failure detail");
            return true;
        };
        Check(host.Confirm("无法建立回滚点", "原因：save failed", true), "unnamed confirmation reaches UI instead of unknown-tool rejection");
        Check(generic == 1 && tools == 0, "host confirmation never enters tool lookup");
        Check(host.Confirm("确认破坏性操作", "工具：write\n参数：{}", true) && tools == 1, "named tool retains tool approval routing");
        host.HostConfirmRequest = (title, detail, destructive) => { throw new InvalidOperationException("display failed"); };
        bool failed = false;
        try { host.Confirm("host", "no tool", true); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "host display error propagates as error, not false denial");

        var loop = (HarnessAgentLoop)FormatterServices.GetUninitializedObject(typeof(HarnessAgentLoop));
        typeof(HarnessAgentLoop).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(loop, new TxTools.Agent.Core.ToolRegistry());
        typeof(HarnessAgentLoop).GetField("_approvalRequest", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(loop, new Func<TxTools.Agent.Core.ITxAgentTool, JObject, bool>((tool, input) => true));
        failed = false;
        try { typeof(HarnessAgentLoop).GetMethod("InvokeApproval", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(loop, new object[] { "missing", new JObject() }); }
        catch (TargetInvocationException ex) { failed = ex.InnerException is InvalidOperationException; }
        Check(failed, "unresolvable tool is diagnosed instead of silently rejected");
    }

    public static void Main(string[] args)
    {
        // Resolve the installed PS SDK without copying it into the repository.
        if (args.Length > 0) AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
            var path = System.IO.Path.Combine(args[0], new AssemblyName(e.Name).Name + ".dll");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        CheckLoop();
        CheckRouting();
        Console.WriteLine("TOTAL PASS: " + checks);
    }
}
