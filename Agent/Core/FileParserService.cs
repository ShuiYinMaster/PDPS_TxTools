// TxTools.Agent / Core / FileParserService.cs
// 上传文件解析器:按扩展名分发到不同的 reader,产出 UploadedFile 的 ParsedSummary/RowCount/ColCount/SheetCount。
//
// 各类型输出策略(所有摘要都控制在 500-2000 字符,前端可折叠):
//   .xlsx: 多 sheet + 每 sheet 首 8 行 x 首 12 列预览,前面加行列数
//   .csv:  首 8 行 x 首 12 列预览,前面加"检测到分隔符 X, 共 N 行"
//   .txt/.md/.log: 前 1500 字符 + 总行数/字符数
//   .json: 尝试 JObject 解析后 pretty print 前 1500 字符
//   .xml:  纯文本首 1500 字符
//   其他:  仅记录 "文件类型不支持解析" + Size
//
// 完整内容不塞进摘要,由 read_uploaded_file 工具按需读。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public static class FileParserService
    {
        private const int PreviewRows = 8;
        private const int PreviewCols = 12;
        private const int TextPreviewChars = 1500;
        private const int CellPreviewChars = 32;

        /// <summary>解析 UploadedFile 就地填充 ParsedSummary/RowCount/ColCount/SheetCount/ParseError。</summary>
        public static void Parse(UploadedFile file)
        {
            if (file == null) return;
            try
            {
                switch ((file.Extension ?? "").ToLowerInvariant())
                {
                    case ".xlsx":
                        ParseXlsx(file);
                        break;
                    case ".docx":
                        ParseDocx(file);
                        break;
                    case ".pptx":
                        ParsePptx(file);
                        break;
                    case ".csv":
                    case ".tsv":
                        ParseDelimited(file);
                        break;
                    case ".json":
                        ParseJson(file);
                        break;
                    case ".xml":
                    case ".html":
                    case ".htm":
                    case ".xaml":
                    case ".svg":
                        ParseXmlLike(file);
                        break;
                    // === 代码文件 —— 语言感知的智能摘要 ===
                    case ".cs":
                    case ".py":
                    case ".js":
                    case ".jsx":
                    case ".ts":
                    case ".tsx":
                    case ".vue":
                    case ".svelte":
                    case ".java":
                    case ".kt":
                    case ".scala":
                    case ".cpp":
                    case ".cc":
                    case ".cxx":
                    case ".hpp":
                    case ".hxx":
                    case ".c":
                    case ".h":
                    case ".go":
                    case ".rs":
                    case ".rb":
                    case ".php":
                    case ".swift":
                    case ".m":
                    case ".mm":
                    case ".sh":
                    case ".bash":
                    case ".zsh":
                    case ".ps1":
                    case ".psm1":
                    case ".bat":
                    case ".cmd":
                    case ".sql":
                    case ".lua":
                    case ".r":
                    case ".dart":
                        ParseCode(file);
                        break;
                    // === 配置/样式 —— 走纯文本 ===
                    case ".yml":
                    case ".yaml":
                    case ".toml":
                    case ".ini":
                    case ".cfg":
                    case ".conf":
                    case ".env":
                    case ".properties":
                    case ".css":
                    case ".scss":
                    case ".sass":
                    case ".less":
                    case ".gitignore":
                    case ".dockerignore":
                    case ".txt":
                    case ".md":
                    case ".log":
                    case ".rst":
                    case ".tex":
                    case "":
                        ParsePlainText(file);
                        break;
                    default:
                        file.ParsedSummary = "文件类型 " + file.Extension + " 未支持自动解析,仅登记文件路径。大小 " + FormatBytes(file.Size) + "。";
                        break;
                }
            }
            catch (Exception ex)
            {
                file.ParseError = ex.Message;
                file.ParsedSummary = "解析失败: " + ex.Message + " (仍可通过 read_uploaded_file 工具获取原始文本尝试)";
            }
        }

        // ── xlsx ──

        private static void ParseXlsx(UploadedFile f)
        {
            var data = XlsxReader.Read(f.LocalPath);
            f.SheetCount = data.Sheets.Count;

            var primary = data.Primary;
            if (primary != null)
            {
                f.RowCount = primary.RowCount;
                f.ColCount = primary.ColCount;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[xlsx] " + f.OriginalName + "  共 " + data.Sheets.Count + " 个 sheet");
            for (int si = 0; si < data.Sheets.Count; si++)
            {
                var sh = data.Sheets[si];
                sb.AppendLine();
                sb.AppendLine("── Sheet[" + si + "] " + (string.IsNullOrEmpty(sh.Name) ? "(未命名)" : sh.Name)
                              + "  行=" + sh.RowCount + "  列=" + sh.ColCount + " ──");
                AppendTablePreview(sb, sh.Rows);
            }

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("警告: " + string.Join("; ", data.Warnings));
            }

            f.ParsedSummary = TrimToMax(sb.ToString(), 2000);
        }

        // ── csv / tsv ──

        private static void ParseDelimited(UploadedFile f)
        {
            var text = ReadTextAuto(f.LocalPath);
            char delim = DetectDelimiter(text, f.Extension);
            var rows = ParseCsvText(text, delim);

            f.RowCount = rows.Count;
            f.ColCount = rows.Count > 0 ? rows.Max(r => r.Count) : 0;

            var sb = new StringBuilder();
            var delimName = delim == '\t' ? "\\t" : delim.ToString();
            sb.AppendLine("[" + f.Extension.TrimStart('.') + "] " + f.OriginalName
                          + "  分隔符=" + delimName + "  行=" + f.RowCount + "  列=" + f.ColCount);
            AppendTablePreview(sb, rows);
            f.ParsedSummary = TrimToMax(sb.ToString(), 1800);
        }

        private static char DetectDelimiter(string text, string extension)
        {
            if (string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase)) return '\t';
            // 用前 8KB 统计 , ; \t 的出现次数
            int lim = Math.Min(text.Length, 8192);
            int commas = 0, semis = 0, tabs = 0;
            for (int i = 0; i < lim; i++)
            {
                var c = text[i];
                if (c == ',') commas++;
                else if (c == ';') semis++;
                else if (c == '\t') tabs++;
            }
            if (tabs > commas && tabs > semis) return '\t';
            if (semis > commas) return ';';
            return ',';
        }

        // ── docx / pptx: 文本抽取(仅供 agent 读上传文件时理解内容) ──

        private static void ParseDocx(UploadedFile f)
        {
            var sb = new StringBuilder();
            try
            {
                using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(f.LocalPath, false))
                {
                    var body = doc.MainDocumentPart?.Document?.Body;
                    if (body != null)
                    {
                        // 段落
                        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                        {
                            var line = p.InnerText;
                            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
                        }
                    }
                }
            }
            catch (Exception ex) { f.ParseError = ex.Message; }

            var full = sb.ToString();
            f.RowCount = full.Split('\n').Length;
            f.ColCount = 0;
            f.SheetCount = 0;
            f.ParsedSummary = "[docx] " + f.OriginalName + "  \u5171 " + f.RowCount + " \u884c\u6587\u672c\n\n"
                + Truncate(full, 2000);
        }

        private static void ParsePptx(UploadedFile f)
        {
            var sb = new StringBuilder();
            int slideCount = 0;
            try
            {
                using (var pres = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(f.LocalPath, false))
                {
                    var presPart = pres.PresentationPart;
                    if (presPart != null && presPart.SlideParts != null)
                    {
                        foreach (var slidePart in presPart.SlideParts)
                        {
                            slideCount++;
                            sb.AppendLine("── Slide " + slideCount + " ──");
                            // 抽全部 <a:t> 文本
                            foreach (var t in slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                            {
                                var s = t.Text;
                                if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine(s);
                            }
                            // notes 也带上
                            if (slidePart.NotesSlidePart != null)
                            {
                                var notes = new StringBuilder();
                                foreach (var t in slidePart.NotesSlidePart.NotesSlide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                                    if (!string.IsNullOrWhiteSpace(t.Text)) notes.AppendLine(t.Text);
                                if (notes.Length > 0)
                                {
                                    sb.AppendLine("[notes]");
                                    sb.Append(notes.ToString());
                                }
                            }
                            sb.AppendLine();
                        }
                    }
                }
            }
            catch (Exception ex) { f.ParseError = ex.Message; }

            f.RowCount = 0;
            f.ColCount = 0;
            f.SheetCount = slideCount;
            f.ParsedSummary = "[pptx] " + f.OriginalName + "  \u5171 " + slideCount + " \u5f20 slide\n\n"
                + Truncate(sb.ToString(), 3000);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n... [截断,完整内容请用 read_uploaded_file 分片读]";
        }

        /// <summary>轻量 CSV 解析:支持 quoted "..."、内嵌双引号 ""、字段内换行、CRLF/LF。</summary>
        private static List<List<string>> ParseCsvText(string text, char delim)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrEmpty(text)) return rows;

            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // 转义 "" 或结束 quote
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                }
                else
                {
                    if (c == '"' && field.Length == 0) inQuotes = true;
                    else if (c == delim) { row.Add(field.ToString()); field.Length = 0; }
                    else if (c == '\r')
                    {
                        row.Add(field.ToString()); field.Length = 0;
                        rows.Add(row); row = new List<string>();
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    }
                    else if (c == '\n')
                    {
                        row.Add(field.ToString()); field.Length = 0;
                        rows.Add(row); row = new List<string>();
                    }
                    else field.Append(c);
                }
            }
            // 收尾最后一行(不以换行结尾也算)
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        // ── JSON ──

        private static void ParseJson(UploadedFile f)
        {
            var text = ReadTextAuto(f.LocalPath);
            var sb = new StringBuilder();
            sb.AppendLine("[json] " + f.OriginalName + "  大小 " + FormatBytes(f.Size));
            try
            {
                var tok = JToken.Parse(text);
                var kind = tok.Type.ToString().ToLowerInvariant();
                sb.AppendLine("根节点类型: " + kind);
                if (tok is JArray arr) sb.AppendLine("数组长度: " + arr.Count);
                else if (tok is JObject obj) sb.AppendLine("对象字段数: " + obj.Count + "  字段: " + string.Join(", ", obj.Properties().Take(10).Select(p => p.Name)));

                sb.AppendLine();
                sb.AppendLine("预览:");
                sb.AppendLine(TrimToMax(JsonConvert.SerializeObject(tok, Formatting.Indented), TextPreviewChars));
            }
            catch (Exception ex)
            {
                sb.AppendLine("JSON 解析失败: " + ex.Message);
                sb.AppendLine();
                sb.AppendLine("原文预览:");
                sb.AppendLine(TrimToMax(text, TextPreviewChars));
            }
            f.ParsedSummary = TrimToMax(sb.ToString(), 2000);
        }

        // ── XML ──

        private static void ParseXmlLike(UploadedFile f)
        {
            var text = ReadTextAuto(f.LocalPath);
            var sb = new StringBuilder();
            sb.AppendLine("[xml] " + f.OriginalName + "  大小 " + FormatBytes(f.Size));
            sb.AppendLine();
            sb.AppendLine("预览:");
            sb.AppendLine(TrimToMax(text, TextPreviewChars));
            f.ParsedSummary = TrimToMax(sb.ToString(), 2000);
        }

        // ── 纯文本 ──

        private static void ParsePlainText(UploadedFile f)
        {
            var text = ReadTextAuto(f.LocalPath);
            int lineCount = text.Length == 0 ? 0 : 1;
            foreach (var c in text) if (c == '\n') lineCount++;

            var sb = new StringBuilder();
            sb.AppendLine("[" + (string.IsNullOrEmpty(f.Extension) ? "text" : f.Extension.TrimStart('.'))
                          + "] " + f.OriginalName + "  " + lineCount + " 行 / " + text.Length + " 字符");
            sb.AppendLine();
            sb.AppendLine("预览:");
            sb.AppendLine(TrimToMax(text, TextPreviewChars));
            f.RowCount = lineCount;
            f.ParsedSummary = TrimToMax(sb.ToString(), 2000);
        }

        // ── 代码文件 —— 语言感知的智能摘要 ──

        /// <summary>
        /// 代码文件解析:
        ///   [语言] 文件名  N 行 (M 有效行,注释/空 K)
        ///   依赖 (imports/using/require/#include 顶部扫 60 行)
        ///   顶层符号 (class/interface/struct/enum + function/method + arrow const)
        ///   前 30 行预览
        /// AI 拿到摘要就知道文件长什么样,不确定细节再 read_uploaded_file。
        /// </summary>
        private static void ParseCode(UploadedFile f)
        {
            var text = ReadTextAuto(f.LocalPath);
            var lines = text.Split('\n');
            var lang = LanguageFromExt(f.Extension);
            var effective = CountEffectiveLines(lines, lang);

            var imports = ExtractImports(lines, lang);
            var symbols = ExtractSymbols(text, lang);

            var sb = new StringBuilder();
            sb.Append("[").Append(lang).Append("] ").Append(f.OriginalName)
              .Append("  ").Append(lines.Length).Append(" 行");
            if (effective > 0 && effective != lines.Length)
                sb.Append(" (").Append(effective).Append(" 有效行,注释/空 ")
                  .Append(lines.Length - effective).Append(")");
            sb.AppendLine();

            if (imports.Count > 0)
            {
                sb.AppendLine();
                sb.Append("依赖 (").Append(imports.Count).AppendLine("):");
                int shown = 0;
                foreach (var imp in imports)
                {
                    sb.Append("  ").AppendLine(imp);
                    if (++shown >= 10)
                    {
                        if (imports.Count > 10)
                            sb.Append("  ...(").Append(imports.Count - 10).AppendLine(" more)");
                        break;
                    }
                }
            }

            if (symbols.Count > 0)
            {
                sb.AppendLine();
                sb.Append("顶层符号 (").Append(symbols.Count).AppendLine("):");
                int shown = 0;
                foreach (var sym in symbols)
                {
                    sb.Append("  ").AppendLine(sym);
                    if (++shown >= 25)
                    {
                        if (symbols.Count > 25)
                            sb.Append("  ...(").Append(symbols.Count - 25).AppendLine(" more)");
                        break;
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("前 30 行预览:");
            int previewLines = Math.Min(30, lines.Length);
            for (int i = 0; i < previewLines; i++)
                sb.AppendLine(lines[i].TrimEnd('\r'));
            if (lines.Length > 30)
                sb.Append("... (还有 ").Append(lines.Length - 30)
                  .AppendLine(" 行,用 read_uploaded_file 读完整内容)");

            f.RowCount = lines.Length;
            f.ParsedSummary = TrimToMax(sb.ToString(), 3000);
        }

        /// <summary>把扩展名映射到语言标识,给 UI/AI 一个明确的类型 tag。</summary>
        private static string LanguageFromExt(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".cs": return "csharp";
                case ".py": return "python";
                case ".js": case ".jsx": case ".vue": case ".svelte": return "javascript";
                case ".ts": case ".tsx": return "typescript";
                case ".java": return "java";
                case ".kt": return "kotlin";
                case ".scala": return "scala";
                case ".cpp": case ".cc": case ".cxx": case ".hpp": case ".hxx": return "cpp";
                case ".c": case ".h": return "c";
                case ".go": return "go";
                case ".rs": return "rust";
                case ".rb": return "ruby";
                case ".php": return "php";
                case ".swift": return "swift";
                case ".m": case ".mm": return "objc";
                case ".sh": case ".bash": case ".zsh": return "shell";
                case ".ps1": case ".psm1": return "powershell";
                case ".bat": case ".cmd": return "batch";
                case ".sql": return "sql";
                case ".lua": return "lua";
                case ".r": return "r";
                case ".dart": return "dart";
                default: return "code";
            }
        }

        /// <summary>统计非空、非纯注释行。仅对单行注释精确;块注释里的行按普通处理。</summary>
        private static int CountEffectiveLines(string[] lines, string lang)
        {
            string singleComment = "//";
            if (lang == "python" || lang == "shell" || lang == "ruby") singleComment = "#";
            else if (lang == "sql") singleComment = "--";
            else if (lang == "batch") singleComment = "REM";

            int count = 0;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith(singleComment, StringComparison.Ordinal)) continue;
                count++;
            }
            return count;
        }

        /// <summary>从顶部 60 行扫 import/using/require/#include 类语句。</summary>
        private static List<string> ExtractImports(string[] lines, string lang)
        {
            var result = new List<string>();
            System.Text.RegularExpressions.Regex regex;

            switch (lang)
            {
                case "csharp":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*using\s+[\w\.]+\s*;"); break;
                case "python":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*(?:from\s+[\w\.]+\s+)?import\s+[\w\.\*,\s]+"); break;
                case "javascript": case "typescript":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*(?:import\s+.*from\s+['""].+['""]|const\s+\w+\s*=\s*require\s*\()"); break;
                case "java": case "kotlin":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*import\s+[\w\.]+\s*;?"); break;
                case "cpp": case "c": case "objc":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*#include\s+[<""].+[>""]"); break;
                case "go":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*import\s+[\(""]"); break;
                case "rust":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*use\s+[\w:]+\s*;"); break;
                case "ruby":
                    regex = new System.Text.RegularExpressions.Regex(@"^\s*(?:require|require_relative)\s+['""]"); break;
                default: return result;
            }

            int scan = Math.Min(60, lines.Length);
            for (int i = 0; i < scan; i++)
            {
                var line = lines[i].TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                if (regex.IsMatch(line)) result.Add(line);
            }
            return result;
        }

        /// <summary>
        /// 顶层符号:class/interface/struct/enum + function/method + JS/TS arrow const。
        /// 用简单正则,不追求 100% 精确;目的是给 AI 一个文件目录级别的概览。
        /// </summary>
        private static List<string> ExtractSymbols(string text, string lang)
        {
            var result = new List<string>();
            var Rx = new Func<string, System.Text.RegularExpressions.Regex>(p =>
                new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.Multiline));

            switch (lang)
            {
                case "csharp":
                case "java":
                case "kotlin":
                    // 类型声明
                    foreach (System.Text.RegularExpressions.Match m in Rx(
                        @"^\s*(?:public|internal|private|protected)?\s*(?:static\s+|abstract\s+|sealed\s+|partial\s+)*(class|interface|struct|enum|record)\s+(\w+)").Matches(text))
                        result.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                    // 方法(粗略:有访问修饰符 + 返回类型 + 名 + 括号)
                    foreach (System.Text.RegularExpressions.Match m in Rx(
                        @"^\s*(?:public|internal|private|protected)\s+(?:static\s+|virtual\s+|override\s+|async\s+|abstract\s+)*[\w<>\[\]\?,\s]+?\s+(\w+)\s*\(").Matches(text))
                    {
                        var name = m.Groups[1].Value;
                        if (!IsReservedWord(name)) result.Add("  " + name + "()");
                    }
                    break;

                case "python":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^class\s+(\w+)").Matches(text))
                        result.Add("class " + m.Groups[1].Value);
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:async\s+)?def\s+(\w+)\s*\(").Matches(text))
                    {
                        // 用缩进区分顶层 def 和 方法
                        var indented = m.Value.StartsWith(" ") || m.Value.StartsWith("\t");
                        result.Add((indented ? "  " : "") + "def " + m.Groups[1].Value + "()");
                    }
                    break;

                case "javascript":
                case "typescript":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:export\s+(?:default\s+)?)?(?:abstract\s+)?class\s+(\w+)").Matches(text))
                        result.Add("class " + m.Groups[1].Value);
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:export\s+(?:default\s+)?)?(?:async\s+)?function\s+\*?\s*(\w+)").Matches(text))
                        result.Add("function " + m.Groups[1].Value + "()");
                    // arrow function 赋给 const
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:export\s+)?const\s+(\w+)\s*=\s*(?:async\s*)?[\(<]").Matches(text))
                        result.Add("const " + m.Groups[1].Value + " = (…) => …");
                    // interface / type (TS)
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:export\s+)?(interface|type|enum)\s+(\w+)").Matches(text))
                        result.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                    break;

                case "cpp":
                case "c":
                case "objc":
                    // class/struct/union
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(class|struct|union|enum)\s+(\w+)").Matches(text))
                        result.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                    // 函数定义(粗略)
                    foreach (System.Text.RegularExpressions.Match m in Rx(
                        @"^(?:[\w:<>,\s\*&]+)\s+(\w+)\s*\([^)]*\)\s*(?:const)?\s*\{?").Matches(text))
                    {
                        var name = m.Groups[1].Value;
                        if (!IsReservedWord(name)) result.Add("  " + name + "()");
                    }
                    break;

                case "go":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^func\s+(?:\([^)]+\)\s+)?(\w+)\s*\(").Matches(text))
                        result.Add("func " + m.Groups[1].Value + "()");
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^type\s+(\w+)\s+(struct|interface)").Matches(text))
                        result.Add(m.Groups[2].Value + " " + m.Groups[1].Value);
                    break;

                case "rust":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:pub\s+)?fn\s+(\w+)").Matches(text))
                        result.Add("fn " + m.Groups[1].Value + "()");
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:pub\s+)?(struct|enum|trait|impl)\s+(\w+)").Matches(text))
                        result.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                    break;

                case "ruby":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(class|module)\s+(\w+)").Matches(text))
                        result.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*def\s+(?:self\.)?(\w+)").Matches(text))
                        result.Add("  def " + m.Groups[1].Value + "()");
                    break;

                case "php":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:abstract\s+|final\s+)?(class|interface|trait)\s+(\w+)").Matches(text))
                        result.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:public\s+|private\s+|protected\s+)?(?:static\s+)?function\s+(\w+)").Matches(text))
                        result.Add("  function " + m.Groups[1].Value + "()");
                    break;

                case "sql":
                    foreach (System.Text.RegularExpressions.Match m in Rx(
                        @"^\s*(CREATE\s+(?:OR\s+REPLACE\s+)?(?:TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER|INDEX))\s+(?:IF\s+NOT\s+EXISTS\s+)?([\w\.]+)").Matches(text))
                        result.Add(m.Groups[1].Value.ToUpperInvariant() + " " + m.Groups[2].Value);
                    break;

                case "shell":
                case "powershell":
                    foreach (System.Text.RegularExpressions.Match m in Rx(@"^\s*(?:function\s+)?(\w+)\s*\(\s*\)\s*\{").Matches(text))
                        result.Add("function " + m.Groups[1].Value + "()");
                    break;
            }

            return result;
        }

        /// <summary>过滤符号提取里会把 if/for/return 之类当函数名的误命中。</summary>
        private static bool IsReservedWord(string s)
        {
            switch (s)
            {
                case "if": case "else": case "for": case "foreach": case "while": case "do":
                case "switch": case "case": case "catch": case "try": case "finally":
                case "return": case "throw": case "using": case "new": case "typeof":
                case "sizeof": case "goto": case "break": case "continue":
                    return true;
            }
            return false;
        }

        // ── 表格预览渲染 ──

        private static void AppendTablePreview(StringBuilder sb, List<List<string>> rows)
        {
            if (rows == null || rows.Count == 0) { sb.AppendLine("(空)"); return; }

            int rMax = Math.Min(rows.Count, PreviewRows);
            for (int i = 0; i < rMax; i++)
            {
                var r = rows[i];
                int cMax = Math.Min(r.Count, PreviewCols);
                var cells = new string[cMax];
                for (int j = 0; j < cMax; j++) cells[j] = TrimCell(r[j]);
                sb.Append("  [" + (i + 1) + "]  ");
                sb.AppendLine(string.Join(" | ", cells));
            }
            if (rows.Count > PreviewRows)
                sb.AppendLine("  … (仅显示前 " + PreviewRows + " 行,共 " + rows.Count + " 行)");
        }

        private static string TrimCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            return s.Length <= CellPreviewChars ? s : s.Substring(0, CellPreviewChars) + "…";
        }

        // ── 文本读取:UTF-8 优先,失败尝试 GBK ──

        internal static string ReadTextAuto(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // 检查 UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            // 尝试 UTF-8 严格解码
            try
            {
                var strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes);
            }
            catch
            {
                // 失败 → GBK 回退(简中 Windows 常见)
                try
                {
                    var gbk = Encoding.GetEncoding("GBK");
                    return gbk.GetString(bytes);
                }
                catch
                {
                    return Encoding.UTF8.GetString(bytes); // 兜底(可能有替换字符)
                }
            }
        }

        // ── 通用小工具 ──

        internal static string TrimToMax(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "\n…(已截断)";
        }

        internal static string FormatBytes(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0.#") + " KB";
            return (n / 1024.0 / 1024.0).ToString("0.##") + " MB";
        }
    }
}
