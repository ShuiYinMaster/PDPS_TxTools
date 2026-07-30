// TxTools.Agent / Tools / Code / CodeReadTools.cs
//
// 源码【读】的四个工具。核心原则:绝不整文件进上下文。
//
//   open_workspace  指定项目根目录,之后所有读写限定在此
//   code_outline    看骨架:类型/成员/行号,不含方法体
//   code_read       按行段或按符号名读细节
//   code_search     跨文件检索,带上下文行
//
// 一个 3000 行的 .cs 整读约 4 万 token,读两个文件窗口就废了。
// 骨架通常只有 100 行左右 —— 先看骨架定位,再精确读那几十行,是数量级的差别。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class OpenWorkspaceTool : TxAgentToolBase
    {
        public override string Name { get { return "open_workspace"; } }

        public override string Description
        {
            get
            {
                return "打开一个源码工作区(项目根目录)，之后所有代码读写都限定在这个目录下。"
                     + "【改任何源码之前必须先调用本工具】。"
                     + "可以传目录，也可以直接传 .csproj/.sln 路径(会自动取所在目录)。"
                     + "返回目录概览:文件数、主要子目录、找到的项目文件。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"path\": { \"type\":\"string\", \"description\":\"项目根目录，或 .csproj/.sln 路径\" }" +
                    "}, \"required\":[\"path\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var path = GetString(input, "path");
            string err;
            var root = CodeWorkspace.Open(path, out err);
            if (root == null) return "Error: " + err;

            var files = CodeWorkspace.EnumerateFiles("*.cs");
            var sb = new StringBuilder();
            sb.AppendLine("工作区已打开: " + root);
            sb.AppendLine("C# 文件: " + files.Count + " 个" + (files.Count >= 2000 ? " (已达上限)" : ""));

            try
            {
                var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                    .Where(f => !CodeWorkspace.IsSkipped(f)).Take(20).ToList();
                var slns = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Take(5).ToList();

                if (slns.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("解决方案:");
                    foreach (var s in slns) sb.AppendLine("  " + CodeWorkspace.Relative(s));
                }
                if (projects.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("项目文件:");
                    foreach (var p in projects) sb.AppendLine("  " + CodeWorkspace.Relative(p));
                }

                // 按目录聚合，给出结构概览而不是罗列几千个文件名
                var byDir = files
                    .GroupBy(f => Path.GetDirectoryName(CodeWorkspace.Relative(f)) ?? "")
                    .OrderByDescending(g => g.Count())
                    .Take(25)
                    .ToList();

                sb.AppendLine();
                sb.AppendLine("主要目录(按文件数):");
                foreach (var g in byDir)
                    sb.AppendLine("  " + (string.IsNullOrEmpty(g.Key) ? "(根)" : g.Key) + "  — " + g.Count() + " 个");
            }
            catch { }

            sb.AppendLine();
            sb.Append("下一步:用 code_search 找入口，或 code_outline 看某个文件的结构。"
                    + "不要用 code_read 整读大文件。");
            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class CodeOutlineTool : TxAgentToolBase
    {
        public override string Name { get { return "code_outline"; } }

        public override string Description
        {
            get
            {
                return "列出 C# 文件的骨架:命名空间、类型、方法、属性及其行号，不含方法体。"
                     + "【读任何大文件之前先调本工具】—— 一个 3000 行的文件骨架只有百来行，"
                     + "看完就知道该读哪一段，比整读省一个数量级的上下文。"
                     + "拿到行号后用 code_read(start_line/end_line) 或 code_read(symbol=\"方法名\") 读细节。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"file\": { \"type\":\"string\", \"description\":\"相对工作区根的路径\" }," +
                    " \"filter\": { \"type\":\"string\", \"description\":\"可选，只列名字含该串的成员\" }" +
                    "}, \"required\":[\"file\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            string err;
            var full = CodeWorkspace.Resolve(GetString(input, "file"), out err);
            if (full == null) return "Error: " + err;
            if (!File.Exists(full)) return "Error: 文件不存在: " + CodeWorkspace.Relative(full);

            var filter = GetString(input, "filter");

            string[] lines;
            try { lines = File.ReadAllLines(full); }
            catch (Exception ex) { return "Error: 读取失败 - " + ex.Message; }

            var syms = CodeWorkspace.Outline(lines);
            if (!string.IsNullOrWhiteSpace(filter))
                syms = syms.Where(x => x.Name != null &&
                    x.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var sb = new StringBuilder();
            sb.AppendLine(CodeWorkspace.Relative(full) + "  (" + lines.Length + " 行)");

            var ns = lines.FirstOrDefault(l => l.TrimStart().StartsWith("namespace "));
            if (ns != null) sb.AppendLine(ns.Trim());

            if (syms.Count == 0)
            {
                sb.AppendLine("(未解析到符号"
                    + (string.IsNullOrWhiteSpace(filter) ? "" : "，或过滤条件无匹配") + ")");
                return sb.ToString();
            }

            string curType = null;
            foreach (var s in syms)
            {
                bool isType = s.Kind == "class" || s.Kind == "interface"
                           || s.Kind == "struct" || s.Kind == "enum" || s.Kind == "record";

                if (isType)
                {
                    curType = s.Name;
                    sb.AppendLine();
                    sb.Append("L").Append(s.Line);
                    if (s.EndLine > s.Line) sb.Append("-").Append(s.EndLine);
                    sb.Append("  ").AppendLine(s.Signature);
                }
                else
                {
                    sb.Append("    L").Append(s.Line).Append("  ").AppendLine(s.Signature);
                }
            }

            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class CodeReadTool : TxAgentToolBase
    {
        /// <summary>单次读取行数上限。超了会截断并提示 —— 整读大文件是最常见的上下文杀手。</summary>
        public static int MaxLines = 400;

        public override string Name { get { return "code_read"; } }

        public override string Description
        {
            get
            {
                return "读源码。三种用法:"
                     + "(1) symbol=\"方法名\" 读该方法/类型的完整定义(推荐)；"
                     + "(2) start_line + end_line 读指定行段；"
                     + "(3) 都不传则读开头若干行。"
                     + "【不要用它整读大文件】超过 " + MaxLines + " 行会被截断。"
                     + "先 code_outline 看骨架定位，再来读具体那一段。"
                     + "输出带行号，行号可直接用于 code_edit 定位。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"file\": { \"type\":\"string\", \"description\":\"相对工作区根的路径\" }," +
                    " \"symbol\": { \"type\":\"string\", \"description\":\"方法/类型名，读它的完整定义\" }," +
                    " \"start_line\": { \"type\":\"integer\" }," +
                    " \"end_line\": { \"type\":\"integer\" }," +
                    " \"context\": { \"type\":\"integer\", \"description\":\"symbol 模式下额外前后各带几行，默认 3\" }" +
                    "}, \"required\":[\"file\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            string err;
            var full = CodeWorkspace.Resolve(GetString(input, "file"), out err);
            if (full == null) return "Error: " + err;
            if (!File.Exists(full)) return "Error: 文件不存在: " + CodeWorkspace.Relative(full);

            string[] lines;
            try { lines = File.ReadAllLines(full); }
            catch (Exception ex) { return "Error: 读取失败 - " + ex.Message; }

            var symbol = GetString(input, "symbol");
            int start = Int(input, "start_line", 0);
            int end = Int(input, "end_line", 0);
            int ctx = Int(input, "context", 3);

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                var syms = CodeWorkspace.Outline(lines);
                var hits = syms.Where(x => string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase)).ToList();

                if (hits.Count == 0)
                    return "Error: 文件里没有符号 \"" + symbol + "\"。先用 code_outline 看有哪些。";

                if (hits.Count > 1)
                {
                    var sb0 = new StringBuilder();
                    sb0.AppendLine("符号 \"" + symbol + "\" 有 " + hits.Count + " 处，请改用行号读取:");
                    foreach (var h in hits)
                        sb0.AppendLine("  L" + h.Line + "  " + h.Signature);
                    return sb0.ToString();
                }

                var sym = hits[0];
                start = Math.Max(1, sym.Line - ctx);
                end = sym.EndLine > sym.Line
                    ? Math.Min(lines.Length, sym.EndLine + ctx)
                    : FindBlockEnd(lines, sym.Line) + ctx;
            }

            if (start <= 0) start = 1;
            if (end <= 0) end = Math.Min(lines.Length, start + MaxLines - 1);

            start = Math.Max(1, Math.Min(start, lines.Length));
            end = Math.Max(start, Math.Min(end, lines.Length));

            bool truncated = false;
            if (end - start + 1 > MaxLines) { end = start + MaxLines - 1; truncated = true; }

            var sb = new StringBuilder();
            sb.AppendLine(CodeWorkspace.Relative(full) + "  L" + start + "-" + end
                + " / 共 " + lines.Length + " 行");
            sb.AppendLine();

            int width = end.ToString().Length;
            for (int i = start; i <= end; i++)
                sb.Append(i.ToString().PadLeft(width)).Append("| ").AppendLine(lines[i - 1]);

            if (truncated)
            {
                sb.AppendLine();
                sb.Append("…已截断到 ").Append(MaxLines).Append(" 行。需要后面的内容，")
                  .Append("用 start_line=").Append(end + 1).Append(" 继续读，")
                  .Append("或先 code_outline 确认真正需要哪一段。");
            }

            return sb.ToString();
        }

        /// <summary>从声明行往下找配对的花括号结束位置。</summary>
        private static int FindBlockEnd(string[] lines, int startLine)
        {
            int depth = 0;
            bool started = false;

            for (int i = startLine - 1; i < lines.Length; i++)
            {
                foreach (var c in lines[i])
                {
                    if (c == '{') { depth++; started = true; }
                    else if (c == '}')
                    {
                        depth--;
                        if (started && depth <= 0) return i + 1;
                    }
                }
                // 单行属性/表达式体
                if (!started && lines[i].TrimEnd().EndsWith(";")) return i + 1;
            }
            return Math.Min(lines.Length, startLine + 80);
        }

        private static int Int(JObject o, string key, int def)
        {
            if (o == null || o[key] == null) return def;
            int v;
            return int.TryParse(o[key].ToString(), out v) ? v : def;
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class CodeSearchTool : TxAgentToolBase
    {
        public static int MaxHits = 60;

        public override string Name { get { return "code_search"; } }

        public override string Description
        {
            get
            {
                return "在工作区里跨文件搜索文本或正则，返回文件、行号和上下文行。"
                     + "这是【定位代码的首选手段】—— 找某个方法在哪定义、哪些地方调用了它、"
                     + "某个常量用在哪里，都用它，不要靠猜文件名去读。"
                     + "拿到行号后用 code_read 读那一段。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"query\": { \"type\":\"string\", \"description\":\"要搜的文本\" }," +
                    " \"regex\": { \"type\":\"boolean\", \"description\":\"query 是否为正则，默认 false\" }," +
                    " \"file_pattern\": { \"type\":\"string\", \"description\":\"文件通配符，默认 *.cs\" }," +
                    " \"context\": { \"type\":\"integer\", \"description\":\"命中行前后各带几行，默认 1\" }," +
                    " \"max_results\": { \"type\":\"integer\" }" +
                    "}, \"required\":[\"query\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            if (!CodeWorkspace.IsOpen)
                return "Error: 尚未打开工作区。先调用 open_workspace(path=\"项目目录\")。";

            var query = GetString(input, "query");
            if (string.IsNullOrWhiteSpace(query)) return "Error: query 不能为空。";

            bool useRegex = input["regex"] != null && input["regex"].Type == JTokenType.Boolean
                            && (bool)input["regex"];
            var pattern = GetString(input, "file_pattern", "*.cs");
            int ctx = input["context"] != null && input["context"].Type == JTokenType.Integer
                      ? (int)input["context"] : 1;
            int max = input["max_results"] != null && input["max_results"].Type == JTokenType.Integer
                      ? (int)input["max_results"] : MaxHits;
            if (max <= 0 || max > MaxHits) max = MaxHits;

            Regex re = null;
            if (useRegex)
            {
                try { re = new Regex(query, RegexOptions.Compiled); }
                catch (Exception ex) { return "Error: 正则无效 - " + ex.Message; }
            }

            var sb = new StringBuilder();
            int hits = 0, filesWithHits = 0;

            foreach (var f in CodeWorkspace.EnumerateFiles(pattern))
            {
                if (hits >= max) break;

                string[] lines;
                try { lines = File.ReadAllLines(f); }
                catch { continue; }

                bool headerWritten = false;

                for (int i = 0; i < lines.Length && hits < max; i++)
                {
                    bool match = re != null
                        ? re.IsMatch(lines[i])
                        : lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;

                    if (!headerWritten)
                    {
                        sb.AppendLine();
                        sb.AppendLine("── " + CodeWorkspace.Relative(f));
                        headerWritten = true;
                        filesWithHits++;
                    }

                    int from = Math.Max(0, i - ctx);
                    int to = Math.Min(lines.Length - 1, i + ctx);
                    for (int j = from; j <= to; j++)
                        sb.Append(j == i ? ">" : " ")
                          .Append((j + 1).ToString().PadLeft(5)).Append("| ")
                          .AppendLine(Clip(lines[j]));

                    hits++;
                }
            }

            if (hits == 0)
                return "没有匹配 \"" + query + "\" 的内容(" + pattern + ")。"
                     + "换个关键词，或用 regex=true 放宽匹配。";

            var head = "命中 " + hits + " 处，分布在 " + filesWithHits + " 个文件"
                     + (hits >= max ? "(已达上限 " + max + "，可能还有更多)" : "") + ":";
            return head + sb.ToString();
        }

        private static string Clip(string s)
        {
            if (s == null) return "";
            s = s.TrimEnd();
            return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
        }
    }
}
