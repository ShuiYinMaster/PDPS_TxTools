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

        /// <summary>
        /// 流式时要求 API 在末尾 chunk 带回 usage。不设置的话 DeepSeek 流式不返回 token 用量。
        /// 部分 provider 不认这个字段,DeepSeekClient 会在 400 时自动去掉重试。
        /// </summary>
        [JsonProperty("stream_options", NullValueHandling = NullValueHandling.Ignore)]
        public StreamOptions StreamOptions { get; set; }

        /// <summary>
        /// 百炼特有:是否启用思考模式。null = 不发送该字段(用服务端默认)。
        ///
        /// 【工具调用场景务必置 false】百炼官方示例在 function calling 里显式传
        /// extra_body={"enable_thinking": False},不是随手写的 ——
        /// 思考模式下代理层对 DeepSeek 系模型的输出解析会出问题,表现为两个症状:
        ///   · &lt;/think&gt; 标签原样漏进 content
        ///   · tool_calls 解析不出来,模型只能在正文里"口述"要调什么工具
        /// 两者是同一个解析器的两个失败面,关掉思考通常一起消失。
        /// </summary>
        [JsonProperty("enable_thinking", NullValueHandling = NullValueHandling.Ignore)]
        public bool? EnableThinking { get; set; }

        /// <summary>
        /// 是否允许一轮里并行发多个工具调用。null = 用服务端默认(通常是允许)。
        ///
        /// 允许并行能省轮次(实测模型会一次发两个 query_scene 查不同 scope),
        /// 但每个写操作都要弹一次审批 —— 连弹几次体验很差。
        /// 觉得烦就置 false 强制串行。
        /// </summary>
        [JsonProperty("parallel_tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public bool? ParallelToolCalls { get; set; }

        /// <summary>
        /// 重复惩罚。对已出现过的 token 降权，抑制退化循环。
        ///
        /// 【为什么需要】长上下文 + 宽松输出预算下，模型会陷入自我强化的重复:
        /// 同一句"让我发送脚本"连写七八十遍，一口气烧掉一万多 token。
        /// 这是通用失败模式，官方端点同样会出现，不是某家代理的问题。
        /// 0.3 左右足够抑制，再大会影响正常的术语复用(代码里同一个变量名要反复出现)。
        /// </summary>
        [JsonProperty("frequency_penalty", NullValueHandling = NullValueHandling.Ignore)]
        public double? FrequencyPenalty { get; set; }

        /// <summary>话题重复惩罚。与 FrequencyPenalty 配合，鼓励换个说法而不是原地打转。</summary>
        [JsonProperty("presence_penalty", NullValueHandling = NullValueHandling.Ignore)]
        public double? PresencePenalty { get; set; }
    }

    public sealed class StreamOptions
    {
        [JsonProperty("include_usage")] public bool IncludeUsage { get; set; }
    }

    /// <summary>
    /// 一条消息。role = system / user / assistant / tool。
    /// - assistant 回合可能携带 ToolCalls (此时 Content 可为 null)。
    /// - tool 回合用 ToolCallId 关联其响应的那次调用。
    /// </summary>
    public sealed class ChatMessage
    {
        [JsonProperty("role")] public string Role { get; set; }

        /// <summary>
        /// 纯文本内容。多模态消息(ContentParts 非空)时这里存文本部分的合并结果,
        /// 供日志/摘要/历史压缩等只关心文字的地方使用。
        /// </summary>
        [JsonIgnore]
        public string Content { get; set; }

        /// <summary>
        /// 多模态内容块(文本 + 图片)。为空时序列化成普通字符串 content,
        /// 非空时序列化成 OpenAI 视觉格式的数组。
        /// 这样纯文本路径逐字节不变 —— 不影响任何现有 provider 的兼容性,也不动 prompt 前缀缓存。
        /// </summary>
        [JsonIgnore]
        public List<ContentPart> ContentParts { get; set; }

        /// <summary>实际参与序列化的 content 字段:string 或 array,二选一。</summary>
        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public object ContentPayload
        {
            get
            {
                if (ContentParts != null && ContentParts.Count > 0) return ContentParts;
                return Content;
            }
            set
            {
                // 反序列化:API 返回的 content 一律是字符串;若是数组则把文本块拼起来
                if (value == null) { Content = null; return; }

                var jarr = value as Newtonsoft.Json.Linq.JArray;
                if (jarr == null) { Content = value.ToString(); return; }

                var sb = new System.Text.StringBuilder();
                foreach (var it in jarr)
                {
                    var t = it["text"];
                    if (t != null) sb.Append((string)t);
                }
                Content = sb.ToString();
            }
        }

        public bool ShouldSerializeContentPayload()
        {
            // assistant 带 tool_calls 时 content 可为 null,交给 NullValueHandling 处理
            return ContentPayload != null;
        }

        /// <summary>是否是多模态消息(含图片)。</summary>
        [JsonIgnore]
        public bool HasImages
        {
            get
            {
                if (ContentParts == null) return false;
                foreach (var p in ContentParts)
                    if (p != null && p.IsImage) return true;
                return false;
            }
        }

        /// <summary>
        /// 推理模型返回的思考内容(DeepSeek reasoner 系列的 reasoning_content)。普通模型为 null。
        /// 只读不写:API 明确要求不能把 reasoning_content 回传到下一轮请求里,
        /// 故用 ShouldSerialize 恒 false 屏蔽序列化 —— 反序列化仍正常填充。
        /// </summary>
        [JsonProperty("reasoning_content", NullValueHandling = NullValueHandling.Ignore)]
        public string ReasoningContent { get; set; }

        public bool ShouldSerializeReasoningContent() { return false; }

        /// <summary>
        /// 对话前缀续写(Beta):置于 messages 末尾的 assistant 消息标记 prefix=true,
        /// 模型会【接着这段内容往下写】,而不是另起一段。
        /// 用途:强制输出格式(预填 "{" 让模型只能补 JSON,不会再包 ```json 围栏)。
        /// 注意该特性需要 beta 端点 —— base_url 要用 https://api.deepseek.com/beta。
        /// </summary>
        [JsonProperty("prefix", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Prefix { get; set; }

        [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolCall> ToolCalls { get; set; }

        [JsonProperty("tool_call_id", NullValueHandling = NullValueHandling.Ignore)]
        public string ToolCallId { get; set; }

        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }

        /// <summary>构造一条带图片的用户消息。images 为 (base64 原文, mime) 列表。</summary>
        public static ChatMessage CreateWithImages(string role, string text,
            IEnumerable<KeyValuePair<string, string>> images)
        {
            var m = new ChatMessage { Role = role, Content = text ?? "" };
            m.ContentParts = new List<ContentPart>();

            if (!string.IsNullOrEmpty(text))
                m.ContentParts.Add(ContentPart.FromText(text));

            if (images != null)
                foreach (var kv in images)
                    m.ContentParts.Add(ContentPart.FromImageBase64(kv.Key, kv.Value));

            return m;
        }
    }

    /// <summary>
    /// 多模态内容块。OpenAI 兼容格式,Kimi(Moonshot) / 千问(DashScope OpenAI 模式) / GPT 都认。
    /// </summary>
    public sealed class ContentPart
    {
        [JsonProperty("type")] public string Type { get; set; }

        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
        public string Text { get; set; }

        [JsonProperty("image_url", NullValueHandling = NullValueHandling.Ignore)]
        public ImageUrl ImageUrl { get; set; }

        [JsonIgnore]
        public bool IsImage { get { return Type == "image_url"; } }

        public static ContentPart FromText(string text)
        {
            return new ContentPart { Type = "text", Text = text };
        }

        /// <summary>base64 内联。mime 形如 image/png、image/jpeg。</summary>
        public static ContentPart FromImageBase64(string base64, string mime)
        {
            if (string.IsNullOrEmpty(mime)) mime = "image/png";
            return new ContentPart
            {
                Type = "image_url",
                ImageUrl = new ImageUrl { Url = "data:" + mime + ";base64," + base64 }
            };
        }

        public static ContentPart FromImageUrl(string url)
        {
            return new ContentPart { Type = "image_url", ImageUrl = new ImageUrl { Url = url } };
        }
    }

    public sealed class ImageUrl
    {
        [JsonProperty("url")] public string Url { get; set; }

        /// <summary>可选精度:low / high / auto。低精度省 token,判断"有没有""是什么"够用。</summary>
        [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
        public string Detail { get; set; }
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
