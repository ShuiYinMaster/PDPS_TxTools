// TxTools.Agent / Core / PendingSnippetStore.cs
//
// 片段的"待定池"——重复出现够次数才固化成正式片段。
//
// ── 为什么不再"一次成功就存" ──
//   原来 run_csharp 成功就自动存 snippet，问题是绝大多数代码是一次性的:
//   查个数量、看个坐标、改一个对象。这些存下来只会把库堆满，
//   反而稀释了真正有复用价值的那几条 —— 检索时噪音盖过信号。
//
//   改成:成功的代码先进待定池，同类操作【重复出现 N 次】才升格为正式片段。
//   重复本身就是"这个操作值得固化"的最强信号，比任何启发式判断都准。
//
// ── 怎么判断"同类" ──
//   按代码结构指纹，不是逐字比对 —— 同一类操作每次的对象名、坐标值都不同，
//   逐字比对永远命中不了。指纹只保留 API 调用序列和控制结构，
//   把字面量、标识符名都抹掉。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxTools.Agent.Core
{
    public sealed class PendingSnippet
    {
        /// <summary>结构指纹，同类操作共享同一个。</summary>
        public string Fingerprint { get; set; }

        /// <summary>见过几次。达到阈值就升格。</summary>
        public int SeenCount { get; set; }

        /// <summary>最近一次的代码。升格时用它 —— 后写的通常比先写的成熟。</summary>
        public string LastCode { get; set; }

        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string ConvId { get; set; }
    }

    public static class PendingSnippetStore
    {
        private const string Folder = "pending";

        /// <summary>
        /// 重复几次算值得固化。
        /// 2 次太松（连着调两遍很常见），4 次太严（等不到）。3 次是实用折中。
        /// </summary>
        public static int PromoteThreshold = 3;

        /// <summary>待定项的保留天数。太久没再出现说明是一次性的，清掉。</summary>
        public static int ExpireDays = 30;

        /// <summary>
        /// 【异步版，工具执行路径应该用这个】
        ///
        /// Observe 内部要遍历待定池、对全部正式片段算相似度、还要写文件。
        /// 片段库上了规模之后，这些同步跑在 run_csharp 的返回路径上会实打实拖慢每次执行 ——
        /// 而"要不要固化成片段"完全不影响本次结果，没有任何理由让用户等它。
        ///
        /// 升格提示通过回调给出，调用方可以选择忽略。
        /// </summary>
        public static void ObserveAsync(string code, string convId, string lang,
                                        Action<string> onPromoted = null)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var name = Observe(code, convId, lang);
                    if (name != null && onPromoted != null) onPromoted(name);
                }
                catch (Exception ex)
                {
                    // 片段固化失败不该影响任何事
                    try { AuditLog.Write("[warn] [Snippet] 后台观察失败: " + ex.Message); } catch { }
                }
            });
        }

        /// <summary>
        /// 记录一次成功执行。返回升格出来的片段名；未达阈值返回 null。
        /// 【同步版，会做全量遍历和文件 I/O】—— 工具执行路径请用 ObserveAsync。
        /// </summary>
        public static string Observe(string code, string convId, string lang)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            // 太短的代码没有固化价值 —— 一行的东西模型自己就会写
            if (code.Trim().Length < 80) return null;

            var normLang = SnippetStore.NormalizeLang(lang);

            var fp = Fingerprint(code, normLang);
            if (string.IsNullOrEmpty(fp)) return null;

            var all = LoadAll();

            // 【先查待定池再查正式库】HasSimilarCode 要遍历全部正式片段算相似度，
            // 是这里最贵的一步。待定池命中时说明这类操作已在观察中，
            // 不必再去正式库比对 —— 把贵的那步挪到只在新指纹时才跑。
            var hit = all.FirstOrDefault(x => x.Fingerprint == fp);

            if (hit == null && SnippetStore.HasSimilarCode(code, 0.75, normLang)) return null;
            if (hit == null)
            {
                hit = new PendingSnippet
                {
                    Fingerprint = fp,
                    SeenCount = 1,
                    LastCode = code,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    ConvId = convId
                };
                Save(hit);
                return null;
            }

            hit.SeenCount++;
            hit.LastCode = code;              // 后写的通常更成熟
            hit.LastSeenUtc = DateTime.UtcNow;
            hit.ConvId = convId;

            if (hit.SeenCount < PromoteThreshold) { Save(hit); return null; }

            // ── 升格 ──
            var name = SnippetStore.AutoName(hit.LastCode);
            var tags = SnippetStore.ExtractTags(hit.LastCode);

            SnippetStore.Upsert(new Snippet
            {
                Name = name,
                Description = SnippetStore.AutoDescription(hit.LastCode, tags)
                            + "（同类操作出现 " + hit.SeenCount + " 次后自动固化）",
                Code = hit.LastCode,
                Lang = normLang,
                Tags = tags,
                Origin = "auto-promoted",
                ConvId = hit.ConvId,
                SeenCount = hit.SeenCount,
                CreatedUtc = DateTime.UtcNow
            });

            MdStore.Delete(Folder, SlugOf(fp));

            try { AuditLog.Write("[info] [Snippet] 重复 " + hit.SeenCount + " 次，已固化为 " + name); }
            catch { }

            return name;
        }

        // ── 结构指纹 ──

        private static readonly Regex StringLit = new Regex("\"[^\"]*\"", RegexOptions.Compiled);
        private static readonly Regex NumLit = new Regex(@"-?\d+(\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex Comment = new Regex(@"//[^\n]*|/\*.*?\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ApiCall = new Regex(@"\b(Tx[A-Za-z0-9_]+|[A-Za-z_]\w*)\s*\(",
            RegexOptions.Compiled);

        /// <summary>
        /// 提取结构指纹:只保留 Tx* 类型名与调用序列，抹掉字面量和局部变量名。
        /// 这样"给 A 对象设坐标"和"给 B 对象设坐标"会得到同一个指纹。
        /// </summary>
        public static string Fingerprint(string code, string lang)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var normLang = SnippetStore.NormalizeLang(lang);

            var t = Comment.Replace(code, " ");
            t = StringLit.Replace(t, "\"\"");
            t = NumLit.Replace(t, "0");

            // 只留 Tx 开头的类型/方法名，它们才是"这段代码在干什么"的骨架
            var tokens = new List<string>();
            foreach (Match m in ApiCall.Matches(t))
            {
                var n = m.Groups[1].Value;
                if (n.StartsWith("Tx", StringComparison.Ordinal)
                    || n.StartsWith("Get", StringComparison.Ordinal)
                    || n.StartsWith("Set", StringComparison.Ordinal)
                    || n.StartsWith("Create", StringComparison.Ordinal)
                    || n.StartsWith("Add", StringComparison.Ordinal)
                    // Python 侧的探查助手 tx_dir / tx_type / tx_sig ——
                    // 它们是 probe_python 代码的骨架，漏掉的话 Python 代码
                    // 基本抽不出指纹，等于 Python 永远进不了待定池。
                    || n.StartsWith("tx_", StringComparison.Ordinal))
                    tokens.Add(n);
            }

            // 骨架太短说明这段代码没什么结构，不值得当作一类
            if (tokens.Count < 3) return null;

            // 顺序有意义，但重复调用同一个方法不加权
            var seq = new List<string>();
            foreach (var x in tokens)
                if (seq.Count == 0 || seq[seq.Count - 1] != x) seq.Add(x);

            // 【语言前缀】C# 和 Python 做同一件事会抽出同一串调用序列。
            // 不加前缀，两种语言的代码会共用一个待定项互相累加计数，
            // 凑够 3 次固化时 LastCode 取的是"最近一次"—— 语言是随机的。
            // 这正是那种不报错、只是把错东西存下来的失败。
            return normLang + "|" + string.Join(">", seq.Take(20));
        }

        // ── 持久化 ──

        private static List<PendingSnippet> LoadAll()
        {
            var list = new List<PendingSnippet>();
            var cutoff = DateTime.UtcNow.AddDays(-ExpireDays);

            foreach (var doc in MdStore.LoadAll(Folder))
            {
                var fp = doc.Get("fingerprint", "");
                if (string.IsNullOrEmpty(fp)) continue;

                var last = doc.GetDate("last_seen");

                // 过期的顺手清掉,避免待定池无限增长
                if (last != default(DateTime) && last < cutoff)
                {
                    MdStore.Delete(Folder, SlugOf(fp));
                    continue;
                }

                list.Add(new PendingSnippet
                {
                    Fingerprint = fp,
                    SeenCount = doc.GetInt("seen_count", 1),
                    LastCode = ExtractCode(doc.Body),
                    FirstSeenUtc = doc.GetDate("first_seen"),
                    LastSeenUtc = last,
                    ConvId = doc.Get("conv_id", "")
                });
            }
            return list;
        }

        private static void Save(PendingSnippet p)
        {
            var doc = new MarkdownDoc();
            doc.Set("key", p.Fingerprint);
            doc.Set("fingerprint", p.Fingerprint);
            doc.Set("seen_count", p.SeenCount);
            doc.Set("conv_id", p.ConvId ?? "");
            doc.Set("first_seen", p.FirstSeenUtc);
            doc.Set("last_seen", p.LastSeenUtc);

            // 围栏语言从指纹前缀取 —— 这些 MD 是要给人直接看的，
            // 一段 Python 顶着 csharp 围栏读起来会误导。
            var lang = (p.Fingerprint ?? "").StartsWith("python|", StringComparison.Ordinal)
                     ? "python" : "csharp";

            var sb = new StringBuilder();
            sb.AppendLine("待定片段（" + lang + "）：同类操作再出现 "
                + Math.Max(0, PromoteThreshold - p.SeenCount) + " 次即自动固化。");
            sb.AppendLine();
            sb.AppendLine("```" + lang);
            sb.AppendLine((p.LastCode ?? "").TrimEnd());
            sb.AppendLine("```");
            doc.Body = sb.ToString();

            MdStore.Write(Folder, SlugOf(p.Fingerprint), doc);
        }

        private static string ExtractCode(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int open = body.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) return body.Trim();
            int lineEnd = body.IndexOf('\n', open);
            if (lineEnd < 0) return "";
            int close = body.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (close < 0) close = body.Length;
            return body.Substring(lineEnd + 1, close - lineEnd - 1).TrimEnd();
        }

        /// <summary>
        /// 指纹里有 > 等非法字符，哈希成文件名。
        /// 【不能用 string.GetHashCode()】.NET Framework 4.8 的字符串哈希每次进程启动随机化，
        /// 跨进程算出的 slug 不一致 → 升格删除 / 过期清理全部失效，文件只增不减。
        /// FNV-1a 是稳定哈希，与进程无关。
        /// </summary>
        private static string SlugOf(string fingerprint)
        {
            const uint fnvPrime = 16777619u;
            uint hash = 2166136261u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(fingerprint ?? ""))
            {
                hash ^= b;
                hash *= fnvPrime;
            }
            return "p_" + hash.ToString("X8");
        }

        /// <summary>当前待定池状态，供诊断。</summary>
        public static string Describe()
        {
            var all = LoadAll().OrderByDescending(x => x.SeenCount).ToList();
            if (all.Count == 0) return "待定池为空。";

            var sb = new StringBuilder();
            sb.AppendLine("待定池 " + all.Count + " 条（重复 " + PromoteThreshold + " 次自动固化）:");
            foreach (var p in all.Take(20))
                sb.Append("  ").Append(p.SeenCount).Append("/").Append(PromoteThreshold)
                  .Append("  ").AppendLine(p.Fingerprint.Length > 90
                      ? p.Fingerprint.Substring(0, 90) + "…" : p.Fingerprint);
            return sb.ToString();
        }
    }
}
