// TxTools.Agent / Core / TaskPlan.cs
// 轻量任务计划：让 agent 把复杂多步任务拆成带状态的清单并随进度更新。
//
// v2 (P0-1 修复)：原全局静态 _items 会跨对话污染 —— 切换对话后清单不切换。
// 现在按 convId 分桶存储，AgentLoop 在 SetConvId / LoadHistory 时调用
// SetActiveConversation(convId) 切换。旧的静态 Update/Render 入口保持兼容
// (update_plan 工具无需改动)，内部路由到当前活动对话。

using System;
using System.Collections.Generic;
using System.Text;

namespace TxTools.Agent.Core
{
    public sealed class TaskItem
    {
        public string Text { get; set; }
        public bool Done { get; set; }
    }

    public static class TaskPlan
    {
        private static readonly Dictionary<string, List<TaskItem>> _byConv =
            new Dictionary<string, List<TaskItem>>(StringComparer.Ordinal);

        // UI 线程与工具线程(harness 后台线程)都可能访问 _byConv / _activeConvId,
        // 不加锁的 Dictionary 并发读写可能抛 InvalidOperationException 或损坏内部状态。
        private static readonly object _sync = new object();

        private const string DefaultKey = "_default";
        private static string _activeConvId = DefaultKey;

        /// <summary>切换当前活动对话。AgentLoop 在切换对话或加载历史时调用。</summary>
        public static void SetActiveConversation(string convId)
        {
            lock (_sync)
                _activeConvId = string.IsNullOrWhiteSpace(convId) ? DefaultKey : convId;
        }

        /// <summary>当前活动对话 id (用于 UI 展示或调试)。</summary>
        public static string ActiveConversationId { get { lock (_sync) return _activeConvId; } }

        /// <summary>清空指定对话的清单。传 null 清全部对话的清单。</summary>
        public static void Clear(string convId = null)
        {
            lock (_sync)
            {
                if (convId == null) { _byConv.Clear(); return; }
                _byConv.Remove(convId);
            }
        }

        /// <summary>update_plan 工具入口：覆盖当前活动对话的清单并返回渲染文本。</summary>
        public static string Update(IEnumerable<TaskItem> items)
        {
            lock (_sync)
            {
                var list = GetOrCreate(_activeConvId);
                list.Clear();
                if (items != null)
                {
                    int n = 0;
                    foreach (var it in items)
                    {
                        if (it == null || string.IsNullOrWhiteSpace(it.Text)) continue;
                        // 计划条数设上限,避免模型一次塞几百条把上下文撑爆
                        if (n >= MaxPlanItems) break;
                        list.Add(new TaskItem { Text = it.Text.Trim(), Done = it.Done });
                        n++;
                    }
                }
                return Render();
            }
        }

        /// <summary>渲染当前活动对话的清单。</summary>
        public static string Render()
        {
            lock (_sync)
            {
                var list = GetOrCreate(_activeConvId);
                if (list.Count == 0) return "（当前无计划）";
                var sb = new StringBuilder();
                sb.AppendLine("当前计划：");
                int done = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    if (it.Done) done++;
                    sb.AppendLine((i + 1) + ". [" + (it.Done ? "x" : " ") + "] " + it.Text);
                }
                sb.Append("进度: " + done + "/" + list.Count);
                return sb.ToString();
            }
        }

        /// <summary>导出当前对话的清单副本（供持久化到 Conversation 元数据）。</summary>
        public static List<TaskItem> Export()
        {
            lock (_sync)
            {
                var list = GetOrCreate(_activeConvId);
                var copy = new List<TaskItem>(list.Count);
                foreach (var it in list) copy.Add(new TaskItem { Text = it.Text, Done = it.Done });
                return copy;
            }
        }

        /// <summary>从持久化数据恢复当前对话的清单。</summary>
        public static void Import(IEnumerable<TaskItem> items)
        {
            lock (_sync)
            {
                var list = GetOrCreate(_activeConvId);
                list.Clear();
                if (items != null)
                {
                    int n = 0;
                    foreach (var it in items)
                    {
                        if (it == null || string.IsNullOrWhiteSpace(it.Text)) continue;
                        if (n >= MaxPlanItems) break;
                        list.Add(new TaskItem { Text = it.Text.Trim(), Done = it.Done });
                        n++;
                    }
                }
            }
        }

        private static List<TaskItem> GetOrCreate(string convId)
        {
            List<TaskItem> list;
            if (!_byConv.TryGetValue(convId, out list))
            {
                list = new List<TaskItem>();
                _byConv[convId] = list;
            }
            return list;
        }

        /// <summary>单对话计划条数上限，防止无界增长把上下文撑爆。</summary>
        public const int MaxPlanItems = 30;
    }
}
