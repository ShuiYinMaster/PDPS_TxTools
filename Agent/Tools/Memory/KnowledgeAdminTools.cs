// TxTools.Agent / Tools / Memory / KnowledgeAdminTools.cs
//
// 知识库的运维工具:建索引、看状态、验证检索。
// 没有这两个工具的话,索引只能靠代码里手动调 BuildAsync,没法在对话里排查。

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class KnowledgeStatusTool : TxAgentToolBase
    {
        public override string Name { get { return "knowledge_status"; } }

        public override string Description
        {
            get
            {
                return "查看本地知识库与向量索引的状态:收录了哪些文档、分了多少节、"
                     + "嵌入器是谁、索引建到什么程度、有没有节漏掉。"
                     + "排查「搜不到东西」时先看这个。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get { return JObject.Parse("{ \"type\":\"object\", \"properties\":{} }"); }
        }

        public override string Execute(JObject input)
        {
            var sb = new StringBuilder();
            var docs = KnowledgeStore.All();

            sb.AppendLine("目录: " + KnowledgeStore.FolderPath());
            sb.AppendLine("文档: " + docs.Count + " 份，共 "
                + docs.Sum(d => d.Sections.Count) + " 节");

            if (docs.Count == 0)
            {
                sb.AppendLine();
                sb.Append("知识库为空。把 .md 文件放进上面的目录即可。");
                return sb.ToString();
            }

            sb.AppendLine();
            foreach (var d in docs)
            {
                var chars = d.Sections.Sum(x => (x.Body ?? "").Length);
                sb.AppendLine("  " + (d.Title ?? d.Name)
                    + "  — " + d.Sections.Count + " 节 / " + chars + " 字");
            }

            sb.AppendLine();
            var emb = KnowledgeIndex.Embedder;
            if (emb == null)
            {
                sb.AppendLine("嵌入器: 未配置 —— 检索走纯关键字。");
                sb.Append("要启用语义检索，启动时设置 KnowledgeIndex.Embedder。");
                return sb.ToString();
            }

            sb.AppendLine("嵌入器: " + emb.Id + "  (维度 " + emb.Dimension
                + ", 批大小 " + emb.BatchSize + ")");

            var idx = Path.Combine(KnowledgeStore.FolderPath(), "vectors.json");
            if (!File.Exists(idx))
            {
                sb.Append("索引: 尚未建立。调 knowledge_reindex 建一次。");
                return sb.ToString();
            }

            try
            {
                var raw = File.ReadAllText(idx, Encoding.UTF8);
                var f = Newtonsoft.Json.JsonConvert.DeserializeObject<IndexFile>(raw);

                int total = docs.Sum(d => d.Sections.Count);
                int have = f.Vectors != null ? f.Vectors.Count : 0;

                sb.AppendLine("索引: " + have + " / " + total + " 节"
                    + "  (" + (raw.Length / 1024) + " KB)");
                sb.AppendLine("建立于: " + f.BuiltUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("索引嵌入器: " + f.EmbedderId
                    + (string.Equals(f.EmbedderId, emb.Id, StringComparison.Ordinal)
                        ? "  ✅ 匹配"
                        : "  ⚠ 与当前嵌入器不一致，向量检索会被跳过，需 knowledge_reindex 重建"));

                if (have < total)
                    sb.AppendLine("⚠ 有 " + (total - have)
                        + " 节没有向量(可能是嵌入失败或文档新增)，重建一次即可。");
            }
            catch (Exception ex)
            {
                sb.AppendLine("索引文件读取失败: " + ex.Message);
            }

            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class KnowledgeReindexTool : TxAgentToolBase, ITxOffUiThreadTool
    {
        public override string Name { get { return "knowledge_reindex"; } }

        public override string Description
        {
            get
            {
                return "重建知识库的向量索引。增量:内容没变的小节复用旧向量，只嵌入新增/改动的。"
                     + "首次使用、新增文档、或 knowledge_status 提示不匹配时调用。"
                     + "大知识库首次建索引可能要几十秒。";
            }
        }

        /// <summary>只写索引文件，不改场景也不改文档。</summary>
        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"force\": { \"type\":\"boolean\", \"description\":\"true 则丢弃现有索引全量重建，默认 false(增量)\" }" +
                    "}, \"required\":[] }");
            }
        }

        public override string Execute(JObject input)
        {
            if (KnowledgeIndex.Embedder == null)
            {
                KnowledgeIndex.Embedder = KnowledgeIndex.AutoSelect();
                if (KnowledgeIndex.Embedder == null)
                    return "Error: 没有可用的嵌入器。需要配置百炼(qwen) 的 API key，"
                         + "或在 models\\embedding\\ 放置本地 ONNX 模型。";
            }

            bool force = input != null && input["force"] != null
                         && input["force"].Type == JTokenType.Boolean && (bool)input["force"];

            if (force)
            {
                try
                {
                    var p = Path.Combine(KnowledgeStore.FolderPath(), "vectors.json");
                    if (File.Exists(p)) File.Delete(p);
                    KnowledgeIndex.Invalidate();
                }
                catch (Exception ex) { return "Error: 清除旧索引失败 - " + ex.Message; }
            }

            KnowledgeStore.Invalidate();   // 文档可能刚改过

            var log = new StringBuilder();
            try
            {
                var n = KnowledgeIndex
                    .BuildAsync(CancellationToken.None, m => log.AppendLine(m))
                    .GetAwaiter().GetResult();

                if (n < 0) return "Error: 没有可用的嵌入器。";

                var sb = new StringBuilder();
                sb.AppendLine("索引重建完成。");
                if (log.Length > 0) sb.Append(log);
                sb.AppendLine();
                sb.Append("用 knowledge_status 核对条数，或直接 search_knowledge 试一下。");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "Error: 重建失败 - " + ex.GetType().Name + ": " + ex.Message
                     + (log.Length > 0 ? "\n\n已完成部分:\n" + log : "");
            }
        }
    }
}
