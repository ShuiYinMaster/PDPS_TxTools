// TxTools.Agent / Core / SystemPromptBuilder.cs
// 系统提示词(含记忆注入)的静态构建工具。
//
// 从旧引擎 AgentLoop 中提取:原 AgentLoop 既是旧引擎又是一个静态工具类,
// harness 桥(PsAgentHost / HarnessAgentLoop)只依赖这里的静态构建逻辑,
// 并不需要旧引擎的实例循环。旧引擎已删除,仅保留此处共享的系统提示词构建。

using System;
using System.Linq;
using System.Text;

namespace TxTools.Agent.Core
{
    /// <summary>
    /// 系统提示词构建:默认提示 + FactsStore.TopN + GotchasStore.TopN。
    /// 带会话内缓存 —— 系统提示词是 prompt 前缀第一段,每轮重建会让前缀击穿缓存,
    /// 命中价从 0.02 涨到 1 元/M,差 50 倍。所以会话内固定一份:
    /// 新记的 fact/gotcha 下次对话才生效,这点延迟完全可以接受。
    /// </summary>
    public static class SystemPromptBuilder
    {
        private static string _promptCache;
        private static readonly object _promptSync = new object();

        /// <summary>开新对话 / 切换对话时调用,让下一次构建重新拉取记忆。</summary>
        public static void InvalidateCache()
        {
            lock (_promptSync) { _promptCache = null; }
        }

        public static string BuildWithMemory()
        {
            lock (_promptSync)
            {
                if (_promptCache != null) return _promptCache;
            }

            var built = BuildCore();
            lock (_promptSync) { _promptCache = built; }
            return built;
        }

        private static string BuildCore()
        {
            var prompt = AgentOptions.DefaultSystemPrompt;
            var sb = new StringBuilder();

            // 事实记忆 (Facts) —— 用户偏好/场景常量/API事实/流程
            var facts = FactsStore.TopN(6);
            if (facts.Count > 0)
            {
                // 【接通 UsedCount】注入即引用:异步回写被选中事实的引用计数,
                // 驱动 TopN 打分里的"被引用次数"权重(此前该方法从未被调用,恒为 0)。
                // 只统计 prompt 注入这条消费路径;会话级缓存保证每轮对话最多记一次。
                var usedIds = facts.Select(f => f.Id).ToList();
                System.Threading.Tasks.Task.Run(delegate
                {
                    foreach (var id in usedIds)
                    {
                        try { FactsStore.IncrementUsed(id); } catch { }
                    }
                });

                sb.AppendLine();
                sb.AppendLine("【历史经验】(仅供参考，不是当前场景事实；冲突时以本轮工具实测为准):");
                foreach (var f in facts)
                    sb.AppendLine("  • [" + f.Category + "] " + f.Content);
            }

            // 踩坑清单 (Gotchas) —— 已知报错的签名与正解
            var gotchas = GotchasStore.TopN(5);
            if (gotchas.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【避坑清单】(写 run_csharp 前核对,遇到相同签名直接用正解写法):");
                foreach (var g in gotchas)
                {
                    var fix = string.IsNullOrEmpty(g.Correction) ? "(暂无正解)" : g.Correction;
                    sb.AppendLine("  • [" + g.Signature + "] " + fix);
                }
            }

            return prompt + sb.ToString();
        }
    }
}
