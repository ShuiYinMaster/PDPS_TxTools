// TxTools.Agent / Core / Tools / ApiLookupTool.cs
// 两个工具:
//   api_lookup —— 查 PDPS API 的真实签名。替代"写一段 probe_python 去 tx_dir 再探 __doc__"
//                 的整套流程,一次调用直接给出可照抄的签名 + 历史踩坑注解。
//   api_note   —— 把踩到的运行期坑登记进知识库,下次查同一类型时自动带出来。
//
// 注意:api_note 写的是本地知识库文件,不改场景,故 IsReadOnly = true —— 
// 否则每记一条都会弹一次审批框。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class ApiLookupTool : ITxAgentTool
    {
        public string Name { get { return "api_lookup"; } }

        public string Description
        {
            get
            {
                return "查询 PDPS (Tecnomatix.Engineering) 类型的真实成员与完整签名，直接反射当前进程已加载的程序集，结果100%准确。"
                     + "【写任何 run_csharp / run_python 代码之前，只要用到不确定的类型或方法，先用本工具确认签名，不要用 probe_python 去猜。】"
                     + "参数 type 支持简名(TxWeldOperation)或全名；member 可填成员名或片段做过滤；"
                     + "不确定类型叫什么时用 search 做模糊搜索；不确定哪个类型有某方法时用 member_search；"
                     + "想知道【谁接收或返回某个类型】(例如哪个方法吃 TxLibraryData) 用 signature_search —— "
                     + "找一个数据类的消费者往往是摸清整条 API 链路的关键。"
                     + "返回内容会附带该类型的历史踩坑注解(如某方法已废弃、某属性 setter 会抛异常)。";
            }
        }

        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""type"":          { ""type"": ""string"", ""description"": ""类型名，简名或全名，如 TxWeldOperation"" },
    ""member"":        { ""type"": ""string"", ""description"": ""可选。成员名或片段，用于过滤该类型的成员"" },
    ""kind"":          { ""type"": ""string"", ""description"": ""可选。只看某类成员：method / property / field / event / ctor"" },
    ""search"":        { ""type"": ""string"", ""description"": ""可选。类型名模糊搜索关键字，与 type 二选一"" },
    ""member_search"": { ""type"": ""string"", ""description"": ""可选。跨所有类型搜成员名，用于'哪个类型有这个方法'"" },
    ""signature_search"": { ""type"": ""string"", ""description"": ""可选。跨所有类型搜签名文本，用于'谁接收/返回这个类型'"" },
    ""inherited"":     { ""type"": ""boolean"", ""description"": ""可选，默认 false。是否包含继承来的成员"" }
  }
}");
            }
        }

        private const int MaxMembers = 120;

        public string Execute(JObject input)
        {
            var typeName = Str(input, "type");
            var member = Str(input, "member");
            var kind = Str(input, "kind");
            var search = Str(input, "search");
            var memberSearch = Str(input, "member_search");
            var signatureSearch = Str(input, "signature_search");
            bool inherited = input["inherited"] != null && (bool)input["inherited"];

            ApiIndex.EnsureBuilt();

            if (!string.IsNullOrWhiteSpace(signatureSearch))
                return RenderSignatureSearch(signatureSearch);

            if (!string.IsNullOrWhiteSpace(memberSearch))
                return RenderMemberSearch(memberSearch);

            if (!string.IsNullOrWhiteSpace(search))
                return RenderTypeSearch(search);

            if (string.IsNullOrWhiteSpace(typeName))
                return "请提供 type(类型名)，或用 search 做类型模糊搜索，或用 member_search 跨类型搜成员。";

            var found = ApiIndex.Find(typeName);
            if (found.Count == 0)
            {
                var near = ApiIndex.SearchTypes(typeName, 15);
                var sb0 = new StringBuilder();
                sb0.AppendLine("未找到类型: " + typeName);
                if (near.Count > 0)
                {
                    sb0.AppendLine("名称相近的类型:");
                    foreach (var n in near) sb0.AppendLine("  " + n);
                }
                else
                {
                    sb0.AppendLine("也没有名称相近的类型。请确认拼写，或用 member_search 从方法名反查。");
                }
                return sb0.ToString();
            }

            if (found.Count > 1 && string.IsNullOrWhiteSpace(member))
            {
                var sb1 = new StringBuilder();
                sb1.AppendLine("简名 " + typeName + " 命中 " + found.Count + " 个类型，请用全名重查:");
                foreach (var f in found) sb1.AppendLine("  " + f.FullName);
                return sb1.ToString();
            }

            return RenderType(found[0], member, kind, inherited);
        }

        private static string RenderType(ApiType t, string memberFilter, string kindFilter, bool inherited)
        {
            var sb = new StringBuilder();

            sb.AppendLine("== " + t.FullName + " ==");
            sb.AppendLine(t.Kind + (t.Obsolete ? "  [已废弃]" : ""));
            if (!string.IsNullOrEmpty(t.BaseType)) sb.AppendLine("基类: " + t.BaseType);
            if (t.Interfaces != null && t.Interfaces.Count > 0)
                sb.AppendLine("接口: " + string.Join(", " , t.Interfaces.Take(12)));

            // 踩坑注解放最前面 —— 这是反射给不出、最容易翻车的信息
            var notes = ApiNotesStore.ForType(t.Name);
            if (notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- 已知注意事项 (来自历史踩坑记录) --");
                foreach (var n in notes)
                {
                    sb.Append("  [").Append(n.Kind).Append("] ");
                    if (!string.IsNullOrEmpty(n.Member)) sb.Append(n.Member).Append(": ");
                    sb.AppendLine(n.Text);
                }
                ApiNotesStore.MarkHit(t.Name);
            }

            var members = t.Members.AsEnumerable();

            if (!inherited)
                members = members.Where(m => string.Equals(m.DeclaredBy, t.Name, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(kindFilter))
                members = members.Where(m => string.Equals(m.Kind, kindFilter, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(memberFilter))
                members = members.Where(m => m.Name.IndexOf(memberFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = members
                .OrderBy(m => KindOrder(m.Kind))
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.AppendLine();
            if (list.Count == 0)
            {
                sb.AppendLine("-- 无匹配成员 --");
                if (!inherited)
                    sb.AppendLine("(默认只列自有成员。若要连继承成员一起看，传 inherited=true)");
                return sb.ToString();
            }

            bool truncated = list.Count > MaxMembers;
            sb.AppendLine("-- 成员 (" + list.Count + (truncated ? "，仅显示前 " + MaxMembers : "") + ") --");

            foreach (var m in list.Take(MaxMembers))
            {
                sb.Append("  ").Append(m.Signature);
                if (m.Obsolete)
                {
                    sb.Append("   [已废弃");
                    if (!string.IsNullOrEmpty(m.ObsoleteMessage)) sb.Append(": ").Append(m.ObsoleteMessage);
                    sb.Append("]");
                }
                if (!string.Equals(m.DeclaredBy, t.Name, StringComparison.OrdinalIgnoreCase))
                    sb.Append("   (继承自 ").Append(m.DeclaredBy).Append(")");
                sb.AppendLine();
            }

            if (truncated)
                sb.AppendLine("... 成员过多，请用 member 参数过滤，例如 member=\"Add\"");

            return sb.ToString();
        }

        private static string RenderTypeSearch(string keyword)
        {
            var hits = ApiIndex.SearchTypes(keyword, 40);
            var sb = new StringBuilder();
            sb.AppendLine("类型搜索 \"" + keyword + "\" 命中 " + hits.Count + " 个:");
            foreach (var h in hits) sb.AppendLine("  " + h);
            if (hits.Count == 0) sb.AppendLine("  (无)");
            else sb.AppendLine("用 type=<名称> 查看具体成员。");
            return sb.ToString();
        }

        private static string RenderSignatureSearch(string keyword)
        {
            var hits = ApiIndex.SearchSignatures(keyword, 60);
            var sb = new StringBuilder();
            sb.AppendLine("签名中含 \"" + keyword + "\" 的成员，命中 " + hits.Count + " 条:");
            foreach (var h in hits) sb.AppendLine("  " + h);
            if (hits.Count == 0)
                sb.AppendLine("  (无。该类型可能只出现在构造函数里，或拼写不符)");
            return sb.ToString();
        }

        private static string RenderMemberSearch(string keyword)
        {
            var hits = ApiIndex.SearchMembers(keyword, 40);
            var sb = new StringBuilder();
            sb.AppendLine("成员搜索 \"" + keyword + "\" 命中 " + hits.Count + " 条:");
            foreach (var h in hits) sb.AppendLine("  " + h);
            if (hits.Count == 0) sb.AppendLine("  (无)");
            return sb.ToString();
        }

        private static int KindOrder(string kind)
        {
            switch (kind)
            {
                case "ctor": return 0;
                case "property": return 1;
                case "method": return 2;
                case "field": return 3;
                case "event": return 4;
                default: return 5;
            }
        }

        private static string Str(JObject o, string key)
        {
            if (o == null) return null;
            var v = o[key];
            return v == null ? null : v.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class ApiNoteTool : ITxAgentTool
    {
        public string Name { get { return "api_note"; } }

        public string Description
        {
            get
            {
                return "把刚踩到的 PDPS API 坑记进知识库，下次 api_lookup 查同一类型时会自动带出来。"
                     + "【当你通过试错发现了签名上看不出来的行为时就调用它】，例如：某方法已废弃需改用别的、"
                     + "某属性 setter 会抛异常、某 API 在 IronPython 下不可用、调用前必须先做某步准备。"
                     + "不要记录能从签名直接看出来的信息，那些 api_lookup 本来就有。";
            }
        }

        /// <summary>只写本地知识库文件，不改场景，故标只读以免每次弹审批框。</summary>
        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""type"", ""text""],
  ""properties"": {
    ""type"":   { ""type"": ""string"", ""description"": ""类型简名，如 TxJoint"" },
    ""member"": { ""type"": ""string"", ""description"": ""可选。具体成员名，如 Name"" },
    ""text"":   { ""type"": ""string"", ""description"": ""一句话说清坑在哪、正确做法是什么"" },
    ""kind"":   { ""type"": ""string"", ""description"": ""gotcha(踩坑) / usage(正确用法) / deprecated(已废弃)，默认 gotcha"" }
  }
}");
            }
        }

        public string Execute(JObject input)
        {
            var type = Str(input, "type");
            var member = Str(input, "member");
            var text = Str(input, "text");
            var kind = Str(input, "kind");

            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(text))
                return "参数不足：type 和 text 都必填。";

            string convId = null;
            try { convId = TxTools.Agent.Harness.HarnessAgentLoop.Current?.CurrentConvId; }
            catch { }

            ApiNotesStore.Record(type, member, text, kind, convId);

            return "已记录到 API 知识库：" + type
                 + (string.IsNullOrWhiteSpace(member) ? "" : "." + member)
                 + " -> " + text;
        }

        private static string Str(JObject o, string key)
        {
            if (o == null) return null;
            var v = o[key];
            return v == null ? null : v.ToString();
        }
    }
}
