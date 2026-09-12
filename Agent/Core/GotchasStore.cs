// TxTools.Agent / Core / GotchasStore.cs
// 踩坑记录：run_csharp 报错时自动摘录的 "反面教材"。
// 目的：把散在人脑/多次对话里的 API 陷阱、语法错误、TxNotImplementedException 沉淀成
// 结构化清单，作为 system prompt 的一部分注入(避坑清单)，模型不再重复踩同一坑。
//
// 数据流：
//   AgentLoop.RunOneTool → run_csharp 输出含错误 → GotchasStore.Record → 落 gotchas.json
//   BuildSystemPromptWithMemory → TopN → 每轮注入到 system prompt "避坑清单"
//   AI 学到正解 → add_gotcha_correction 工具 → GotchasStore.AddCorrection
//   对话末萃取 → LessonExtractor 补充 Correction
//
// 存储:memory/gotchas/*.md,一条一文件,文件名即签名(见 MdStore)。首次访问自动从 gotchas.json 迁移。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class Gotcha
    {
        public string Id { get; set; }
        /// <summary>去重签名。例：CS0117:TxCollisionRoot.CollisionPairs 或 TxNotImplementedException:TxJoint.Name</summary>
        public string Signature { get; set; }
        /// <summary>错误分类：CS0117 / TxNotImplementedException / 编译失败 / runtime</summary>
        public string ErrorType { get; set; }
        /// <summary>完整错误消息(截 300 字)。</summary>
        public string ErrorMessage { get; set; }
        /// <summary>触发的代码片段(截 500 字)，尽量定位到出错行附近。</summary>
        public string CodeSnippet { get; set; }
        /// <summary>正确用法(初始空，AI 学到后通过 add_gotcha_correction 或萃取补充)。</summary>
        public string Correction { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime LastHitUtc { get; set; }
        /// <summary>被踩次数。用于排序注入优先级。</summary>
        public int HitCount { get; set; }
        public string ConvId { get; set; }
    }

    public static class GotchasStore
    {
        private const string Folder = "gotchas";

        public static List<Gotcha> All()
        {
            EnsureMigrated();

            var list = new List<Gotcha>();
            foreach (var doc in MdStore.LoadAll(Folder))
            {
                var g = FromDoc(doc);
                if (g != null) list.Add(g);
            }
            return list;
        }

        // ── MD 映射 ──
        //  文件名取自签名(如 CS1061_TxDocument_FullPath.md),扫一眼目录就知道踩过哪些坑。
        //  正解带代码时是围栏块,可直接复制。

        private static MarkdownDoc ToDoc(Gotcha g)
        {
            var doc = new MarkdownDoc();
            doc.Set("key", g.Signature ?? "");
            doc.Set("id", g.Id ?? "");
            doc.Set("signature", g.Signature ?? "");
            doc.Set("error_type", g.ErrorType ?? "");
            doc.Set("hit_count", g.HitCount);
            doc.Set("conv_id", g.ConvId ?? "");
            doc.Set("created", g.CreatedUtc);
            doc.Set("last_hit", g.LastHitUtc);

            var sb = new StringBuilder();
            sb.AppendLine("## 错误");
            sb.AppendLine();
            sb.AppendLine((g.ErrorMessage ?? "").Trim());
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(g.CodeSnippet))
            {
                sb.AppendLine("## 触发代码");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(g.CodeSnippet.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("## 正解");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(g.Correction)
                ? "(暂无。确认正确写法后用 add_gotcha_correction 补充)"
                : g.Correction.Trim());
            doc.Body = sb.ToString();

            return doc;
        }

        private static Gotcha FromDoc(MarkdownDoc doc)
        {
            if (doc == null) return null;
            var sig = doc.Get("signature", doc.Get("key", ""));
            if (string.IsNullOrWhiteSpace(sig)) return null;

            return new Gotcha
            {
                Id = doc.Get("id", ""),
                Signature = sig,
                ErrorType = doc.Get("error_type", ""),
                ErrorMessage = Section(doc.Body, "错误"),
                CodeSnippet = StripFence(Section(doc.Body, "触发代码")),
                Correction = NormalizeCorrection(Section(doc.Body, "正解")),
                HitCount = doc.GetInt("hit_count", 0),
                ConvId = doc.Get("conv_id", ""),
                CreatedUtc = doc.GetDate("created"),
                LastHitUtc = doc.GetDate("last_hit")
            };
        }

        /// <summary>取 "## 标题" 到下一个 "## " 之间的内容。</summary>
        private static string Section(string body, string title)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var marker = "## " + title;
            int i = body.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return "";
            i += marker.Length;

            int next = body.IndexOf("\n## ", i, StringComparison.Ordinal);
            var seg = next < 0 ? body.Substring(i) : body.Substring(i, next - i);
            return seg.Trim();
        }

        private static string StripFence(string seg)
        {
            if (string.IsNullOrEmpty(seg)) return "";
            int open = seg.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) return seg.Trim();
            int lineEnd = seg.IndexOf('\n', open);
            if (lineEnd < 0) return "";
            int close = seg.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (close < 0) close = seg.Length;
            return seg.Substring(lineEnd + 1, close - lineEnd - 1).TrimEnd();
        }

        /// <summary>"(暂无...)" 这类占位文本读回来要还原成空,否则会被当成已有正解。</summary>
        private static string NormalizeCorrection(string seg)
        {
            if (string.IsNullOrWhiteSpace(seg)) return "";
            if (seg.TrimStart().StartsWith("(暂无", StringComparison.Ordinal)) return "";
            return seg.Trim();
        }

        private static void EnsureMigrated()
        {
            MdStore.MigrateOnce(Folder, "gotchas.json", json =>
            {
                var list = JsonConvert.DeserializeObject<List<Gotcha>>(json);
                if (list == null) return;
                foreach (var g in list)
                {
                    if (g == null || string.IsNullOrWhiteSpace(g.Signature)) continue;
                    WriteOne(g);
                }
            });
        }

        private static void WriteOne(Gotcha g)
        {
            MdStore.Write(Folder, SlugOf(g.Signature), ToDoc(g));
        }

        private static string SlugOf(string signature)
        {
            return MdStore.UniqueSlug(Folder, MarkdownDoc.Slug(signature), signature);
        }

        /// <summary>
        /// 从错误输出+代码里自动提炼一条 Gotcha 并落库。
        /// 已存在同签名 → HitCount++ 并更新 LastHitUtc；新签名 → 新增一条。
        /// 提取不出签名(如输出根本不像错误)则不入库,返回 null。
        /// </summary>
        public static Gotcha Record(string code, string errorOutput, string convId)
        {
            var sig = ExtractSignature(errorOutput, code);
            if (string.IsNullOrEmpty(sig)) return null;

            var all = All();
            var existing = all.FirstOrDefault(g => string.Equals(g.Signature, sig, StringComparison.Ordinal));
            if (existing != null)
            {
                existing.HitCount++;
                existing.LastHitUtc = DateTime.UtcNow;
                WriteOne(existing);
                return existing;
            }

            var g = new Gotcha
            {
                Id = "gc_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"),
                Signature = sig,
                ErrorType = ExtractErrorType(errorOutput),
                ErrorMessage = Truncate(errorOutput, 300),
                CodeSnippet = Truncate(ExtractOffendingLines(code, errorOutput), 500),
                Correction = "",
                CreatedUtc = DateTime.UtcNow,
                LastHitUtc = DateTime.UtcNow,
                HitCount = 1,
                ConvId = convId
            };
            WriteOne(g);
            return g;
        }

        /// <summary>为已存在签名补充正确用法。返回是否找到并更新。</summary>
        public static bool AddCorrection(string signature, string correction)
        {
            if (string.IsNullOrWhiteSpace(signature)) return false;
            var all = All();
            var g = all.FirstOrDefault(x =>
                string.Equals(x.Signature, signature, StringComparison.OrdinalIgnoreCase));
            if (g == null) return false;
            g.Correction = correction ?? "";
            WriteOne(g);
            return true;
        }

        public static bool Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var g = All().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g == null) return false;
            return MdStore.Delete(Folder, SlugOf(g.Signature));
        }

        /// <summary>
        /// 取"最值得注入 prompt"的 Top-N。
        /// 有正解 → 压倒性优先(50 分,盖过 HitCount*3 的常见区间):
        ///   没有正解的坑注入后只剩"(暂无正解)"占位,对模型没有行动价值,
        ///   旧权重(仅 +5)下高频无正解的坑会长期霸榜,把有用的挤出去;
        /// HitCount 高 → 常踩，同类坑里先注入踩得多的；
        /// 最近 30 天有被踩过 → 仍活跃 +2。
        /// </summary>
        public static List<Gotcha> TopN(int n)
        {
            return All()
                .Where(g => !string.IsNullOrWhiteSpace(g.Correction))
                .OrderByDescending(g =>
                    (!string.IsNullOrEmpty(g.Correction) ? 50.0 : 0.0)
                    + g.HitCount * 3.0
                    + ((DateTime.UtcNow - g.LastHitUtc).TotalDays < 30 ? 2.0 : 0.0))
                .Take(n)
                .ToList();
        }

        // ── 签名与分类的启发式提取 ──

        private static readonly Regex CsErrorPattern =
            new Regex(@"CS(\d{4})", RegexOptions.Compiled);
        private static readonly Regex TypeMemberPattern =
            new Regex(@"([A-Z][A-Za-z0-9_]+)\.([A-Za-z_][A-Za-z0-9_]+)", RegexOptions.Compiled);
        private static readonly Regex TxNotImplPattern =
            new Regex(@"TxNotImplementedException", RegexOptions.Compiled);
        private static readonly Regex ExceptionTypePattern =
            new Regex(@"([A-Z][A-Za-z0-9_]+Exception)", RegexOptions.Compiled);
        private static readonly Regex LineColPattern =
            new Regex(@"\((\d+),\d+\)", RegexOptions.Compiled);

        // 中英文的引号字符类:
        //   U+0022 " 半角双引号     U+0027 ' 半角单引号
        //   U+201C " 中文左双引号   U+201D " 中文右双引号
        //   U+2018 ' 中文左单引号   U+2019 ' 中文右单引号
        private const string QuoteChars = @"[""'\u201C\u201D\u2018\u2019]";

        // CS1061 / CS0117 / 其他"XX 不包含 YY 的定义"类错误 —— 中英文引号都覆盖
        //   中文: "Type"不包含"Member"的定义
        //   英文: 'Type' does not contain a definition for 'Member'
        // 报错消息结构完全一致,只差 CS 号,合并成一个通用 pattern。
        private static readonly Regex CsQuotedTypeMemberPattern = new Regex(
            @"CS\d{4}.*?" + QuoteChars + @"([\w\.]+)" + QuoteChars +
            @".*?" + QuoteChars + @"([\w]+)" + QuoteChars,
            RegexOptions.Compiled | RegexOptions.Singleline);

        // CS0246 找不到类型或命名空间名:   "找不到类型或命名空间名 'XXX'"
        private static readonly Regex Cs0246Pattern = new Regex(
            @"CS0246.*?" + QuoteChars + @"([\w\.]+)" + QuoteChars,
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>已知的命名空间前缀 — 提取 Type.Member 时跳过这些,避免误把命名空间当作类名。</summary>
        private static readonly HashSet<string> NamespacePrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Tecnomatix", "Tecnomatix.Engineering", "Tecnomatix.Engineering.Ui",
            "System", "System.Collections", "System.Collections.Generic", "System.IO",
            "System.Text", "System.Text.RegularExpressions", "System.Linq",
            "System.Threading", "System.Threading.Tasks", "System.Reflection",
            "System.Windows.Forms", "System.Diagnostics",
            "Newtonsoft.Json", "Newtonsoft.Json.Linq", "Microsoft.Web.WebView2",
            "TxTools", "TxTools.Agent", "TxTools.Agent.Core"
        };

        /// <summary>
        /// 提取错误签名。签名应精确到"具体报错的 Type.Member",不同 API 缺失应产生不同签名,
        /// 这样避坑清单里每条正解才能对应精确的 API,而不是一坨笼统的 CS1061 归并。
        /// 抽不出时返回 null,表示这条错误不值得入库。
        /// </summary>
        public static string ExtractSignature(string errorOutput, string code)
        {
            if (string.IsNullOrEmpty(errorOutput)) return null;

            // 1) TxNotImplementedException → 找目标 API
            if (TxNotImplPattern.IsMatch(errorOutput))
            {
                var target = FindSpecificTypeMember(errorOutput) ?? FirstTypeMemberInCode(code);
                if (string.IsNullOrEmpty(target)) target = "unknown";
                return "TxNotImplementedException:" + target;
            }

            // 2) C# 编译错误 — 优先用具体模式提取 Type.Member
            var cs = CsErrorPattern.Match(errorOutput);
            if (cs.Success)
            {
                var csCode = cs.Value;

                // 2a) CS1061 / CS0117 / 类似的"XX 不包含 YY 的定义"错误 —— 引号提取最准
                //     覆盖中英文所有引号变体,不同 CS 号共用一个模式
                var m = CsQuotedTypeMemberPattern.Match(errorOutput);
                if (m.Success)
                {
                    var typeName = ShortTypeName(m.Groups[1].Value);
                    var member = m.Groups[2].Value;
                    return csCode + ":" + typeName + "." + member;
                }

                // 2b) CS0246: 找不到类型或命名空间名 —— 只有单个引号包裹的名字
                if (csCode == "CS0246")
                {
                    var m2 = Cs0246Pattern.Match(errorOutput);
                    if (m2.Success) return csCode + ":" + ShortTypeName(m2.Groups[1].Value);
                }

                // 2c) 兜底:从错误上下文遍历 Type.Member,跳过命名空间前缀,取第一个真正类名
                var specific = FindSpecificTypeMember(errorOutput);
                if (!string.IsNullOrEmpty(specific)) return csCode + ":" + specific;

                // 2d) 最后退化:CS 号 + 首行 hash
                return csCode + ":" + ShortHash(FirstLine(errorOutput));
            }

            // 3) 其他运行时异常
            var exMatch = ExceptionTypePattern.Match(errorOutput);
            if (exMatch.Success)
            {
                return exMatch.Value + ":" + ShortHash(FirstLine(errorOutput));
            }

            return null;
        }

        /// <summary>去掉命名空间前缀,只留最短类名。"Tecnomatix.Engineering.TxWeldOperation" → "TxWeldOperation"</summary>
        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            int dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        /// <summary>
        /// 从错误输出里遍历所有 Type.Member,跳过命名空间前缀(如 Tecnomatix.Engineering),
        /// 返回第一个"真正类名+成员"的组合(如 TxWeldOperation.Parent)。
        /// </summary>
        private static string FindSpecificTypeMember(string errorOutput)
        {
            if (string.IsNullOrEmpty(errorOutput)) return null;
            foreach (Match m in TypeMemberPattern.Matches(errorOutput))
            {
                var typePart = m.Groups[1].Value;
                var memberPart = m.Groups[2].Value;

                // 跳过命名空间前缀本身
                if (NamespacePrefixes.Contains(typePart)) continue;

                // 也跳过组合的命名空间(如 typePart="Tecnomatix" 后接 memberPart="Engineering" 拼起来还是命名空间)
                var combined = typePart + "." + memberPart;
                if (NamespacePrefixes.Contains(combined)) continue;

                // 优先取形似 TxXxx 或 IXxx 之类真正的 PS SDK 类名
                return m.Value;
            }
            return null;
        }

        private static string ExtractErrorType(string errorOutput)
        {
            if (string.IsNullOrEmpty(errorOutput)) return "unknown";
            var cs = CsErrorPattern.Match(errorOutput);
            if (cs.Success) return cs.Value;
            if (TxNotImplPattern.IsMatch(errorOutput)) return "TxNotImplementedException";
            var ex = ExceptionTypePattern.Match(errorOutput);
            if (ex.Success) return ex.Value;
            if (errorOutput.IndexOf("编译失败", StringComparison.Ordinal) >= 0) return "编译失败";
            return "runtime";
        }

        /// <summary>从代码里抠出出错行附近的片段(带行号则精准定位;否则前 8 行)。</summary>
        private static string ExtractOffendingLines(string code, string errorOutput)
        {
            if (string.IsNullOrEmpty(code)) return "";
            var lines = code.Split(new[] { '\n' }, StringSplitOptions.None);

            var m = LineColPattern.Match(errorOutput ?? "");
            if (m.Success)
            {
                int ln;
                if (int.TryParse(m.Groups[1].Value, out ln) && ln > 0 && ln <= lines.Length)
                {
                    int from = Math.Max(0, ln - 2);
                    int to = Math.Min(lines.Length - 1, ln + 1);
                    var sb = new StringBuilder();
                    for (int i = from; i <= to; i++) sb.AppendLine(lines[i].TrimEnd('\r'));
                    return sb.ToString().TrimEnd();
                }
            }

            var head = new StringBuilder();
            for (int i = 0; i < Math.Min(lines.Length, 8); i++)
                head.AppendLine(lines[i].TrimEnd('\r'));
            return head.ToString().TrimEnd();
        }

        private static string FirstTypeMemberInCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var m = TypeMemberPattern.Match(code);
            return m.Success ? m.Value : null;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var nl = s.IndexOf('\n');
            return nl >= 0 ? s.Substring(0, nl).Trim() : s.Trim();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string ShortHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0";
            unchecked
            {
                int h = 5381;
                foreach (var c in s) h = ((h << 5) + h) ^ c;
                return h.ToString("x8");
            }
        }

    }
}
