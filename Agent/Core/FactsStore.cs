// TxTools.Agent / Core / FactsStore.cs
// 跨对话保留的用户偏好、场景常量、验证过的事实。
// 来源两种：
//   (1) LessonExtractor 在对话末萃取产生
//   (2) AI 主动调用 add_fact 工具 (或用户点 UI 上的 "记住这个" 按钮)
// 用途：
//   BuildSystemPromptWithMemory 每轮把 TopN 注入 system prompt 头部,
//   让模型把它们当作"对话默认前提",不再重复问相同的偏好。
//
// 与 GotchasStore 的分工：
//   Facts = 用户/场景层面的"是什么" (稳定成立的正面知识)
//   Gotchas = API/语法层面的"不是什么" (报错→正解)
// 两者并列注入,不合并,方便老化和维护。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class Fact
    {
        public string Id { get; set; }
        /// <summary>事实内容,简明陈述,如 "用户偏好复用现有工具而非 run_csharp"。</summary>
        public string Content { get; set; }
        /// <summary>preference / scene_constant / api_fact / workflow / misc</summary>
        public string Category { get; set; }
        public DateTime CreatedUtc { get; set; }
        /// <summary>最近一次被"重复确认"的时间(相似事实被 Add 时刷新)。</summary>
        public DateTime? LastConfirmedUtc { get; set; }
        public string ConvId { get; set; }
        /// <summary>被 list_facts / prompt 注入后引用的次数,用于排序。</summary>
        public int UsedCount { get; set; }
    }

    public static class FactsStore
    {
        private const string Folder = "facts";

        public static List<Fact> All()
        {
            EnsureMigrated();

            var list = new List<Fact>();
            foreach (var doc in MdStore.LoadAll(Folder))
            {
                var f = FromDoc(doc);
                if (f != null) list.Add(f);
            }
            return list;
        }

        // ── MD 映射 ──
        //  事实本身是一句话,放正文;类别/计数/时间放 frontmatter。
        //  文件名取自内容前若干字,目录扫一眼就知道记住了些什么。

        private static MarkdownDoc ToDoc(Fact f)
        {
            var doc = new MarkdownDoc();
            doc.Set("key", f.Id ?? "");
            doc.Set("id", f.Id ?? "");
            doc.Set("category", f.Category ?? "misc");
            doc.Set("used_count", f.UsedCount);
            doc.Set("conv_id", f.ConvId ?? "");
            doc.Set("created", f.CreatedUtc);
            if (f.LastConfirmedUtc.HasValue) doc.Set("last_confirmed", f.LastConfirmedUtc.Value);
            doc.Body = (f.Content ?? "").Trim();
            return doc;
        }

        private static Fact FromDoc(MarkdownDoc doc)
        {
            if (doc == null) return null;
            var content = (doc.Body ?? "").Trim();
            if (content.Length == 0) return null;

            var lc = doc.GetDate("last_confirmed");

            return new Fact
            {
                Id = doc.Get("id", doc.Get("key", "")),
                Content = content,
                Category = doc.Get("category", "misc"),
                UsedCount = doc.GetInt("used_count", 0),
                ConvId = doc.Get("conv_id", ""),
                CreatedUtc = doc.GetDate("created"),
                LastConfirmedUtc = lc == default(DateTime) ? (DateTime?)null : lc
            };
        }

        private static void EnsureMigrated()
        {
            MdStore.MigrateOnce(Folder, "facts.json", json =>
            {
                var list = JsonConvert.DeserializeObject<List<Fact>>(json);
                if (list == null) return;
                foreach (var f in list)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.Content)) continue;
                    if (string.IsNullOrEmpty(f.Id))
                        f.Id = "f_" + Math.Abs(f.Content.GetHashCode()).ToString("D10");
                    WriteOne(f);
                }
            });
        }

        private static void WriteOne(Fact f)
        {
            MdStore.Write(Folder, SlugOf(f), ToDoc(f));
        }

        /// <summary>文件名用内容前 40 字,可读;撞名时 MdStore 会按 id 补后缀。</summary>
        private static string SlugOf(Fact f)
        {
            var basis = (f.Content ?? f.Id ?? "fact").Trim();
            if (basis.Length > 40) basis = basis.Substring(0, 40);
            return MdStore.UniqueSlug(Folder, MarkdownDoc.Slug(basis), f.Id ?? basis);
        }

        /// <summary>
        /// 追加事实(带去重):内容相似度 Jaccard≥0.7 视为同一条,仅刷新 LastConfirmedUtc,
        /// 避免多次萃取产生重复记录。返回落库(或已存在)的条目。
        /// </summary>
        public static Fact Add(string content, string category, string convId)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            content = content.Trim();

            var all = All();
            var similar = FindSimilar(all, content, 0.7);
            if (similar != null)
            {
                similar.LastConfirmedUtc = DateTime.UtcNow;
                WriteOne(similar);
                return similar;
            }

            var f = new Fact
            {
                Id = "f_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"),
                Content = content,
                Category = string.IsNullOrEmpty(category) ? "misc" : category,
                CreatedUtc = DateTime.UtcNow,
                LastConfirmedUtc = DateTime.UtcNow,
                ConvId = convId
            };
            WriteOne(f);
            return f;
        }

        public static bool Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var f = All().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (f == null) return false;
            return MdStore.Delete(Folder, SlugOf(f));
        }

        public static List<Fact> FindByKeyword(string query)
        {
            var all = All();
            if (string.IsNullOrWhiteSpace(query)) return all;
            var keywords = query.ToLowerInvariant()
                .Split(new[] { ' ', ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries);
            return all.Where(f => keywords.Any(kw =>
                f.Content != null && f.Content.ToLowerInvariant().Contains(kw))).ToList();
        }

        /// <summary>取 Top-N 用于 system prompt 注入。类别加权 + 最近确认时间衰减。</summary>
        public static List<Fact> TopN(int n)
        {
            return All()
                .Where(f => f.Category == "preference" || f.Category == "api_fact")
                .GroupBy(f => (f.Content ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(f => Score(f)).First())
                .OrderByDescending(f => Score(f))
                .Take(n)
                .ToList();
        }

        public static void IncrementUsed(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var all = All();
            var f = all.FirstOrDefault(x => x.Id == id);
            if (f == null) return;
            f.UsedCount++;
            WriteOne(f);
        }

        // ── 排序打分：类别 + 最近确认 + 引用次数 ──

        private static double Score(Fact f)
        {
            double s = 0;
            // 偏好 > workflow > scene_constant > api_fact > misc
            switch ((f.Category ?? "").ToLowerInvariant())
            {
                case "preference":     s += 10; break;
                case "workflow":       s += 6;  break;
                case "scene_constant": s += 4;  break;
                case "api_fact":       s += 3;  break;
                default:               s += 1;  break;
            }
            s += f.UsedCount;
            if (f.LastConfirmedUtc.HasValue)
            {
                var days = (DateTime.UtcNow - f.LastConfirmedUtc.Value).TotalDays;
                if (days < 7) s += 3;
                else if (days < 30) s += 1;
                else if (days > 180) s -= 2;
            }
            return s;
        }

        private static Fact FindSimilar(List<Fact> all, string content, double threshold)
        {
            var target = Tokenize(content);
            if (target.Count == 0) return null;
            foreach (var f in all)
            {
                var set = Tokenize(f.Content);
                if (set.Count == 0) continue;
                int inter = set.Intersect(target).Count();
                int union = set.Union(target).Count();
                if (union > 0 && (double)inter / union >= threshold) return f;
            }
            return null;
        }

        /// <summary>粗糙分词：按标点/空格切,保留长度≥2 的 token。中英通用,不做词形还原。</summary>
        private static HashSet<string> Tokenize(string s)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(s)) return set;
            foreach (var t in s.ToLowerInvariant().Split(
                new[] { ' ', ',', '。', '，', '、', ':', '：', ';', ';', '.', '/', '\\', '(', ')', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries))
                if (t.Length >= 2) set.Add(t);
            return set;
        }

    }
}
