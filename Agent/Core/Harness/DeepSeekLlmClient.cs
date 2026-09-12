// TxTools.Agent / Core / Harness / DeepSeekLlmClient.cs
// ILlmClient / IStreamingLlmClient 的 DeepSeek 实现:把 TxAgent.Core 的 LlmRequest/LlmResponse
// 与现有 TxTools.Agent.Core 的 ChatRequest/ChatMessage/ToolDef 互相翻译,
// 复用既有的 DeepSeekClient(直连 OpenAI 兼容 /v1/chat/completions)。
//
// 流式:直接走 DeepSeekClient.SendStreamAsync —— 正文分片、思考内容分片、token 用量
// 三路回调都接上,tool_calls 的跨分片拼接由 DeepSeekClient 内部完成。
// 思考内容仅推理模型(DeepSeek reasoner 系列)返回,普通模型不会触发对应回调。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TxAgent.Core;        // ILlmClient / IStreamingLlmClient / LlmRequest / LlmResponse / LlmStreamHandlers / ToolCall / ToolSchema / MessageRole
using TxTools.Agent.Core; // DeepSeekClient / ChatRequest / ChatMessage / ToolDef / FunctionDef / TokenUsage / LlmApiException

// 两个命名空间都定义了 ChatMessage / ToolCall,用别名消歧义:
//   本文件里裸名 ChatMessage 指旧格式(TxTools.Agent.Core)侧;ToolCall 指 harness(TxAgent.Core)侧。
using ChatMessage = TxTools.Agent.Core.ChatMessage;
using ToolCall = TxAgent.Core.ToolCall;

namespace TxTools.Agent.Harness
{
    public sealed class DeepSeekLlmClient : IStreamingLlmClient
    {
        private readonly DeepSeekClient _client;

        public string ModelId { get; private set; }
        public string ReasoningEffort { get; set; } = "low";

        /// <summary>
        /// 是否启用流式。默认 true;若某个 provider 的流式有问题,把它置 false 即可
        /// 让 AgentLoop 自动退回非流式路径(行为正确,只是没有逐字效果)。
        /// </summary>
        public bool SupportsStreaming { get; set; }

        public DeepSeekLlmClient(DeepSeekClient client, string modelId)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ModelId = string.IsNullOrEmpty(modelId) ? "deepseek-chat" : modelId;
            SupportsStreaming = true;
        }

        // ── 非流式 ──

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            try
            {
                var req = BuildRequest(request, false);
                ChatResponse resp = await _client.SendAsync(req, ct);

                if (resp == null || resp.Choices == null || resp.Choices.Count == 0)
                    return LlmResponse.Error("API 返回空响应(无 choices)。");

                var msg = resp.Choices[0].Message;
                var outResp = BuildResponse(msg, false);
                outResp.FinishReason = resp.Choices[0].FinishReason;
                if (resp.Usage != null)
                {
                    outResp.PromptTokens = resp.Usage.PromptTokens;
                    outResp.CompletionTokens = resp.Usage.CompletionTokens;
                }
                return outResp;
            }
            catch (LlmApiException ex)
            {
                LogException(ex);
                return LlmResponse.Error("API 错误: " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消信号必须透传,供 harness 中止
            }
            catch (Exception ex)
            {
                LogException(ex);
                return LlmResponse.Error("调用失败[" + ex.GetType().Name + "]: " + ex.Message);
            }
        }

        // ── 流式 ──

        public async Task<LlmResponse> CompleteStreamAsync(
            LlmRequest request, LlmStreamHandlers handlers, CancellationToken ct)
        {
            if (!SupportsStreaming)
                return await CompleteAsync(request, ct).ConfigureAwait(false);

            try
            {
                var req = BuildRequest(request, true);
                TokenUsage usage = null;
                string repetitionHint = null;

                ChatMessage msg = await _client.SendStreamAsync(
                    req,
                    text => { if (handlers != null) handlers.Content(text); },
                    ct,
                    u => { usage = u; },
                    text => { if (handlers != null) handlers.Reasoning(text); },
                    // 检测到退化循环时客户端已主动截断，这里把纠正提示带回给 AgentLoop。
                    // 走调用参数而非共享字段 —— 共享 client 跨对话并发时字段会互相覆盖。
                    h => { repetitionHint = h; }
                ).ConfigureAwait(false);

                if (msg == null)
                    return LlmResponse.Error("流式返回空消息。");

                var outResp = BuildResponse(msg, true);
                outResp.RepetitionHint = repetitionHint;
                if (usage != null)
                {
                    outResp.PromptTokens = usage.PromptTokens;
                    outResp.CompletionTokens = usage.CompletionTokens;

                    // SendStreamAsync 只回聚合后的 ChatMessage,拿不到 finish_reason。
                    // 用量贴着上限 + 没有任何产出 ⇒ 基本可判定是被 max_tokens 截断。
                    bool empty = string.IsNullOrWhiteSpace(outResp.Content) && !outResp.HasToolCalls;
                    if (empty && req.MaxTokens > 0
                        && usage.CompletionTokens >= req.MaxTokens - 32)
                        outResp.FinishReason = "length";
                }
                return outResp;
            }
            catch (LlmApiException ex)
            {
                LogException(ex);
                return LlmResponse.Error("API 错误: " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogException(ex);
                return LlmResponse.Error("流式调用失败[" + ex.GetType().Name + "]: " + ex.Message);
            }
        }

        // ── 请求组装 ──

        /// <summary>
        /// 工具调用时是否关闭思考模式(百炼的 enable_thinking 参数)。
        ///
        /// 【默认 false】曾经怀疑思考模式导致 tool_calls 解析失败,后来抓原始 SSE 报文
        /// 证明与它无关 —— 真正的原因是续片里的空串把工具名覆盖了(已修)。
        /// 思考模式对复杂任务的规划质量有帮助,没有证据就不该关掉它。
        /// 若将来确实遇到某个端点必须关思考才能调工具,把它置 true。
        /// </summary>
        public bool DisableThinkingWithTools { get; set; } = false;

        /// <summary>
        /// 是否允许并行工具调用。null = 不发送该字段。
        /// 置 false 可避免"一轮弹好几次审批"，代价是多花几轮。
        /// </summary>
        public bool? ParallelToolCalls { get; set; }

        /// <summary>重复惩罚。null 则不发送。默认 0.3 —— 见 ChatRequest.FrequencyPenalty 的说明。</summary>
        public double? FrequencyPenalty { get; set; } = 0.3;

        public double? PresencePenalty { get; set; } = 0.3;

        private ChatRequest BuildRequest(LlmRequest request, bool stream)
        {
            var tools = TranslateTools(request.Tools);

            var req = new ChatRequest
            {
                Model = ModelId,
                MaxTokens = Math.Max(1, request.MaxTokens),
                Temperature = request.Temperature,
                Stream = stream,
                Messages = TranslateMessages(request.Messages),
                Tools = tools
            };

            // DeepSeek V4 documents low/high/max and requires reasoning replay with tools.
            // Do not send provider-specific fields to proxies or unrelated models.
            if (_client.IsOfficialDeepSeek && ModelId.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase))
            {
                req.ReasoningEffort = ReasoningEffort == "high" || ReasoningEffort == "max" ? ReasoningEffort : "low";
                if (tools != null && tools.Count > 0)
                    for (int i = 0; i < req.Messages.Count; i++)
                    {
                        var message = req.Messages[i];
                        if (message.Role != "assistant") continue;
                        message.SendReasoningContent = true;
                        // Legacy archives contain no reasoning; never fabricate it.
                        if (message.ReasoningContent == null) message.ReasoningContent = "";
                    }
            }

            // 只在真的带工具时关思考:纯对话轮次让模型正常思考,质量更好
            if (DisableThinkingWithTools && tools != null && tools.Count > 0)
                req.EnableThinking = false;

            if (ParallelToolCalls.HasValue && tools != null && tools.Count > 0)
                req.ParallelToolCalls = ParallelToolCalls;

            req.FrequencyPenalty = FrequencyPenalty;
            req.PresencePenalty = PresencePenalty;

            return req;
        }

        // ── 响应翻译 ──

        private static LlmResponse BuildResponse(ChatMessage msg, bool alreadyStreamed)
        {
            var outResp = new LlmResponse
            {
                Content = msg != null ? msg.Content : null,
                ReasoningContent = msg != null ? msg.ReasoningContent : null,
                AlreadyStreamed = alreadyStreamed
            };

            // 诊断:模型在正文里"口述"要调用某工具,却没发出结构化 tool_calls。
            // 这是第三方代理端点(如百炼代理的 deepseek 系列)对 tools 支持不完整的典型症状 ——
            // 模型自己以为调了,实际请求里 tool_calls 是空的,于是它会反复道歉重试、空烧 token。
            // 这里只记日志不改行为,便于事后定位是模型侧问题而非本地代码问题。
            if ((msg == null || msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                && LooksLikeNarratedToolCall(msg != null ? msg.Content : null))
            {
                try
                {
                    TxTools.Agent.Core.AuditLog.Write(
                        "[warn] [LLM] 模型在正文里描述了工具调用但未返回 tool_calls —— "
                        + "该端点可能不支持 function calling，建议换用官方端点或其它模型。");
                }
                catch { }
            }

            if (msg != null && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                outResp.ToolCalls = new List<ToolCall>(msg.ToolCalls.Count);
                foreach (var tc in msg.ToolCalls)
                {
                    outResp.ToolCalls.Add(new ToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Function != null ? tc.Function.Name : null,
                        ArgumentsJson = tc.Function != null ? tc.Function.Arguments : null
                    });
                }
            }
            return outResp;
        }

        /// <summary>正文里出现"调用/工具没生效"这类自述,基本可判定 function calling 没生效。</summary>
        private static bool LooksLikeNarratedToolCall(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            if (content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (content.IndexOf("工具没被正确调用", StringComparison.Ordinal) >= 0) return true;
            if (content.IndexOf("工具调用没有正确发出", StringComparison.Ordinal) >= 0) return true;
            if (content.IndexOf("没有收到任何工具返回", StringComparison.Ordinal) >= 0) return true;
            if (content.IndexOf("无法正确调用工具", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static void LogException(Exception ex)
        {
            // 记完整堆栈,便于定位版本冲突等运行期问题;
            // 一律返回 Error 而非抛出 —— harness 会走重试/错误回灌路径,UI 不至于直接崩。
            try { TxTools.Agent.Core.AuditLog.Write("[error] DeepSeekLlmClient 异常:\n" + ex); }
            catch { }
        }

        // ── TxAgent.Core.ChatMessage  <->  TxTools.Agent.Core.ChatMessage ──

        private static List<ChatMessage> TranslateMessages(IList<TxAgent.Core.ChatMessage> src)
        {
            if (src == null) return new List<ChatMessage>();
            var dst = new List<ChatMessage>(src.Count);
            foreach (var m in src)
            {
                var cm = new ChatMessage();
                switch (m.Role)
                {
                    case MessageRole.System: cm.Role = "system"; break;
                    case MessageRole.User: cm.Role = "user"; break;
                    case MessageRole.Assistant: cm.Role = "assistant"; break;
                    case MessageRole.Tool: cm.Role = "tool"; break;
                    default: cm.Role = "user"; break;
                }
                cm.Content = m.Content;
                cm.ReasoningContent = m.ReasoningContent;
                cm.ToolCallId = m.ToolCallId;
                // The wire flag remains off here; BuildRequest enables it only for compatible tool requests.
                if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    cm.ToolCalls = new List<TxTools.Agent.Core.ToolCall>(m.ToolCalls.Count);
                    foreach (var tc in m.ToolCalls)
                    {
                        cm.ToolCalls.Add(new TxTools.Agent.Core.ToolCall
                        {
                            Id = tc.Id,
                            Type = "function",
                            Function = new FunctionCall { Name = tc.Name, Arguments = tc.ArgumentsJson }
                        });
                    }
                }
                dst.Add(cm);
            }
            return Sanitize(dst);
        }

        /// <summary>
        /// 发送前清洗。历史里可能残留 API 不接受的消息 —— 一条脏消息会让【之后每一轮】
        /// 都 400,而且报错信息不会告诉你是哪一条,极难定位。这里在出口统一挡掉:
        ///
        ///   • assistant 既无 content 又无 tool_calls
        ///       → 400 Invalid assistant message: content or tool_calls must be set
        ///       模型返回空响应时会产生,直接丢弃。
        ///   • tool 消息的 tool_call_id 在前面找不到对应的 tool_call
        ///       → 400 配对失败。上下文裁剪切断配对时会产生,直接丢弃。
        ///   • tool 消息 content 为 null → 补空串。
        ///   • tool_call 的 arguments 为 null → 补 "{}"。
        /// </summary>
        private static List<ChatMessage> Sanitize(List<ChatMessage> list)
        {
            if (list == null) return new List<ChatMessage>();

            var known = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<ChatMessage>(list.Count);

            foreach (var m in list)
            {
                if (m == null) continue;

                if (m.Role == "assistant")
                {
                    bool hasContent = !string.IsNullOrWhiteSpace(m.Content);
                    bool hasCalls = m.ToolCalls != null && m.ToolCalls.Count > 0;
                    if (!hasContent && !hasCalls) continue;   // 空 assistant,丢

                    if (hasCalls)
                    {
                        foreach (var tc in m.ToolCalls)
                        {
                            if (tc == null) continue;
                            if (tc.Function != null && tc.Function.Arguments == null)
                                tc.Function.Arguments = "{}";
                            if (!string.IsNullOrEmpty(tc.Id)) known.Add(tc.Id);
                        }
                    }
                    outList.Add(m);
                    continue;
                }

                if (m.Role == "tool")
                {
                    // 找不到配对的 tool_call 就丢 —— 留着必然 400
                    if (string.IsNullOrEmpty(m.ToolCallId) || !known.Contains(m.ToolCallId)) continue;
                    if (m.Content == null) m.Content = "";
                    outList.Add(m);
                    continue;
                }

                if (m.Content == null) m.Content = "";
                outList.Add(m);
            }

            return outList;
        }

        // ── TxAgent.Core.ToolSchema  <->  TxTools.Agent.Core.ToolDef ──

        private static List<ToolDef> TranslateTools(IList<ToolSchema> src)
        {
            if (src == null || src.Count == 0) return null;
            var dst = new List<ToolDef>(src.Count);
            foreach (var s in src)
            {
                JObject parameters;
                try
                {
                    parameters = JObject.Parse(s.ParametersJsonSchema ?? "{\"type\":\"object\",\"properties\":{}}");
                }
                catch
                {
                    parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{}}");
                }
                dst.Add(new ToolDef
                {
                    Type = "function",
                    Function = new FunctionDef
                    {
                        Name = s.Name,
                        Description = s.Description,
                        Parameters = parameters
                    }
                });
            }
            return dst;
        }
    }
}
