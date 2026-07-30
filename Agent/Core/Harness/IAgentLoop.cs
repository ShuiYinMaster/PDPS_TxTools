// TxTools.Agent / Core / Harness / IAgentLoop.cs
// AgentLoop(旧) 与 HarnessAgentLoop(新 harness 桥) 的公共表面。
// UI(TxAgentForm)只依赖这个接口,因此切换引擎只需在 BuildLoop 里换实现,
// 其余事件订阅/持久化/审批逻辑一行不用改。
//
// 思考内容 / 流式重置属于新 harness 独有能力,单独放在可选接口 IStreamingAgentLoop 里,
// 旧 AgentLoop 不实现也不受影响 —— UI 用 as 探测即可,零改动兼容。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public interface IAgentLoop
    {
        event Action<string> AssistantText;
        event Action<string> AssistantDelta;
        event Action<string, JObject> ToolCalled;
        event Action<string, string, bool> ToolCompleted;
        event Action<string> Info;
        event Action HistoryChanged;
        event Action<int, int, int> TokenUsed;

        /// <summary>变更类工具审批回调(同步 bool)。</summary>
        Func<ITxAgentTool, JObject, bool> ApprovalRequest { get; set; }

        /// <summary>主动提问(ask_user)回调。</summary>
        Func<string, string, string[], string> AskUserRequest { get; set; }

        IReadOnlyList<ChatMessage> FullHistory { get; }
        IReadOnlyList<ChatMessage> WorkingMemory { get; }

        string CurrentConvId { get; }
        int TotalPromptTokens { get; }
        int TotalCompletionTokens { get; }
        int TotalTokens { get; }

        void SetConvId(string convId);
        void Reset();
        void LoadHistory(IEnumerable<ChatMessage> msgs);
        Task SendAsync(string userText, CancellationToken ct);
        Task<LessonExtractor.ExtractResult> ExtractLessonsAsync(CancellationToken ct);
    }

    /// <summary>
    /// 可选能力接口:思考内容流 + 流式重置。仅 HarnessAgentLoop 实现。
    /// UI 侧这样接:
    ///     var s = _loop as IStreamingAgentLoop;
    ///     if (s != null) { s.ReasoningDelta += OnReasoningDelta; s.ContentReset += OnContentReset; }
    /// </summary>
    public interface IStreamingAgentLoop
    {
        /// <summary>思考内容增量(推理模型的 reasoning_content)。普通模型不会触发。</summary>
        event Action<string> ReasoningDelta;

        /// <summary>一轮开始输出思考内容,UI 可据此展开"思考中"折叠块。</summary>
        event Action ReasoningStarted;

        /// <summary>思考结束、正文开始,UI 可据此收起折叠块。</summary>
        event Action ReasoningEnded;

        /// <summary>
        /// LLM 重试导致已发出的增量作废。UI 收到后应清空当前这一轮的文本气泡,
        /// 否则重试内容会和失败的半截内容拼接在一起。
        /// </summary>
        event Action ContentReset;

        /// <summary>当前是否真的在走 token 级流式(false 表示按轮次整段发出)。</summary>
        bool StreamingActive { get; }
    }
}
