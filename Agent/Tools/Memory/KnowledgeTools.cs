// TxTools.Agent / Tools / Memory / KnowledgeTools.cs
//
// 本地知识库的两个工具。配合系统提示词里常驻的目录使用:
//   目录告诉模型"有什么" → read_knowledge 取具体那一节
//   不确定在哪一节时 → search_knowledge 先找

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class SearchKnowledgeTool : TxAgentToolBase
    {
        public override string Name { get { return "search_knowledge"; } }

        public override string Description
        {
            get
            {
                return "在本地知识库(用户自备的参考文档)里按关键字检索，返回匹配的小节及命中行。"
                     + "系统提示词里已列出知识库目录 —— 若你从目录就能判断答案在哪一节，"
                     + "直接用 read_knowledge 取，不必先搜。"
                     + "本工具用于「知道大概讲过但不记得在哪节」的情况。"
                     + "拿到 ref 后用 read_knowledge 取完整内容再作答，不要只凭命中行片段下结论。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"query\": { \"type\": \"string\", \"description\": \"关键字，多个用空格分隔\" }," +
                    "  \"max_results\": { \"type\": \"integer\", \"description\": \"返回小节数上限，默认 5\" }" +
                    "}, \"required\": [\"query\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var query = GetString(input, "query", "");
            int max = input != null && input["max_results"] != null
                      && input["max_results"].Type == JTokenType.Integer
                ? (int)input["max_results"] : 5;

            if (string.IsNullOrWhiteSpace(query)) return "参数 query 不能为空。";

            if (KnowledgeStore.IsEmpty)
                return "本地知识库为空。用户可以把 .md 文档放到 "
                     + KnowledgeStore.FolderPath() + " 目录下，重开对话即可生效。";

            var keywords = query.ToLowerInvariant()
                .Split(new[] { ' ', ',', '|', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(k => k.Length >= 2)
                .ToArray();
            if (keywords.Length == 0) return "关键字过短或无效(需≥2字符)。";

            List<KnowledgeIndex.Hit> hits;
            try
            {
                hits = KnowledgeIndex
                    .SearchAsync(query, keywords, max, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 检索本身故障时,明确说明并给出正确的替代路径。
                // 早先这里报错后模型会转去用 read_knowledge 挨个猜小节名,
                // 连猜二十几轮 —— 那是最差的降级方式。
                try { AuditLog.Write("[error] [Knowledge] 检索失败: " + ex); } catch { }
                return "Error: 知识库检索出错 - " + ex.GetType().Name + ": " + ex.Message
                     + "\n这是检索工具本身的故障，不是查询词的问题。"
                     + "\n不要改用 read_knowledge 逐个猜小节名 —— 那样成功率极低。"
                     + "\n请先调 knowledge_status 看索引状态，必要时 knowledge_reindex 重建，"
                     + "或直接告知用户这个故障。";
            }
            if (hits.Count == 0)
                return "知识库里没有匹配 \"" + query + "\" 的内容。"
                     + "换个说法再试，或直接看系统提示词里的知识库目录判断该读哪一节。";

            var sb = new StringBuilder();
            sb.AppendLine("命中 " + hits.Count + " 节:");
            foreach (var h in hits)
            {
                sb.AppendLine();
                sb.Append("[").Append(h.Section.Ref).Append("]");
                if (!string.IsNullOrEmpty(h.Section.Path))
                    sb.Append("  (").Append(h.Section.Path).Append(")");
                sb.Append("  ").Append(h.How).AppendLine();
                foreach (var line in h.Lines) sb.AppendLine("  · " + line);
            }
            sb.AppendLine();
            sb.Append("用 read_knowledge(ref=\"…\") 取完整内容。");
            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class ReadKnowledgeTool : TxAgentToolBase
    {
        public override string Name { get { return "read_knowledge"; } }

        public override string Description
        {
            get
            {
                return "读取本地知识库某一节的完整内容。ref 格式:「文档名#小节名」。"
                     + "【小节名必须是确切存在的】不要靠猜 —— 猜不中会返回候选列表或让你去搜，"
                     + "反复试探小节名是最浪费轮次的做法。"
                     + "不知道叫什么就先 search_knowledge，拿到 ref 再来读。"
                     + "省略 #小节名 读整篇，大文档只会返回目录。"
                     + "【凡是知识库里写了的事，以知识库为准，不要凭训练记忆作答】—— "
                     + "这些是用户自备的规范/约定/内部资料，你的通用知识里没有。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"ref\": { \"type\": \"string\", \"description\": \"文档名#小节名，如 PS开发规范#坐标系约定\" }" +
                    "}, \"required\": [\"ref\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var reference = GetString(input, "ref", "");

            if (KnowledgeStore.IsEmpty)
                return "本地知识库为空。用户可以把 .md 文档放到 "
                     + KnowledgeStore.FolderPath() + " 目录下，重开对话即可生效。";

            string error;
            var text = KnowledgeStore.Read(reference, out error);
            if (error != null) return "Error: " + error;
            return text;
        }
    }
}
