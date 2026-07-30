// TxTools.Agent / Core / SnippetStore.cs
// 代码片段库：把摸索出的可用 run_csharp 代码持久化到 snippets.json，跨对话检索复用。
// 这是给 codegen 路径的"方法记忆"——摸清一次 API、存下可用代码，以后先查库、命中就直接用。
// 存储:memory/snippets/*.md,一条一文件(见 MdStore)。首次访问自动从 snippets.json 迁移。
//
// v2 增强：Tags(语义标签) + SuccessCount(复用计数) + AutoSaved(自动存) + Origin(来源)
// → 支持按标签语义检索、按复用频率排序、自动去重。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class Snippet
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Code { get; set; }

        /// <summary>
        /// 代码语言："csharp" 或 "python"。缺省按 csharp 处理（历史片段都是 C#）。
        /// 【为什么必须有】两种语言共用一个片段库，而取出的片段是要直接送进
        /// run_csharp 或 run_python 执行的。没有这个字段，模型只能靠看代码猜，
        /// 猜错的表现是编译报一堆莫名其妙的错 —— 而库里为什么会有 Python
        /// 这件事，模型根本无从得知。
        /// </summary>
        public string Lang { get; set; }

        public DateTime CreatedUtc { get; set; }
        /// <summary>语义标签(自动从代码提取)，如 robot,label,alignment,weld 等。</summary>
        public List<string> Tags { get; set; }
        /// <summary>被复用且【确实跑通】的次数。</summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// 复用后【执行失败】的次数。
        /// 【只记成功次数是不够的】用了 5 次成功 1 次的片段，和用 1 次成功 1 次的，
        /// 光看 SuccessCount 排序完全一样 —— 前者其实是在坑人。
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// 取出后【没能判定用没用上】的次数。
        /// 会话中断、模型取出后换了思路、执行代码与片段对不上 —— 都算这一类。
        /// 【它不进成功率的分母】把这些记成失败，等于让噪声决定片段的去留；
        /// 记成成功，就退回"取出即成功"的老毛病。所以单列一档，只用于诊断:
        /// 未判定远多于已判定，说明这条片段总被取出却从来没真正用上，
        /// 那是检索层把它推错了地方，不是片段本身有问题。
        /// </summary>
        public int UndecidedCount { get; set; }

        /// <summary>
        /// 成功率。没被复用过时返回 1，避免新片段一上来就被判死刑。
        /// </summary>
        public double SuccessRate
        {
            get
            {
                int total = SuccessCount + FailureCount;
                return total == 0 ? 1.0 : (double)SuccessCount / total;
            }
        }

        /// <summary>该片段被"见到"过几次（同类操作重复出现的次数）。见 PendingStore。</summary>
        public int SeenCount { get; set; }
        /// <summary>最后一次被 get_snippet 调用的时间。</summary>
        public DateTime LastUsedUtc { get; set; }
        /// <summary>来源：auto = 自动存，manual = AI 主动 save_snippet。</summary>
        public string Origin { get; set; }
        /// <summary>来自哪段对话(conv_xxx)，方便回溯。</summary>
        public string ConvId { get; set; }

        /// <summary>修订历史，每次打补丁追加一行。用于排查"这段代码怎么变成现在这样的"。</summary>
        public List<string> Revisions { get; set; }

        public Snippet()
        {
            Tags = new List<string>();
            Revisions = new List<string>();
            Lang = "csharp";
        }
    }

    public static class SnippetStore
    {
        private const string FileName = "snippets.json";

        private const string Folder = "snippets";

        public static List<Snippet> All()
        {
            EnsureMigrated();

            var list = new List<Snippet>();
            foreach (var doc in MdStore.LoadAll(Folder))
            {
                var sn = FromDoc(doc);
                if (sn != null) list.Add(sn);
            }
            return list;
        }

        // ── MD 映射 ──
        //  frontmatter 放元数据,正文放说明 + 代码围栏。
        //  这样一条 snippet 就是一份能直接读、直接改、直接复制的文档。

        private static MarkdownDoc ToDoc(Snippet s)
        {
            var doc = new MarkdownDoc();
            doc.Set("key", s.Name ?? "");
            doc.Set("name", s.Name ?? "");
            doc.SetList("tags", s.Tags);
            doc.Set("success_count", s.SuccessCount);
            doc.Set("failure_count", s.FailureCount);
            doc.Set("undecided_count", s.UndecidedCount);
            doc.Set("seen_count", s.SeenCount);
            doc.Set("lang", NormalizeLang(s.Lang));
            doc.Set("origin", s.Origin ?? "");
            doc.Set("conv_id", s.ConvId ?? "");
            doc.Set("created", s.CreatedUtc);
            if (s.LastUsedUtc != default(DateTime)) doc.Set("last_used", s.LastUsedUtc);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(s.Description))
            {
                sb.AppendLine(s.Description.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("```" + NormalizeLang(s.Lang));
            sb.AppendLine((s.Code ?? "").TrimEnd());
            sb.AppendLine("```");

            if (s.Revisions != null && s.Revisions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 修订历史");
                foreach (var r in s.Revisions) sb.Append("- ").AppendLine(r);
            }

            doc.Body = sb.ToString();

            return doc;
        }

        private static Snippet FromDoc(MarkdownDoc doc)
        {
            if (doc == null) return null;
            var name = doc.Get("name", doc.Get("key", ""));
            if (string.IsNullOrWhiteSpace(name)) return null;

            string desc, code;
            SplitBody(doc.Body, out desc, out code);

            return new Snippet
            {
                Name = name,
                Description = desc,
                Code = code,
                Tags = doc.GetList("tags"),
                SuccessCount = doc.GetInt("success_count", 0),
                FailureCount = doc.GetInt("failure_count", 0),
                UndecidedCount = doc.GetInt("undecided_count", 0),
                SeenCount = doc.GetInt("seen_count", 0),
                Lang = NormalizeLang(doc.Get("lang", "csharp")),
                Origin = doc.Get("origin", ""),
                ConvId = doc.Get("conv_id", ""),
                CreatedUtc = doc.GetDate("created"),
                LastUsedUtc = doc.GetDate("last_used")
            };
        }

        /// <summary>正文拆成"围栏前的说明"和"围栏里的代码"。没有围栏就整篇当代码。</summary>
        private static void SplitBody(string body, out string desc, out string code)
        {
            desc = "";
            code = "";
            if (string.IsNullOrEmpty(body)) return;

            int open = body.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) { code = body.Trim(); return; }

            desc = body.Substring(0, open).Trim();

            int lineEnd = body.IndexOf('\n', open);
            if (lineEnd < 0) return;

            int close = body.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (close < 0) close = body.Length;

            code = body.Substring(lineEnd + 1, close - lineEnd - 1).TrimEnd();
        }

        private static void EnsureMigrated()
        {
            MdStore.MigrateOnce(Folder, "snippets.json", json =>
            {
                var list = JsonConvert.DeserializeObject<List<Snippet>>(json);
                if (list == null) return;
                foreach (var s in list)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                    if (s.Tags == null) s.Tags = new List<string>();
                    WriteOne(s);
                }
            });
        }

        private static void WriteOne(Snippet s)
        {
            var slug = MdStore.UniqueSlug(Folder, MarkdownDoc.Slug(s.Name), s.Name);
            MdStore.Write(Folder, slug, ToDoc(s));
        }

        private static string SlugOf(string name)
        {
            return MdStore.UniqueSlug(Folder, MarkdownDoc.Slug(name), name);
        }

        public static Snippet Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return All().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按标签+关键字检索，返回按匹配度排序的片段(名称+说明，不含代码)。</summary>
        public static List<Snippet> FindByTagOrKeyword(string query)
        {
            var all = All();
            if (string.IsNullOrWhiteSpace(query)) return all.OrderByDescending(s => s.SuccessCount).ToList();

            var keywords = query.ToLowerInvariant()
                .Split(new[] { ' ', ',', '|', '/' }, StringSplitOptions.RemoveEmptyEntries);

            // 计分：每个匹配的 tag +3，匹配的 name/description 关键字 +1
            var scored = all.Select(s =>
            {
                int score = 0;
                foreach (var kw in keywords)
                {
                    if (s.Tags != null && s.Tags.Any(t => t.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0))
                        score += 3;
                    if (s.Name != null && s.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 1;
                    if (s.Description != null && s.Description.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 1;
                }
                // 基础分 = 复用次数权重（被用过多次的更可信）
                score += s.SuccessCount;
                return new { Snippet = s, Score = score };
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Snippet)
            .ToList();

            return scored;
        }

        /// <summary>按名新增或覆盖一条片段。</summary>
        public static void Upsert(Snippet snippet)
        {
            if (snippet == null || string.IsNullOrWhiteSpace(snippet.Name)) return;
            EnsureMigrated();
            if (snippet.CreatedUtc == default(DateTime)) snippet.CreatedUtc = DateTime.UtcNow;
            if (snippet.Tags == null) snippet.Tags = new List<string>();
            WriteOne(snippet);   // 一物一文件:只动这一条,不用整份读-改-写
        }

        // 【已删除 IncrementUsage】它被 get_snippet 用作"取出即成功"，
        // 而取出不等于用成功。归因改由 SnippetUsageLedger 在代码实际执行后完成。
        // 这里直接删掉而不是留一个空实现:留空实现会静默地让统计停止更新，
        // 编译错误反而会立刻指出所有还在用老语义的调用点。

        /// <summary>
        /// 记录一次【判定不出来】的取用。不进成功率分母，只作诊断。
        /// </summary>
        public static void RecordUndecided(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var s = Get(name);
            if (s == null) return;

            s.UndecidedCount++;
            s.LastUsedUtc = DateTime.UtcNow;
            WriteOne(s);
        }

        /// <summary>
        /// 记录一次复用结果。
        /// 【失败也要记】只记成功的话，一个成功率 20% 的片段会因为被反复尝试
        /// 而排到前面，反过来坑更多人。
        /// </summary>
        public static void RecordOutcome(string name, bool success)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var s = Get(name);
            if (s == null) return;

            if (success) s.SuccessCount++;
            else s.FailureCount++;

            s.LastUsedUtc = DateTime.UtcNow;
            WriteOne(s);
        }

        /// <summary>
        /// 给已有片段打补丁。只改需要改的那一段，保留其余部分与统计。
        /// oldText 必须唯一命中，理由和 code_edit 一样:命中 0 次说明记错了，
        /// 多次说明定位不够具体，两种都必须让调用方重来而不是替它选。
        /// </summary>
        public static string Patch(string name, string oldText, string newText, string reason)
        {
            if (string.IsNullOrWhiteSpace(name)) return "name 不能为空。";
            if (string.IsNullOrEmpty(oldText)) return "old_text 不能为空。";

            var s = Get(name);
            if (s == null) return "找不到片段 \"" + name + "\"。";

            var code = (s.Code ?? "").Replace("\r\n", "\n");
            var oldN = oldText.Replace("\r\n", "\n");

            int count = 0, i = 0;
            while ((i = code.IndexOf(oldN, i, StringComparison.Ordinal)) >= 0) { count++; i += oldN.Length; }

            if (count == 0)
                return "old_text 在该片段里找不到。先用 get_snippet 读出原文照抄，不要凭记忆写。";
            if (count > 1)
                return "old_text 在该片段里出现 " + count + " 次，无法确定改哪一处。请多带几行上下文使其唯一。";

            int at = code.IndexOf(oldN, StringComparison.Ordinal);
            s.Code = code.Substring(0, at) + (newText ?? "").Replace("\r\n", "\n")
                   + code.Substring(at + oldN.Length);

            if (s.Revisions == null) s.Revisions = new List<string>();
            s.Revisions.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  "
                + (string.IsNullOrWhiteSpace(reason) ? "(未说明原因)" : reason.Trim()));

            // 打过补丁说明之前是错的,失败计数清零重新观察 ——
            // 否则旧账会一直压着修好后的版本
            s.FailureCount = 0;

            WriteOne(s);
            return "已更新片段 \"" + s.Name + "\"。修订记录: " + s.Revisions[s.Revisions.Count - 1];
        }

        /// <summary>
        /// 语言名归一。只认 csharp / python 两种，其余一律当 csharp ——
        /// 【不引入"未知"这一档】未知语言的片段取出来照样没法执行，
        /// 多一档只会让每个消费方都要处理一种它不知道怎么办的情况。
        /// </summary>
        public static string NormalizeLang(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "csharp";
            var t = lang.Trim().ToLowerInvariant();
            if (t == "python" || t == "py" || t == "ironpython") return "python";
            return "csharp";
        }

        /// <summary>
        /// 检测是否已有非常相似的片段（避免自动存重复代码）。
        /// 【必须按语言隔离】C# 和 Python 写同一件事，去掉字面量后行结构可能高度相似，
        /// 不隔离就会出现"已经有一条了"从而漏存另一种语言的版本。
        /// </summary>
        public static bool HasSimilarCode(string code, double threshold, string lang)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            var want = NormalizeLang(lang);
            var all = All();
            var normalized = Normalize(code);
            foreach (var s in all)
            {
                if (string.IsNullOrWhiteSpace(s.Code)) continue;
                if (NormalizeLang(s.Lang) != want) continue;
                var sNorm = Normalize(s.Code);
                // 简单 Jaccard: 共有行 / 总行
                var linesA = normalized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var linesB = sNorm.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var setA = new HashSet<string>(linesA);
                var setB = new HashSet<string>(linesB);
                int inter = setA.Intersect(setB).Count();
                int union = setA.Union(setB).Count();
                if (union > 0 && (double)inter / union >= threshold)
                    return true;
            }
            return false;
        }

        /// <summary>取"最有价值的"N 条片段（按 SuccessCount + 最近使用排序），用于注入系统提示。</summary>
        /// <summary>
        /// 按"值不值得推荐"排序。
        /// 成功率是【乘数】不是加分项 —— 一个成功率 20% 的片段哪怕被用了 10 次，
        /// 也不该排在成功率 100% 用过 2 次的前面。
        /// </summary>
        public static List<Snippet> TopN(int n)
        {
            // 【bugfix】原式为
            //   (s.SuccessCount * 10 + (…).TotalDays < 7 ? 5 : 0) * s.SuccessRate
            // + 的优先级高于 <，整个括号被解析成 ((复用次数*10 + 天数) < 7) ? 5 : 0，
            // 复用次数权重被整个吃掉，排序退化成一个几乎恒为 0 的布尔值。
            // 不报错、不崩溃，只是注入系统提示的"最值得复用"长期是错的。
            return All()
                .Where(s => !IsUnreliable(s))
                .OrderByDescending(s =>
                    (s.SuccessCount * 10
                     + ((DateTime.UtcNow - s.CreatedUtc).TotalDays < 7 ? 5 : 0))
                    * s.SuccessRate)
                .Take(n)
                .ToList();
        }

        /// <summary>
        /// 明确不可靠的片段:用过至少 3 次且成功率低于四成。
        /// 这种不该再被推荐 —— 但也不删，留着让人看到并决定是修还是弃。
        /// </summary>
        public static bool IsUnreliable(Snippet s)
        {
            return s != null && (s.SuccessCount + s.FailureCount) >= 3 && s.SuccessRate < 0.4;
        }

        public static bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var s = Get(name);
            if (s == null) return false;
            return MdStore.Delete(Folder, SlugOf(s.Name));
        }

        // ── 标签提取：从 C# 代码中自动识别 PS SDK 类名 / 操作 ──

        /// <summary>从 run_csharp 代码中提取语义标签。</summary>
        public static List<string> ExtractTags(string code)
        {
            var tags = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) return tags;

            // PS SDK 类型 → 标签映射
            var typeTagMap = new Dictionary<string, string>
            {
                { "ITxRobot", "robot" }, { "TxRobot", "robot" },
                { "TxLabel", "label" }, { "TxLabelCreationData", "label" },
                { "ITxLocatableObject", "location" }, { "TxTransformation", "transform" },
                { "TxVector", "coordinate" },
                { "TxWeldPoint", "weld" }, { "ITxWeldPoint", "weld" },
                { "TxWeldLocationOperation", "weld" }, { "ITxWeldLocationOperation", "weld" },
                { "TxOperation", "operation" }, { "ITxOperation", "operation" },
                { "TxTypeFilter", "query" },
                { "TxSelection", "selection" },
                { "PhysicalRoot", "physical" }, { "OperationRoot", "operation" }, { "MfgRoot", "mfg" },
                { "TxApplication.ActiveDocument", "scene" },
                { "TxApplication.ActiveSelection", "selection" },
                { "DrivingJoints", "kinematics" }, { "TCPData", "tcp" },
                { "CollisionSet", "collision" }, { "CollisionDetectionData", "collision" },
                { "ExportData", "export" }, { "TxOlpOperationToSimulationOperator", "simulation" },
                { "ITxLeadingPart", "leadingpart" },
                { "GetAllDescendants", "traverse" }, { "GetObjectsByName", "find" },
                { "CreateLabel", "create" }, { "CreateObject", "create" },
                { "AbsoluteLocation", "location" },
                { "log(", "logging" }
            };

            foreach (var kv in typeTagMap)
            {
                if (code.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 && !tags.Contains(kv.Value))
                    tags.Add(kv.Value);
            }

            // 操作动词检测
            if (code.IndexOf("rename", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("rename");
            if (code.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("align");
            if (code.IndexOf("batch", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("batch");
            if (code.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("export");
            if (code.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("delete");
            if (code.IndexOf("create", StringComparison.OrdinalIgnoreCase) >= 0 && !tags.Contains("create")) tags.Add("create");
            if (code.IndexOf("simulate", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("simulation");
            if (code.IndexOf("inspect", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("inspect");
            if (code.IndexOf("scan", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("scan");

            // 常用 .NET 集合 → 标签
            if (code.IndexOf("Dictionary", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("dict");
            if (code.IndexOf("HashSet", StringComparison.OrdinalIgnoreCase) >= 0) tags.Add("dedup");

            // 最多保留 8 个标签，太多反而噪声
            if (tags.Count > 8) tags = tags.Take(8).ToList();
            return tags;
        }

        /// <summary>从代码首行 log 或关键操作生成自动片段名。</summary>
        public static string AutoName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "auto_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            // 优先取 log("=== ... ===") 里的标题
            foreach (var line in code.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("log(") || trimmed.StartsWith("print("))
                {
                    // 提取 log 里的字符串内容
                    int start = trimmed.IndexOf('"') + 1;
                    int end = trimmed.LastIndexOf('"');
                    if (start > 0 && end > start)
                    {
                        var content = trimmed.Substring(start, end - start)
                            .Replace("=", "").Replace("==", "").Trim();
                        // 截短到 40 字符
                        if (content.Length > 40) content = content.Substring(0, 40);
                        return "auto_" + content.Replace(' ', '_').Replace('/', '_');
                    }
                }
            }

            // 回退：取代码中第一个 meaningful 变量名
            var firstVar = code.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("var ") || l.StartsWith("string ") || l.StartsWith("int "))
                .Select(l => l.Split(new[] { ' ', '=' }, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1).FirstOrDefault())
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(firstVar))
                return "auto_" + firstVar;

            return "auto_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        /// <summary>从代码生成简短描述（取前两行非空非注释行）。</summary>
        public static string AutoDescription(string code, List<string> tags)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            var lines = code.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//") && !string.IsNullOrWhiteSpace(l))
                .Take(2)
                .ToList();
            var desc = string.Join("; ", lines);
            if (desc.Length > 80) desc = desc.Substring(0, 80) + "…";
            if (tags != null && tags.Count > 0)
                desc = "[" + string.Join(",", tags) + "] " + desc;
            return desc;
        }

        private static string Normalize(string code)
        {
            // 去注释、去空行、去 log 里的动态内容 → 做相似度比较
            var sb = new StringBuilder();
            foreach (var line in code.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.StartsWith("//")) continue;
                if (string.IsNullOrWhiteSpace(t)) continue;
                sb.AppendLine(t);
            }
            return sb.ToString();
        }

    }
}
