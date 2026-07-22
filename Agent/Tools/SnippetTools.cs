// TxTools.Agent / Tools / SnippetTools.cs
// 代码片段库工具：让 AI 把摸索出的可用 run_csharp 代码存下来，并在新需求时检索复用。
// 都是只读(只读写本地文本，不改场景)。
//
// v2：新增 find_snippet(语义标签匹配) + get_snippet 时自动增加使用计数。

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
                       "当你通过探查 API + run_csharp 跑通了一个有价值的做法，应当用它存下来：" +
                       "name 简短可检索，description 说明用途/前提，code 为可直接交给 run_csharp 执行的方法体。" +
                       "tags 为语义标签(如 robot,label,weld 等，可多个)，自动从代码提取但也可手动指定。" +
                       "注意：run_csharp 执行成功后系统会自动存片段(带 auto_ 前缀)，" +
                       "你只需对需要手动覆盖或补充说明的片段调用此工具。";
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
                        ""tags"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""语义标签(如 robot,label,weld), 可留空自动提取"" }
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

            var tags = GetString(input, "tags", null);
            var tagList = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(tags))
            {
                // tags 可能是 JSON 数组字符串或逗号分隔字符串
                try
                {
                    var arr = JArray.Parse(tags);
                    foreach (var t in arr) tagList.Add(t.ToString());
                }
                catch
                {
                    tagList.AddRange(tags.Split(new[] { ',', ' ', '|' }, System.StringSplitOptions.RemoveEmptyEntries));
                }
            }
            // 如果没手动指定 tags，自动从代码提取
            if (tagList.Count == 0) tagList = SnippetStore.ExtractTags(code);

            SnippetStore.Upsert(new Snippet
            {
                Name = name.Trim(),
                Description = GetString(input, "description", ""),
                Code = code,
                Tags = tagList,
                Origin = "manual"
            });
            return "已保存片段: " + name.Trim() + " [tags=" + string.Join(",", tagList) + "]";
        }
    }

    /// <summary>列出片段库(可按关键字过滤)，只给名称与说明，不含代码。</summary>
    public sealed class ListSnippetsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_snippets"; } }

        public override string Description
        {
            get { return "列出已保存的代码片段(name + description + tags，可按 keyword 过滤)。遇到新需求先查这里有没有现成可复用的做法。"; }
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

            if (list.Count == 0) return "片段库为空（或无匹配）。";
            var sb = new StringBuilder();
            sb.AppendLine("片段 " + list.Count + " 条：");
            foreach (var s in list)
            {
                var tagStr = s.Tags != null && s.Tags.Count > 0 ? "[" + string.Join(",", s.Tags) + "]" : "";
                var usageStr = s.SuccessCount > 0 ? "(复用" + s.SuccessCount + "次)" : "";
                sb.AppendLine("• " + s.Name + " " + tagStr + " — "
                    + (string.IsNullOrEmpty(s.Description) ? "(无说明)" : s.Description) + " " + usageStr);
            }
            return sb.ToString();
        }
    }

    /// <summary>取出某片段的完整代码，用于直接交给 run_csharp。</summary>
    public sealed class GetSnippetTool : TxAgentToolBase
    {
        public override string Name { get { return "get_snippet"; } }

        public override string Description
        {
            get { return "按 name 取出某片段的完整代码(可直接或稍改后交给 run_csharp 执行)。取出后系统会自动增加该片段的复用计数。"; }
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

            // 增加复用计数（越用越聪明）
            SnippetStore.IncrementUsage(name);

            var sb = new StringBuilder();
            sb.AppendLine("片段: " + snip.Name);
            if (!string.IsNullOrEmpty(snip.Description)) sb.AppendLine("说明: " + snip.Description);
            if (snip.Tags != null && snip.Tags.Count > 0)
                sb.AppendLine("标签: " + string.Join(",", snip.Tags));
            sb.AppendLine("复用: " + snip.SuccessCount + "次");
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
                return "用自然语言描述你想做的事，系统会按语义标签匹配最相关的片段。" +
                       "例如输入 '给机器人创建标签' 或 '查询焊点坐标' 就能找到相关代码。" +
                       "比 list_snippets 更智能——它同时匹配标签、名称和描述，按相关度排序。";
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
            if (list.Count == 0) return "未找到匹配的片段。试试换个描述，或用 list_snippets 查看所有片段。";

            var sb = new StringBuilder();
            sb.AppendLine("匹配片段 " + list.Count + " 条(按相关度排序)：");
            foreach (var s in list)
            {
                var tagStr = s.Tags != null && s.Tags.Count > 0 ? "[" + string.Join(",", s.Tags) + "]" : "";
                var usageStr = s.SuccessCount > 0 ? "(复用" + s.SuccessCount + "次)" : "";
                sb.AppendLine("• " + s.Name + " " + tagStr + " — "
                    + (string.IsNullOrEmpty(s.Description) ? "(无说明)" : s.Description) + " " + usageStr);
            }
            sb.AppendLine("用 get_snippet 按名称取出完整代码。");
            return sb.ToString();
        }
    }
}
