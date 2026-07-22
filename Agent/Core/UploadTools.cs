// TxTools.Agent / Core / UploadTools.cs
// 让 AI 感知与按需读取已上传文件的两个只读工具。
//
//   list_uploaded_files:  列出当前对话所有已上传文件(id/名称/大小/摘要),AI 用它决定要不要精读
//   read_uploaded_file:   按 id 读取更多内容
//                          - xlsx: 参数 sheet(索引或名称) + row_from/row_to 切片
//                          - csv:  row_from/row_to 切片
//                          - text: char_from/char_to 切片
//                          若不指定切片,默认返回前 200 行(表格)/8000 字符(文本)
//
// 设计要点:
// - 都是只读工具(免审批)。UI 层已经在上传时展示了摘要,这里为深度读取而生。
// - convId 通过 AgentLoop.Current?.CurrentConvId 拿(与记忆工具同套路),
//   工具的 lambda 参数由 TxAgentCommand 注入。
// - 单次返回上限 12000 字符,防止大文件塞爆一轮 token。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class ListUploadedFilesTool : TxAgentToolBase
    {
        private readonly Func<string> _getConvId;
        public ListUploadedFilesTool(Func<string> convIdGetter) { _getConvId = convIdGetter; }

        public override string Name { get { return "list_uploaded_files"; } }
        public override string Description
        {
            get
            {
                return "列出用户在当前对话中已上传的所有文件(id/文件名/类型/大小/解析摘要)。" +
                       "用户提到\"我刚上传的文件\"/\"那个 xlsx\"时,先用它拿 file_id,再用 read_uploaded_file 精读。" +
                       "上传时前端已把每份文件的摘要直接注入用户消息前缀,通常无需再调本工具 —— 只在需要找 id 时用。";
            }
        }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema
        {
            get { return JObject.Parse("{ \"type\": \"object\", \"properties\": {} }"); }
        }

        public override string Execute(JObject input)
        {
            var convId = _getConvId != null ? _getConvId() : null;
            var files = UploadStore.ByConv(convId);
            if (files.Count == 0) return "当前对话尚未上传任何文件。";

            var sb = new StringBuilder();
            sb.AppendLine("当前对话已上传 " + files.Count + " 个文件:");
            foreach (var f in files)
            {
                sb.AppendLine();
                sb.AppendLine("[" + f.Id + "]  " + f.OriginalName
                    + "  (" + FileParserService.FormatBytes(f.Size)
                    + (f.RowCount > 0 ? ", " + f.RowCount + "行" : "")
                    + (f.ColCount > 0 ? "×" + f.ColCount + "列" : "")
                    + (f.SheetCount > 0 ? ", " + f.SheetCount + "个sheet" : "")
                    + ")");
                if (!string.IsNullOrEmpty(f.ParseError))
                    sb.AppendLine("  ⚠ 解析警告: " + f.ParseError);
                sb.AppendLine("  摘要: " + FileParserService.TrimToMax(f.ParsedSummary ?? "(未生成)", 300).Replace("\n", " ↵ "));
            }
            return sb.ToString();
        }
    }

    public sealed class ReadUploadedFileTool : TxAgentToolBase
    {
        private const int MaxCharsPerCall = 12000;

        public override string Name { get { return "read_uploaded_file"; } }
        public override string Description
        {
            get
            {
                return "按 file_id 精读已上传文件的具体内容。" +
                       "xlsx: 用 sheet(索引数或名称) + row_from/row_to 切片(1-based,含边界);" +
                       "csv/tsv: row_from/row_to 切片;" +
                       "文本类: char_from/char_to 切片;" +
                       "不指定切片时默认表格前 200 行 / 文本前 8000 字符。" +
                       "单次返回最多 12000 字符,大数据请分片调用。";
            }
        }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\": \"object\", \"properties\": {" +
                    "  \"file_id\": { \"type\": \"string\", \"description\": \"文件 id(list_uploaded_files 或用户消息前缀里可获得)\" }," +
                    "  \"sheet\": { \"type\": \"string\", \"description\": \"仅 xlsx:sheet 索引(0-based 字符串) 或 sheet 名。默认 0\" }," +
                    "  \"row_from\": { \"type\": \"integer\", \"description\": \"起始行(1-based,含)。表格类可选\" }," +
                    "  \"row_to\": { \"type\": \"integer\", \"description\": \"结束行(1-based,含)。表格类可选\" }," +
                    "  \"char_from\": { \"type\": \"integer\", \"description\": \"起始字符(0-based)。文本类可选\" }," +
                    "  \"char_to\": { \"type\": \"integer\", \"description\": \"结束字符(0-based,不含)。文本类可选\" }" +
                    "}, \"required\": [\"file_id\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var fileId = GetString(input, "file_id");
            if (string.IsNullOrWhiteSpace(fileId)) return "参数 file_id 不能为空。";

            var f = UploadStore.Get(fileId);
            if (f == null) return "未找到 file_id: " + fileId + "。用 list_uploaded_files 查看现有文件。";
            if (!File.Exists(f.LocalPath)) return "文件已被清理: " + f.OriginalName;

            var ext = (f.Extension ?? "").ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".xlsx":
                        return ReadXlsx(f, input);
                    case ".csv":
                    case ".tsv":
                        return ReadDelimited(f, input);
                    case ".txt":
                    case ".md":
                    case ".log":
                    case ".json":
                    case ".xml":
                    case "":
                        return ReadText(f, input);
                    default:
                        return "文件类型 " + ext + " 不支持精读。文件大小 " + FileParserService.FormatBytes(f.Size) + "。";
                }
            }
            catch (Exception ex)
            {
                return "读取异常: " + ex.Message;
            }
        }

        // ── xlsx ──

        private static string ReadXlsx(UploadedFile f, JObject input)
        {
            var data = XlsxReader.Read(f.LocalPath);
            if (data.Sheets.Count == 0) return "xlsx 没有可读 sheet。";

            var sheetToken = GetString(input, "sheet", "0");
            XlsxSheet sheet = null;
            int sheetIdx = -1;
            if (int.TryParse(sheetToken, out sheetIdx) && sheetIdx >= 0 && sheetIdx < data.Sheets.Count)
                sheet = data.Sheets[sheetIdx];
            else
            {
                sheet = data.Sheets.FirstOrDefault(s => string.Equals(s.Name, sheetToken, StringComparison.OrdinalIgnoreCase));
                if (sheet == null) sheet = data.Sheets[0];
                sheetIdx = data.Sheets.IndexOf(sheet);
            }

            int total = sheet.RowCount;
            int rf = GetInt(input, "row_from", 1);
            int rt = GetInt(input, "row_to", Math.Min(total, rf + 199));   // 默认前 200 行
            if (rf < 1) rf = 1;
            if (rt < rf) rt = rf;
            if (rt > total) rt = total;

            var sb = new StringBuilder();
            sb.AppendLine("[xlsx] " + f.OriginalName + "  Sheet[" + sheetIdx + "]="
                          + (string.IsNullOrEmpty(sheet.Name) ? "(未命名)" : sheet.Name)
                          + "  行 " + rf + "-" + rt + " / 共 " + total);
            sb.AppendLine();

            for (int i = rf - 1; i < rt; i++)
            {
                sb.Append("[" + (i + 1) + "] ");
                sb.AppendLine(string.Join(" | ", sheet.Rows[i]));
                if (sb.Length > MaxCharsPerCall) break;
            }
            return Cap(sb.ToString());
        }

        // ── csv / tsv ──

        private static string ReadDelimited(UploadedFile f, JObject input)
        {
            var text = FileParserService.ReadTextAuto(f.LocalPath);
            char delim = (f.Extension ?? "").Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelim(text);
            var rows = ParseCsv(text, delim);
            int total = rows.Count;

            int rf = GetInt(input, "row_from", 1);
            int rt = GetInt(input, "row_to", Math.Min(total, rf + 199));
            if (rf < 1) rf = 1;
            if (rt < rf) rt = rf;
            if (rt > total) rt = total;

            var sb = new StringBuilder();
            sb.AppendLine("[" + f.Extension.TrimStart('.') + "] " + f.OriginalName
                          + "  行 " + rf + "-" + rt + " / 共 " + total + "  分隔符=" + (delim == '\t' ? "\\t" : delim.ToString()));
            sb.AppendLine();
            for (int i = rf - 1; i < rt; i++)
            {
                sb.Append("[" + (i + 1) + "] ");
                sb.AppendLine(string.Join(" | ", rows[i]));
                if (sb.Length > MaxCharsPerCall) break;
            }
            return Cap(sb.ToString());
        }

        // ── 文本类 ──

        private static string ReadText(UploadedFile f, JObject input)
        {
            var text = FileParserService.ReadTextAuto(f.LocalPath);
            int total = text.Length;

            int cf = GetInt(input, "char_from", 0);
            int ct = GetInt(input, "char_to", Math.Min(total, cf + 8000));
            if (cf < 0) cf = 0;
            if (ct <= cf) ct = Math.Min(total, cf + 8000);
            if (ct > total) ct = total;

            var slice = text.Substring(cf, ct - cf);
            var head = "[" + (string.IsNullOrEmpty(f.Extension) ? "text" : f.Extension.TrimStart('.'))
                       + "] " + f.OriginalName + "  字符 " + cf + "-" + ct + " / 共 " + total + "\n\n";
            return Cap(head + slice);
        }

        // ── 内联 CSV 解析(与 FileParserService 一致的行为,独立以免形成 internal 循环) ──

        private static char DetectDelim(string text)
        {
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

        private static List<List<string>> ParseCsv(string text, char delim)
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
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        // ── 小工具 ──

        private static int GetInt(JObject input, string key, int fallback)
        {
            if (input == null) return fallback;
            var t = input[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return (int)t; } catch { return fallback; }
        }

        private static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= MaxCharsPerCall ? s : s.Substring(0, MaxCharsPerCall) + "\n\n…(单次返回上限 " + MaxCharsPerCall + " 字符,请分片再读)";
        }
    }
}
