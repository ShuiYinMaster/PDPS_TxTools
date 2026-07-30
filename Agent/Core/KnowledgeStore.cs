// TxTools.Agent / Core / KnowledgeStore.cs
//
// 用户自备 Markdown 文档的知识库。
//
// 放置位置:  {插件目录}\memory\knowledge\*.md   (不可写则回退 %LOCALAPPDATA%)
// 用户直接把 md 丢进去即可,不需要任何导入步骤。
//
// ── 为什么是"目录常驻 + 按需取节",不是别的两种做法 ──
//
//   整篇注入系统提示词:文档一大就把上下文吃光,而且大部分内容和当前问题无关。
//   纯检索(只给 search 工具):模型不知道知识库里有什么,压根想不到去搜 ——
//     这是知识库最常见的失败模式,建了没人用。
//
//   所以:把每份文档的标题 + 小节标题(几百 token)固定注入系统提示词,
//   模型看得见"有哪些资料、大致讲什么",需要细节时再 read_knowledge 取那一节。
//
// ── 分节 ──
//   按 Markdown 的 ## / ### 标题切块。一节就是一个可独立检索、可独立注入的单位。
//   没有标题的文档整篇算一节。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxTools.Agent.Core
{
    public sealed class KnowledgeSection
    {
        /// <summary>所属文档(不含扩展名的文件名,或 frontmatter 里的 title)。</summary>
        public string Doc { get; set; }
        /// <summary>小节标题。整篇无标题时为空。</summary>
        public string Heading { get; set; }
        /// <summary>标题层级:2=##,3=###。0 表示整篇。</summary>
        public int Level { get; set; }
        public string Body { get; set; }

        /// <summary>
        /// 祖先标题链,如 "工艺设计器简介 › 工艺设计器接口"。
        /// 【这个很关键】「已知问题」这种标题单看毫无信息量,
        /// 带上父级路径之后模型才知道它在讲什么,检索时也能命中上层主题词。
        /// </summary>
        public string Path { get; set; }

        /// <summary>同名标题的区分序号。文档里"已知问题"可能出现十几次。</summary>
        public int Ordinal { get; set; }

        /// <summary>检索/引用用的定位串。同名标题带序号,如 "PD手册#已知问题~3"。</summary>
        public string Ref
        {
            get
            {
                if (string.IsNullOrEmpty(Heading)) return Doc;
                var r = Doc + "#" + Heading;
                return Ordinal > 1 ? r + "~" + Ordinal : r;
            }
        }

        /// <summary>展示用的完整路径。</summary>
        public string FullTitle
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return Heading ?? Doc;
                return Path + " › " + Heading;
            }
        }
    }

    public sealed class KnowledgeDoc
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Path { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public List<KnowledgeSection> Sections { get; set; }

        public KnowledgeDoc() { Sections = new List<KnowledgeSection>(); }
    }

    public static class KnowledgeStore
    {
        private const string Folder = "knowledge";

        // 文件不常变,缓存住;按目录最后修改时间判断失效
        private static List<KnowledgeDoc> _cache;
        private static DateTime _cacheStamp;
        private static readonly object _sync = new object();

        public static string FolderPath()
        {
            return MdStore.FolderPath(Folder);
        }

        public static List<KnowledgeDoc> All()
        {
            lock (_sync)
            {
                var stamp = LatestWriteUtc();
                if (_cache != null && stamp == _cacheStamp) return _cache;

                _cache = LoadAll();
                _cacheStamp = stamp;
                return _cache;
            }
        }

        /// <summary>手动刷新(用户刚丢进新文件时)。</summary>
        public static void Invalidate()
        {
            lock (_sync) { _cache = null; }
        }

        public static bool IsEmpty { get { return All().Count == 0; } }

        // ── 目录:注入系统提示词的那一段 ──

        /// <summary>
        /// 生成知识库目录。只有标题,没有正文 —— 几百 token,可以常驻系统提示词。
        /// 返回空串表示没有任何文档,调用方应整段跳过,不要注入一个空标题。
        /// </summary>
        public static string BuildToc(int maxLinesPerDoc = 25)
        {
            var docs = All();
            if (docs.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("━━━ 本地知识库 ━━━");
            sb.AppendLine("以下是已收录资料的骨架。需要具体内容时:");
            sb.AppendLine("  · 大致知道在哪 → read_knowledge(ref=\"文档名#小节名\")");
            sb.AppendLine("  · 不确定在哪 → search_knowledge(query=\"…\") 先找");
            sb.AppendLine("骨架只列到章级，细分小节没有列出，不代表没有 —— 拿不准就搜。");

            foreach (var d in docs)
            {
                sb.AppendLine();
                sb.Append("【").Append(d.Title ?? d.Name).Append("】");
                if (!string.IsNullOrWhiteSpace(d.Description))
                    sb.Append("  ").Append(Clip(d.Description, 80));
                sb.Append("  (").Append(d.Sections.Count).AppendLine(" 节)");

                // 大部头只列顶层骨架 —— 950 个小节全列出来会把系统提示词撑爆,
                // 而且模型也读不完。列到章级足够它判断"这份文档管不管我这个问题"。
                var top = d.Sections
                    .Where(x => !string.IsNullOrEmpty(x.Heading) && x.Level <= 2)
                    .Select(x => x.Heading)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 顶层太少说明文档是平铺的,退一级
                if (top.Count <= 1)
                    top = d.Sections
                        .Where(x => !string.IsNullOrEmpty(x.Heading) && x.Level <= 3)
                        .Select(x => x.Heading)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                if (top.Count == 0) { sb.AppendLine("  (整篇，无小节)"); continue; }

                foreach (var h in top.Take(maxLinesPerDoc))
                    sb.Append("  · ").AppendLine(Clip(h, 60));

                if (top.Count > maxLinesPerDoc)
                    sb.AppendLine("  …(还有 " + (top.Count - maxLinesPerDoc) + " 章，用 search_knowledge 查)");
            }

            return sb.ToString();
        }

        // ── 检索 ──

        public sealed class Hit
        {
            public KnowledgeSection Section;
            public double Score;
            public List<string> Lines = new List<string>();
        }

        /// <summary>
        /// 关键字检索。标题命中权重远高于正文 —— 小节标题通常就是这一节讲什么，
        /// 命中标题基本等于命中主题。
        /// </summary>
        public static List<Hit> Search(string[] keywords, int max)
        {
            var hits = new List<Hit>();
            if (keywords == null || keywords.Length == 0) return hits;

            foreach (var d in All())
            {
                foreach (var sec in d.Sections)
                {
                    // 面包屑一起参与匹配:搜"工艺设计器 已知问题"时,
                    // 父级路径里的词也算命中,能把同名小节区分开
                    var headLower = ((sec.Heading ?? "") + " " + (sec.Path ?? "")
                                     + " " + (d.Title ?? d.Name)).ToLowerInvariant();
                    var bodyLower = (sec.Body ?? "").ToLowerInvariant();

                    double score = 0;
                    int matched = 0;

                    foreach (var kw in keywords)
                    {
                        bool any = false;
                        if (headLower.Contains(kw)) { score += 6; any = true; }

                        int c = Count(bodyLower, kw);
                        if (c > 0) { score += Math.Min(c, 5); any = true; }

                        if (any) matched++;
                    }

                    if (score <= 0) continue;

                    // 命中的关键字越全，越可能是想找的那节
                    score *= (1.0 + 0.5 * (matched - 1));

                    var h = new Hit { Section = sec, Score = score };
                    h.Lines = MatchingLines(sec.Body, keywords, 3);
                    hits.Add(h);
                }
            }

            return hits.OrderByDescending(x => x.Score).Take(max).ToList();
        }

        /// <summary>按 "文档#小节" 精确取一节。小节名可省略,则返回整篇。</summary>
        public static string Read(string reference, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(reference))
            {
                error = "ref 不能为空。格式:文档名 或 文档名#小节名。";
                return null;
            }

            var parts = reference.Split(new[] { '#' }, 2);
            var docName = parts[0].Trim();
            var heading = parts.Length > 1 ? parts[1].Trim() : null;

            var doc = All().FirstOrDefault(d =>
                string.Equals(d.Name, docName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Title, docName, StringComparison.OrdinalIgnoreCase));

            if (doc == null)
            {
                var names = All().Select(d => d.Title ?? d.Name).ToList();
                error = "找不到文档 \"" + docName + "\"。"
                      + (names.Count > 0 ? "已收录:" + string.Join("、", names) : "知识库为空。");
                return null;
            }

            if (string.IsNullOrEmpty(heading))
            {
                // 【整篇读要有闸门】实测一份手册 126 万字,整读会瞬间撑爆上下文。
                // 大文档改为只返回目录,让调用方挑具体小节。
                var totalChars = doc.Sections.Sum(x => (x.Body ?? "").Length);

                if (totalChars > MaxWholeDocChars)
                {
                    var toc = new StringBuilder();
                    toc.Append("【").Append(doc.Title ?? doc.Name)
                       .Append("】共 ").Append(doc.Sections.Count).Append(" 节 / ")
                       .Append(totalChars).AppendLine(" 字，过大，不返回全文。");
                    toc.AppendLine("以下是章级目录，挑一节用 ref=\"文档名#小节名\" 读；");
                    toc.AppendLine("不确定在哪就用 search_knowledge。");
                    toc.AppendLine();

                    var chapters = doc.Sections
                        .Where(x => !string.IsNullOrEmpty(x.Heading) && x.Level <= 2)
                        .Select(x => x.Heading)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(60).ToList();

                    if (chapters.Count == 0)
                        chapters = doc.Sections
                            .Where(x => !string.IsNullOrEmpty(x.Heading))
                            .Select(x => x.Heading)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(60).ToList();

                    foreach (var c in chapters) toc.Append("  · ").AppendLine(c);
                    return toc.ToString();
                }

                var sb = new StringBuilder();
                sb.AppendLine("【" + (doc.Title ?? doc.Name) + "】全文");
                foreach (var sec in doc.Sections)
                {
                    if (!string.IsNullOrEmpty(sec.Heading))
                        sb.AppendLine().AppendLine(new string('#', sec.Level) + " " + sec.Heading);
                    sb.AppendLine(sec.Body);
                }
                return sb.ToString();
            }

            // 精确匹配(含 ~序号 形式的 Ref)
            var target = doc.Sections.FirstOrDefault(x =>
                string.Equals(x.Ref, reference, StringComparison.OrdinalIgnoreCase))
                ?? doc.Sections.FirstOrDefault(x =>
                       string.Equals(x.Heading, heading, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                // 【绝不静默取第一个模糊命中】
                // 早先的做法是取第一个包含该串的小节 —— 于是 "#TCP" 返回了 "TCPF Speed"、
                // "#Tool" 返回了 "Graphic Viewer toolbar"。模型拿到看似成功的错误内容,
                // 不会意识到要重试,只会基于错的东西继续推理。
                var fuzzy = doc.Sections
                    .Where(x => x.Heading != null &&
                                x.Heading.IndexOf(heading, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(15).ToList();

                var sb2 = new StringBuilder();
                sb2.Append("文档 \"").Append(doc.Title ?? doc.Name)
                   .Append("\" 里没有名为 \"").Append(heading).AppendLine("\" 的小节。");

                if (fuzzy.Count == 1)
                {
                    // 只有一个模糊命中时直接给它 —— 没有歧义
                    target = fuzzy[0];
                }
                else if (fuzzy.Count > 1)
                {
                    sb2.AppendLine("名称包含它的小节有 " + fuzzy.Count + " 个，请用完整的 ref 重试:");
                    foreach (var f in fuzzy)
                        sb2.AppendLine("  " + f.Doc + "#" + f.Heading
                            + (f.Ordinal > 1 ? "~" + f.Ordinal : "")
                            + (string.IsNullOrEmpty(f.Path) ? "" : "    (" + f.Path + ")"));
                    error = sb2.ToString();
                    return null;
                }
                else
                {
                    // 一个都没有:别罗列几千个小节名,那对模型毫无用处 ——
                    // 直接引导它去用检索,那才是"不知道在哪"时该走的路
                    sb2.AppendLine("该文档共 " + doc.Sections.Count + " 节，无法逐一列出。");
                    sb2.Append("请改用 search_knowledge(query=\"").Append(heading)
                       .AppendLine("\") 先定位，拿到 ref 后再读。");
                    error = sb2.ToString();
                    return null;
                }
            }

            var head = string.IsNullOrEmpty(target.Path)
                ? target.Ref
                : target.Doc + " › " + target.Path + " › " + target.Heading;
            return "【" + head + "】\n" + target.Body;
        }

        // ── 加载与分节 ──

        private static List<KnowledgeDoc> LoadAll()
        {
            var list = new List<KnowledgeDoc>();
            try
            {
                var dir = FolderPath();
                if (!Directory.Exists(dir)) return list;

                foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var raw = File.ReadAllText(f, Encoding.UTF8);
                        var doc = MarkdownDoc.Parse(raw);

                        var kd = new KnowledgeDoc
                        {
                            Name = Path.GetFileNameWithoutExtension(f),
                            Title = doc.Get("title", Path.GetFileNameWithoutExtension(f)),
                            Description = doc.Get("description", ""),
                            Path = f,
                            ModifiedUtc = File.GetLastWriteTimeUtc(f)
                        };

                        kd.Sections = Split(kd.Title ?? kd.Name, doc.Body);
                        if (kd.Sections.Count > 0) list.Add(kd);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        /// <summary>整篇读取的字符上限。超过则只返回目录，避免一次读爆上下文。</summary>
        public static int MaxWholeDocChars = 30000;

        /// <summary>单节正文的字符上限。超了按段落切分,否则嵌入时会被截断、检索出来也没法读。</summary>
        public static int MaxSectionChars = 3000;

        /// <summary>标题里含这些词的小节整节跳过。</summary>
        private static readonly string[] SkipHeadings =
        {
            "目录索引", "目录", "index", "导航", "使用说明", "how to use",
            "版权", "copyright", "免责声明", "修订记录", "changelog"
        };

        /// <summary>
        /// 按 # / ## / ### 标题分节,并为每节记录祖先路径。
        ///
        /// 相比只认 ## 的做法,这里做了四件事:
        ///   1. H1 也参与分节 —— 大部头手册常用 H1 分卷/分章,忽略它会丢掉整层上下文;
        ///   2. 维护标题栈生成面包屑 —— 「已知问题」单看没信息量,带上父级才可检索;
        ///   3. 超长节按段落再切 —— 一节 100KB 的表格嵌入必被截断,检索出来也没法用;
        ///   4. 剥离锚点/HTML 标签等纯导航噪声 —— 它们进不了语义,只会稀释向量。
        ///
        /// 目录索引类小节整节跳过:它包含全文所有标题,任何关键词都能命中,
        /// 会稳定挤掉真正有内容的小节 —— 这是大部头知识库最典型的检索污染源。
        /// </summary>
        private static List<KnowledgeSection> Split(string docName, string body)
        {
            var result = new List<KnowledgeSection>();
            if (string.IsNullOrWhiteSpace(body)) return result;

            var lines = body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            var stack = new List<string>();     // 祖先标题栈,索引即层级-1
            string curHeading = null;
            int curLevel = 0;
            var buf = new StringBuilder();
            bool inFence = false;
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            Action flush = delegate
            {
                var text = Clean(buf.ToString());
                buf.Length = 0;

                if (string.IsNullOrEmpty(text)) return;
                if (curHeading != null && ShouldSkip(curHeading)) return;

                var path = curLevel > 1 && stack.Count > 0
                    ? string.Join(" › ", stack.Take(Math.Min(curLevel - 1, stack.Count)))
                    : "";

                foreach (var piece in SplitLong(text))
                {
                    // 【去重键必须和 Ref 的构成一致】Ref = 文档#标题(~序号),不含 path。
                    // 早先用 "标题|路径" 做键,结果「9.Robot › TCPF Speed」和
                    // 「3.Getting Started › TCPF Speed」序号都是 1,Ref 撞车,
                    // 检索时 ToDictionary 直接抛"已添加了具有相同键的项"。
                    var key = curHeading ?? "";
                    int n;
                    seen.TryGetValue(key, out n);
                    n++;
                    seen[key] = n;

                    result.Add(new KnowledgeSection
                    {
                        Doc = docName,
                        Heading = curHeading,
                        Path = path,
                        Level = curLevel,
                        Ordinal = n,
                        Body = piece
                    });
                }
            };

            foreach (var line in lines)
            {
                var t = line.TrimStart();

                if (t.StartsWith("```")) { inFence = !inFence; buf.AppendLine(line); continue; }
                if (inFence) { buf.AppendLine(line); continue; }

                int level = 0;
                while (level < t.Length && t[level] == '#') level++;

                if (level >= 1 && level <= 4 && level < t.Length && t[level] == ' ')
                {
                    flush();

                    var heading = Clean(t.Substring(level + 1)).Trim();

                    // 维护祖先栈:进入 level 层时,把 level 及更深的都弹掉
                    while (stack.Count >= level) stack.RemoveAt(stack.Count - 1);
                    while (stack.Count < level - 1) stack.Add("");
                    stack.Add(heading);

                    curHeading = heading;
                    curLevel = level;
                    continue;
                }

                buf.AppendLine(line);
            }

            flush();
            return result;
        }

        private static bool ShouldSkip(string heading)
        {
            var h = (heading ?? "").ToLowerInvariant();
            foreach (var k in SkipHeadings)
                if (h.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>剥离锚点、HTML 标签、内部跳转链接 —— 这些是导航结构,不是语义内容。</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // <a id="v1-s2"></a> 之类
            text = Regex.Replace(text, @"<a\s+id=[""'][^""']*[""']\s*>\s*</a>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</?[a-zA-Z][^>]{0,80}>", "");

            // [标题](#anchor) → 标题;外链保留文字
            text = Regex.Replace(text, @"\[([^\]]+)\]\(#[^)]*\)", "$1");

            return text.Trim();
        }

        /// <summary>超长正文按段落边界切分,尽量不切断表格行和代码块。</summary>
        private static IEnumerable<string> SplitLong(string text)
        {
            if (text.Length <= MaxSectionChars) { yield return text; yield break; }

            var lines = text.Split('\n');
            var buf = new StringBuilder();
            bool inFence = false;

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```")) inFence = !inFence;

                // 只在段落/表格行边界切,代码围栏内绝不切
                if (!inFence && buf.Length >= MaxSectionChars && line.Trim().Length == 0)
                {
                    yield return buf.ToString().Trim();
                    buf.Length = 0;
                    continue;
                }

                buf.AppendLine(line);

                // 表格没有空行,只能按行数硬切
                if (!inFence && buf.Length >= MaxSectionChars * 2)
                {
                    yield return buf.ToString().Trim();
                    buf.Length = 0;
                }
            }

            if (buf.Length > 0) yield return buf.ToString().Trim();
        }

        private static DateTime LatestWriteUtc()
        {
            try
            {
                var dir = FolderPath();
                if (!Directory.Exists(dir)) return DateTime.MinValue;

                var files = Directory.GetFiles(dir, "*.md");
                if (files.Length == 0) return DateTime.MinValue;

                var max = DateTime.MinValue;
                foreach (var f in files)
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > max) max = t;
                }
                // 文件数变化也要失效(删了一个但时间戳没变的情况)
                return max.AddTicks(files.Length);
            }
            catch { return DateTime.MinValue; }
        }

        // ── 辅助 ──

        private static List<string> MatchingLines(string body, string[] keywords, int max)
        {
            var outList = new List<string>();
            if (string.IsNullOrEmpty(body)) return outList;

            foreach (var raw in body.Split('\n'))
            {
                if (outList.Count >= max) break;
                var line = raw.Trim();
                if (line.Length < 4) continue;

                var lower = line.ToLowerInvariant();
                if (!keywords.Any(k => lower.Contains(k))) continue;

                outList.Add(Clip(line.TrimStart('#', '-', '*', ' '), 140));
            }
            return outList;
        }

        private static int Count(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
