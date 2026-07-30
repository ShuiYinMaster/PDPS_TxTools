// TxTools.Agent / Core / Code/ CodeWorkspace.cs
//
// 源码工作区:根目录管理、路径安全、C# 骨架解析。
//
// ── 为什么要有"工作区"这个概念 ──
//   AI 改源码是高风险操作。不设边界的话,一次路径拼错就可能改到系统目录或别的项目。
//   所以必须先显式 open_workspace 指定根目录,之后所有读写都被限制在这个根之下,
//   路径穿越(..\..\)一律拒绝。
//
// ── 为什么骨架用正则而不是 Roslyn ──
//   Roslyn(Microsoft.CodeAnalysis)是个重依赖,而这个工程已经在 Newtonsoft 上
//   踩过版本冲突的坑。骨架只需要"有哪些类型/成员、在第几行",
//   正则 + 花括号计数足够,且不会因为语法新特性解析失败而整个罢工。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TxTools.Agent.Core
{
    public sealed class CodeSymbol
    {
        /// <summary>class / interface / struct / enum / method / property / field / ctor</summary>
        public string Kind { get; set; }
        public string Name { get; set; }
        /// <summary>去掉修饰符后的声明行,可直接读懂签名。</summary>
        public string Signature { get; set; }
        public int Line { get; set; }
        /// <summary>成员所属类型;类型本身为 null。</summary>
        public string Owner { get; set; }
        /// <summary>类型的结束行,便于按类型读取整段。成员为 0。</summary>
        public int EndLine { get; set; }
    }

    public static class CodeWorkspace
    {
        private static readonly object _sync = new object();
        private static string _root;

        /// <summary>当前工作区根目录。未打开时为 null。</summary>
        public static string Root
        {
            get { lock (_sync) { return _root; } }
        }

        public static bool IsOpen { get { return !string.IsNullOrEmpty(Root); } }

        public static string Open(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "路径不能为空。"; return null; }

                var full = Path.GetFullPath(path.Trim().Trim('"'));

                // 传了 .csproj/.sln 就取其所在目录
                if (File.Exists(full)) full = Path.GetDirectoryName(full);

                if (!Directory.Exists(full)) { error = "目录不存在: " + full; return null; }

                // 防呆:根目录别设成盘符或系统目录
                var trimmed = full.TrimEnd('\\', '/');
                if (trimmed.Length <= 3)
                {
                    error = "拒绝把整个盘符设为工作区根目录,请指定具体项目目录。";
                    return null;
                }
                var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(sys) &&
                    full.StartsWith(sys, StringComparison.OrdinalIgnoreCase))
                {
                    error = "拒绝把 Windows 目录设为工作区根目录。";
                    return null;
                }

                lock (_sync) { _root = full; }
                return full;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public static void Close()
        {
            lock (_sync) { _root = null; }
        }

        /// <summary>
        /// 把相对路径解析成绝对路径,并确保它在工作区内。
        /// 越界返回 null 并给出说明 —— 这是防止 AI 改到工作区外的唯一屏障,不要绕过。
        /// </summary>
        public static string Resolve(string relative, out string error)
        {
            error = null;

            var root = Root;
            if (string.IsNullOrEmpty(root))
            {
                error = "尚未打开工作区。先调用 open_workspace(path=\"项目目录\")。";
                return null;
            }

            if (string.IsNullOrWhiteSpace(relative)) { error = "文件路径不能为空。"; return null; }

            try
            {
                var raw = relative.Trim().Trim('"');
                var full = Path.IsPathRooted(raw)
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(Path.Combine(root, raw));

                var rootNorm = root.TrimEnd('\\', '/') + "\\";
                if (!full.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    error = "路径超出工作区范围: " + full + "\n工作区根目录: " + root;
                    return null;
                }

                return full;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public static string Relative(string fullPath)
        {
            var root = Root;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return fullPath;
            var rootNorm = root.TrimEnd('\\', '/') + "\\";
            return fullPath.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(rootNorm.Length)
                : fullPath;
        }

        // ── 文件枚举 ──

        private static readonly string[] SkipDirs =
            { "\\bin\\", "\\obj\\", "\\.git\\", "\\.vs\\", "\\packages\\", "\\node_modules\\", "\\.svn\\" };

        public static bool IsSkipped(string fullPath)
        {
            var p = "\\" + (fullPath ?? "").Replace('/', '\\').Trim('\\') + "\\";
            foreach (var d in SkipDirs)
                if (p.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static List<string> EnumerateFiles(string pattern = "*.cs", int max = 2000)
        {
            var list = new List<string>();
            var root = Root;
            if (string.IsNullOrEmpty(root)) return list;

            try
            {
                foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    if (IsSkipped(f)) continue;
                    list.Add(f);
                    if (list.Count >= max) break;
                }
            }
            catch { }
            return list;
        }

        // ── C# 骨架解析 ──

        private static readonly Regex TypeRe = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|new|unsafe)\s+)*"
            + @"(class|interface|struct|enum|record)\s+([A-Za-z_]\w*)",
            RegexOptions.Compiled);

        private static readonly Regex MethodRe = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|virtual|override|sealed|abstract|async|extern|new|unsafe|partial)\s+)+"
            + @"[\w<>\[\],\.\?\s]+?\s+([A-Za-z_]\w*)\s*(<[^>(]*>)?\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex PropRe = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|virtual|override|sealed|abstract|new)\s+)+"
            + @"[\w<>\[\],\.\?]+\s+([A-Za-z_]\w*)\s*(\{|=>)",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析 C# 文件骨架:类型 + 成员 + 行号,不含方法体。
        /// 一个 3000 行的文件骨架通常只有 100 行左右 —— 这是"先看结构再读细节"的基础。
        /// </summary>
        public static List<CodeSymbol> Outline(string[] lines)
        {
            var result = new List<CodeSymbol>();
            if (lines == null) return result;

            var typeStack = new List<KeyValuePair<CodeSymbol, int>>();  // 类型 + 其起始深度
            int depth = 0;
            bool inBlockComment = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var line = StripComments(raw, ref inBlockComment);
                if (line.Trim().Length == 0) { depth += Delta(line); continue; }

                var t = line.Trim();

                // 类型声明
                var mt = TypeRe.Match(line);
                if (mt.Success)
                {
                    var sym = new CodeSymbol
                    {
                        Kind = mt.Groups[1].Value,
                        Name = mt.Groups[2].Value,
                        Signature = Compact(t),
                        Line = i + 1,
                        Owner = typeStack.Count > 0 ? typeStack[typeStack.Count - 1].Key.Name : null
                    };
                    result.Add(sym);
                    typeStack.Add(new KeyValuePair<CodeSymbol, int>(sym, depth));
                    depth += Delta(line);
                    continue;
                }

                // 成员:只认类型内部第一层
                if (typeStack.Count > 0 && depth == typeStack[typeStack.Count - 1].Value + 1)
                {
                    var owner = typeStack[typeStack.Count - 1].Key;

                    var mm = MethodRe.Match(line);
                    if (mm.Success && !t.StartsWith("return") && !t.StartsWith("if")
                        && !t.StartsWith("for") && !t.StartsWith("while") && !t.StartsWith("switch"))
                    {
                        result.Add(new CodeSymbol
                        {
                            Kind = string.Equals(mm.Groups[1].Value, owner.Name, StringComparison.Ordinal)
                                   ? "ctor" : "method",
                            Name = mm.Groups[1].Value,
                            Signature = Compact(t),
                            Line = i + 1,
                            Owner = owner.Name
                        });
                    }
                    else
                    {
                        var mp = PropRe.Match(line);
                        if (mp.Success)
                            result.Add(new CodeSymbol
                            {
                                Kind = "property",
                                Name = mp.Groups[1].Value,
                                Signature = Compact(t),
                                Line = i + 1,
                                Owner = owner.Name
                            });
                    }
                }

                int d = Delta(line);
                depth += d;

                // 类型闭合
                while (typeStack.Count > 0 && depth <= typeStack[typeStack.Count - 1].Value)
                {
                    typeStack[typeStack.Count - 1].Key.EndLine = i + 1;
                    typeStack.RemoveAt(typeStack.Count - 1);
                }
            }

            foreach (var kv in typeStack) kv.Key.EndLine = lines.Length;
            return result;
        }

        private static int Delta(string line)
        {
            int n = 0;
            bool inStr = false, inChar = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '{') n++;
                else if (c == '}') n--;
            }
            return n;
        }

        private static string StripComments(string line, ref bool inBlock)
        {
            var sb = new StringBuilder(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                if (inBlock)
                {
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/') { inBlock = false; i++; }
                    continue;
                }
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/') break;
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*') { inBlock = true; i++; continue; }
                sb.Append(line[i]);
            }
            return sb.ToString();
        }

        private static string Compact(string s)
        {
            s = s.TrimEnd('{', ' ', '\t');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length <= 160 ? s : s.Substring(0, 160) + "…";
        }
    }
}
