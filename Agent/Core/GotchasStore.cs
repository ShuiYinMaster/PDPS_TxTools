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
// 路径策略与 KeyStore/RecipeStore 一致(优先插件目录，回退 LocalAppData)。

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
        private const string FileName = "gotchas.json";

        public static List<Gotcha> All()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var list = JsonConvert.DeserializeObject<List<Gotcha>>(File.ReadAllText(path, Encoding.UTF8));
                    if (list != null) return list;
                }
                catch { }
            }
            return new List<Gotcha>();
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
                SaveAll(all);
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
            all.Add(g);
            SaveAll(all);
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
            SaveAll(all);
            return true;
        }

        public static bool Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var all = All();
            int n = all.RemoveAll(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return false;
            SaveAll(all);
            return true;
        }

        /// <summary>
        /// 取"最值得注入 prompt"的 Top-N：
        /// HitCount 高 → 常踩，必须记住；有 Correction → 能给出正解，价值 +5；
        /// 最近 30 天有被踩过 → 仍活跃 +2。
        /// </summary>
        public static List<Gotcha> TopN(int n)
        {
            return All()
                .OrderByDescending(g =>
                    g.HitCount * 3.0
                    + (!string.IsNullOrEmpty(g.Correction) ? 5.0 : 0.0)
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

        // ── 持久化 ──

        private static void SaveAll(List<Gotcha> all)
        {
            var json = JsonConvert.SerializeObject(all ?? new List<Gotcha>(), Formatting.Indented);
            foreach (var path in CandidatePaths())
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, json, Encoding.UTF8);
                    return;
                }
                catch { }
            }
        }

        private static string[] CandidatePaths()
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools.Agent");

            if (string.IsNullOrEmpty(pluginDir))
                return new[] { Path.Combine(localDir, FileName) };

            return new[]
            {
                Path.Combine(pluginDir, FileName),
                Path.Combine(localDir, FileName)
            };
        }
    }
}