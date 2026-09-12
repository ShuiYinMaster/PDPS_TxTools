// TxTools.Agent / Core / RecipeStore.cs
//
// 配方 = 已经跑稳的脚本 + 参数声明。
//
// ── 配方和片段的区别 ──
//   片段(Snippet)是给模型看的:它检索、取出、改改再执行。
//   配方(Recipe)是给人点的:选对象 → 点执行，整个过程不经过模型、不花 token。
//
//   所以配方比片段多两样东西:参数声明(哪些地方是可变的)、以及"人可读的名字和说明"。
//   代码本身则要求更严 —— 片段允许改改再用，配方是原样执行的。
//
// ── 参数绑定为什么不存在这里 ──
//   参数声明(叫什么、什么类型、必填与否)是配方的一部分,跟着文件走。
//   参数的【值】不是 —— 对象绑定是 ITxObject.Id,形如 "3,57,2,1",
//   这个 Id 只在当前 study 内有意义。存进配方文件,换个 study 打开就会指向别的东西,
//   而它不会报错,只会安静地对错误的对象执行操作。
//   所以绑定值只活在侧边栏的内存里,并且跟 study 名一起校验。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    /// <summary>配方的一个参数声明。</summary>
    public sealed class RecipeParam
    {
        /// <summary>代码里用的变量名。必须是合法标识符 —— 它会被直接写进生成的前置代码。</summary>
        public string Name { get; set; }

        /// <summary>界面上显示的名字。</summary>
        public string Label { get; set; }

        /// <summary>object / objects / number / text / bool</summary>
        public string Kind { get; set; }

        /// <summary>期望的 PS 类型，如 TxRobot。仅用于生成强制转换与界面提示。</summary>
        public string TypeHint { get; set; }

        public bool Required { get; set; }
        public string Default { get; set; }
        public string Help { get; set; }

        public RecipeParam()
        {
            Kind = "object";
            Required = true;
        }
    }

    public sealed class Recipe
    {
        public string Id { get; set; }              // slug，文件名
        public string Name { get; set; }
        public string Description { get; set; }
        public string Lang { get; set; }            // csharp / python
        public string Code { get; set; }
        public List<RecipeParam> Params { get; set; }

        /// <summary>来源片段名，便于回溯与后续 patch。</summary>
        public string SourceSnippet { get; set; }

        public int RunCount { get; set; }
        public int FailCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime LastRunUtc { get; set; }

        public Recipe()
        {
            Params = new List<RecipeParam>();
            Lang = "csharp";
        }

        /// <summary>
        /// 配方名转 API 安全的工具名(function.name 要求 ^[a-zA-Z0-9_-]+$)。
        /// 规则:纯 ASCII 安全名原样返回;含非 ASCII 提取 ASCII 子串;全中文退回 djb2 哈希兜底。
        /// </summary>
        public static string ToApiSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "recipe_unknown";

            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$"))
                return name;

            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('_');
            }
            var extracted = sb.ToString().Trim('_', '-');
            if (extracted.Length >= 2) return extracted;

            // 全部是非 ASCII 字符 → 稳定哈希兜底 (djb2, 跨 .NET 版本不变)
            uint hash = 5381;
            foreach (char c in name)
                hash = ((hash << 5) + hash) + c;
            return "recipe_" + hash.ToString("x8");
        }
    }

    public static class RecipeStore
    {
        private const string Folder = "recipes";

        /// <summary>
        /// 片段要跑成功过几次才够格出现在"可固化为配方"列表里。
        /// 【这个门槛依赖归因数据是真的】如果 SuccessCount 还是"取出即成功"那套算法，
        /// 这里推给用户的就是一堆没验证过的代码，而用户会一键执行它们。
        /// </summary>
        public static int PromoteMinSuccess = 2;

        // ── 读 ──

        public static List<Recipe> All()
        {
            var list = new List<Recipe>();
            foreach (var doc in MdStore.LoadAll(Folder))
            {
                var r = FromDoc(doc);
                if (r != null) list.Add(r);
            }
            return list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static Recipe Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return All().FirstOrDefault(r =>
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // ── 写 ──

        public static string Upsert(Recipe r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Name))
                return "配方名不能为空。";
            if (string.IsNullOrWhiteSpace(r.Code))
                return "配方代码不能为空。";

            var bad = ValidateParams(r.Params);
            if (bad != null) return bad;

            if (string.IsNullOrWhiteSpace(r.Id)) r.Id = Slug(r.Name);
            if (r.CreatedUtc == default(DateTime)) r.CreatedUtc = DateTime.UtcNow;
            r.Lang = SnippetStore.NormalizeLang(r.Lang);

            MdStore.Write(Folder, r.Id, ToDoc(r));
            RaiseChanged();
            return "已保存配方: " + r.Name;
        }

        public static bool Delete(string id)
        {
            var r = Get(id);
            if (r == null) return false;
            MdStore.Delete(Folder, r.Id);
            RaiseChanged();
            return true;
        }

        // ── 变更通知 ──
        //  侧边栏前端等 recipe.changed 推送来刷新列表,宿主此前从未发过 ——
        //  聊天里 save/delete 之后侧边栏一直显示旧数据,只能手点刷新。
        //  RecordRun 只动计数不触发:执行完前端自己会刷一次列表,再推就重复。

        /// <summary>配方新增/更新/删除后触发。订阅方(UI 推送、工具表同步)各自兜异常。</summary>
        public static event Action RecipesChanged;

        private static void RaiseChanged()
        {
            var h = RecipesChanged;
            if (h == null) return;
            foreach (Action d in h.GetInvocationList())
            {
                try { d(); }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [Recipe] RecipesChanged 订阅者异常: " + ex.Message); } catch { }
                }
            }
        }

        /// <summary>记录一次执行结果。成败都记 —— 只记成功等于不记。</summary>
        public static void RecordRun(string id, bool ok)
        {
            var r = Get(id);
            if (r == null) return;
            if (ok) r.RunCount++; else r.FailCount++;
            r.LastRunUtc = DateTime.UtcNow;
            MdStore.Write(Folder, r.Id, ToDoc(r));
        }

        // ── 校验 ──

        /// <summary>
        /// 参数名会被原样写进生成的代码，所以必须是合法 C#/Python 标识符。
        /// 【这里必须拦住】一个叫 "robot name" 或 "机器人" 的参数名，
        /// 生成出来的是语法错误的代码，而报错信息会指向生成后的代码行，
        /// 跟"参数名起错了"这个真实原因隔了两层。
        /// </summary>
        public static string ValidateParams(List<RecipeParam> ps)
        {
            if (ps == null) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var p in ps)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name))
                    return "参数名不能为空。";

                var n = p.Name.Trim();
                if (!IsIdentifier(n))
                    return "参数名 \"" + n + "\" 不是合法标识符（只能用字母、数字、下划线，且不能以数字开头）。"
                         + "它会被直接写进生成的代码。";

                if (!seen.Add(n))
                    return "参数名重复: " + n;

                var k = (p.Kind ?? "").Trim().ToLowerInvariant();
                if (k != "object" && k != "objects" && k != "number" && k != "text" && k != "bool")
                    return "参数 " + n + " 的 kind 非法: \"" + p.Kind
                         + "\"，只能是 object / objects / number / text / bool。";
                p.Kind = k;
                p.Name = n;
            }
            return null;
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_') return false;
            for (int i = 1; i < s.Length; i++)
                if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
            // 只挡 ASCII 范围外的字母:char.IsLetter 对中文返回 true，
            // 但生成的 C# 代码里中文变量名虽然合法却会让人困惑，Python 2.7 更是直接不支持。
            foreach (var c in s) if (c > 127) return false;
            return true;
        }

        // ── 持久化 ──
        //
        // 配方文件是给人直接翻看和手改的，所以正文用清晰的分节，
        // 参数不塞进 frontmatter（一行 JSON 挤在 key: value 里没法读也没法改）。

        private const string ParamHeader = "## 参数";
        private const string CodeHeader = "## 代码";

        private static MarkdownDoc ToDoc(Recipe r)
        {
            var doc = new MarkdownDoc();
            doc.Set("key", r.Id);
            doc.Set("name", r.Name ?? "");
            doc.Set("lang", SnippetStore.NormalizeLang(r.Lang));
            doc.Set("source_snippet", r.SourceSnippet ?? "");
            doc.Set("run_count", r.RunCount);
            doc.Set("fail_count", r.FailCount);
            doc.Set("created", r.CreatedUtc);
            doc.Set("last_run", r.LastRunUtc);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(r.Description))
            {
                sb.AppendLine(r.Description.Trim());
                sb.AppendLine();
            }

            sb.AppendLine(ParamHeader);
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(JsonConvert.SerializeObject(r.Params ?? new List<RecipeParam>(),
                                                      Formatting.Indented));
            sb.AppendLine("```");
            sb.AppendLine();

            sb.AppendLine(CodeHeader);
            sb.AppendLine();
            sb.AppendLine("```" + SnippetStore.NormalizeLang(r.Lang));
            sb.AppendLine((r.Code ?? "").TrimEnd());
            sb.AppendLine("```");

            doc.Body = sb.ToString();
            return doc;
        }

        private static Recipe FromDoc(MarkdownDoc doc)
        {
            if (doc == null) return null;
            var key = doc.Get("key", "");
            if (string.IsNullOrEmpty(key)) return null;

            var body = doc.Body ?? "";

            var r = new Recipe
            {
                Id = key,
                Name = doc.Get("name", key),
                Lang = SnippetStore.NormalizeLang(doc.Get("lang", "csharp")),
                SourceSnippet = doc.Get("source_snippet", ""),
                RunCount = doc.GetInt("run_count", 0),
                FailCount = doc.GetInt("fail_count", 0),
                CreatedUtc = doc.GetDate("created"),
                LastRunUtc = doc.GetDate("last_run"),
                Description = SectionBefore(body, ParamHeader).Trim(),
                Code = FencedAfter(body, CodeHeader)
            };

            var json = FencedAfter(body, ParamHeader);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    r.Params = JsonConvert.DeserializeObject<List<RecipeParam>>(json)
                               ?? new List<RecipeParam>();
                }
                catch (Exception ex)
                {
                    // 【不要吞掉】参数区解析失败时如果当成"没有参数"，
                    // 配方会带着未替换的变量名去执行，报一个跟真实原因无关的错。
                    // 宁可让这条配方整个不出现，并在日志里说清楚。
                    try
                    {
                        AuditLog.Write("[warn] [Recipe] " + key
                            + " 的参数区不是合法 JSON，已跳过该配方: " + ex.Message);
                    }
                    catch { }
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(r.Code)) return null;
            return r;
        }

        private static string SectionBefore(string body, string header)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int i = body.IndexOf(header, StringComparison.Ordinal);
            return i < 0 ? body : body.Substring(0, i);
        }

        /// <summary>取某个小节标题之后的第一个围栏块内容。</summary>
        private static string FencedAfter(string body, string header)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int h = body.IndexOf(header, StringComparison.Ordinal);
            if (h < 0) return "";

            int open = body.IndexOf("```", h, StringComparison.Ordinal);
            if (open < 0) return "";
            int lineEnd = body.IndexOf('\n', open);
            if (lineEnd < 0) return "";
            int close = body.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (close < 0) close = body.Length;

            return body.Substring(lineEnd + 1, close - lineEnd - 1).TrimEnd();
        }

        private static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in (name ?? "").Trim())
            {
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
                else if (c == ' ' || c == '_' || c == '-') sb.Append('_');
            }
            var s = sb.ToString().Trim('_');
            // 全中文名会 slug 成空串 —— 退回哈希，保证文件名唯一且稳定
            if (s.Length == 0)
                s = "r_" + Math.Abs((name ?? "").GetHashCode()).ToString("D10");
            return s;
        }

        // ── 候选 ──

        /// <summary>
        /// 够格固化为配方的片段:确实跑成功过、且没被标为不可靠、且还没做成配方。
        /// </summary>
        public static List<Snippet> PromotionCandidates()
        {
            var existing = new HashSet<string>(
                All().Select(r => r.SourceSnippet ?? "").Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            return SnippetStore.All()
                .Where(s => s.SuccessCount >= PromoteMinSuccess)
                .Where(s => !SnippetStore.IsUnreliable(s))
                .Where(s => !existing.Contains(s.Name))
                .OrderByDescending(s => s.SuccessCount)
                .Take(10)
                .ToList();
        }
    }
}
