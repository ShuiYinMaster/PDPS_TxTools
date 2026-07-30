// TxTools.Agent / Tools / Memory / SnippetPatchTools.cs
//
// 片段的"改"与"体检"。
//
// 原来只有 save_snippet(整篇覆盖)。但实际场景里更常见的是
// "这段基本能用，只有一处要改" —— 覆盖会丢掉标签、统计和修订历史，
// 而且模型得把整段代码重写一遍，又贵又容易在无关处改动。

using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class PatchSnippetTool : TxAgentToolBase
    {
        public override string Name { get { return "patch_snippet"; } }

        public override string Description
        {
            get
            {
                return "修改已有代码片段的一小段，而不是整篇覆盖。"
                     + "【发现库里某个片段有问题或有更好写法时用它】——"
                     + "比如某个 API 已废弃、某处漏了空值判断、参数写法要改。"
                     + "old_text 必须在该片段里恰好出现一次，所以要带足够上下文让它唯一；"
                     + "先 get_snippet 读出原文照抄，不要凭记忆写。"
                     + "reason 写清为什么改，会记进修订历史，便于以后回溯。"
                     + "整篇重写请用 save_snippet。";
            }
        }

        /// <summary>只改本地片段库，不动场景。</summary>
        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"name\":     { \"type\":\"string\", \"description\":\"片段名\" }," +
                    " \"old_text\": { \"type\":\"string\", \"description\":\"要替换的原文，须在该片段里唯一\" }," +
                    " \"new_text\": { \"type\":\"string\", \"description\":\"替换成的新内容。传空串表示删除这段\" }," +
                    " \"reason\":   { \"type\":\"string\", \"description\":\"为什么改，会记进修订历史\" }" +
                    "}, \"required\":[\"name\",\"old_text\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var name = GetString(input, "name");
            var oldText = GetString(input, "old_text");
            var newText = GetString(input, "new_text", "") ?? "";
            var reason = GetString(input, "reason");

            var result = SnippetStore.Patch(name, oldText, newText, reason);
            return result.StartsWith("已更新", StringComparison.Ordinal) ? result : "Error: " + result;
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class SnippetHealthTool : TxAgentToolBase
    {
        public override string Name { get { return "snippet_health"; } }

        public override string Description
        {
            get
            {
                return "查看代码片段库的健康状况：总数、成功率分布、哪些片段不可靠、待定池状态。"
                     + "片段复用后失败、或怀疑库里有过时代码时看它。"
                     + "标为「不可靠」的片段说明多次复用都失败了 —— "
                     + "要么用 patch_snippet 修好，要么让用户确认后删掉。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get { return JObject.Parse("{ \"type\":\"object\", \"properties\":{} }"); }
        }

        public override string Execute(JObject input)
        {
            var all = SnippetStore.All();
            var sb = new StringBuilder();

            sb.AppendLine("片段库共 " + all.Count + " 条。");

            if (all.Count > 0)
            {
                var used = all.Where(s => s.SuccessCount + s.FailureCount > 0).ToList();
                var unreliable = all.Where(SnippetStore.IsUnreliable).ToList();
                var never = all.Count(s => s.SuccessCount + s.FailureCount == 0);

                sb.AppendLine("  已判定复用: " + used.Count + "，从未判定过: " + never);
                if (used.Count > 0)
                    sb.AppendLine("  平均成功率: "
                        + (used.Average(s => s.SuccessRate) * 100).ToString("0.0") + "%");

                // 【取出多但判不出来的】这类片段的问题多半不在代码本身，
                // 而在检索层老把它推到用不上的场合 —— 跟"复用失败"是两码事，分开看。
                var ghost = all.Where(s => s.UndecidedCount >= 3
                                        && s.UndecidedCount > (s.SuccessCount + s.FailureCount) * 2)
                               .OrderByDescending(s => s.UndecidedCount).ToList();
                if (ghost.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("常被取出却判不出用没用上（多半是检索推错了场合，不是代码有问题）:");
                    foreach (var s in ghost.Take(10))
                        sb.Append("  ").Append(s.Name)
                          .Append("  未判定 ").Append(s.UndecidedCount)
                          .Append(" 次 / 已判定 ").Append(s.SuccessCount + s.FailureCount)
                          .AppendLine(" 次");
                }

                if (unreliable.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("⚠ 不可靠片段(复用≥3次且成功率<40%)，建议修或删:");
                    foreach (var s in unreliable.OrderBy(x => x.SuccessRate))
                        sb.Append("  ").Append(s.Name)
                          .Append("  ").Append(s.SuccessCount).Append("成/")
                          .Append(s.FailureCount).Append("败  ")
                          .Append((s.SuccessRate * 100).ToString("0")).AppendLine("%");
                }

                var top = SnippetStore.TopN(5);
                if (top.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("最值得复用的:");
                    foreach (var s in top)
                        sb.Append("  ").Append(s.Name)
                          .Append("  用过 ").Append(s.SuccessCount + s.FailureCount)
                          .Append(" 次，成功率 ")
                          .Append((s.SuccessRate * 100).ToString("0")).AppendLine("%");
                }
            }

            sb.AppendLine();
            sb.AppendLine(PendingSnippetStore.Describe());
            sb.Append(SnippetUsageLedger.Describe());
            return sb.ToString();
        }
    }
}
