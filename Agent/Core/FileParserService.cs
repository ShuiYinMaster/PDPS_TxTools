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
                        ParseXmlLike(file);
                        break;
                    case ".txt":
                    case ".md":
                    case ".log":
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