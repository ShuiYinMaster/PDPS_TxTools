using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TxTools.Agent.Core;
using TxTools.Agent.Harness;

public static class StorageRegression
{
    static int checks;
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static ChatRequest Build(DeepSeekLlmClient client, TxAgent.Core.LlmRequest request)
    {
        return (ChatRequest)typeof(DeepSeekLlmClient).GetMethod("BuildRequest", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(client, new object[] { request, false });
    }
    public static void Main()
    {
        var message = new ChatMessage("assistant", "answer") { ReasoningContent = "provider reasoning <script>" };
        Check(!JsonConvert.SerializeObject(message).Contains("reasoning_content"), "ordinary API excludes reasoning");
        var settings = (JsonSerializerSettings)typeof(ConversationStore).GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var conv = new Conversation { Id = "storage_regression", Title = "test", Messages = new List<ChatMessage> { new ChatMessage("user", "test"), message } };
        var json = JsonConvert.SerializeObject(conv, settings);
        Check(json.Contains("reasoning_content"), "archive includes reasoning independently of wire flag");
        Check(JsonConvert.DeserializeObject<Conversation>(json).Messages[1].ReasoningContent == message.ReasoningContent, "archive reasoning round trip");
        Check(JsonConvert.DeserializeObject<Conversation>("{\"Id\":\"old\",\"Messages\":[{\"role\":\"assistant\",\"content\":\"old answer\"}]}").Messages[0].ReasoningContent == null, "legacy archive compatible");
        var request = new TxAgent.Core.LlmRequest {
            Messages = new List<TxAgent.Core.ChatMessage> {
                new TxAgent.Core.ChatMessage { Role = TxAgent.Core.MessageRole.Assistant, Content = "answer", ReasoningContent = "provider reasoning" }
            }, Tools = new List<TxAgent.Core.ToolSchema> { new TxAgent.Core.ToolSchema { Name = "test", Description = "test", ParametersJsonSchema = "{\"type\":\"object\",\"properties\":{}}" } }, MaxTokens = 8192
        };
        var official = new DeepSeekLlmClient(new DeepSeekClient("not-a-real-key"), "deepseek-v4-flash");
        var wire = Build(official, request);
        Check(wire.ReasoningEffort == "low", "official V4 default low");
        Check(JsonConvert.SerializeObject(wire).Contains("reasoning_content"), "official tools replay reasoning");
        official.ReasoningEffort = "max";
        Check(Build(official, request).ReasoningEffort == "max", "explicit max override retained");
        official.ReasoningEffort = "invalid";
        Check(Build(official, request).ReasoningEffort == "low", "invalid effort safely normalized");
        var proxy = new DeepSeekLlmClient(new DeepSeekClient("not-a-real-key", "https://example.invalid"), "deepseek-v4-flash");
        var proxyJson = JsonConvert.SerializeObject(Build(proxy, request));
        Check(!proxyJson.Contains("reasoning_effort") && !proxyJson.Contains("reasoning_content"), "proxy not sent provider-specific fields");
        request.Tools = null;
        Check(!JsonConvert.SerializeObject(Build(official, request)).Contains("reasoning_content"), "no tools excludes reasoning replay");
        var session = new TxAgent.Core.AgentSession(null);
        session.Add(request.Messages[0]);
        Check(session.TotalTokens >= ("answer".Length + "provider reasoning".Length) / 2, "context budget counts reasoning");
        ConversationStore.Save(conv);
        Check(ConversationStore.Load(conv.Id).Messages[1].ReasoningContent == message.ReasoningContent, "disk save/load preserves reasoning");
        conv.Title = "second version";
        ConversationStore.Save(conv);
        var path = Path.Combine(ConversationStore.FolderPathPublic(), conv.Id + ".json");
        Check(File.Exists(path + ".bak"), "atomic replace keeps previous archive");
        Check(ConversationStore.List().Exists(c => c.Id == conv.Id && c.Title == "second version"), "metadata listing reads latest title");
        File.WriteAllText(path, "{invalid");
        Check(ConversationStore.Load(conv.Id).Title == "test", "damaged archive falls back to previous version");
        // Restore the test fixture to valid JSON; never touch deployment conversations.
        ConversationStore.Save(conv);
        CheckLoopPersistence();
        Console.WriteLine("TOTAL PASS: " + checks);
    }

    sealed class Host : TxAgent.Core.IAgentHost
    {
        public TxAgent.Core.HostMode Mode { get { return TxAgent.Core.HostMode.Standalone; } }
        public void Invoke(Action action) { action(); }
        public T Invoke<T>(Func<T> func) { return func(); }
        public bool Confirm(string title, string detail, bool destructive) { return false; }
        public TxAgent.Core.RestorePoint CreateRestorePoint(string reason) { return TxAgent.Core.RestorePoint.None("test"); }
        public void Log(string level, string text) { }
    }
    sealed class MockClient : TxAgent.Core.IStreamingLlmClient
    {
        public Func<TxAgent.Core.LlmStreamHandlers, TxAgent.Core.LlmResponse> Run;
        public bool SupportsStreaming { get { return true; } }
        public string ModelId { get { return "test"; } }
        public Task<TxAgent.Core.LlmResponse> CompleteAsync(TxAgent.Core.LlmRequest r, CancellationToken ct) { return Task.FromResult(Run(null)); }
        public Task<TxAgent.Core.LlmResponse> CompleteStreamAsync(TxAgent.Core.LlmRequest r, TxAgent.Core.LlmStreamHandlers h, CancellationToken ct) { return Task.FromResult(Run(h)); }
    }
    sealed class Tool : TxAgent.Core.ITool
    {
        public string Name { get { return "test"; } }
        public string Description { get { return "test"; } }
        public string ParametersJsonSchema { get { return "{}"; } }
        public bool IsWrite { get { return false; } }
        public bool IsDestructive { get { return false; } }
        public TxAgent.Core.ToolResult Execute(string args, TxAgent.Core.IAgentHost host) { return TxAgent.Core.ToolResult.Ok("verified result"); }
    }
    static void CheckLoopPersistence()
    {
        foreach (var mode in new[] { "complete", "cancel", "error", "reasoning-only" })
        {
            var session = new TxAgent.Core.AgentSession(null);
            var mock = new MockClient { Run = h => {
                h.Reasoning("partial reasoning");
                if (mode == "cancel") throw new OperationCanceledException();
                if (mode == "error") return TxAgent.Core.LlmResponse.Error("test failure");
                return new TxAgent.Core.LlmResponse { ReasoningContent = "partial reasoning", Content = mode == "complete" ? "answer" : null, AlreadyStreamed = true };
            }};
            var loop = new TxAgent.Core.AgentLoop(mock, new TxAgent.Core.ToolRegistry(), new Host(), new TxAgent.Core.AgentLoopOptions { EnableStreaming = true, MaxLlmRetries = 0 });
            try { loop.RunAsync(session, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { if (mode != "cancel") throw; }
            Check(session.Messages.Count == 1 && session.Messages[0].ReasoningContent == "partial reasoning", mode + " reasoning persisted");
            Check(!string.IsNullOrWhiteSpace(session.Messages[0].Content), mode + " archive has valid assistant payload");
        }
        var toolSession = new TxAgent.Core.AgentSession(null);
        var registry = new TxAgent.Core.ToolRegistry();
        registry.Register(new Tool());
        int call = 0;
        var client = new MockClient { Run = h => ++call == 1
            ? new TxAgent.Core.LlmResponse { ToolCalls = new List<TxAgent.Core.ToolCall> { new TxAgent.Core.ToolCall { Id = "t1", Name = "test", ArgumentsJson = "{}" } }, ReasoningContent = "plan", AlreadyStreamed = true }
            : new TxAgent.Core.LlmResponse { Content = "done", AlreadyStreamed = true } };
        var toolLoop = new TxAgent.Core.AgentLoop(client, registry, new Host(), new TxAgent.Core.AgentLoopOptions { EnableStreaming = true });
        bool paired = false;
        toolLoop.ToolFinished += (tc, result) => paired = toolSession.Messages[toolSession.Messages.Count - 1].ToolCallId == tc.Id;
        toolLoop.RunAsync(toolSession, CancellationToken.None).GetAwaiter().GetResult();
        Check(paired, "tool completion persistence observes matching result");
    }
}
