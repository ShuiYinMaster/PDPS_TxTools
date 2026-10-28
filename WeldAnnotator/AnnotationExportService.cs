using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TxTools.ExportGun;
using Theme = TxTools.Common.FormUiKit.Theme;
using D = System.Drawing;
using DI = System.Drawing.Imaging;

namespace TxTools.WeldAnnotator
{
    public sealed class AnnotationExportResult
    {
        public int AnnotatedPoints { get; set; }
        public int ShapeCount { get; set; }
        public int Warnings { get; set; }
    }

    public sealed class AnnotationExportOptions
    {
        public bool NewSheet { get; set; } = true;
        public bool WriteList { get; set; } = true;
        public LabelNamingMode LabelMode { get; set; } = LabelNamingMode.Sequence;
        public string Prefix { get; set; } = "";
        public string Suffix { get; set; } = "";
    }

    /// <summary>窗口与 Agent 共用的可编辑 Excel 标注导出。必须在 PS 主线程执行。</summary>
    public static class AnnotationExportService
    {
        public const string DefaultCategory = "二层板点焊";

        private static string GetAnnotationLabel(WeldAnnotationPoint pt, int ordinal, AnnotationExportOptions options)
        {
            switch (options.LabelMode)
            {
                case LabelNamingMode.PointName: return pt.Name ?? "";
                case LabelNamingMode.Prefix: return options.Prefix + ordinal;
                case LabelNamingMode.Suffix: return ordinal + options.Suffix;
                case LabelNamingMode.SeqAndName: return ordinal + "-" + pt.Name;
                default: return ordinal.ToString();
            }
        }

        public static AnnotationExportResult ExportToActiveExcel(D.Bitmap bmp, List<WeldAnnotationPoint> points,
            Dictionary<int, string> categories, AnnotationStyle style, AnnotationExportOptions options,
            Action<string, string> log = null, Action<int> progress = null)
        {
            if (bmp == null) throw new ArgumentNullException(nameof(bmp));
            if (points == null) throw new ArgumentNullException(nameof(points));
            categories = categories ?? new Dictionary<int, string>();
            options = options ?? new AnnotationExportOptions();
            var sink = log ?? ((level, message) => { });
            int warnings = 0, annotatedPoints = 0;
            log = (level, message) => { if (level == "WARN" || level == "ERR") warnings++; sink(level, message); };
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");

            dynamic xlApp = null;
            bool savedScreenUpdating = true;
            try
            {
                bmp.Save(tmp, DI.ImageFormat.Png);
                try { xlApp = Marshal.GetActiveObject("Excel.Application"); }
                catch { throw new Exception("未检测到已打开的 Excel，请先打开工作簿"); }

                dynamic wb = xlApp.ActiveWorkbook;
                if (wb == null) throw new Exception("Excel 中没有活动工作簿");

                try { savedScreenUpdating = (bool)xlApp.ScreenUpdating; xlApp.ScreenUpdating = false; } catch { }

                dynamic ws;
                if (options.NewSheet)
                {
                    dynamic sheets = wb.Sheets;
                    ws = sheets.Add(System.Reflection.Missing.Value, sheets[sheets.Count],
                        System.Reflection.Missing.Value, System.Reflection.Missing.Value);
                    ws.Name = "焊点标注_" + DateTime.Now.ToString("MMddHHmmssfff");
                }
                else { ws = wb.ActiveSheet; }

                double targetLeft = -1, targetTop = -1, targetWidth = -1, targetHeight = -1;
                bool scaleToTarget = false;
                try
                {
                    dynamic sel = xlApp.Selection;
                    int selCellCount = 0; try { selCellCount = (int)sel.Cells.Count; } catch { }
                    bool selMerged = false; try { selMerged = (bool)sel.MergeCells; } catch { }
                    targetLeft = (double)sel.Left;
                    targetTop  = (double)sel.Top;
                    if (selCellCount == 1 && !selMerged) { scaleToTarget = false; }
                    else { targetWidth = (double)sel.Width; targetHeight = (double)sel.Height; scaleToTarget = targetWidth > 4 && targetHeight > 4; }
                }
                catch { targetLeft = -1; targetTop = -1; scaleToTarget = false; }

                const int msoFalsePic = 0;
                dynamic a1 = ws.Cells[1, 1];
                double a1Left = (double)a1.Left, a1Top = (double)a1.Top;
                dynamic pic = ws.Shapes.AddPicture(tmp, msoFalsePic, true, a1Left, a1Top, -1, -1);

                const double maxWidthPt = 800.0;
                if ((double)pic.Width > maxWidthPt)
                {
                    double r = maxWidthPt / (double)pic.Width;
                    pic.Width = maxWidthPt;
                    pic.Height = (double)pic.Height * r;
                }

                string picName = "WPPicture_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                try { pic.Name = picName; } catch { }

                var shapeNamesForGroup = new List<string> { picName };
                double picLeft   = (double)pic.Left, picTop = (double)pic.Top;
                double picWidth  = (double)pic.Width, picHeight = (double)pic.Height;
                double sx = picWidth / bmp.Width, sy = picHeight / bmp.Height;

                const int msoShapeOval           = 9;
                const int msoTextOrientationHoriz = 1;
                const int msoFalse               = 0;
                const int msoTrue                = -1;
                const int xlHAlignCenter         = -4108;
                const int xlVAlignCenter         = -4108;
                const int msoConnectorStraight   = 1;
                const int xlFreeFloating         = 3;

                try { pic.Placement = xlFreeFloating; } catch { }

                int oleDot       = D.ColorTranslator.ToOle(style.DotColor);
                int oleLine      = D.ColorTranslator.ToOle(style.LineColor);
                int oleBoxFill   = D.ColorTranslator.ToOle(style.BoxFillColor);
                int oleBoxBorder = D.ColorTranslator.ToOle(style.BoxBorderColor);
                int oleText      = D.ColorTranslator.ToOle(style.TextColor);

                double dotR      = style.DotRadius;
                const double BOX_W_PT = 28.0, BOX_H_PT = 18.0;
                int shapeCount = 0;
                int inVp = points.Count(p => p.InViewport);
                int processed = 0;

                var placedPts = points.Where(p => p.InViewport).ToList();
                var labelPos  = new Dictionary<int, Tuple<double, double>>();

                if (placedPts.Count > 0)
                {
                    var pointEdges = placedPts.Select((p, idx) =>
                    {
                        double cx = picLeft + p.ScreenX * sx, cy = picTop + p.ScreenY * sy;
                        double dL = cx - picLeft, dR = picLeft + picWidth - cx;
                        double dT = cy - picTop,   dB = picTop + picHeight - cy;
                        double minD = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
                        int side = minD == dT ? 0 : minD == dR ? 1 : minD == dB ? 2 : 3;
                        return new { Pt = p, Cx = cx, Cy = cy, Side = side };
                    }).ToList();

                    foreach (int sideId in new[] { 0, 1, 2, 3 })
                    {
                        var grp = pointEdges.Where(e => e.Side == sideId).ToList();
                        if (grp.Count == 0) continue;
                        if (sideId == 0 || sideId == 2) grp = grp.OrderBy(e => e.Cx).ToList();
                        else grp = grp.OrderBy(e => e.Cy).ToList();

                        int n = grp.Count;
                        for (int i = 0; i < n; i++)
                        {
                            var e = grp[i];
                            double t = n == 1 ? 0.5 : (i + 0.5) / n;
                            double bx, by;
                            if (sideId == 0)      { bx = picLeft + t * (picWidth - BOX_W_PT); by = picTop - BOX_H_PT - 4; }
                            else if (sideId == 2) { bx = picLeft + t * (picWidth - BOX_W_PT); by = picTop + picHeight + 4; }
                            else if (sideId == 3) { bx = picLeft - BOX_W_PT - 4; by = picTop + t * (picHeight - BOX_H_PT); }
                            else                 { bx = picLeft + picWidth + 4;  by = picTop + t * (picHeight - BOX_H_PT); }
                            labelPos[e.Pt.Index] = Tuple.Create(bx, by);
                        }
                    }
                }

                int ordinal = 0;
                foreach (var pt in points)
                {
                    if (!pt.InViewport) continue;
                    ordinal++;

                    double cx = picLeft + pt.ScreenX * sx, cy = picTop + pt.ScreenY * sy;
                    double bx, by;
                    if (labelPos.TryGetValue(pt.Index, out var bp)) { bx = bp.Item1; by = bp.Item2; }
                    else { bx = cx + style.OffsetX; by = cy - style.OffsetY; }

                    dynamic dotShape = null, boxShape = null;

                    if (!categories.TryGetValue(pt.Index, out string cat) || string.IsNullOrEmpty(cat))
                        cat = DefaultCategory;
                    int shapeId = msoShapeOval;
                    if (style.CategoryShapes != null && style.CategoryShapes.ContainsKey(cat))
                        shapeId = style.CategoryShapes[cat];
                    bool filled = true;
                    if (style.CategoryFilled != null && style.CategoryFilled.ContainsKey(cat))
                        filled = style.CategoryFilled[cat];

                    try
                    {
                        dotShape = ws.Shapes.AddShape(shapeId, cx - dotR, cy - dotR, dotR * 2.0, dotR * 2.0);
                        if (filled)
                        {
                            try { dotShape.Fill.ForeColor.RGB = oleDot; dotShape.Fill.Solid(); } catch { }
                            try { dotShape.Line.ForeColor.RGB = oleDot; dotShape.Line.Weight = 1.0; } catch { }
                        }
                        else
                        {
                            try { dotShape.Fill.Visible = msoFalse; } catch { }
                            try { dotShape.Line.ForeColor.RGB = oleDot; dotShape.Line.Weight = 1.75; } catch { }
                        }
                        try { dotShape.Placement = xlFreeFloating; } catch { }
                        try { dotShape.Name = "WPDot_" + pt.Index; shapeNamesForGroup.Add("WPDot_" + pt.Index); } catch { }
                        shapeCount++;
                    }
                    catch (Exception ex) { log("WARN", "AddShape 失败: " + ex.Message); }

                    try
                    {
                        string label = GetAnnotationLabel(pt, ordinal, options);
                        double estW = Math.Max(BOX_W_PT, label.Length * 6.5 + 6.0);
                        boxShape = ws.Shapes.AddTextbox(msoTextOrientationHoriz, bx, by, estW, BOX_H_PT);
                        try { boxShape.Fill.ForeColor.RGB = oleBoxFill; boxShape.Fill.Solid(); } catch { }
                        try { boxShape.Line.ForeColor.RGB = oleBoxBorder; boxShape.Line.Weight = 1.0; } catch { }
                        bool setOk = false;
                        try { boxShape.TextFrame.Characters().Text = label; setOk = true; } catch { }
                        if (!setOk) try { boxShape.TextFrame2.TextRange.Text = label; } catch { }
                        try { boxShape.TextFrame.HorizontalAlignment = xlHAlignCenter; } catch { }
                        try { boxShape.TextFrame.VerticalAlignment   = xlVAlignCenter; } catch { }
                        try
                        {
                            double pad = style.BoxPadding;
                            boxShape.TextFrame.MarginLeft = pad; boxShape.TextFrame.MarginRight = pad;
                            boxShape.TextFrame.MarginTop  = pad; boxShape.TextFrame.MarginBottom = pad;
                        }
                        catch { }
                        try
                        {
                            dynamic chars = boxShape.TextFrame.Characters();
                            chars.Font.Name   = style.TextFont.Name;
                            chars.Font.Size   = style.TextFont.SizeInPoints;
                            chars.Font.Bold   = style.TextFont.Bold;
                            chars.Font.Italic = style.TextFont.Italic;
                            chars.Font.Color  = oleText;
                        }
                        catch { }
                        try { boxShape.Placement = xlFreeFloating; } catch { }
                        try { boxShape.Name = "WPBox_" + pt.Index; shapeNamesForGroup.Add("WPBox_" + pt.Index); } catch { }
                        shapeCount++;
                    }
                    catch (Exception ex) { log("WARN", "AddTextbox 失败: " + ex.Message); }

                    if (dotShape != null && boxShape != null)
                    {
                        try
                        {
                            dynamic conn = ws.Shapes.AddConnector(msoConnectorStraight, cx, cy, bx, by);
                            try { conn.Line.ForeColor.RGB = oleLine; } catch { }
                            try { conn.Line.Weight = (double)style.LineWidth; } catch { }
                            try { conn.Line.Visible = msoTrue; } catch { }
                            try { conn.ConnectorFormat.BeginConnect(dotShape, 1); } catch (Exception ex) { log("WARN", "BeginConnect: " + ex.Message); }
                            try { conn.ConnectorFormat.EndConnect(boxShape, 1); }   catch (Exception ex) { log("WARN", "EndConnect: "   + ex.Message); }
                            try { conn.RerouteConnections(); } catch { }
                            try { conn.Line.ForeColor.RGB = oleLine; } catch { }
                            try { conn.Line.Weight = (double)style.LineWidth; } catch { }
                            try { conn.Placement = xlFreeFloating; } catch { }
                            try { conn.Name = "WPConn_" + pt.Index; shapeNamesForGroup.Add("WPConn_" + pt.Index); } catch { }
                            shapeCount++;
                            annotatedPoints++;
                        }
                        catch (Exception ex) { log("WARN", "AddConnector 失败: " + ex.Message); }
                    }

                    processed++;
                    if (inVp > 0)
                        try { progress?.Invoke(55 + (int)(processed * 40.0 / inVp)); } catch { }
                }

                double finalPicWidth = (double)pic.Width, finalPicHeight = (double)pic.Height;
                try
                {
                    if (shapeNamesForGroup.Count >= 2)
                    {
                        object namesArr = shapeNamesForGroup.ToArray();
                        dynamic range   = ws.Shapes.Range(namesArr);
                        dynamic group   = range.Group();
                        try { group.Name = "WPGroup_" + Guid.NewGuid().ToString("N").Substring(0, 12); } catch { }
                        try { group.Placement = xlFreeFloating; } catch { }

                        if (targetLeft >= 0 && targetTop >= 0)
                        {
                            try { group.Left = targetLeft; } catch { }
                            try { group.Top  = targetTop;  } catch { }
                        }
                        if (scaleToTarget && targetWidth > 0 && targetHeight > 0)
                        {
                            double gw = (double)group.Width, gh = (double)group.Height;
                            double scale = Math.Min(targetWidth / gw, targetHeight / gh);
                            try { group.LockAspectRatio = true; } catch { }
                            try { group.Width = gw * scale; } catch { }
                            try { if (Math.Abs((double)group.Height - gh * scale) > 1) group.Height = gh * scale; } catch { }
                        }
                        finalPicWidth  = (double)group.Width;
                        finalPicHeight = (double)group.Height;
                        log("INFO", $"[Annotator] 已组合 {shapeNamesForGroup.Count} 个 Shape 为单组" +
                            (scaleToTarget ? "，已缩放至选中单元格" : ""));
                    }
                }
                catch (Exception ex) { log("WARN", "[Annotator] Group/缩放失败: " + ex.Message + "（保留为独立 Shape）"); }

                picHeight = finalPicHeight; picWidth = finalPicWidth;
                if (targetLeft >= 0) picLeft = targetLeft;
                if (targetTop  >= 0) picTop  = targetTop;

                if (options.WriteList && points.Count > 0)
                {
                    int ds = (int)Math.Ceiling(picHeight / 15.0) + 3;
                    string[] hdr = { "#", "焊点名称", "操作名", "X(mm)", "Y(mm)", "Z(mm)", "视口内", "类型" };
                    for (int c = 0; c < hdr.Length; c++)
                    {
                        dynamic cell = ws.Cells[ds, c + 1];
                        cell.Value2 = hdr[c];
                        cell.Font.Bold = true;
                        cell.Interior.Color = D.ColorTranslator.ToOle(Theme.GridHeader);
                        cell.Font.Color     = D.ColorTranslator.ToOle(D.SystemColors.ControlText);   // 浅底配深字
                    }
                    for (int i = 0; i < points.Count; i++)
                    {
                        var pt = points[i]; int row = ds + 1 + i;
                        if (!categories.TryGetValue(pt.Index, out string catRow) || string.IsNullOrEmpty(catRow))
                            catRow = DefaultCategory;
                        ((dynamic)ws.Cells[row, 1]).Value2 = pt.Index;
                        ((dynamic)ws.Cells[row, 2]).Value2 = pt.Name;
                        ((dynamic)ws.Cells[row, 3]).Value2 = pt.OpName;
                        ((dynamic)ws.Cells[row, 4]).Value2 = pt.X;
                        ((dynamic)ws.Cells[row, 5]).Value2 = pt.Y;
                        ((dynamic)ws.Cells[row, 6]).Value2 = pt.Z;
                        ((dynamic)ws.Cells[row, 7]).Value2 = pt.InViewport ? "是" : "否";
                        ((dynamic)ws.Cells[row, 8]).Value2 = catRow;
                        if (i % 2 == 1)
                        {
                            dynamic rng = ws.Range[ws.Cells[row, 1], ws.Cells[row, 8]];
                            rng.Interior.Color = D.ColorTranslator.ToOle(Theme.GridAlt);
                        }
                    }
                    dynamic dr2 = ws.Range[ws.Cells[ds, 1], ws.Cells[ds + points.Count, 8]];
                    dr2.Columns.AutoFit();
                }

                ws.Activate();
                xlApp.Visible = true;
                log("INFO", $"[Annotator] Excel 写入完成：{shapeCount} 个 Shape，{inVp} 个视口内点；" +
                    (options.WriteList ? "附焊点数据表" : "未附数据表"));
                return new AnnotationExportResult { AnnotatedPoints = annotatedPoints, ShapeCount = shapeCount, Warnings = warnings };
            }
            finally
            {
                if (xlApp != null) try { xlApp.ScreenUpdating = savedScreenUpdating; } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
