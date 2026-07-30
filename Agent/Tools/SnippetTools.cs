// TxTools.Agent / Tools / SnippetTools.cs
// 代码片段库工具:让 AI 把摸索出的可用 run_csharp 代码存下来,并在新需求时检索复用。
// 都是只读(只读写本地文本,不改场景)。
//
// v2:新增 find_snippet(语义标签匹配) + get_snippet 时自动增加使用计数。
// v3 (bugfix): SaveSnippetTool 里 tags 参数解析改成按 JToken 类型分支 —— 之前统一
//   走 GetString,当 AI 按 schema 传 array 时会崩 "Can not convert Array to String"。

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using System.Text;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    /// <summary>保存一段可复用的 C# 代码到片段库。</summary>
    public sealed class SaveSnippetTool : TxAgentToolBase
    {
        public override string Name { get { return "save_snippet"; } }

        public override string Description
        {
            get
            {
                return "把一段验证可用的 run_csharp 代码存入片段库以便日后复用(按 name 覆盖)。" +
                       "当你通过探查 API + run_csharp 跑通了一个有价值的做法,应当用它存下来:" +
                       "name 简短可检索,description 说明用途/前提,code 为可直接交给 run_csharp 执行的方法体。" +
                       "tags 为语义标签数组(如 [\"robot\",\"label\",\"weld\"]),自动从代码提取但也可手动指定。" +
                       "注意:run_csharp 执行成功【不会】立刻存成片段 —— " +
                       "同类操作重复出现 3 次后系统才自动固化(带 auto_ 前缀)。" +
                       "所以碰到确实值得复用、又不想等它重复三次的做法，就用本工具主动存下来。" +
                       "lang 务必填对(csharp / python)：取出时模型要靠它决定送给哪个执行工具。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""片段名(唯一, 简短可检索)"" },
                        ""description"": { ""type"": ""string"", ""description"": ""用途/前提/注意事项"" },
                        ""code"": { ""type"": ""string"", ""description"": ""可直接交给 run_csharp 的 C# 5 方法体"" },
                        ""tags"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""语义标签数组(如 [\""robot\"",\""label\"",\""weld\""]), 可留空自动提取"" },
                        ""lang"": { ""type"": ""string"", ""enum"": [""csharp"", ""python""], ""description"": ""代码语言。存 run_csharp 的代码填 csharp, 存 run_python/probe_python 的代码填 python。留空按 csharp 处理"" }
                    },
                    ""required"": [""name"", ""code""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var name = GetString(input, "name", null);
            var code = GetString(input, "code", null);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
                return "name 和 code 不能为空。";

            // === bugfix: tags 按 JToken 类型分支解析,不走 GetString ===
            //   schema 里 tags 是 array,AI 按规范会传 JArray;
            //   宽容处理其他情况:JSON 数组文本字符串、逗号分隔字符串、null 都能吃。
            var tagList = ParseTags(input["tags"]);
            if (tagList.Count == 0)
                tagList = SnippetStore.ExtractTags(code);   // 无 tags 则自动提取

            SnippetStore.Upsert(new Snippet
            {
                Name = name.Trim(),
                Description = GetString(input, "description", ""),
                Code = code,
                Lang = SnippetStore.NormalizeLang(GetString(input, "lang", "csharp")),
                Tags = tagList,
                Origin = "manual"
            });
            return "已保存片段: " + name.Trim() + " [tags=" + string.Join(",", tagList) + "]";
        }

        /// <summary>
        /// 弹性 tags 解析,接受:
        ///   - JArray (AI 按 schema 规范传的正宗数组)
        ///   - JValue string —— 可能是 JSON 数组文本 [\"a\",\"b\"] 或逗号/空格/竖线分隔的字符串
        ///   - null / 缺失 —— 返回空列表(交给调用方兜底自动提取)
        /// </summary>
        private static List<string> ParseTags(JToken tok)
        {
            var result = new List<string>();
            if (tok == null || tok.Type == JTokenType.Null) return result;

            // 情况 1: 正宗数组
            if (tok.Type == JTokenType.Array)
            {
                foreach (var t in (JArray)tok)
                {
                    if (t == null || t.Type == JTokenType.Null) continue;
                    var s = t.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
                }
                return result;
            }

            // 情况 2: 字符串 —— 先按 JSON 数组尝试,失败退回分隔符 split
            if (tok.Type == JTokenType.String)
            {
                var raw = (string)tok;
                if (string.IsNullOrWhiteSpace(raw)) return result;
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    try
                    {
                        foreach (var t in JArray.Parse(trimmed))
                        {
                            var s = t.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
                        }
                        return result;
                    }
                    catch { /* 落到 split */ }
                }
                foreach (var p in raw.Split(new[] { ',', ' ', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = p.Trim();
                    if (t.Length > 0) result.Add(t);
                }
                return result;
            }

            // 情况 3: 其他类型(number/bool 等 AI 误传) —— 尽力用 ToString 单个塞进去
            try
            {
                var s = tok.ToString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
            }
            catch { }
            return result;
        }
    }

    /// <summary>列出片段库(可按关键字过滤),只给名称与说明,不含代码。</summary>
    public sealed class ListSnippetsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_snippets"; } }

        public override string Description
        {
            get { return "列出已保存的代码片段(name + description + tags,可按 keyword 过滤)。遇到新需求先查这里有没有现成可复用的做法。"; }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""keyword"": { ""type"": ""string"", ""description"": ""按名称/说明/标签模糊过滤, 留空列全部"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var keyword = GetString(input, "keyword", null);
            var list = string.IsNullOrWhiteSpace(keyword)
                ? SnippetStore.All()
                : SnippetStore.FindByTagOrKeyword(keyword);

            if (list.Count == 0) return "片段库为空(或无匹配)。";
            var sb = new StringBuilder();
            sb.AppendLine("片段 " + list.Count + " 条:");
            foreach (var s in list)
            {
                var tagStr = s.Tags != null && s.Tags.Count > 0 ? "[" + string.Join(",", s.Tags) + "]" : "";
                var usageStr = s.SuccessCount + s.FailureCount > 0
                    ? "(" + s.SuccessCount + "成/" + s.FailureCount + "败)" : "";
                sb.AppendLine("• " + s.Name + " <" + SnippetStore.NormalizeLang(s.Lang) + "> " + tagStr + " — "
                    + (string.IsNullOrEmpty(s.Description) ? "(无说明)" : s.Description) + " " + usageStr);
            }
            return sb.ToString();
        }
    }

    /// <summary>取出某片段的完整代码,用于直接交给 run_csharp。</summary>
    public sealed class GetSnippetTool : TxAgentToolBase
    {
        public override string Name { get { return "get_snippet"; } }

        public override string Description
        {
            get
            {
                return "按 name 取出某片段的完整代码。" +
                       "【注意片段有 C# 和 Python 两种】返回结果里的「语言」一行决定它该送给哪个工具，" +
                       "不要看着代码眼熟就往 run_csharp 里塞。" +
                       "取出后系统会在你随后执行代码时自动判定这次复用成没成，回填到该片段的成功率。" +
                       "所以:取出来发现不合用就别执行，直接换一条或自己写 —— " +
                       "不会因此给它记失败；改动越小，判定越准。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""片段名"" }
                    },
                    ""required"": [""name""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var name = GetString(input, "name", null);
            var snip = SnippetStore.Get(name);
            if (snip == null) return "未找到片段: " + name;

            // 【不再在这里记成功】取出只登记"待判定",
            // 等随后真正执行代码时由 SnippetUsageLedger 回填成功/失败。
            SnippetUsageLedger.Register(snip.Name, snip.Code, snip.Lang);

            var lang = SnippetStore.NormalizeLang(snip.Lang);

            var sb = new StringBuilder();
            sb.AppendLine("片段: " + snip.Name);
            sb.AppendLine("语言: " + lang
                + (lang == "python" ? "  → 交给 run_python / probe_python 执行"
                                    : "  → 交给 run_csharp 执行"));
            if (!string.IsNullOrEmpty(snip.Description)) sb.AppendLine("说明: " + snip.Description);
            if (snip.Tags != null && snip.Tags.Count > 0)
                sb.AppendLine("标签: " + string.Join(",", snip.Tags));
            sb.AppendLine("复用记录: " + snip.SuccessCount + " 成 / " + snip.FailureCount + " 败"
                + (snip.UndecidedCount > 0 ? " / " + snip.UndecidedCount + " 未判定" : ""));
            sb.AppendLine("--- 代码 ---");
            sb.Append(snip.Code);
            return sb.ToString();
        }
    }

    /// <summary>按语义描述/标签查找最匹配的片段(比 list_snippets 更智能)。</summary>
    public sealed class FindSnippetTool : TxAgentToolBase
    {
        public override string Name { get { return "find_snippet"; } }

        public override string Description
        {
            get
            {
                return "用自然语言描述你想做的事,系统会按语义标签匹配最相关的片段。" +
                       "例如输入 '给机器人创建标签' 或 '查询焊点坐标' 就能找到相关代码。" +
                       "比 list_snippets 更智能 —— 它同时匹配标签、名称和描述,按相关度排序。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""query"": { ""type"": ""string"", ""description"": ""你想做什么的自然语言描述(如 '给机器人创建标签', '导出焊点坐标')"" }
                    },
                    ""required"": [""query""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var query = GetString(input, "query", null);
            if (string.IsNullOrWhiteSpace(query))
                return "请输入你想做什么的描述(如 '查询机器人基座', '创建标签')。";

            var list = SnippetStore.FindByTagOrKeyword(query);
            if (list.Count == 0) return "未找到匹配的片段。试试换个描述,或用 list_snippets 查看所有片段。";

            var sb = new StringBuilder();
            sb.AppendLine("匹配片段 " + list.Count + " 条(按相关度排序):");
            foreach (var s in list)
            {
                var tagStr = s.Tags != null && s.Tags.Count > 0 ? "[" + string.Join(",", s.Tags) + "]" : "";
                var usageStr = s.SuccessCount + s.FailureCount > 0
                    ? "(" + s.SuccessCount + "成/" + s.FailureCount + "败)" : "";
                sb.AppendLine("• " + s.Name + " <" + SnippetStore.NormalizeLang(s.Lang) + "> " + tagStr + " — "
                    + (string.IsNullOrEmpty(s.Description) ? "(无说明)" : s.Description) + " " + usageStr);
            }
            sb.AppendLine("用 get_snippet 按名称取出完整代码。");
            return sb.ToString();
        }
    }
}