// TxTools.Agent / Core / SnippetStore.cs
// 代码片段库：把摸索出的可用 run_csharp 代码持久化到 snippets.json，跨对话检索复用。
// 这是给 codegen 路径的"方法记忆"——摸清一次 API、存下可用代码，以后先查库、命中就直接用。
// 路径策略与 RecipeStore/KeyStore 一致(优先插件目录，回退 LocalAppData)。
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
        public DateTime CreatedUtc { get; set; }
        /// <summary>语义标签(自动从代码提取)，如 robot,label,alignment,weld 等。</summary>
        public List<string> Tags { get; set; }
        /// <summary>被 get_snippet 调用次数，越大说明越有用。</summary>
        public int SuccessCount { get; set; }
        /// <summary>最后一次被 get_snippet 调用的时间。</summary>
        public DateTime LastUsedUtc { get; set; }
        /// <summary>来源：auto = 自动存，manual = AI 主动 save_snippet。</summary>
        public string Origin { get; set; }
        /// <summary>来自哪段对话(conv_xxx)，方便回溯。</summary>
        public string ConvId { get; set; }

        public Snippet()
        {
            Tags = new List<string>();
        }
    }

    public static class SnippetStore
    {
        private const string FileName = "snippets.json";

        public static List<Snippet> All()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var list = JsonConvert.DeserializeObject<List<Snippet>>(File.ReadAllText(path, Encoding.UTF8));
                    if (list != null)
                    {
                        // 兼容旧版无 Tags 字段的片段
                        foreach (var s in list)
                            if (s.Tags == null) s.Tags = new List<string>();
                        return list;
                    }
                }
                catch { }
            }
            return new List<Snippet>();
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
            var all = All();
            all.RemoveAll(s => string.Equals(s.Name, snippet.Name, StringComparison.OrdinalIgnoreCase));
            if (snippet.CreatedUtc == default(DateTime)) snippet.CreatedUtc = DateTime.UtcNow;
            if (snippet.Tags == null) snippet.Tags = new List<string>();
            all.Add(snippet);
            SaveAll(all);
        }

        /// <summary>增加片段的 SuccessCount 并更新 LastUsedUtc（复用计数）。</summary>
        public static void IncrementUsage(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var all = All();
            var s = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (s == null) return;
            s.SuccessCount++;
            s.LastUsedUtc = DateTime.UtcNow;
            SaveAll(all);
        }

        /// <summary>检测是否已有非常相似的片段（避免自动存重复代码）。</summary>
        public static bool HasSimilarCode(string code, double threshold)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            var all = All();
            var normalized = Normalize(code);
            foreach (var s in all)
            {
                if (string.IsNullOrWhiteSpace(s.Code)) continue;
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
        public static List<Snippet> TopN(int n)
        {
            var all = All();
            return all.OrderByDescending(s => s.SuccessCount * 10 + (int)((DateTime.UtcNow - s.CreatedUtc).TotalDays < 7 ? 5 : 0))
                       .Take(n)
                       .ToList();
        }

        public static bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var all = All();
            int n = all.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return false;
            SaveAll(all);
            return true;
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
                if (trimmed.StartsWith("log("))
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

        private static void SaveAll(List<Snippet> all)
        {
            var json = JsonConvert.SerializeObject(all ?? new List<Snippet>(), Formatting.Indented);
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
