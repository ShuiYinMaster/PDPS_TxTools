// TxTools.Agent / Core / MemoryTools.cs
// 记忆系统对外暴露的 6 个工具:
//   search_past_conversations  — 跨对话搜索(只读,走 MD 摘要索引)
//   read_past_conversation     — 按 id 取某次对话的详情(只读)
//   list_gotchas               — 列出踩坑清单(只读)
//   add_gotcha_correction      — 补充踩坑正解(写库)
//   list_facts                 — 列出已知事实(只读)
//   add_fact                   — 追加事实(写库)
//
// 注意:
// - "写库" 类工具 IsReadOnly=false,会走 AgentLoop 的 ApprovalRequest 审批流。
//   若想让它们免审批(高频使用),把工具名加入 AgentOptions.AutoApproveTools。
// - AddFact 需要注入当前 convId,通过构造函数传入 Func<string> 委托实现松耦合,
//   避免直接依赖 AgentLoop 单例。
// - ExtractLessons 不作为工具暴露(会阻塞 UI 主线程);由 UI 层直接 await
//   AgentLoop.ExtractLessonsAsync 触发。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    // ─────────────────────────────────────────────────────────────────
    // 1) search_past_conversations
    // ─────────────────────────────────────────────────────────────────

    public sealed class SearchPastConversationsTool : TxAgentToolBase
    {
        private readonly Func<string> _currentConvIdGetter;

        /// <param name="currentConvIdGetter">用于自动排除当前对话(避免自搜)。可传 null。</param>
        public SearchPastConversationsTool(Func<string> currentConvIdGetter = null)
        {
            _currentConvIdGetter = currentConvIdGetter;
        }

        public override string Name { get { return "search_past_conversations"; } }

        public override string Description
        {
            get
            {
                return "在过往所有对话中搜索,返回匹配的对话标题、时间、涉及的工具和相关摘要片段。" +
                       "遇到「我之前是否处理过X」/「上次那个方案怎么做的」/「记得当时的结论吗」等需要跨对话回忆时使用。" +
                       "关键字除了普通词,还可以直接搜工具名(如 run_csharp)或 PS 类型名(如 TxWeldPoint) —— " +
                       "索引对这两类做了加权,命中率最高。" +
                       "本工具搜的是摘要索引;拿到 conv_id 后用 read_past_conversation 取该对话的细节。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"query\": { \"type\": \"string\", \"description\": \"关键字,多个用空格分隔\" }," +
                    "  \"max_results\": { \"type\": \"integer\", \"description\": \"返回对话数上限,默认 5\" }," +
                    "  \"include_current\": { \"type\": \"boolean\", \"description\": \"是否包含当前对话,默认 false\" }" +
                    "}, \"required\": [\"query\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var query = GetString(input, "query", "");
            int maxResults = input != null && input["max_results"] != null && input["max_results"].Type == JTokenType.Integer
                ? (int)input["max_results"] : 5;
            bool includeCurrent = input != null && input["include_current"] != null && input["include_current"].Type == JTokenType.Boolean
                ? (bool)input["include_current"] : false;

            if (string.IsNullOrWhiteSpace(query)) return "参数 query 不能为空。";

            var keywords = query.ToLowerInvariant()
                .Split(new[] { ' ', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(k => k.Length >= 2)
                .ToArray();
            if (keywords.Length == 0) return "关键字过短或无效(需≥2字符)。";

            // 老对话没有索引,首次搜索时补建一次
            ConversationIndex.EnsureAll();

            string excludeId = includeCurrent ? null
                : (_currentConvIdGetter != null ? _currentConvIdGetter() : null);

            var hits = ConversationIndex.Search(keywords, excludeId, maxResults);
            if (hits.Count == 0)
                return "未找到相关对话。可换用更具体的关键字,例如工具名(run_csharp)或 PS 类型名(TxWeldPoint)。";

            var sb = new StringBuilder();
            sb.AppendLine("找到 " + hits.Count + " 条相关对话:");
            foreach (var h in hits)
            {
                sb.AppendLine();
                sb.AppendLine("[" + h.Id + "] " + (string.IsNullOrEmpty(h.Title) ? "(无标题)" : h.Title)
                    + " — " + h.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    + "  score=" + h.Score.ToString("0.0"));
                if (h.Tools != null && h.Tools.Count > 0)
                    sb.AppendLine("  工具: " + string.Join(", ", h.Tools.Take(8)));
                foreach (var s2 in h.Snippets) sb.AppendLine("  · " + s2);
            }
            sb.AppendLine();
            sb.AppendLine("需要某条的完整经过,用 read_past_conversation(conv_id=\"...\")。");
            return sb.ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 1b) read_past_conversation —— 按 id 取细节
    // ─────────────────────────────────────────────────────────────────

    public sealed class ReadPastConversationTool : TxAgentToolBase
    {
        public override string Name { get { return "read_past_conversation"; } }

        public override string Description
        {
            get
            {
                return "读取某个历史对话的详情。先用 search_past_conversations 拿到 conv_id 再调本工具。" +
                       "默认返回该对话的逐轮摘要(用户问了什么 → 调了哪些工具 → 结论);" +
                       "传 query 时额外回原始消息里命中该关键字的完整片段,用于查当时的具体代码或参数。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"conv_id\": { \"type\": \"string\", \"description\": \"对话 id,形如 conv_20260727095802719\" }," +
                    "  \"query\": { \"type\": \"string\", \"description\": \"可选。在原始消息里检索该关键字并返回完整片段\" }," +
                    "  \"max_excerpts\": { \"type\": \"integer\", \"description\": \"query 命中片段数上限,默认 5\" }" +
                    "}, \"required\": [\"conv_id\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var convId = GetString(input, "conv_id");
            var query = GetString(input, "query", "");
            int maxExcerpts = input != null && input["max_excerpts"] != null && input["max_excerpts"].Type == JTokenType.Integer
                ? (int)input["max_excerpts"] : 5;

            if (string.IsNullOrWhiteSpace(convId)) return "参数 conv_id 不能为空。";

            var sb = new StringBuilder();

            var doc = ConversationIndex.Get(convId);
            if (doc == null)
            {
                // 索引缺失(可能是刚导入的旧对话),现建一次
                var c0 = ConversationStore.Load(convId);
                if (c0 == null) return "未找到对话 " + convId + "。请先用 search_past_conversations 确认 id。";
                ConversationIndex.Rebuild(c0);
                doc = ConversationIndex.Get(convId);
            }

            if (doc != null)
            {
                sb.AppendLine("[" + convId + "] " + doc.Get("title", "(无标题)"));
                sb.AppendLine("时间: " + doc.Get("updated", "?") + "  轮数: " + doc.Get("turns", "?"));
                var tools = doc.Get("tools", "");
                if (!string.IsNullOrWhiteSpace(tools) && tools != "[]") sb.AppendLine("工具: " + tools);
                var types = doc.Get("types", "");
                if (!string.IsNullOrWhiteSpace(types) && types != "[]") sb.AppendLine("涉及类型: " + types);
                var files = doc.Get("files", "");
                if (!string.IsNullOrWhiteSpace(files) && files != "[]") sb.AppendLine("附件: " + files);
                sb.AppendLine();
                sb.AppendLine(doc.Body);
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var conv = ConversationStore.Load(convId);
                if (conv == null || conv.Messages == null)
                {
                    sb.AppendLine("(原始记录不可读,无法检索片段)");
                }
                else
                {
                    var kws = query.ToLowerInvariant()
                        .Split(new[] { ' ', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(k => k.Length >= 2).ToArray();

                    var found = 0;
                    sb.AppendLine("── 原始记录中命中 \"" + query + "\" 的片段 ──");

                    foreach (var m in conv.Messages)
                    {
                        if (found >= maxExcerpts) break;
                        if (m == null || string.IsNullOrEmpty(m.Content)) continue;
                        if (m.Role == "system") continue;

                        var lower = m.Content.ToLowerInvariant();
                        if (!kws.Any(k => lower.Contains(k))) continue;

                        found++;
                        sb.AppendLine();
                        sb.AppendLine("[" + m.Role + "] " + Excerpt(m.Content, kws, 500));
                    }

                    if (found == 0) sb.AppendLine("(无命中)");
                }
            }

            return sb.ToString();
        }

        private static string Excerpt(string content, string[] keywords, int span)
        {
            var lower = content.ToLowerInvariant();
            int first = int.MaxValue;
            foreach (var kw in keywords)
            {
                int i = lower.IndexOf(kw, StringComparison.Ordinal);
                if (i >= 0 && i < first) first = i;
            }
            if (first == int.MaxValue) first = 0;

            int from = Math.Max(0, first - 120);
            int len = Math.Min(span, content.Length - from);
            var s = content.Substring(from, len);
            if (from > 0) s = "…" + s;
            if (from + len < content.Length) s += "…";
            return s;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 2) list_gotchas
    // ─────────────────────────────────────────────────────────────────

    public sealed class ListGotchasTool : TxAgentToolBase
    {
        public override string Name { get { return "list_gotchas"; } }

        public override string Description
        {
            get
            {
                return "列出已记录的 PS SDK / C# 踩坑清单(错误签名、错误消息、正确用法)。" +
                       "写 run_csharp 前想主动查一遍避坑,或用户问“之前踩过什么坑”时使用。" +
                       "系统提示已注入 Top-15,本工具是完整清单入口。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"only_with_correction\": { \"type\": \"boolean\", \"description\": \"仅列出已有正解的\" }," +
                    "  \"max_results\": { \"type\": \"integer\", \"description\": \"返回数,默认 30\" }" +
                    "} }");
            }
        }

        public override string Execute(JObject input)
        {
            bool onlyWithFix = input != null && input["only_with_correction"] != null
                               && input["only_with_correction"].Type == JTokenType.Boolean
                ? (bool)input["only_with_correction"] : false;
            int max = input != null && input["max_results"] != null && input["max_results"].Type == JTokenType.Integer
                ? (int)input["max_results"] : 30;

            var all = GotchasStore.All();
            if (onlyWithFix) all = all.Where(g => !string.IsNullOrEmpty(g.Correction)).ToList();
            if (all.Count == 0) return "尚无踩坑记录。";

            var top = all.OrderByDescending(g => g.HitCount).Take(max).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("踩坑清单(共 " + top.Count + " 条):");
            foreach (var g in top)
            {
                sb.AppendLine();
                sb.AppendLine("[" + g.Signature + "] hits=" + g.HitCount
                    + " type=" + (g.ErrorType ?? ""));
                sb.AppendLine("  错误: " + Truncate(g.ErrorMessage, 140));
                if (!string.IsNullOrEmpty(g.Correction))
                    sb.AppendLine("  正解: " + Truncate(g.Correction, 220));
                else
                    sb.AppendLine("  正解: (暂无,若已知请用 add_gotcha_correction 补充)");
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 3) add_gotcha_correction
    // ─────────────────────────────────────────────────────────────────

    public sealed class AddGotchaCorrectionTool : TxAgentToolBase
    {
        public override string Name { get { return "add_gotcha_correction"; } }

        public override string Description
        {
            get
            {
                return "为已记录的踩坑补充正确用法。当你在对话中确认了某个报错 API/语法的正确写法后," +
                       "应主动调用本工具存档,避免下次重复踩。" +
                       "signature 必须与 list_gotchas 显示的签名严格一致(可先用 list_gotchas 查看)。";
            }
        }

        // 写入持久化数据,走审批;若嫌频繁,把工具名加到 AutoApproveTools 白名单。
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"signature\": { \"type\": \"string\", \"description\": \"踩坑签名,与 list_gotchas 一致\" }," +
                    "  \"correction\": { \"type\": \"string\", \"description\": \"正确写法,建议附最小可运行示例\" }" +
                    "}, \"required\": [\"signature\", \"correction\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var sig = GetString(input, "signature");
            var correction = GetString(input, "correction");
            if (string.IsNullOrWhiteSpace(sig)) return "参数 signature 不能为空。";
            if (string.IsNullOrWhiteSpace(correction)) return "参数 correction 不能为空。";
            bool ok = GotchasStore.AddCorrection(sig, correction);
            return ok
                ? ("已为 [" + sig + "] 补充正确用法。")
                : ("未找到签名 [" + sig + "]。请用 list_gotchas 核对现有签名。");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 4) list_facts
    // ─────────────────────────────────────────────────────────────────

    public sealed class ListFactsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_facts"; } }

        public override string Description
        {
            get
            {
                return "列出跨对话保留的用户偏好、场景常量、验证过的事实。" +
                       "可按关键字或类别(preference/scene_constant/api_fact/workflow/misc)过滤。" +
                       "用户问“你还记得什么”/需要回忆共识/或想复用先前判断时使用。" +
                       "系统提示已注入 Top-10,本工具是完整清单入口。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"query\": { \"type\": \"string\", \"description\": \"可选,关键字过滤\" }," +
                    "  \"category\": { \"type\": \"string\", \"description\": \"可选,preference/scene_constant/api_fact/workflow/misc\" }," +
                    "  \"max_results\": { \"type\": \"integer\", \"description\": \"默认 30\" }" +
                    "} }");
            }
        }

        public override string Execute(JObject input)
        {
            var query = GetString(input, "query", "");
            var cat = GetString(input, "category", "");
            int max = input != null && input["max_results"] != null && input["max_results"].Type == JTokenType.Integer
                ? (int)input["max_results"] : 30;

            IEnumerable<Fact> src = string.IsNullOrWhiteSpace(query)
                ? (IEnumerable<Fact>)FactsStore.All()
                : FactsStore.FindByKeyword(query);

            if (!string.IsNullOrWhiteSpace(cat))
                src = src.Where(f => string.Equals(f.Category, cat, StringComparison.OrdinalIgnoreCase));

            var list = src.Take(max).ToList();
            if (list.Count == 0) return "无匹配事实。";

            var sb = new StringBuilder();
            sb.AppendLine("已知事实(共 " + list.Count + " 条):");
            foreach (var f in list)
                sb.AppendLine("  · [" + f.Category + "] " + f.Content);
            return sb.ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // 5) add_fact
    // ─────────────────────────────────────────────────────────────────

    public sealed class AddFactTool : TxAgentToolBase
    {
        private readonly Func<string> _getConvId;

        /// <param name="convIdGetter">拿当前对话 id 用于溯源。可传 null(存 null convId)。</param>
        public AddFactTool(Func<string> convIdGetter)
        {
            _getConvId = convIdGetter;
        }

        public override string Name { get { return "add_fact"; } }

        public override string Description
        {
            get
            {
                return "追加一条跨对话保留的事实/偏好。适用场景:" +
                       "(1) 用户明确表达偏好(“我一般用 XZ 平面对称”);" +
                       "(2) 给出场景常量(“这个 study 有 8 台机器人”);" +
                       "(3) 你在对话中验证了一条 PS SDK 事实。" +
                       "category: preference / scene_constant / api_fact / workflow / misc。" +
                       "已存在相似事实会自动去重、刷新确认时间。";
            }
        }

        // 写库,走审批。低风险,可考虑加入 AutoApproveTools 免弹窗。
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"content\": { \"type\": \"string\", \"description\": \"事实内容,20-80 字,简明陈述\" }," +
                    "  \"category\": { \"type\": \"string\", \"description\": \"preference/scene_constant/api_fact/workflow/misc\" }" +
                    "}, \"required\": [\"content\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var content = GetString(input, "content");
            var category = GetString(input, "category", "misc");
            if (string.IsNullOrWhiteSpace(content)) return "参数 content 不能为空。";

            var convId = _getConvId != null ? _getConvId() : null;
            var f = FactsStore.Add(content, category, convId);
            return f != null
                ? ("已记录事实: [" + f.Category + "] " + f.Content)
                : "记录失败。";
        }
    }
}