// =============================================================================
// WeldAnnotatorForm.cs  v4  —  焊点标注截图导出插件（核心逻辑）
//
// 拆分为 5 个文件：
//   WeldPointAnnotatorCmd.cs   — 命令入口
//   AnnotationStyle.cs         — 样式数据模型
//   WeldAnnotatorForm.UI.cs    — UI 构建（partial）
//   AnnotationStyleForm.cs     — 样式设置对话框
//   WeldAnnotatorForm.cs       — 本文件：字段 + 构造 + 事件 + 业务逻辑
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using C1.Win.C1FlexGrid;
using TxTools.ExportGun;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;

using D  = System.Drawing;
using DI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace TxTools.WeldAnnotator
{
    public partial class WeldAnnotatorForm : PickAwareTxForm
    {
        // ════════════════════════════════════════════════════════════════
        //  UI 控件字段
        // ════════════════════════════════════════════════════════════════

        // 卡片①：显示控制
        private WF.Button _btnSnap, _btnRestore, _btnShowOnly;
        private WF.Label  _lblSnapStatus;
        // 卡片②/③：导出设置
        private WF.Label    _lblCount;
        private WF.CheckBox _chkNewSheet, _chkWriteList, _chkAutoThick;
        // 卡片④：操作
        private WF.Button _btnExport, _btnClear, _btnStyle;

        private ToolStripProgressBar _progress;

        // 左侧 OP 选择
        private TxObjGridCtrl _opGrid;
        private WF.Label      _lblOpHint;

        // 焊点列表
        private TxFlexGrid _grid;

        // 状态栏 + 日志
        private StatusStrip          _status;
        private ToolStripStatusLabel _lblStatus;
        private WF.Panel             _logPanel;
        private RichTextBox          _logBox;
        private bool                 _logVisible;

        // 标注命名模式
        private WF.ComboBox _cmbLabelMode;
        private WF.TextBox  _txtPrefix, _txtSuffix;
        private WF.Label    _lblPrefix, _lblSuffix;

        // 共享 ToolTip
        private readonly WF.ToolTip _sharedToolTip = new WF.ToolTip();

        // ════════════════════════════════════════════════════════════════
        //  常量
        // ════════════════════════════════════════════════════════════════
        private const int C_IDX = 0, C_NAME = 1, C_OP = 2, C_X = 3, C_Y = 4, C_Z = 5, C_VIS = 6, C_TYPE = 7;

        private static readonly string[] CATEGORY_OPTIONS = {
            "二层板点焊", "二层板补焊",
            "三层板及以上点焊", "三层板及以上补焊",
            "焊缝", "CO2焊点",
            "螺母", "螺钉螺栓",
            "胶",
            "强度校验点", "重要特性", "关键特性"
        };
        private const string DEFAULT_CATEGORY = "二层板点焊";

        // ════════════════════════════════════════════════════════════════
        //  数据
        // ════════════════════════════════════════════════════════════════
        private List<WeldAnnotationPoint> _points         = new List<WeldAnnotationPoint>();
        private string                    _selOpName;
        private ITxObject                 _selOpObj;
        private readonly DisplaySession _displaySession;
        private AnnotationStyle           _style          = new AnnotationStyle();
        private Dictionary<int, string>   _categories     = new Dictionary<int, string>();
        private HashSet<int>              _categoriesManuallyEdited = new HashSet<int>();

        private static readonly D.Size _myDefaultSize = new D.Size(1000, 760);
        private static readonly D.Size _myMinimumSize = new D.Size(820, 700);
        private bool _dpiApplied;

        // ════════════════════════════════════════════════════════════════
        //  构造 + 生命周期
        // ════════════════════════════════════════════════════════════════
        public WeldAnnotatorForm()
        {
            _displaySession = new DisplaySession(s => Log("INFO", s));
            SemiModal = false;
            FormUiKit.InitStandardForm(this, "焊点标注截图导出",
                _myDefaultSize, _myMinimumSize);

            Font = FormUiKit.BaseFont;
            ShowInTaskbar = true;
            PlaceTopRight();

            BuildMainArea();
            BuildLogPanel();
            BuildStatusBar();
            BuildCardPanel();

            this.FormClosing += WeldAnnotatorForm_FormClosing;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, _myDefaultSize);
            PlaceTopRight();
        }

        public override void OnInitTxForm()
        {
            base.OnInitTxForm();
        }

        private void PlaceTopRight()
        {
            try
            {
                StartPosition = FormStartPosition.Manual;
                var scr = Screen.FromControl(this).WorkingArea;
                Location = new D.Point(
                    Math.Max(scr.Left, scr.Right - Width - 20),
                    Math.Max(scr.Top, Math.Min(scr.Top + 60, scr.Bottom - Height)));
            }
            catch { }
        }

        private void WeldAnnotatorForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                if (!_displaySession.HasPendingChanges) return;

                string msg =
                    "检测到场景中存在本插件产生的未恢复显示状态：\n\n" +
                    "  • 有本窗口产生的显示/隐藏变更，未恢复\n" +
                    "\n关闭窗口前是否恢复？\n" +
                    "  [是] 恢复显示后关闭\n" +
                    "  [否] 直接关闭（场景保持当前状态）\n" +
                    "  [取消] 返回窗口";

                var r = MessageBox.Show(this, msg, "未恢复的显示状态",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes)
                {
                    if (!_displaySession.Restore())
                    {
                        e.Cancel = true;
                        SetStatus("部分显示状态恢复失败，请查看日志后重试");
                    }
                    UpdateDisplayControls();
                }
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                Log("ERR", "关闭前恢复检查异常：" + ex.Message);
                SetStatus("关闭前恢复失败：" + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  OP 选择事件
        // ════════════════════════════════════════════════════════════════
        private void OnOpInserted(object sender, TxObjGridCtrl_ObjectInsertedEventArgs e)
        {
            try
            {
                ITxObject txo = _opGrid.GetObject(0);
                if (txo == null) return;

                _selOpName = SafeGetName(txo);
                _selOpObj  = txo;
                _lblOpHint.Text      = _selOpName;
                _lblOpHint.ForeColor = D.Color.DarkGreen;
                SetStatus("已选择操作：" + _selOpName + "  正在读取焊点...");

                bool ok = ReadWeldPointsFromSelectedOp();
                ApplyAutoThicknessIfEnabled();
                RefreshGrid();
                SetStatus(ok
                    ? $"已选择 [{_selOpName}]，读取到 {_points.Count} 个焊点"
                    : $"已选择 [{_selOpName}]，但未读取到焊点（可能该 OP 下无焊点）");
            }
            catch (Exception ex) { Log("WARN", "OnOpInserted：" + ex.Message); }
        }

        private void OnOpDeleted(object sender, TxObjGridCtrl_RowDeletedEventArgs e)
        {
            try
            {
                if (_opGrid.Count > 0)
                {
                    ITxObject txo = _opGrid.GetObject(0);
                    if (txo != null)
                    {
                        _selOpName = SafeGetName(txo);
                        _selOpObj  = txo;
                        _lblOpHint.Text      = _selOpName;
                        _lblOpHint.ForeColor = D.Color.DarkGreen;
                        ReadWeldPointsFromSelectedOp();
                        RefreshGrid();
                        return;
                    }
                }
            }
            catch { }
            _selOpName = null;
            _selOpObj  = null;
            _lblOpHint.Text      = "选OP或视口选中";
            _lblOpHint.ForeColor = D.Color.Gray;
            _points.Clear();
            RefreshGrid();
        }

        // ════════════════════════════════════════════════════════════════
        //  快照控制
        // ════════════════════════════════════════════════════════════════
        private void BtnSnap_Click(object sender, EventArgs e)
        {
            SetStatus("拍摄快照中...");
            try
            {
                int cnt = _displaySession.CaptureSnapshot();
                _lblSnapStatus.Text      = $"已记录 {cnt} 对象";
                _lblSnapStatus.ForeColor = D.Color.DarkGreen;
                _btnRestore.Enabled      = true;
                SetStatus($"快照完成，记录 {cnt} 个对象显示状态");
            }
            catch (Exception ex) { Log("ERR", "拍摄快照失败：" + ex.Message); SetStatus("快照失败：" + ex.Message); }
            finally { UpdateDisplayControls(); }
        }

        private void BtnRestore_Click(object sender, EventArgs e)
        {
            SetStatus("恢复显示状态中...");
            try
            {
                bool ok = _displaySession.Restore();
                _lblSnapStatus.Text = ok ? "已恢复显示状态" : "部分恢复失败，可重试";
                _lblSnapStatus.ForeColor = ok ? D.Color.DarkGreen : Theme.StatusWarn;
                SetStatus(ok ? "已恢复显示状态" : "部分显示状态恢复失败，请查看日志");
            }
            catch (Exception ex) { Log("ERR", "恢复失败：" + ex.Message); SetStatus("恢复失败：" + ex.Message); }
            finally { UpdateDisplayControls(); }
        }

        private void BtnShowOnly_Click(object sender, EventArgs e)
        {
            if (_selOpObj == null) { SetStatus("请先在左侧选择操作节点"); return; }
            SetStatus("准备白名单...");

            var op = new OperationInfo
            {
                Name      = _selOpName ?? SafeGetName(_selOpObj),
                TypeLabel = _selOpObj.GetType().Name,
                PsObject  = _selOpObj
            };
            try { PsReader.FillPoints(op, PointType.WeldPoint, false, s => Log("INFO", s)); }
            catch (Exception ex) { Log("WARN", "FillPoints 异常：" + ex.Message); }

            if (op.Points == null || op.Points.Count == 0)
            {
                TxMessageBox.ShowModal($"操作 [{op.Name}] 下未找到任何焊点。", "未找到焊点",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("未找到焊点");
                return;
            }

            var whitelist = new List<ITxObject>();
            TxRobot robot = null;
            try { robot = PsReader.FindRobotForOperation(_selOpObj); } catch { }
            if (robot != null) whitelist.Add(robot);
            if (robot != null)
            {
                ITxObject tool = null;
                try
                {
                    dynamic dr = robot;
                    try { tool = dr.ActiveTool as ITxObject; } catch { }
                    if (tool == null) try { tool = dr.CurrentTool as ITxObject; } catch { }
                }
                catch { }
                if (tool != null) whitelist.Add(tool);
            }

            int boundCount = 0;
            foreach (var pi in op.Points)
            {
                if (pi?.AllAppearances == null) continue;
                foreach (var ar in pi.AllAppearances)
                {
                    if (ar?.RawObject is ITxDisplayableObject) { whitelist.Add(ar.RawObject); boundCount++; }
                }
            }

            if (boundCount == 0)
            {
                TxMessageBox.ShowModal(
                    $"已读取 {op.Points.Count} 个焊点，但未能从中获取任何绑定的外观对象。\n\n" +
                    "可能原因：\n• 焊点未绑定零件/外观（PS 中右键焊点 → 检查 Assigned Parts）\n" +
                    "• FillAppearancesForOperation 在此操作类型下未触发\n\n" +
                    "为避免把场景中所有对象都隐藏，已取消操作。",
                    "白名单无外观", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("已取消：未能获取焊点绑定外观");
                return;
            }

            SetStatus("设置仅显示操作外观...");
            try
            {
                bool ok = _displaySession.ShowOnly(whitelist);
                _lblSnapStatus.Text       = ok ? "已隐藏其他对象" : "部分显示/隐藏失败";
                _lblSnapStatus.ForeColor  = Theme.StatusWarn;
                SetStatus(ok ? "已仅显示操作绑定外观（操作节点和点位均隐藏）" : "部分显示/隐藏未达目标，请查看日志");
            }
            catch (InvalidOperationException iex) { Log("WARN", iex.Message); TxMessageBox.ShowModal(iex.Message, "无法执行", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (Exception ex) { Log("ERR", "操作失败：" + ex.Message); SetStatus("失败：" + ex.Message); }
            finally { UpdateDisplayControls(); }
        }

        private void UpdateDisplayControls()
        {
            _btnRestore.Enabled = _displaySession.CanRestore;
            _btnRestore.Text = _displaySession.HasSnapshot ? "恢复快照" : "恢复显示";
        }

        // ════════════════════════════════════════════════════════════════
        //  读取焊点 / 清空 / 导出
        // ════════════════════════════════════════════════════════════════
        private bool ReadWeldPointsFromSelectedOp()
        {
            if (_selOpObj == null) { _points.Clear(); return false; }

            var op = new OperationInfo
            {
                Name      = _selOpName ?? SafeGetName(_selOpObj),
                TypeLabel = _selOpObj.GetType().Name,
                PsObject  = _selOpObj
            };
            try { PsReader.FillPoints(op, PointType.WeldPoint, false, s => Log("INFO", s)); }
            catch (Exception ex) { Log("WARN", $"FillPoints [{op.Name}] 异常：{ex.Message}"); }

            var result = new List<WeldAnnotationPoint>();
            int idx = 1;
            if (op.Points != null)
            {
                foreach (var pi in op.Points)
                {
                    if (pi == null || pi.Type != PointType.WeldPoint || pi.Position == null || pi.Position.Length < 3) continue;

                    int boundParts = 0;
                    if (pi.AllAppearances != null && pi.AllAppearances.Count > 0)
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var ar in pi.AllAppearances)
                        {
                            if (ar == null) continue;
                            string key = !string.IsNullOrEmpty(ar.ParentPartName) ? ar.ParentPartName : (ar.Name ?? "");
                            if (!string.IsNullOrEmpty(key)) seen.Add(key);
                        }
                        boundParts = seen.Count;
                    }

                    result.Add(new WeldAnnotationPoint
                    {
                        Index          = idx++,
                        Name           = string.IsNullOrEmpty(pi.Name) ? ("P" + idx) : pi.Name,
                        OpName         = op.Name ?? "(未知)",
                        X = pi.Position[0], Y = pi.Position[1], Z = pi.Position[2],
                        WorldTx        = null,
                        BoundPartsCount = boundParts
                    });
                }
            }

            _points = result;
            foreach (var wp in _points)
                if (!_categories.ContainsKey(wp.Index))
                    _categories[wp.Index] = DEFAULT_CATEGORY;

            Log("INFO", $"[Annotator] 读取 [{op.Name}] 共 {result.Count} 个焊点");
            return result.Count > 0;
        }

        private static string SafeGetName(ITxObject o)
        {
            try { dynamic d = o; return (d.Name as string) ?? o.GetType().Name; }
            catch { return o?.GetType().Name ?? "(null)"; }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            _points.Clear();
            _categories.Clear();
            _categoriesManuallyEdited.Clear();
            RefreshGrid();
            SetStatus("已清空");
        }

        private void ApplyAutoThicknessIfEnabled()
        {
            if (_chkAutoThick == null || !_chkAutoThick.Checked || _points == null || _points.Count == 0) return;
            int changed = 0;
            foreach (var pt in _points)
            {
                if (_categoriesManuallyEdited.Contains(pt.Index)) continue;
                string cat = CategoryByThickness(pt.BoundPartsCount);
                if (cat == null) continue;
                _categories.TryGetValue(pt.Index, out string old);
                if (old == cat) continue;
                _categories[pt.Index] = cat;
                changed++;
            }
            Log("INFO", $"[自动板厚] 已自动设置 {changed} 个焊点的分类（按绑定零件数）");
            RefreshGrid();
        }

        private static string CategoryByThickness(int parts)
        {
            if (parts <= 1) return null;
            if (parts == 2) return "二层板点焊";
            return "三层板及以上点焊";
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            if (_selOpObj == null) { SetStatus("请先在左侧选择操作节点（在PS中点击一个OP即可）"); return; }

            SetStatus("读取焊点...");
            if (!ReadWeldPointsFromSelectedOp()) { SetStatus($"OP [{_selOpName}] 下未读取到任何焊点，无法导出。"); return; }
            ApplyAutoThicknessIfEnabled();
            RefreshGrid();

            _btnExport.Enabled  = false;
            _progress.Visible   = true;
            _progress.Value     = 0;
            try
            {
                SetStatus("截图中..."); _progress.Value = 15;
                using (var bmp = PsReader.CaptureActiveViewer(D.Size.Empty, false, s => Log("INFO", s)))
                {
                if (bmp == null) throw new Exception("截图失败（CaptureActiveViewer 返回 null）");

                SetStatus("计算焊点位置..."); _progress.Value = 35;
                PsReader.ProjectPointsToScreen(_points, bmp.Width, bmp.Height, s => Log("INFO", s));
                RefreshGrid();

                SetStatus("写入Excel（含可编辑标注）..."); _progress.Value = 55;
                ExportToActiveExcel(bmp, _style);
                }

                _progress.Value = 100;
                int inVp = _points.Count(p => p.InViewport);
                SetStatus($"导出完成：{_points.Count} 个焊点（视口内 {inVp} 个）");
                Log("INFO", $"已写入活动Excel：{_points.Count} 个焊点，{inVp} 个视口内点已生成可编辑 Shape 标注");
            }
            catch (Exception ex) { Log("ERR", "导出失败：" + ex.Message); SetStatus("导出失败：" + ex.Message); }
            finally { _btnExport.Enabled = true; _progress.Visible = false; }
        }

        private void BtnStyle_Click(object sender, EventArgs e)
        {
            using (var sf = new AnnotationStyleForm(_style))
                if (sf.ShowDialog(this) == DialogResult.OK) { _style = sf.Result; Log("INFO", "样式已更新"); }
        }

        // ════════════════════════════════════════════════════════════════
        //  Grid
        // ════════════════════════════════════════════════════════════════
        private void RefreshGrid()
        {
            _grid.Rows.Count = _points.Count + 1;
            for (int i = 0; i < _points.Count; i++)
            {
                var pt = _points[i];
                int row = i + 1;
                _grid[row, C_IDX]  = pt.Index.ToString();
                _grid[row, C_NAME] = pt.Name;
                _grid[row, C_OP]   = pt.OpName;
                _grid[row, C_X]    = pt.X.ToString("F2");
                _grid[row, C_Y]    = pt.Y.ToString("F2");
                _grid[row, C_Z]    = pt.Z.ToString("F2");
                _grid[row, C_VIS]  = pt.InViewport ? "是" : (pt.ScreenX == 0 && pt.ScreenY == 0 ? "-" : "否");

                if (!_categories.TryGetValue(pt.Index, out string cat) || string.IsNullOrEmpty(cat))
                    _categories[pt.Index] = cat = DEFAULT_CATEGORY;
                _grid[row, C_TYPE] = cat;

                var cs = _grid.Rows[row].Style ?? _grid.Styles.Add("r" + row);
                cs.BackColor = pt.InViewport ? Theme.RowOk
                             : (pt.ScreenX != 0 || pt.ScreenY != 0) ? Theme.RowWarn
                             : D.Color.Empty;
                _grid.Rows[row].Style = cs;
            }

            int inVp = _points.Count(p => p.InViewport);
            _lblCount.Text = _points.Count > 0
                ? $"焊点：{_points.Count} 个  视口内：{inVp} 个"
                : "焊点：0 个";

            try
            {
                int[] minWidths = { 36, 100, 90, 70, 70, 70, 50, 90 };
                for (int c = 0; c < _grid.Cols.Count && c < minWidths.Length; c++)
                {
                    _grid.AutoSizeCol(c);
                    if (_grid.Cols[c].Width < minWidths[c]) _grid.Cols[c].Width = minWidths[c];
                    _grid.Cols[c].Width += 12;
                }
            }
            catch { }
        }

        private void Grid_AfterEdit(object sender, RowColEventArgs e)
        {
            try
            {
                if (e.Col != C_TYPE) return;
                if (e.Row < 1 || e.Row - 1 >= _points.Count) return;
                string newVal = _grid[e.Row, C_TYPE] as string;
                if (string.IsNullOrEmpty(newVal)) newVal = DEFAULT_CATEGORY;
                int key = _points[e.Row - 1].Index;
                _categories[key] = newVal;
                _categoriesManuallyEdited.Add(key);
                Log("INFO", $"[Annotator] 点 [{_points[e.Row - 1].Name}] 类型改为 [{newVal}]");
            }
            catch (Exception ex) { Log("WARN", "Grid_AfterEdit: " + ex.Message); }
        }

        // ════════════════════════════════════════════════════════════════
        //  标注文字
        // ════════════════════════════════════════════════════════════════
        private string GetAnnotationLabel(WeldAnnotationPoint pt, int ordinal)
        {
            var mode = (LabelNamingMode)(_cmbLabelMode?.SelectedIndex ?? 0);
            string name = pt.Name ?? "";
            string pfx  = _txtPrefix?.Text ?? "";
            string sfx  = _txtSuffix?.Text ?? "";
            switch (mode)
            {
                case LabelNamingMode.PointName:  return name;
                case LabelNamingMode.Prefix:     return pfx + ordinal;
                case LabelNamingMode.Suffix:     return ordinal + sfx;
                case LabelNamingMode.SeqAndName: return ordinal + "-" + name;
                default:                         return ordinal.ToString();
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Excel 导出（dynamic COM）
        // ════════════════════════════════════════════════════════════════
        private void ExportToActiveExcel(D.Bitmap bmp, AnnotationStyle style)
        {
            AnnotationExportService.ExportToActiveExcel(bmp, _points, _categories, style,
                new AnnotationExportOptions
                {
                    NewSheet = _chkNewSheet.Checked,
                    WriteList = _chkWriteList.Checked,
                    LabelMode = (LabelNamingMode)(_cmbLabelMode?.SelectedIndex ?? 0),
                    Prefix = _txtPrefix?.Text ?? "",
                    Suffix = _txtSuffix?.Text ?? ""
                }, Log, value => { if (_progress != null) _progress.Value = value; });
        }

        // ════════════════════════════════════════════════════════════════
        //  辅助
        // ════════════════════════════════════════════════════════════════
        private void SetStatus(string msg) { _lblStatus.Text = msg; _status.Refresh(); }

        private void Log(string level, string msg)
        {
            if (_logBox == null) return;
            D.Color c = level == "ERR" ? Theme.LogErr
                      : level == "WARN" ? Theme.LogWarn
                                        : Theme.LogText;   // 跟随主题，避免浅灰/黄字在白底日志上不可读
            _logBox.SelectionColor = c;
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {level}: {msg}\r\n");
            _logBox.ScrollToCaret();
            if (level != "INFO" && !_logVisible) _logPanel.Visible = _logVisible = true;
        }
    }
}
