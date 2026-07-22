// TxTools.Agent / Core / LlmModels.cs
// DeepSeek (OpenAI 兼容) Chat Completions 的数据契约。
// 纯 .NET，不依赖 Process Simulate。C# 7.3 / .NET Framework 4.8。
// 依赖: Newtonsoft.Json (放 libs\ 用 HintPath 引用)。

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    /// <summary>POST /chat/completions 的请求体。</summary>
    public sealed class ChatRequest
    {
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("messages")] public List<ChatMessage> Messages { get; set; }

        [JsonProperty("tools", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolDef> Tools { get; set; }

        [JsonProperty("max_tokens")] public int MaxTokens { get; set; }
        [JsonProperty("temperature")] public double? Temperature { get; set; }
        [JsonProperty("stream")] public bool Stream { get; set; }
    }

    /// <summary>
    /// 一条消息。role = system / user / assistant / tool。
    /// - assistant 回合可能携带 ToolCalls (此时 Content 可为 null)。
    /// - tool 回合用 ToolCallId 关联其响应的那次调用。
    /// </summary>
    public sealed class ChatMessage
    {
        [JsonProperty("role")] public string Role { get; set; }

        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public string Content { get; set; }

        [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolCall> ToolCalls { get; set; }

        [JsonProperty("tool_call_id", NullValueHandling = NullValueHandling.Ignore)]
        public string ToolCallId { get; set; }

        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    public sealed class ToolCall
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("function")] public FunctionCall Function { get; set; }
    }

    public sealed class FunctionCall
    {
        [JsonProperty("name")] public string Name { get; set; }
        // OpenAI 规范：arguments 是 JSON 字符串，需自行 Parse。
        [JsonProperty("arguments")] public string Arguments { get; set; }
    }

    /// <summary>工具声明 (发给模型)。</summary>
    public sealed class ToolDef
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("function")] public FunctionDef Function { get; set; }
    }

    public sealed class FunctionDef
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("parameters")] public JObject Parameters { get; set; }
    }

    /// <summary>API 返回的 token 用量（prompt_tokens / completion_tokens / total_tokens）。</summary>
    public sealed class TokenUsage
    {
        [JsonProperty("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonProperty("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonProperty("total_tokens")] public int TotalTokens { get; set; }
    }

    public sealed class ChatResponse
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("choices")] public List<Choice> Choices { get; set; }
        [JsonProperty("usage")] public TokenUsage Usage { get; set; }
    }

    public sealed class Choice
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("message")] public ChatMessage Message { get; set; }
        [JsonProperty("finish_reason")] public string FinishReason { get; set; }
    }
}