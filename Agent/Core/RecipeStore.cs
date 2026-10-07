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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public sealed class RecipeChoice
    {
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("value")] public string Value { get; set; }
    }

    /// <summary>执行同一份代码时覆盖的标量参数。对象仍由当前研究选取。</summary>
    public sealed class RecipeAction
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("args")] public Dictionary<string, string> Args { get; set; } = new Dictionary<string, string>();
    }

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
        public List<RecipeChoice> Choices { get; set; }
        public bool ObjectFilter { get; set; }

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
        public List<RecipeAction> Actions { get; set; } = new List<RecipeAction>();

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
        private const string DefaultResourcePrefix = "TxTools.Agent.DefaultRecipes.";
        private static readonly object DefaultSync = new object();
        private static readonly HashSet<string> DefaultFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Seed each bundled recipe once. The marker preserves intentional deletions.
        private static void EnsureDefaults()
        {
            string folder = MdStore.FolderPath(Folder);
            lock (DefaultSync)
            {
                if (DefaultFolders.Contains(folder)) return;
                try
                {
                    string marker = Path.Combine(folder, ".default-recipes-installed");
                    var installed = new HashSet<string>(File.Exists(marker)
                        ? File.ReadAllLines(marker, Encoding.UTF8) : new string[0], StringComparer.Ordinal);
                    var assembly = typeof(RecipeStore).Assembly;
                    foreach (string resource in assembly.GetManifestResourceNames()
                        .Where(n => n.StartsWith(DefaultResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
                    {
                        string text;
                        using (var stream = assembly.GetManifestResourceStream(resource))
                        using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
                        var recipe = FromDoc(MarkdownDoc.Parse(text));
                        if (recipe == null || !IsIdentifier(recipe.Id) || ValidateDefinition(recipe) != null)
                            throw new InvalidOperationException("默认配方定义无效：" + resource);
                        string path = Path.Combine(folder, recipe.Id + ".md");
                        UpgradeBundledDefault(assembly, path, recipe);
                        if (installed.Contains(recipe.Id)) continue;
                        if (!File.Exists(path))
                        {
                            string temporary = path + ".seed-" + Guid.NewGuid().ToString("N");
                            try
                            {
                                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                                File.Move(temporary, path);
                            }
                            catch (IOException) { if (!File.Exists(path)) throw; }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                        installed.Add(recipe.Id);
                    }
                    File.WriteAllLines(marker, installed.OrderBy(id => id, StringComparer.Ordinal), new UTF8Encoding(false));
                    DefaultFolders.Add(folder);
                }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [Recipe] 安装默认配方失败：" + ex.Message); } catch { }
                }
            }
        }

        // Upgrade only the exact original definition; user edits and deletions stay intact.
        private static void UpgradeBundledDefault(Assembly assembly, string path, Recipe replacement)
        {
            if (!File.Exists(path)) return;
            var current = FromDoc(MarkdownDoc.Load(path));
            if (current == null || current.Id != replacement.Id) return;
            bool original = false;
            foreach (string resource in assembly.GetManifestResourceNames().Where(n =>
                n.StartsWith("TxTools.Agent.LegacyRecipes." + replacement.Id + ".", StringComparison.Ordinal)
                && n.EndsWith(".md", StringComparison.Ordinal)))
            {
                Recipe legacy;
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    legacy = FromDoc(MarkdownDoc.Parse(reader.ReadToEnd()));
                if (legacy != null && current.Name == legacy.Name && current.Lang == legacy.Lang
                    && current.SourceSnippet == legacy.SourceSnippet
                    && current.Description == legacy.Description && current.Code == legacy.Code
                    && JsonConvert.SerializeObject(current.Params) == JsonConvert.SerializeObject(legacy.Params)
                    && JsonConvert.SerializeObject(current.Actions) == JsonConvert.SerializeObject(legacy.Actions))
                { original = true; break; }
            }
            if (!original) return;
            replacement.RunCount = current.RunCount;
            replacement.FailCount = current.FailCount;
            replacement.CreatedUtc = current.CreatedUtc;
            replacement.LastRunUtc = current.LastRunUtc;
            replacement.SourceSnippet = current.SourceSnippet;
            string temporary = path + ".upgrade-" + Guid.NewGuid().ToString("N");
            string backup = path + (replacement.Id == "default_geometry_weld_points" ? ".before-keyword-" : ".before-controls-") + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, ToDoc(replacement).ToString(), new UTF8Encoding(false));
                File.Replace(temporary, path, backup);
                AuditLog.Write("[info] [Recipe] 已升级默认配方“" + replacement.Name + "”，原文件备份：" + backup);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>
        /// 片段要跑成功过几次才够格出现在"可固化为配方"列表里。
        /// 【这个门槛依赖归因数据是真的】如果 SuccessCount 还是"取出即成功"那套算法，
        /// 这里推给用户的就是一堆没验证过的代码，而用户会一键执行它们。
        /// </summary>
        public static int PromoteMinSuccess = 2;

        // ── 读 ──

        public static List<Recipe> All()
        {
            EnsureDefaults();
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

            var bad = ValidateDefinition(r);
            if (bad != null) return bad;

            if (string.IsNullOrWhiteSpace(r.Id)) r.Id = Slug(r.Name);
            if (r.CreatedUtc == default(DateTime)) r.CreatedUtc = DateTime.UtcNow;
            r.Lang = SnippetStore.NormalizeLang(r.Lang);

            if (!MdStore.Write(Folder, r.Id, ToDoc(r)))
                return "保存配方失败，请检查配方目录的写入权限。";
            RaiseChanged();
            return "已保存配方: " + r.Name;
        }

        /// <summary>分享文件沿用本地 Markdown 格式，只携带定义，不携带本机运行记录。</summary>
        public static string ExportMarkdown(Recipe r)
        {
            if (r == null) throw new ArgumentNullException("r");
            var doc = ToDoc(r);
            doc.Set("recipe_format", "1");
            doc.Set("source_snippet", "");
            doc.Set("run_count", 0);
            doc.Set("fail_count", 0);
            doc.Set("last_run", default(DateTime));
            return doc.ToString();
        }

        /// <summary>校验后导入为新副本。外部 key 不参与文件路径，同名配方不会被覆盖。</summary>
        public static bool TryImportMarkdown(string text, out Recipe imported, out string error)
        {
            imported = null;
            error = null;
            var doc = MarkdownDoc.Parse((text ?? "").TrimStart('\uFEFF'));
            var version = doc.Get("recipe_format", "1");
            if (version != "1") { error = "不支持的配方文件版本: " + version; return false; }
            var name = doc.Get("name", "").Trim();
            var lang = doc.Get("lang", "csharp").Trim().ToLowerInvariant();
            if (name.Length == 0) { error = "文件缺少配方名称（name）。"; return false; }
            if (lang != "csharp" && lang != "python")
            { error = "配方语言只能是 csharp 或 python。"; return false; }

            var json = FencedAfter(doc.Body, ParamHeader);
            var code = FencedAfter(doc.Body, CodeHeader);
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(code))
            { error = "文件须包含“## 参数”下的 JSON 数组和“## 代码”下的完整代码块。"; return false; }
            List<RecipeParam> ps;
            try
            {
                ps = JsonConvert.DeserializeObject<List<RecipeParam>>(json);
                if (ps == null) throw new FormatException("参数须为 JSON 数组，无参数时请使用 []。");
            }
            catch (Exception ex) { error = "参数区不是合法 JSON 数组: " + ex.Message; return false; }
            error = ValidateParams(ps);
            if (error != null) return false;
            List<RecipeAction> actions;
            if (!TryReadActions(doc.Body, out actions, out error)) return false;
            error = ValidateDefinition(new Recipe { Params = ps, Actions = actions });
            if (error != null) return false;

            var existing = All();
            var displayName = name;
            int copy = 2;
            while (existing.Any(r => string.Equals(r.Name, displayName, StringComparison.OrdinalIgnoreCase)))
                displayName = name + "（导入 " + copy++ + "）";
            var rnew = new Recipe
            {
                Id = "import_" + Guid.NewGuid().ToString("N"), Name = displayName,
                Description = SectionBefore(doc.Body, ParamHeader).Trim(),
                Lang = lang, Code = code, Params = ps, Actions = actions, CreatedUtc = DateTime.UtcNow
            };
            var saved = Upsert(rnew);
            if (!saved.StartsWith("已保存配方: ", StringComparison.Ordinal))
            { error = saved; return false; }
            imported = rnew;
            return true;
        }

        /// <summary>聊天详情省略 frontmatter，避免元数据被当作 Markdown 正文。</summary>
        public static string DisplayMarkdown(Recipe r)
        {
            return "## 配方：" + (r.Name ?? "").Replace("\r", " ").Replace("\n", " ")
                + "\n\n语言：" + SnippetStore.NormalizeLang(r.Lang) + "\n\n" + ToDoc(r).Body;
        }

        public static string CodeFence(string code)
        {
            int longest = 2, current = 0;
            foreach (char c in code ?? "")
            {
                current = c == '`' ? current + 1 : 0;
                longest = Math.Max(longest, current);
            }
            return new string('`', longest + 1);
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
                if (k != "object" && k != "objects" && k != "number" && k != "text" && k != "bool" && k != "color")
                    return "参数 " + n + " 的 kind 非法: \"" + p.Kind
                         + "\"，只能是 object / objects / number / text / bool / color。";
                p.Kind = k;
                p.Name = n;
                if (n == "__recipe_action") return "参数名 __recipe_action 为配方执行按钮保留。";
                if ((k == "object" || k == "objects") && !string.IsNullOrEmpty(p.Default))
                    return "对象参数不能保存默认绑定，请在当前研究中重新选取。";
                if (p.ObjectFilter && k != "object" && k != "objects") return "只有对象参数可以启用类型和名称筛选。";
                if (k == "color" && !string.IsNullOrEmpty(p.Default))
                {
                    var colorError = ValidateValue(p, p.Default);
                    if (colorError != null) return colorError;
                }
                if (p.Choices != null && p.Choices.Count > 0)
                {
                    if (k != "text" && k != "number") return "只有文本或数字参数可以设置选项。";
                    var values = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var choice in p.Choices)
                    {
                        if (choice == null || choice.Value == null || string.IsNullOrWhiteSpace(choice.Label)
                            || !values.Add(choice.Value)) return "参数 " + n + " 的选项须有标签且值不能重复。";
                        var valueError = ValidateValue(p, choice.Value);
                        if (valueError != null) return valueError;
                    }
                    if (!string.IsNullOrEmpty(p.Default) && !values.Contains(p.Default))
                        return "参数 " + n + " 的默认值不在选项中。";
                }
            }
            return null;
        }

        public static string ValidateDefinition(Recipe recipe)
        {
            var error = ValidateParams(recipe.Params);
            if (error != null) return error;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in recipe.Actions ?? new List<RecipeAction>())
            {
                if (action == null || !IsIdentifier(action.Id) || !ids.Add(action.Id)
                    || string.IsNullOrWhiteSpace(action.Label)) return "执行按钮须有唯一英文标识和中文标签。";
                foreach (var pair in action.Args ?? new Dictionary<string, string>())
                {
                    var p = (recipe.Params ?? new List<RecipeParam>()).FirstOrDefault(x => x.Name == pair.Key);
                    if (p == null) return "执行按钮引用了不存在的参数: " + pair.Key;
                    if (p.Kind == "object" || p.Kind == "objects") return "执行按钮不能保存对象绑定。";
                    error = ValidateValue(p, pair.Value);
                    if (error != null) return error;
                    if (p.Required && string.IsNullOrWhiteSpace(pair.Value)) return "执行按钮的必填参数不能为空: " + pair.Key;
                }
            }
            return null;
        }

        public static string ValidateValue(RecipeParam p, string value)
        {
            if (p.Choices != null && p.Choices.Count > 0 && !p.Choices.Any(c => c != null && c.Value == value))
                return "参数“" + (p.Label ?? p.Name) + "”请选择列表中的值。";
            if (p.Kind == "color" && !System.Text.RegularExpressions.Regex.IsMatch(value ?? "", "^#[0-9a-fA-F]{6}$"))
                return "参数“" + (p.Label ?? p.Name) + "”请选择有效颜色。";
            double number;
            if (p.Kind == "number" && (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number)))
                return "参数“" + (p.Label ?? p.Name) + "”须为有限数字。";
            if (p.Kind == "bool" && value != "1" && value != "0" && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return "布尔参数值须为 true 或 false。";
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
        private const string ActionHeader = "## 执行按钮";

        private static bool TryReadActions(string body, out List<RecipeAction> actions, out string error)
        {
            actions = new List<RecipeAction>(); error = null;
            if (FindHeader(body ?? "", ActionHeader) < 0) return true;
            try
            {
                actions = JsonConvert.DeserializeObject<List<RecipeAction>>(FencedAfter(body, ActionHeader));
                if (actions == null) throw new FormatException("须为 JSON 数组。");
                return true;
            }
            catch (Exception ex) { error = "执行按钮区不是合法 JSON 数组: " + ex.Message; return false; }
        }

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

            if (r.Actions != null && r.Actions.Count > 0)
            {
                sb.AppendLine(ActionHeader);
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(JsonConvert.SerializeObject(r.Actions, Formatting.Indented));
                sb.AppendLine("```");
                sb.AppendLine();
            }

            sb.AppendLine(CodeHeader);
            sb.AppendLine();
            var fence = CodeFence(r.Code);
            sb.AppendLine(fence + SnippetStore.NormalizeLang(r.Lang));
            sb.AppendLine((r.Code ?? "").TrimEnd());
            sb.AppendLine(fence);

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

            string error;
            List<RecipeAction> actions;
            if (!TryReadActions(body, out actions, out error))
            {
                try { AuditLog.Write("[warn] [Recipe] " + key + ": " + error); } catch { }
                return null;
            }
            r.Actions = actions;
            error = ValidateDefinition(r);
            if (error != null)
            {
                try { AuditLog.Write("[warn] [Recipe] " + key + ": " + error); } catch { }
                return null;
            }
            if (string.IsNullOrWhiteSpace(r.Code)) return null;
            return r;
        }

        private static string SectionBefore(string body, string header)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int i = FindHeader(body, header);
            return i < 0 ? body : body.Substring(0, i);
        }

        /// <summary>取某个小节标题之后的第一个围栏块内容。</summary>
        private static string FencedAfter(string body, string header)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int h = FindHeader(body, header);
            if (h < 0) return "";
            int lineEnd = body.IndexOf('\n', h);
            if (lineEnd < 0) return "";
            var tail = body.Substring(lineEnd + 1).Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = tail.Split('\n');
            int open = 0;
            while (open < lines.Length && string.IsNullOrWhiteSpace(lines[open])) open++;
            if (open >= lines.Length) return "";
            var match = System.Text.RegularExpressions.Regex.Match(lines[open].Trim(), @"^(`{3,}|~{3,})[^`~]*$");
            if (!match.Success) return "";
            var fence = match.Groups[1].Value;
            for (int close = open + 1; close < lines.Length; close++)
            {
                var line = lines[close].Trim();
                if (line.Length >= fence.Length && line.All(c => c == fence[0]))
                    return string.Join("\n", lines.Skip(open + 1).Take(close - open - 1)).TrimEnd();
            }
            return ""; // 未闭合的代码块不能作为可执行配方导入。
        }

        private static int FindHeader(string body, string header)
        {
            string fence = null;
            int offset = 0;
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.Trim();
                if (fence != null)
                {
                    if (line.Length >= fence.Length && line.All(c => c == fence[0])) fence = null;
                }
                else
                {
                    if (line == header) return offset;
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^(`{3,}|~{3,})[^`~]*$");
                    if (m.Success) fence = m.Groups[1].Value;
                }
                offset += raw.Length + 1;
            }
            return -1;
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
                s = Recipe.ToApiSafeName(name);
            return s;
        }

        // ── 候选 ──

        /// <summary>自动片段的旧说明常常只是代码前两行，优先展示代码中已有的中文用途注释。</summary>
        public static string CandidateDescription(Snippet s)
        {
            if (!string.IsNullOrWhiteSpace(s.Description)
                && !string.Equals(s.Origin, "auto", StringComparison.OrdinalIgnoreCase))
                return s.Description;
            foreach (var raw in (s.Code ?? "").Split('\n').Take(20))
            {
                var line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)) line = line.Substring(2).Trim();
                else if (line.StartsWith("#", StringComparison.Ordinal)) line = line.Substring(1).Trim();
                else continue;
                if (line.Length >= 6 && line.Any(c => c >= '\u4e00' && c <= '\u9fff'))
                    return line;
            }
            return string.IsNullOrWhiteSpace(s.Description)
                ? "未记录用途说明，请查看代码确认功能。" : s.Description;
        }

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
