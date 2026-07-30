using System;
using System.Collections.Generic;
using System.Linq;

namespace TxAgent.Core
{
    /// <summary>
    /// 一次会话的上下文。负责消息累积与超预算裁剪。
    /// 裁剪策略：Pinned 永不动；从最老的非 Pinned 消息开始丢；
    /// 丢弃时保证 Assistant(带 ToolCalls) 与其对应的 Tool 结果成对丢弃，否则会破坏配对。
    /// </summary>
    public sealed class AgentSession
    {
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();

        /// <summary>上下文 token 预算。超过后触发裁剪。</summary>
        public int TokenBudget { get; set; }

        /// <summary>裁剪后保留的最近消息条数下限，避免裁得太狠丢失当前任务。</summary>
        public int MinKeepRecent { get; set; }

        public AgentSession(string systemPrompt)
        {
            TokenBudget = 48000;
            MinKeepRecent = 8;

            if (!string.IsNullOrEmpty(systemPrompt))
                Add(ChatMessage.CreateSystem(systemPrompt));
        }

        public IReadOnlyList<ChatMessage> Messages
        {
            get { return _messages; }
        }

        public void Add(ChatMessage message)
        {
            if (message == null) return;
            if (message.ApproxTokens <= 0)
                message.ApproxTokens = EstimateTokens(message);
            _messages.Add(message);
        }

        public void AddUser(string text)
        {
            Add(ChatMessage.CreateUser(text));
        }

        /// <summary>
        /// 把任务目标固定住，不参与裁剪。长任务跑偏多半是因为目标被裁掉了。
        /// </summary>
        public void PinTaskGoal(string goal)
        {
            var msg = ChatMessage.CreateUser("【当前任务目标，始终有效】" + goal);
            msg.Pinned = true;
            Add(msg);
        }

        public int TotalTokens
        {
            get { return _messages.Sum(m => m.ApproxTokens); }
        }

        /// <summary>按预算裁剪。返回被丢弃的消息条数。</summary>
        public int TrimToBudget()
        {
            if (TotalTokens <= TokenBudget) return 0;

            int dropped = 0;
            int guard = 0;

            while (TotalTokens > TokenBudget && guard++ < 1000)
            {
                int idx = FindFirstDroppableIndex();
                if (idx < 0) break;

                // 若该条是带工具调用的 Assistant，连同其后紧跟的 Tool 结果一并丢弃
                int removeCount = 1;
                if (_messages[idx].HasToolCalls)
                {
                    int j = idx + 1;
                    while (j < _messages.Count && _messages[j].Role == MessageRole.Tool)
                    {
                        removeCount++;
                        j++;
                    }
                }

                // 保证尾部至少留够 MinKeepRecent 条
                if (_messages.Count - removeCount < MinKeepRecent) break;

                _messages.RemoveRange(idx, removeCount);
                dropped += removeCount;
            }

            return dropped;
        }

        private int FindFirstDroppableIndex()
        {
            int tailStart = Math.Max(0, _messages.Count - MinKeepRecent);
            for (int i = 0; i < tailStart; i++)
            {
                var m = _messages[i];
                if (m.Pinned) continue;
                // 孤立的 Tool 消息不单独丢，等它的 Assistant 一起走
                if (m.Role == MessageRole.Tool) continue;
                return i;
            }
            return -1;
        }

        /// <summary>粗估：中文按 1.5 字/token，英文按 4 字符/token，取保守值。</summary>
        private static int EstimateTokens(ChatMessage m)
        {
            int len = 0;
            if (!string.IsNullOrEmpty(m.Content)) len += m.Content.Length;
            if (m.ToolCalls != null)
            {
                foreach (var tc in m.ToolCalls)
                {
                    len += (tc.Name ?? "").Length;
                    len += (tc.ArgumentsJson ?? "").Length;
                }
            }
            return Math.Max(1, len / 2);
        }
    }
}
