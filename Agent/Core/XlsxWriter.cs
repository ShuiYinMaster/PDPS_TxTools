// TxTools.Agent / Core / XlsxWriter.cs
// 通用 .xlsx 写出：手写最小 Open XML (SpreadsheetML)，单元格用 inlineStr (全字符串)，
// 避开 sharedStrings / 数字格式的复杂度，结构对 Office365/WPS 都兼容。UTF-8 无 BOM。
// 参考 ExcelExporter.cs 的 Open XML 骨架，泛化为任意列。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TxTools.Agent.Core
{
    public static class XlsxWriter
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string CtRelPkg = "application/vnd.openxmlformats-package.relationships+xml";
        private const string CtWorkbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
        private const string CtWorksheet = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

        /// <summary>把 headers + rows 写成单 sheet 的 xlsx，返回完整路径。</summary>
        public static string Write(string path, string sheetName,
                                   IList<string> headers, IList<IList<string>> rows)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Utf8NoBom))
            {
                WriteEntry(zip, "[Content_Types].xml", ContentTypes());
                WriteEntry(zip, "_rels/.rels", RootRels());
                WriteEntry(zip, "xl/workbook.xml", Workbook(SanitizeSheetName(sheetName)));
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                WriteEntry(zip, "xl/worksheets/sheet1.xml", Sheet(headers, rows));
            }
            return path;
        }

        private static void WriteEntry(ZipArchive zip, string name, string xml)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), Utf8NoBom))
                w.Write(xml);
        }

        private static string ContentTypes()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Types xmlns=\"" + NsPkgRel.Replace("relationships", "content-types") + "\">" +
                   "<Default Extension=\"rels\" ContentType=\"" + CtRelPkg + "\"/>" +
                   "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                   "<Override PartName=\"/xl/workbook.xml\" ContentType=\"" + CtWorkbook + "\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"" + CtWorksheet + "\"/>" +
                   "</Types>";
        }

        private static string RootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"" + NsPkgRel + "\">" +
                   "<Relationship Id=\"rId1\" Type=\"" + NsRel + "/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string Workbook(string sheetName)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<workbook xmlns=\"" + NsMain + "\" xmlns:r=\"" + NsRel + "\">" +
                   "<sheets><sheet name=\"" + Esc(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                   "</workbook>";
        }

        private static string WorkbookRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"" + NsPkgRel + "\">" +
                   "<Relationship Id=\"rId1\" Type=\"" + NsRel + "/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                   "</Relationships>";
        }

        private static string Sheet(IList<string> headers, IList<IList<string>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\"><sheetData>");

            int r = 1;
            if (headers != null && headers.Count > 0) { AppendRow(sb, r, headers); r++; }
            if (rows != null)
                foreach (var row in rows) { AppendRow(sb, r, row); r++; }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static void AppendRow(StringBuilder sb, int rowIndex, IList<string> cells)
        {
            sb.Append("<row r=\"").Append(rowIndex).Append("\">");
            if (cells != null)
                for (int c = 0; c < cells.Count; c++)
                {
                    var refStr = ColumnLetter(c) + rowIndex;
                    sb.Append("<c r=\"").Append(refStr).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(Esc(cells[c] ?? ""))
                      .Append("</t></is></c>");
                }
            sb.Append("</row>");
        }

        private static string ColumnLetter(int index)
        {
            var sb = new StringBuilder();
            index++; // 1-based
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        private static string SanitizeSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Sheet1";
            foreach (var ch in new[] { '[', ']', '*', '?', '/', '\\', ':' }) name = name.Replace(ch, '_');
            if (name.Length > 31) name = name.Substring(0, 31);
            return name;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
