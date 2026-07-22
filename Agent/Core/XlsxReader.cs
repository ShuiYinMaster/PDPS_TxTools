// TxTools.Agent / Core / XlsxReader.cs   (v2 — 官方 SDK 版)
//
// 之前是自己手写 System.IO.Compression + XmlDocument/XmlReader 解析,遇到 Excel 保存的复杂
// xlsx(带命名空间前缀/带样式引用/inline string 混用)容易漏读。改用微软官方
//   DocumentFormat.OpenXml
// (NuGet: DocumentFormat.OpenXml, 建议 2.20.0 —— 明确支持 .NET Framework 4.6+)
// 代码量降到 100 行,依赖单一 dll,正确性靠 SDK 背书。
//
// XlsxData / XlsxSheet 数据契约保持不变 —— FileParserService / UploadTools 无需改动。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TxTools.Agent.Core
{
    public sealed class XlsxSheet
    {
        public string Name { get; set; }
        /// <summary>行 × 列。空单元格用 ""。列数不齐会补齐到最大列。</summary>
        public List<List<string>> Rows { get; set; }

        public int RowCount { get { return Rows != null ? Rows.Count : 0; } }
        public int ColCount
        {
            get
            {
                if (Rows == null) return 0;
                int max = 0;
                foreach (var r in Rows) if (r.Count > max) max = r.Count;
                return max;
            }
        }

        public XlsxSheet() { Rows = new List<List<string>>(); }
    }

    public sealed class XlsxData
    {
        public List<XlsxSheet> Sheets { get; set; }
        public int PrimarySheetIndex { get; set; }
        public List<string> Warnings { get; set; }

        public XlsxData()
        {
            Sheets = new List<XlsxSheet>();
            Warnings = new List<string>();
            PrimarySheetIndex = 0;
        }

        public XlsxSheet Primary
        {
            get
            {
                if (Sheets == null || Sheets.Count == 0) return null;
                int idx = Math.Max(0, Math.Min(PrimarySheetIndex, Sheets.Count - 1));
                return Sheets[idx];
            }
        }
    }

    public static class XlsxReader
    {
        public static XlsxData Read(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 为空");
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            using (var doc = SpreadsheetDocument.Open(path, false))
                return ReadFromDoc(doc);
        }

        public static XlsxData Read(byte[] content)
        {
            if (content == null || content.Length == 0) throw new ArgumentException("content 为空");
            // MemoryStream 需要保留到 SpreadsheetDocument 关闭
            var ms = new MemoryStream(content, writable: false);
            try
            {
                using (var doc = SpreadsheetDocument.Open(ms, false))
                    return ReadFromDoc(doc);
            }
            finally { ms.Dispose(); }
        }

        // ─────────────────────────────────────────────────────────────

        private static XlsxData ReadFromDoc(SpreadsheetDocument doc)
        {
            var data = new XlsxData();
            var wb = doc.WorkbookPart;
            if (wb == null) { data.Warnings.Add("no WorkbookPart"); return data; }

            // 1) SharedStrings — InnerText 会自动扁平化 rich text 里所有 <t> 节点
            var sst = new List<string>();
            var sstPart = wb.SharedStringTablePart;
            if (sstPart != null && sstPart.SharedStringTable != null)
            {
                foreach (var si in sstPart.SharedStringTable.Elements<SharedStringItem>())
                    sst.Add(si.InnerText ?? "");
            }
            Debug.WriteLine("[XlsxReader] sharedStrings loaded: " + sst.Count);

            // 2) Sheets — Workbook.Sheets 里按顺序列出所有 sheet
            var sheetList = wb.Workbook != null && wb.Workbook.Sheets != null
                ? wb.Workbook.Sheets.Elements<Sheet>().ToList()
                : new List<Sheet>();
            Debug.WriteLine("[XlsxReader] 找到 " + sheetList.Count + " 个 sheet");

            foreach (var sh in sheetList)
            {
                var xsh = new XlsxSheet { Name = sh.Name != null ? sh.Name.Value : "" };
                var relId = sh.Id != null ? sh.Id.Value : null;

                WorksheetPart wsp = null;
                if (!string.IsNullOrEmpty(relId))
                {
                    try { wsp = (WorksheetPart)wb.GetPartById(relId); }
                    catch (Exception ex)
                    {
                        data.Warnings.Add("sheet " + xsh.Name + " 部件未找到: " + ex.Message);
                    }
                }

                if (wsp != null && wsp.Worksheet != null)
                {
                    var sheetData = wsp.Worksheet.Elements<SheetData>().FirstOrDefault();
                    if (sheetData != null) FillRows(xsh, sheetData, sst);
                }

                Debug.WriteLine("[XlsxReader] Sheet \"" + xsh.Name + "\" rows=" + xsh.RowCount + " cols=" + xsh.ColCount);
                data.Sheets.Add(xsh);
            }

            return data;
        }

        /// <summary>遍历 SheetData 的 Row/Cell,按列引用 A1/B2 正确安放到列位置,空 cell 补空串。</summary>
        private static void FillRows(XlsxSheet xsh, SheetData sheetData, List<string> sst)
        {
            int maxCol = 0;

            foreach (var row in sheetData.Elements<Row>())
            {
                var cells = new List<string>();
                foreach (var c in row.Elements<Cell>())
                {
                    int colIdx = ColLetterToIndex(c.CellReference != null ? c.CellReference.Value : null);
                    // 补齐前面缺失的空 cell(如 A1 有值、C1 有值、B1 无 <c> 元素)
                    while (colIdx >= 0 && cells.Count < colIdx) cells.Add("");
                    cells.Add(GetCellText(c, sst));
                }
                if (cells.Count > maxCol) maxCol = cells.Count;
                xsh.Rows.Add(cells);
            }

            // 每行补齐到最大列宽,方便消费方按等长网格处理
            if (maxCol > 0)
                foreach (var r in xsh.Rows)
                    while (r.Count < maxCol) r.Add("");
        }

        private static string GetCellText(Cell c, List<string> sst)
        {
            // SharedString:CellValue 是索引数字
            if (c.DataType != null && c.DataType.Value == CellValues.SharedString)
            {
                var s = c.CellValue != null ? c.CellValue.Text : null;
                int idx;
                if (int.TryParse(s, out idx) && idx >= 0 && idx < sst.Count)
                    return sst[idx];
                return s ?? "";
            }

            // InlineString:<c t="inlineStr"><is><t>...</t></is></c>
            if (c.DataType != null && c.DataType.Value == CellValues.InlineString)
            {
                return c.InlineString != null ? (c.InlineString.InnerText ?? "") : "";
            }

            // Boolean: "1"/"0"
            if (c.DataType != null && c.DataType.Value == CellValues.Boolean)
            {
                var v = c.CellValue != null ? c.CellValue.Text : null;
                return v == "1" ? "TRUE" : "FALSE";
            }

            // Error: 错误码,如 #N/A
            if (c.DataType != null && c.DataType.Value == CellValues.Error)
            {
                return c.CellValue != null ? (c.CellValue.Text ?? "#ERR") : "#ERR";
            }

            // 剩余(Number/Date/Formula/未指定): 直接取缓存值文本
            // 注意:Excel 里日期本质是数字,想显示成日期需要看单元格样式;这里保持原始数值字符串。
            return c.CellValue != null ? (c.CellValue.Text ?? "") : "";
        }

        /// <summary>"A1"/"B2"/"AC12" → 0-based 列索引;空/无效返回 -1。</summary>
        private static int ColLetterToIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return -1;
            int col = 0;
            for (int i = 0; i < cellRef.Length; i++)
            {
                char ch = cellRef[i];
                if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
                else break; // 数字部分开始
            }
            return col - 1;
        }
    }
}