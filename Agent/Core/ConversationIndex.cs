// TxTools.Agent / Core / ConversationIndex.cs
// 对话的 Markdown 摘要索引。
//
// 解决的问题:
//   原 search_past_conversations 每次搜索都把【全部对话的完整 JSON】读进内存逐条扫,
//   几十个对话就是几十万字。既慢,又只能做关键词 Contains ——
//   "插入焊枪 CGR" 搜不到当初用 AddComponentsFromFiles 描述的那次操作。
//
// 做法:
//   每个对话额外生成一份 conversations/index/{id}.md,几百字,语义密度高:
//     frontmatter 放结构化线索(涉及的工具、PS 类型、附件名、轮数)
//     正文放逐轮摘要(用户问了什么 → 调了哪些工具 → 结论是什么)
//   检索先扫这些小文件,命中后再按需读 JSON 取细节。
//
// 摘要用规则拼装,不调 LLM —— 免费、零延迟、每次保存增量重建。
// 真需要更好的语义摘要,可以在此之上再挂一层可选的 LLM 精炼。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxTools.Agent.Core
{
    public sealed class ConvIndexHit
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public double Score { get; set; }
        public List<string> Snippets { get; set; }
        public List<string> Tools { get; set; }

        public ConvIndexHit()
        {
            Snippets = new List<string>();
            Tools = new List<string>();
        }
    }

    public static class ConversationIndex
    {
        private const string IndexFolder = "index";

        /// <summary>正文最多收录的轮数,超出只保留最近的 —— 摘要要小才有意义。</summary>
        private const int MaxTurnsInBody = 40;

        // ── 生成 ──

        /// <summary>为一个对话(重新)生成摘要索引。内容没变化时跳过写盘。</summary>
        public static void Rebuild(Conversation conv)
        {
            if (conv == null || string.IsNullOrEmpty(conv.Id)) return;
            if (conv.Messages == null || conv.Messages.Count == 0) return;

            try
            {
                var path = IndexPath(conv.Id);

                // 消息数没变就不重写(每轮都重建整份 MD 没必要)
                var old = MarkdownDoc.Load(path);
                if (old != null && old.GetInt("messages", -1) == conv.Messages.Count) return;

                var doc = BuildDoc(conv);
                doc.SaveTo(path);
            }
            catch { /* 索引是加速层,失败不影响主流程 */ }
        }

        public static void Delete(string convId)
        {
            try
            {
                var p = IndexPath(convId);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        /// <summary>给尚无索引的历史对话补建。首次搜索时调用一次即可。</summary>
        public static int EnsureAll()
        {
            int built = 0;
            try
            {
                foreach (var meta in ConversationStore.List())
                {
                    var p = IndexPath(meta.Id);
                    if (File.Exists(p)) continue;

                    var conv = ConversationStore.Load(meta.Id);
                    if (conv == null) continue;

                    Rebuild(conv);
                    built++;
                }
            }
            catch { }
            return built;
        }

        private static MarkdownDoc BuildDoc(Conversation conv)
        {
            var doc = new MarkdownDoc();

            var tools = new List<string>();
            var types = new List<string>();
            var files = new List<string>();
            var turns = new List<Turn>();

            Turn cur = null;

            foreach (var m in conv.Messages)
            {
                if (m == null) continue;
                if (m.Role == "system") continue;

                if (m.Role == "user")
                {
                    // 压缩摘要那条不算真实用户轮次
                    if (m.Content != null && m.Content.StartsWith("[前序对话摘要]", StringComparison.Ordinal))
                        continue;

                    cur = new Turn { Question = FirstMeaningfulLine(m.Content) };
                    turns.Add(cur);
                    CollectAttachments(m.Content, files);
                    CollectTypes(m.Content, types);
                    continue;
                }

                if (cur == null)
                {
                    cur = new Turn { Question = "(无用户消息)" };
                    turns.Add(cur);
                }

                if (m.Role == "assistant")
                {
                    if (m.ToolCalls != null)
                    {
                        foreach (var tc in m.ToolCalls)
                        {
                            var n = tc != null && tc.Function != null ? tc.Function.Name : null;
                            if (string.IsNullOrEmpty(n)) continue;
                            cur.Tools.Add(n);
                            if (!tools.Contains(n)) tools.Add(n);
                            if (tc.Function != null) CollectTypes(tc.Function.Arguments, types);
                        }
                    }
                    // 不带工具调用的助手文本 = 该轮结论
                    if ((m.ToolCalls == null || m.ToolCalls.Count == 0)
                        && !string.IsNullOrWhiteSpace(m.Content))
                    {
                        cur.Conclusion = m.Content;
                    }
                    CollectTypes(m.Content, types);
                }
                else if (m.Role == "tool")
                {
                    CollectTypes(m.Content, types);
                    if (!string.IsNullOrEmpty(m.Content)
                        && m.Content.IndexOf("【执行失败】", StringComparison.Ordinal) >= 0)
                        cur.HadFailure = true;
                }
            }

            doc.Set("id", conv.Id);
            doc.Set("title", conv.Title ?? "");
            doc.Set("created", conv.CreatedUtc);
            doc.Set("updated", conv.UpdatedUtc);
            doc.Set("messages", conv.Messages.Count);
            doc.Set("turns", turns.Count);
            doc.SetList("tools", tools.Take(25));
            doc.SetList("types", types.Take(25));
            if (files.Count > 0) doc.SetList("files", files.Take(10));

            var sb = new StringBuilder();
            var shown = turns.Count > MaxTurnsInBody
                ? turns.Skip(turns.Count - MaxTurnsInBody).ToList()
                : turns;

            if (turns.Count > shown.Count)
                sb.AppendLine("_(略去较早的 " + (turns.Count - shown.Count) + " 轮)_").AppendLine();

            int idx = turns.Count - shown.Count;
            foreach (var t in shown)
            {
                idx++;
                sb.AppendLine("## " + idx + ". " + Clip(t.Question, 90));
                if (t.Tools.Count > 0)
                    sb.AppendLine("- 工具: " + string.Join(" → ", t.Tools.Take(12)));
                if (t.HadFailure)
                    sb.AppendLine("- 过程中有工具执行失败");
                if (!string.IsNullOrWhiteSpace(t.Conclusion))
                    sb.AppendLine("- 结论: " + Clip(t.Conclusion, 200));
                sb.AppendLine();
            }

            doc.Body = sb.ToString();
            return doc;
        }

        private sealed class Turn
        {
            public string Question;
            public string Conclusion;
            public bool HadFailure;
            public readonly List<string> Tools = new List<string>();
        }

        // ── 检索 ──

        /// <summary>
        /// 在索引里搜。命中的 frontmatter 字段权重高于正文 ——
        /// 工具名和 PS 类型名是最强的语义锚点。
        /// </summary>
        public static List<ConvIndexHit> Search(string[] keywords, string excludeId, int max)
        {
            var hits = new List<ConvIndexHit>();
            if (keywords == null || keywords.Length == 0) return hits;

            try
            {
                var dir = IndexFolderPath();
                if (!Directory.Exists(dir)) return hits;

                foreach (var f in Directory.GetFiles(dir, "*.md"))
                {
                    var doc = MarkdownDoc.Load(f);
                    if (doc == null) continue;

                    var id = doc.Get("id", Path.GetFileNameWithoutExtension(f));
                    if (!string.IsNullOrEmpty(excludeId)
                        && string.Equals(id, excludeId, StringComparison.Ordinal)) continue;

                    var title = doc.Get("title", "");
                    var metaBlob = string.Join(" ",
                        doc.Get("tools", ""), doc.Get("types", ""), doc.Get("files", ""));
                    var body = doc.Body ?? "";

                    var titleLower = title.ToLowerInvariant();
                    var metaLower = metaBlob.ToLowerInvariant();
                    var bodyLower = body.ToLowerInvariant();

                    double score = 0;
                    int matched = 0;

                    foreach (var kw in keywords)
                    {
                        bool any = false;
                        if (titleLower.Contains(kw)) { score += 5; any = true; }
                        if (metaLower.Contains(kw)) { score += 3; any = true; }

                        int c = CountOccurrences(bodyLower, kw);
                        if (c > 0) { score += Math.Min(c, 5); any = true; }

                        if (any) matched++;
                    }

                    if (score <= 0) continue;

                    // 命中的关键字越全,越可能是想找的那次
                    score *= (1.0 + 0.5 * (matched - 1));

                    var hit = new ConvIndexHit
                    {
                        Id = id,
                        Title = title,
                        UpdatedUtc = doc.GetDate("updated"),
                        Score = score,
                        Tools = doc.GetList("tools")
                    };
                    hit.Snippets = ExtractSnippets(body, keywords, 3);
                    hits.Add(hit);
                }
            }
            catch { }

            return hits.OrderByDescending(h => h.Score).Take(max).ToList();
        }

        /// <summary>取某个对话的完整摘要正文(供 read_past_conversation 用)。</summary>
        public static MarkdownDoc Get(string convId)
        {
            return MarkdownDoc.Load(IndexPath(convId));
        }

        // ── 辅助 ──

        private static readonly Regex TxTypeRe =
            new Regex(@"\b(I?Tx[A-Z][A-Za-z0-9_]{2,})\b", RegexOptions.Compiled);

        private static void CollectTypes(string text, List<string> into)
        {
            if (string.IsNullOrEmpty(text) || into.Count >= 25) return;
            foreach (Match m in TxTypeRe.Matches(text))
            {
                var v = m.Groups[1].Value;
                if (!into.Contains(v)) into.Add(v);
                if (into.Count >= 25) return;
            }
        }

        private static void CollectAttachments(string content, List<string> into)
        {
            if (string.IsNullOrEmpty(content)) return;
            if (content.IndexOf("[已附加文件]", StringComparison.Ordinal) != 0) return;

            foreach (var raw in content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = Regex.Match(line, @"^\s*\d+\)\s+(.*?)\s+\(id=");
                if (!m.Success) continue;
                var name = m.Groups[1].Value.Trim();
                if (name.Length > 0 && !into.Contains(name)) into.Add(name);
            }
        }

        private static string FirstMeaningfulLine(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "(空)";

            // 附件前缀不是用户真正说的话,跳到正文
            const string footer = "[如需精读完整内容,调用 read_uploaded_file(file_id=...)]";
            int f = content.IndexOf(footer, StringComparison.Ordinal);
            if (f >= 0) content = content.Substring(f + footer.Length);

            foreach (var raw in content.Split('\n'))
            {
                var t = raw.Trim();
                if (t.Length > 0) return t;
            }
            return "(仅附件)";
        }

        private static List<string> ExtractSnippets(string body, string[] keywords, int max)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(body)) return result;

            foreach (var raw in body.Split('\n'))
            {
                if (result.Count >= max) break;
                var line = raw.Trim();
                if (line.Length < 4) continue;

                var lower = line.ToLowerInvariant();
                if (!keywords.Any(k => lower.Contains(k))) continue;

                line = line.TrimStart('#', '-', ' ', '_');
                result.Add(Clip(line, 140));
            }
            return result;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ── 路径 ──

        private static string IndexFolderPath()
        {
            var dir = Path.Combine(ConversationStore.FolderPathPublic(), IndexFolder);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string IndexPath(string convId)
        {
            return Path.Combine(IndexFolderPath(), MarkdownDoc.Slug(convId) + ".md");
        }
    }
}
