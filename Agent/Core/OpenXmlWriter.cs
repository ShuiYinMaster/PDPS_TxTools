// TxTools.Agent / Core / OpenXmlWriter.cs
// 用微软官方 DocumentFormat.OpenXml SDK 从零生成 Word 文档 (.docx)。
//
// 为什么不搞样式表:
//   docx 完整样式定义 (StyleDefinitionsPart) 要写几百行 XML,agent 生成的报告类
//   文档看得清即可。这里用 inline RunProperties (Bold + FontSize) 表现标题层级 ——
//   打开就是加粗+大字号,兼容 Word/WPS,视觉直观。真需要严格样式再另加 Styles.xml。
//
// PPT 生成不在这个文件 —— 见 PptxExportTool.cs 里的模板法说明。

using System;
using System.Collections.Generic;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace TxTools.Agent.Core
{
    /// <summary>docx 一段(章节)的数据契约。可含 heading + 多段正文 + 可选表格。</summary>
    public sealed class DocxSection
    {
        public string Heading { get; set; }
        public int HeadingLevel { get; set; } = 2;           // 1..3
        public List<string> Paragraphs { get; set; } = new List<string>();
        /// <summary>可选表格,行 × 列。第一行会作为表头(加粗)。</summary>
        public List<List<string>> Table { get; set; }
    }

    public static class OpenXmlWriter
    {
        // 字号 (半点为单位: 32 = 16pt)
        private const string TitleSize = "40";               // 20pt
        private const string H1Size = "32";                  // 16pt
        private const string H2Size = "28";                  // 14pt
        private const string H3Size = "24";                  // 12pt
        private const string BodySize = "22";                // 11pt

        public static void WriteDocx(string path, string title, IList<DocxSection> sections)
        {
            using (var wordDoc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                var body = new Body();

                if (!string.IsNullOrEmpty(title))
                    body.Append(MakeHeading(title, 0));    // level 0 = 大标题

                if (sections != null)
                {
                    foreach (var s in sections)
                    {
                        if (!string.IsNullOrEmpty(s.Heading))
                            body.Append(MakeHeading(s.Heading, Math.Max(1, Math.Min(3, s.HeadingLevel))));

                        if (s.Paragraphs != null)
                            foreach (var p in s.Paragraphs)
                                body.Append(MakeParagraph(p));

                        if (s.Table != null && s.Table.Count > 0)
                            body.Append(MakeTable(s.Table));
                    }
                }

                // 文档节属性(页面大小/边距) —— 缺省 A4 竖版
                body.Append(new SectionProperties(
                    new PageSize { Width = 11906U, Height = 16838U },          // A4
                    new PageMargin { Top = 1440, Right = 1440U, Bottom = 1440, Left = 1440U, Header = 720U, Footer = 720U, Gutter = 0U }
                ));

                mainPart.Document = new Document(body);
                mainPart.Document.Save();
            }
        }

        // ── 辅助:各种块级元素构造 ──

        /// <summary>level 0=大标题, 1..3=分级标题, 其他=正文加粗。</summary>
        private static Paragraph MakeHeading(string text, int level)
        {
            string size;
            switch (level)
            {
                case 0: size = TitleSize; break;
                case 1: size = H1Size; break;
                case 2: size = H2Size; break;
                case 3: size = H3Size; break;
                default: size = BodySize; break;
            }
            return new Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines { Before = "240", After = "120" }
                ),
                new Run(
                    new RunProperties(new Bold(), new FontSize { Val = size }),
                    new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve }
                )
            );
        }

        private static Paragraph MakeParagraph(string text)
        {
            var p = new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { After = "80" })
            );
            // 段内 \n 转成 Word 里的软换行
            var parts = (text ?? "").Split(new[] { '\n' }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) p.Append(new Run(new Break()));
                p.Append(new Run(
                    new RunProperties(new FontSize { Val = BodySize }),
                    new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve }
                ));
            }
            return p;
        }

        private static Table MakeTable(List<List<string>> rows)
        {
            var table = new Table();

            // 表格边框 + 100% 宽
            table.AppendChild(new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "CCCCCC" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "CCCCCC" }
                )
            ));

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var isHeader = (r == 0);
                var tr = new TableRow();
                foreach (var cell in row)
                {
                    var tc = new TableCell();
                    // 单元格阴影(表头淡灰)
                    if (isHeader)
                    {
                        tc.Append(new TableCellProperties(
                            new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F2F2F2" }
                        ));
                    }
                    var runProps = isHeader
                        ? new RunProperties(new Bold(), new FontSize { Val = BodySize })
                        : new RunProperties(new FontSize { Val = BodySize });
                    tc.Append(new Paragraph(
                        new Run(runProps, new Text(cell ?? "") { Space = SpaceProcessingModeValues.Preserve })
                    ));
                    tr.Append(tc);
                }
                table.Append(tr);
            }

            return table;
        }
    }
}
