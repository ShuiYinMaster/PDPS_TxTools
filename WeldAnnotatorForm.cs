// =============================================================================
// WeldAnnotatorForm.cs  v3  —  焊点标注截图导出插件
// C# 7.3 / Tecnomatix.Engineering SDK
//
// GUI 布局（参考 RobotReachabilityChecker 风格）：
//   TxToolStrip          ← 顶部操作按钮
//   TabControl（顶部）    ← Tab1:快照控制  Tab2:标注信息
//   ┌──────────┬─────────────────────────────┐
//   │左侧卡片   │ TxFlexGrid（焊点列表）       │
//   │GroupBox  │                             │
//   │TxObjGrid │                             │
//   └──────────┴─────────────────────────────┘
//   StatusStrip + 可折叠日志面板
//
// 所有 PS 操作通过 PsReader 完成，Form 只处理 UI 逻辑。
// Excel 写入：dynamic COM（无需 Microsoft.Office.Interop.Excel 引用）。
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
using MyPlugin.ExportGun;     // PsReader, WeldAnnotationPoint

using D = System.Drawing;
using DI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace WeldAnnotator
{
    // =========================================================================
    // 命令入口
    // =========================================================================
    public class WeldPointAnnotatorCmd : TxButtonCommand
    {
        public override string Name { get { return "WeldAnnotator.Annotate"; } }
        public override string Category { get { return "My Plugins"; } }
        public override string Tooltip { get { return "焊点标注截图 → Excel"; } }
        public override string Description { get { return "将PS视口截图与焊点标注导出到活动Excel"; } }
        public override string Bitmap { get { return "WeldAnnotator.bmp"; } }

        public override void Execute(object param)
        {
            try
            {
                WeldAnnotatorForm form = new WeldAnnotatorForm();
                form.Show();
            }
            catch (Exception ex)
            {
                TxMessageBox.ShowModal("插件启动失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // =========================================================================
    // 标注文字命名模式
    // =========================================================================
    public enum LabelNamingMode
    {
        Sequence = 0,   // 按顺序号（1, 2, 3, ...）
        PointName = 1,   // 按焊点名称
        Prefix = 2,   // 前缀 + 序号
        Suffix = 3    // 序号 + 后缀
    }

    // =========================================================================
    // 标注样式
    // =========================================================================
    public class AnnotationStyle
    {
        public D.Color DotColor = D.Color.Red;
        public int DotRadius = 8;
        public D.Color LineColor = D.Color.Red;
        public float LineWidth = 1.5f;
        public D.Color BoxBorderColor = D.Color.Red;
        public D.Color BoxFillColor = D.Color.Red;
        public D.Color TextColor = D.Color.Black;
        public D.Font TextFont = new D.Font("Arial", 9f, D.FontStyle.Regular);
        public int BoxPadding = 4;
        public int OffsetX = 40;
        public int OffsetY = 40;

        // 分类 → Excel MsoAutoShapeType 形状 ID。默认按图例约定。
        // 可用形状号（msoAutoShapeType 常用值）：
        //   1  矩形, 4  菱形, 5  圆角矩形, 7  等腰三角形, 9  椭圆（圆）,
        //   10 正六边形, 12 五角星, 20 环（胶囊类），其他可按需扩展
        public System.Collections.Generic.Dictionary<string, int> CategoryShapes
            = new System.Collections.Generic.Dictionary<string, int>
            {
                { "二层板点焊",         9  },   // 实心圆
                { "二层板补焊",         9  },   // 空心圆（Filled=false）
                { "三层板及以上点焊",   7  },   // 实心三角
                { "三层板及以上补焊",   7  },   // 空心三角
                { "焊缝",              1  },   // 矩形（会拉成细长条）
                { "CO2焊点",           1  },   // 实心矩形
                { "螺母",              10 },   // 实心六边形
                { "螺钉螺栓",          1  },   // 空心矩形
                { "胶",                5  },   // 圆角矩形（胶囊）
                { "强度校验点",         12 },   // 实心五角星
                { "重要特性",          12 },   // 空心五角星
                { "关键特性",          12 }    // 实心五角星（加粗）
            };

        // 分类 → 是否实心（true=填充颜色，false=空心仅描边）
        public System.Collections.Generic.Dictionary<string, bool> CategoryFilled
            = new System.Collections.Generic.Dictionary<string, bool>
            {
                { "二层板点焊",         true  },
                { "二层板补焊",         false },
                { "三层板及以上点焊",   true  },
                { "三层板及以上补焊",   false },
                { "焊缝",              true  },
                { "CO2焊点",           true  },
                { "螺母",              true  },
                { "螺钉螺栓",          false },
                { "胶",                false },
                { "强度校验点",         true  },
                { "重要特性",          false },
                { "关键特性",          true  }
            };
    }

    // =========================================================================
    // 主窗体
    // =========================================================================
    public class WeldAnnotatorForm : TxForm
    {
        // ── 顶部卡片（三张独立 GroupBox） ─────────────────────────────────────
        // 卡片①：快照控制
        private WF.Button _btnSnap, _btnRestore, _btnShowOnly, _btnShowAll;
        private WF.Label _lblSnapStatus;
        // 卡片②：导出选项
        private WF.Label _lblCount;
        private WF.CheckBox _chkNewSheet;
        private WF.CheckBox _chkWriteList;
        // 卡片③：操作
        private WF.Button _btnExport, _btnClear, _btnStyle;

        // 进度条现居底部状态栏
        private ToolStripProgressBar _progress;

        // ── 左侧卡片：TxObjGridCtrl ────────────────────────────────────────────
        private WF.GroupBox _leftCard;
        private TxObjGridCtrl _opGrid;
        private WF.Label _lblOpHint;

        // ── TxFlexGrid 焊点列表 ───────────────────────────────────────────────
        private TxFlexGrid _grid;

        // ── 状态栏 + 日志 ─────────────────────────────────────────────────────
        private StatusStrip _status;
        private ToolStripStatusLabel _lblStatus;
        private WF.Panel _logPanel;
        private RichTextBox _logBox;
        private bool _logVisible;

        // ── 列索引 ────────────────────────────────────────────────────────────
        private const int C_IDX = 0;
        private const int C_NAME = 1;
        private const int C_OP = 2;
        private const int C_X = 3;
        private const int C_Y = 4;
        private const int C_Z = 5;
        private const int C_VIS = 6;
        private const int C_TYPE = 7;   // 焊点分类（下拉：焊点/补焊点/强度校验点）

        // ── 卡片④：标注内容 ──────────────────────────────────────────────────
        private WF.ComboBox _cmbLabelMode;   // 命名模式：序号/名称/前缀+序号/序号+后缀
        private WF.TextBox _txtPrefix;      // 前缀文本
        private WF.TextBox _txtSuffix;      // 后缀文本
        private WF.Label _lblPrefix, _lblSuffix;

        // ── 数据 ─────────────────────────────────────────────────────────────
        private List<WeldAnnotationPoint> _points = new List<WeldAnnotationPoint>();
        private string _selOpName = null;
        private ITxObject _selOpObj = null;
        private List<Tuple<ITxObject, bool>> _dispSnapshot = null;
        private AnnotationStyle _style = new AnnotationStyle();

        // 焊点分类（key = 焊点在 _points 中的 Index 字段；默认 "焊点"）
        private System.Collections.Generic.Dictionary<int, string> _categories
            = new System.Collections.Generic.Dictionary<int, string>();

        private static readonly string[] CATEGORY_OPTIONS = {
            "二层板点焊", "二层板补焊",
            "三层板及以上点焊", "三层板及以上补焊",
            "焊缝", "CO2焊点",
            "螺母", "螺钉螺栓",
            "胶",
            "强度校验点", "重要特性", "关键特性"
        };
        private const string DEFAULT_CATEGORY = "二层板点焊";

        // =========================================================================
        public WeldAnnotatorForm()
        {
            Text = "焊点标注截图导出";
            Size = new D.Size(1200, 620);
            MinimumSize = new D.Size(700, 480);
            StartPosition = FormStartPosition.Manual;
            Font = new D.Font("Tahoma", 9f);

            // 关键顺序：Fill 先 Add（Z-order 最低，绘制在底层），
            // 各边缘 Dock 后 Add（绘制在上层，避免被 Fill 控件覆盖）。
            BuildMainArea();    // Dock=Fill      — 先加
            BuildLogPanel();    // Dock=Bottom
            BuildStatusBar();   // Dock=Bottom
            BuildCardPanel();   // Dock=Top      — 三张独立卡片（最后加，绘制最上层）
        }

        // ── PS宿主回调：仅聚焦 Grid，不做任何自动加载 ────────────────────────
        public override void OnInitTxForm()
        {
            base.OnInitTxForm();
            BeginInvoke(new Action(delegate ()
            {
                try
                {
                    if (_opGrid != null && _opGrid.Visible) _opGrid.Focus();
                }
                catch { }
            }));
        }

        // =========================================================================
        // 构建 UI
        // =========================================================================

        // ── 顶部卡片区（四个独立 GroupBox） ─────────────────────────────────
        //
        // 布局要点（避免 WinForms 常见坑）：
        //   1) cardHost 不用 AutoSize — Dock=Top 指定高度更稳定
        //   2) cardHost WrapContents=true 让窗口变窄时卡片自动换行
        //   3) GroupBox 用 AutoSize=GrowAndShrink 单独撑开宽度
        //   4) GroupBox 内放一个 Dock=Fill 的 Panel 容纳内部 FlowLayoutPanel，
        //      让内部控件能相对 GroupBox 的客户区垂直居中（Anchor 在
        //      FlowLayoutPanel 里无效，所以必须用 Panel + 手动定位）
        private void BuildCardPanel()
        {
            WF.FlowLayoutPanel cardHost = new WF.FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(6, 4, 6, 4),
                BackColor = D.SystemColors.Control
            };

            WF.GroupBox snapCard = MakeCard("快照控制");
            _btnSnap = MkCardBtn("拍摄快照", BtnSnap_Click, D.Color.FromArgb(0, 112, 192));
            _btnRestore = MkCardBtn("恢复快照", BtnRestore_Click, D.Color.FromArgb(84, 130, 53));
            _btnShowOnly = MkCardBtn("仅显示操作外观", BtnShowOnly_Click, D.Color.FromArgb(197, 90, 17));
            _btnShowAll = MkCardBtn("恢复全部显示", BtnShowAll_Click, D.Color.FromArgb(100, 100, 100));
            _btnRestore.Enabled = false;
            _lblSnapStatus = new WF.Label
            {
                Text = "未拍摄快照",
                AutoSize = true,
                ForeColor = D.Color.Gray,
                Font = new D.Font("Tahoma", 8f),
                Margin = new Padding(8, 9, 0, 0)
            };
            FillCard(snapCard, new WF.Control[]
            {
                _btnSnap, _btnRestore, MakeSeparator(),
                _btnShowOnly, _btnShowAll, _lblSnapStatus
            });

            WF.GroupBox outCard = MakeCard("导出选项");
            _lblCount = new WF.Label
            {
                Text = "焊点：0 个",
                AutoSize = true,
                Margin = new Padding(0, 7, 14, 0),
                Font = new D.Font("Tahoma", 9f, D.FontStyle.Bold)
            };
            _chkNewSheet = new WF.CheckBox
            {
                Text = "写入新Sheet",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 7, 10, 0)
            };
            _chkWriteList = new WF.CheckBox
            {
                Text = "导出焊点列表",
                Checked = false,
                AutoSize = true,
                Margin = new Padding(0, 7, 8, 0)
            };
            new WF.ToolTip().SetToolTip(_chkWriteList,
                "勾选后，在截图下方附加焊点数据表（序号/名称/操作/XYZ/视口状态/类型）。");
            FillCard(outCard, new WF.Control[] { _lblCount, _chkNewSheet, _chkWriteList });

            WF.GroupBox nameCard = MakeCard("标注内容");
            WF.Label lblMode = new WF.Label
            {
                Text = "模式：",
                AutoSize = true,
                Margin = new Padding(0, 7, 2, 0)
            };
            _cmbLabelMode = new WF.ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 100,
                Margin = new Padding(0, 4, 8, 0)
            };
            _cmbLabelMode.Items.AddRange(new object[]
            { "按序号", "按焊点名", "前缀+序号", "序号+后缀" });
            _cmbLabelMode.SelectedIndex = 0;
            _cmbLabelMode.SelectedIndexChanged += (s, e) => UpdateLabelCardEnabled();
            _lblPrefix = new WF.Label { Text = "前缀：", AutoSize = true, Margin = new Padding(0, 7, 2, 0) };
            _txtPrefix = new WF.TextBox { Width = 50, Margin = new Padding(0, 4, 8, 0) };
            _lblSuffix = new WF.Label { Text = "后缀：", AutoSize = true, Margin = new Padding(0, 7, 2, 0) };
            _txtSuffix = new WF.TextBox { Width = 50, Margin = new Padding(0, 4, 0, 0) };
            FillCard(nameCard, new WF.Control[]
            {
                lblMode, _cmbLabelMode,
                _lblPrefix, _txtPrefix, _lblSuffix, _txtSuffix
            });
            UpdateLabelCardEnabled();

            WF.GroupBox actCard = MakeCard("操作");
            _btnExport = MkCardBtn("导出到Excel", BtnExport_Click, D.Color.FromArgb(0, 120, 215));
            _btnExport.Font = new D.Font("Tahoma", 9f, D.FontStyle.Bold);
            _btnClear = MkCardBtn("清空列表", BtnClear_Click, D.Color.FromArgb(183, 28, 28));
            _btnStyle = MkCardBtn("标注样式", BtnStyle_Click, D.Color.FromArgb(66, 66, 66));
            FillCard(actCard, new WF.Control[] { _btnExport, _btnClear, _btnStyle });

            cardHost.Controls.Add(snapCard);
            cardHost.Controls.Add(outCard);
            cardHost.Controls.Add(nameCard);
            cardHost.Controls.Add(actCard);
            Controls.Add(cardHost);
        }

        /// <summary>
        /// 生成卡片 GroupBox：AutoSize=GrowAndShrink 让卡片宽度跟随内部控件真实宽度；
        /// 高度在 FillCard 里经过 GroupBox 计算后固定为 74px。
        /// </summary>
        private WF.GroupBox MakeCard(string title)
        {
            return new WF.GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new D.Font("Tahoma", 8.5f, D.FontStyle.Bold),
                Padding = new Padding(10, 18, 10, 10),
                Margin = new Padding(0, 0, 6, 0)
            };
        }

        /// <summary>
        /// 把一批控件放到卡片内：inner FlowLayoutPanel 用 AutoSize 撑开宽度，
        /// 由于它是 GroupBox 的唯一子控件，GroupBox 会随之 AutoSize 到合适大小。
        /// 垂直居中通过监听 GroupBox 的 SizeChanged 事件在 inner 的 Location 上修正。
        /// </summary>
        private void FillCard(WF.GroupBox card, WF.Control[] items)
        {
            WF.FlowLayoutPanel inner = new WF.FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            foreach (WF.Control c in items) inner.Controls.Add(c);
            card.Controls.Add(inner);

            // 把 inner 在 GroupBox 客户区域中垂直居中（水平默认贴 Padding.Left）
            EventHandler recenter = (s, e) =>
            {
                // GroupBox.DisplayRectangle 已经扣除了标题栏和 Padding
                var dr = card.DisplayRectangle;
                int top = dr.Top + Math.Max(0, (dr.Height - inner.Height) / 2);
                inner.Location = new D.Point(dr.Left, top);
            };
            card.SizeChanged += recenter;
            inner.SizeChanged += recenter;
            card.HandleCreated += recenter;
        }

        private WF.Label MakeSeparator()
        {
            return new WF.Label
            {
                Text = "│",
                AutoSize = true,
                ForeColor = D.Color.Silver,
                Margin = new Padding(4, 7, 4, 0)
            };
        }

        /// <summary>
        /// 根据命名模式启用/禁用前缀、后缀输入框。
        /// </summary>
        private void UpdateLabelCardEnabled()
        {
            if (_cmbLabelMode == null) return;
            LabelNamingMode m = (LabelNamingMode)_cmbLabelMode.SelectedIndex;
            bool pfx = m == LabelNamingMode.Prefix;
            bool sfx = m == LabelNamingMode.Suffix;
            _lblPrefix.Enabled = _txtPrefix.Enabled = pfx;
            _lblSuffix.Enabled = _txtSuffix.Enabled = sfx;
        }

        private WF.Button MkCardBtn(string text, EventHandler h, D.Color fg)
        {
            WF.Button b = new WF.Button
            {
                Text = text,
                Height = 25,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = fg,
                Font = new D.Font("Tahoma", 8.5f, D.FontStyle.Bold),
                Margin = new Padding(0, 2, 4, 2),
                Padding = new Padding(6, 1, 6, 1)
            };
            b.FlatAppearance.BorderColor = D.Color.FromArgb(200, 200, 200);
            b.Click += h;
            return b;
        }

        // ── 主区域：左侧卡片 + TxFlexGrid ────────────────────────────────────
        private void BuildMainArea()
        {
            // 容器 Panel 填充剩余空间
            WF.Panel main = new WF.Panel { Dock = DockStyle.Fill };

            // ── 左侧卡片（GroupBox + TxObjGridCtrl）──────────────────────
            _leftCard = new WF.GroupBox
            {
                Text = "操作节点（OP）",
                Dock = DockStyle.Left,
                Width = 200,
                Font = new D.Font("Tahoma", 8.5f, D.FontStyle.Bold),
                Padding = new Padding(4, 16, 4, 4)
            };

            _opGrid = new TxObjGridCtrl
            {
                Dock = DockStyle.Fill,
                ListenToPick = true,   // 等待用户拾取
                EnableMultipleSelection = false,
                EnableRecurringObjects = false
            };
            // 插入时自动确认 OP（无需"确认选择"按钮）
            _opGrid.ObjectInserted +=
                new TxObjGridCtrl_ObjectInsertedEventHandler(OnOpInserted);
            _opGrid.RowDeleted +=
                new TxObjGridCtrl_RowDeletedEventHandler(OnOpDeleted);

            _lblOpHint = new WF.Label
            {
                Text = "选OP或视口选中",
                Dock = DockStyle.Bottom,
                Height = 16,
                ForeColor = D.Color.Gray,
                Font = new D.Font("Tahoma", 7.5f),
                TextAlign = D.ContentAlignment.MiddleCenter
            };

            _leftCard.Controls.Add(_opGrid);
            _leftCard.Controls.Add(_lblOpHint);

            // 分割线
            WF.Panel divider = new WF.Panel
            {
                Dock = DockStyle.Left,
                Width = 1,
                BackColor = D.SystemColors.ControlDark
            };

            // ── TxFlexGrid 焊点列表 ───────────────────────────────────────
            _grid = new TxFlexGrid
            {
                Dock = DockStyle.Fill,
                AllowEditing = true,                        // 启用编辑（下方只对 C_TYPE 开放）
                SelectionMode = SelectionModeEnum.Row
            };
            _grid.Rows.Fixed = 1;
            _grid.Cols.Count = 8;                            // 新增 C_TYPE
            _grid.Rows.Count = 1;

            string[] hdrs = { "#", "焊点名称", "操作名", "X(mm)", "Y(mm)", "Z(mm)", "视口", "类型" };
            int[] widths = { 36, 140, 110, 78, 78, 78, 52, 92 };
            for (int c = 0; c < hdrs.Length; c++)
            {
                _grid[0, c] = hdrs[c];
                _grid.Cols[c].Width = widths[c];
                _grid.Cols[c].AllowSorting = false;
                // 只有"类型"列可编辑；其余列只读
                _grid.Cols[c].AllowEditing = (c == C_TYPE);
            }
            // 类型列：下拉选择（只能选这三个选项）
            _grid.Cols[C_TYPE].ComboList = string.Join("|", CATEGORY_OPTIONS);
            _grid.Cols[C_TYPE].TextAlign = TextAlignEnum.CenterCenter;

            _grid.Styles[CellStyleEnum.Fixed].BackColor = D.Color.FromArgb(68, 114, 196);
            _grid.Styles[CellStyleEnum.Fixed].ForeColor = D.Color.White;
            _grid.Styles[CellStyleEnum.Fixed].Font = new D.Font("Tahoma", 8.5f, D.FontStyle.Bold);
            _grid.Styles[CellStyleEnum.Alternate].BackColor = D.Color.FromArgb(242, 242, 242);

            // 类型列编辑后：写回 _categories
            _grid.AfterEdit += Grid_AfterEdit;

            // 注意 DockStyle.Fill 的控件必须最后 Add
            main.Controls.Add(_grid);
            main.Controls.Add(divider);
            main.Controls.Add(_leftCard);

            Controls.Add(main);
        }

        /// <summary>
        /// Grid 单元格编辑完成后：若编辑的是"类型"列，把新值写回 _categories。
        /// </summary>
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
                Log("INFO", string.Format(
                    "[Annotator] 点 [{0}] 类型改为 [{1}]", _points[e.Row - 1].Name, newVal));
            }
            catch (Exception ex) { Log("WARN", "Grid_AfterEdit: " + ex.Message); }
        }

        private void BuildStatusBar()
        {
            _status = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel("就绪")
            {
                Spring = true,
                TextAlign = D.ContentAlignment.MiddleLeft
            };
            _progress = new ToolStripProgressBar
            {
                Width = 150,
                Visible = false,
                Alignment = ToolStripItemAlignment.Right
            };
            ToolStripButton tsLog = new ToolStripButton("日志 ▲");
            tsLog.Click += (s, e) => ToggleLog(tsLog);
            _status.Items.AddRange(new ToolStripItem[] { _lblStatus, _progress, tsLog });
            Controls.Add(_status);
        }

        private void BuildLogPanel()
        {
            _logPanel = new WF.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 110,
                Visible = false
            };
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = D.Color.FromArgb(30, 30, 30),
                ForeColor = D.Color.LightGray,
                Font = new D.Font("Consolas", 8f),
                ReadOnly = true
            };
            _logPanel.Controls.Add(_logBox);
            Controls.Add(_logPanel);
        }

        private void ToggleLog(ToolStripButton btn)
        {
            _logVisible = !_logVisible;
            _logPanel.Visible = _logVisible;
            btn.Text = _logVisible ? "日志 ▼" : "日志 ▲";
        }

        // =========================================================================
        // TxObjGridCtrl 事件（自动确认，无需按钮）
        // =========================================================================

        private void OnOpInserted(object sender, TxObjGridCtrl_ObjectInsertedEventArgs e)
        {
            // 每次插入后立即读取第0个对象作为当前选中 OP
            try
            {
                ITxObject txo = _opGrid.GetObject(0);
                if (txo == null) return;

                // 接受任意可作为焊点容器的 ITxObject（TxWeldOperation /
                // TxCompoundOperation / 甚至 TxWeldPoint 等），由 FillPoints 兜底
                _selOpName = SafeGetName(txo);
                _selOpObj = txo;
                _lblOpHint.Text = _selOpName;
                _lblOpHint.ForeColor = D.Color.DarkGreen;
                SetStatus("已选择操作：" + _selOpName + "  正在读取焊点...");

                // 立即读取焊点并刷新网格（供用户预览）
                bool ok = ReadWeldPointsFromSelectedOp();
                RefreshGrid();
                if (ok)
                    SetStatus(string.Format(
                        "已选择 [{0}]，读取到 {1} 个焊点", _selOpName, _points.Count));
                else
                    SetStatus(string.Format(
                        "已选择 [{0}]，但未读取到焊点（可能该 OP 下无焊点）", _selOpName));
            }
            catch (Exception ex) { Log("WARN", "OnOpInserted：" + ex.Message); }
        }

        private void OnOpDeleted(object sender, TxObjGridCtrl_RowDeletedEventArgs e)
        {
            // 重新读取（可能还有其他行）
            try
            {
                if (_opGrid.Count > 0)
                {
                    ITxObject txo = _opGrid.GetObject(0);
                    if (txo != null)
                    {
                        _selOpName = SafeGetName(txo);
                        _selOpObj = txo;
                        _lblOpHint.Text = _selOpName;
                        _lblOpHint.ForeColor = D.Color.DarkGreen;
                        ReadWeldPointsFromSelectedOp();
                        RefreshGrid();
                        return;
                    }
                }
            }
            catch { }
            _selOpName = null;
            _selOpObj = null;
            _lblOpHint.Text = "选OP或视口选中";
            _lblOpHint.ForeColor = D.Color.Gray;
            _points.Clear();
            RefreshGrid();
        }

        // =========================================================================
        // 快照控制
        // =========================================================================

        private void BtnSnap_Click(object sender, EventArgs e)
        {
            SetStatus("拍摄快照中...");
            try
            {
                _dispSnapshot = PsReader.SnapshotDisplayStates(s => Log("INFO", s));
                int cnt = _dispSnapshot != null ? _dispSnapshot.Count : 0;
                _lblSnapStatus.Text = string.Format("快照：{0} 个对象", cnt);
                _lblSnapStatus.ForeColor = D.Color.DarkGreen;
                _btnRestore.Enabled = (_dispSnapshot != null && cnt > 0);
                SetStatus(string.Format("快照完成，已记录 {0} 个对象显示状态", cnt));
            }
            catch (Exception ex)
            {
                Log("ERR", "拍摄快照失败：" + ex.Message);
                SetStatus("快照失败：" + ex.Message);
            }
        }

        private void BtnRestore_Click(object sender, EventArgs e)
        {
            if (_dispSnapshot == null || _dispSnapshot.Count == 0)
            {
                SetStatus("无快照，请先拍摄快照");
                return;
            }
            SetStatus("恢复快照中...");
            try
            {
                PsReader.RestoreDisplayStates(_dispSnapshot, s => Log("INFO", s));
                SetStatus("快照已恢复");
            }
            catch (Exception ex)
            {
                Log("ERR", "恢复快照失败：" + ex.Message);
                SetStatus("恢复失败：" + ex.Message);
            }
        }

        private void BtnShowOnly_Click(object sender, EventArgs e)
        {
            if (_selOpObj == null)
            {
                SetStatus("请先在左侧选择操作节点");
                return;
            }
            SetStatus("设置仅显示操作外观...");
            try
            {
                PsReader.ShowOnlyOperationAppearance(_selOpObj, s => Log("INFO", s));
                SetStatus("已仅显示操作绑定外观");
            }
            catch (Exception ex)
            {
                Log("ERR", "操作失败：" + ex.Message);
                SetStatus("失败：" + ex.Message);
            }
        }

        private void BtnShowAll_Click(object sender, EventArgs e)
        {
            SetStatus("恢复全部显示...");
            try
            {
                PsReader.ShowAllDevices(s => Log("INFO", s));
                SetStatus("已恢复全部显示");
            }
            catch (Exception ex)
            {
                Log("ERR", "失败：" + ex.Message);
                SetStatus("失败：" + ex.Message);
            }
        }

        // =========================================================================
        // 读取焊点 / 清空 / 导出
        // =========================================================================

        /// <summary>
        /// 从左侧 TxObjGridCtrl 中已选中的 OP 读取焊点。
        /// 调用 PsReader 原有逻辑：FillPoints 四级降级
        /// （L1 WeldOpEnum → L2 Locations → L3 MfgFeatures → L4 DeepWalk）。
        /// 成功时将结果写入 _points；失败或未选 OP 时返回 false（调用方自行提示）。
        /// </summary>
        private bool ReadWeldPointsFromSelectedOp()
        {
            if (_selOpObj == null)
            {
                _points.Clear();
                return false;
            }

            OperationInfo op = new OperationInfo
            {
                Name = _selOpName ?? SafeGetName(_selOpObj),
                TypeLabel = _selOpObj.GetType().Name,
                PsObject = _selOpObj
            };
            try
            {
                PsReader.FillPoints(op, PointType.WeldPoint, false, s => Log("INFO", s));
            }
            catch (Exception ex)
            {
                Log("WARN", string.Format("FillPoints [{0}] 异常：{1}", op.Name, ex.Message));
            }

            List<WeldAnnotationPoint> result = new List<WeldAnnotationPoint>();
            int idx = 1;
            if (op.Points != null)
            {
                foreach (PointInfo pi in op.Points)
                {
                    if (pi == null) continue;
                    if (pi.Type != PointType.WeldPoint) continue;
                    if (pi.Position == null || pi.Position.Length < 3) continue;

                    result.Add(new WeldAnnotationPoint
                    {
                        Index = idx++,
                        Name = string.IsNullOrEmpty(pi.Name) ? ("P" + idx) : pi.Name,
                        OpName = op.Name ?? "(未知)",
                        X = pi.Position[0],
                        Y = pi.Position[1],
                        Z = pi.Position[2],
                        WorldTx = null
                    });
                }
            }

            _points = result;

            // 保留已有的分类选择（修复：导出时调用此方法会清空已编辑的类型）
            // 仅对字典中不存在的 Index 设默认值；已存在的保持用户选择。
            foreach (WeldAnnotationPoint wp in _points)
                if (!_categories.ContainsKey(wp.Index))
                    _categories[wp.Index] = DEFAULT_CATEGORY;

            Log("INFO", string.Format(
                "[Annotator] 读取 [{0}] 共 {1} 个焊点", op.Name, result.Count));
            return result.Count > 0;
        }

        private static string SafeGetName(ITxObject o)
        {
            try { dynamic d = o; return (d.Name as string) ?? o.GetType().Name; }
            catch { return o != null ? o.GetType().Name : "(null)"; }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            _points.Clear();
            RefreshGrid();
            SetStatus("已清空");
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            // 先从左侧已选 OP 读取最新焊点（保证数据与当前选择一致）
            if (_selOpObj == null)
            {
                SetStatus("请先在左侧选择操作节点（在PS中点击一个OP即可）");
                return;
            }

            SetStatus("读取焊点...");
            bool hasPts = ReadWeldPointsFromSelectedOp();
            RefreshGrid();
            if (!hasPts)
            {
                SetStatus(string.Format(
                    "OP [{0}] 下未读取到任何焊点，无法导出。请确认该 OP 包含 TxWeldPoint。",
                    _selOpName));
                return;
            }

            _btnExport.Enabled = false;
            _progress.Visible = true;
            _progress.Value = 0;
            try
            {
                // 截图（3-arg 实现：Size.Empty 让 PsReader 自动读取视口实际尺寸）
                SetStatus("截图中...");
                _progress.Value = 15;
                D.Bitmap bmp = PsReader.CaptureActiveViewer(
                    D.Size.Empty, false, s => Log("INFO", s));
                if (bmp == null) throw new Exception("截图失败（CaptureActiveViewer 返回 null）");

                // 投影（计算每个焊点在截图中的像素坐标）
                SetStatus("计算焊点位置...");
                _progress.Value = 35;
                PsReader.ProjectPointsToScreen(
                    _points, bmp.Width, bmp.Height, s => Log("INFO", s));
                RefreshGrid();

                // 写入 Excel（纯净截图 + 可编辑 Shape 标注）
                SetStatus("写入Excel（含可编辑标注）...");
                _progress.Value = 55;
                ExportToActiveExcel(bmp, _style);

                _progress.Value = 100;
                int inVp = _points.Count(p => p.InViewport);
                SetStatus(string.Format("导出完成：{0} 个焊点（视口内 {1} 个）",
                    _points.Count, inVp));
                Log("INFO", string.Format(
                    "已写入活动Excel：{0} 个焊点，{1} 个视口内点已生成可编辑 Shape 标注",
                    _points.Count, inVp));
            }
            catch (Exception ex)
            {
                Log("ERR", "导出失败：" + ex.Message);
                SetStatus("导出失败：" + ex.Message);
            }
            finally
            {
                _btnExport.Enabled = true;
                _progress.Visible = false;
            }
        }

        private void BtnStyle_Click(object sender, EventArgs e)
        {
            AnnotationStyleForm sf = new AnnotationStyleForm(_style);
            if (sf.ShowDialog(this) == DialogResult.OK)
            {
                _style = sf.Result;
                Log("INFO", "样式已更新");
            }
        }

        // =========================================================================
        // Grid 刷新
        // =========================================================================

        private void RefreshGrid()
        {
            _grid.Rows.Count = _points.Count + 1;
            for (int i = 0; i < _points.Count; i++)
            {
                WeldAnnotationPoint pt = _points[i];
                int row = i + 1;
                _grid[row, C_IDX] = pt.Index.ToString();
                _grid[row, C_NAME] = pt.Name;
                _grid[row, C_OP] = pt.OpName;
                _grid[row, C_X] = pt.X.ToString("F2");
                _grid[row, C_Y] = pt.Y.ToString("F2");
                _grid[row, C_Z] = pt.Z.ToString("F2");
                _grid[row, C_VIS] = pt.InViewport ? "是" : (pt.ScreenX == 0 && pt.ScreenY == 0 ? "-" : "否");

                // 类型：若字典中没有，初始化为默认分类
                string cat;
                if (!_categories.TryGetValue(pt.Index, out cat) || string.IsNullOrEmpty(cat))
                {
                    cat = DEFAULT_CATEGORY;
                    _categories[pt.Index] = cat;
                }
                _grid[row, C_TYPE] = cat;

                CellStyle cs = _grid.Rows[row].Style ?? _grid.Styles.Add("r" + row);
                if (pt.InViewport)
                    cs.BackColor = D.Color.FromArgb(198, 239, 206);
                else if (pt.ScreenX != 0 || pt.ScreenY != 0)
                    cs.BackColor = D.Color.FromArgb(255, 235, 156);
                else
                    cs.BackColor = D.Color.Empty;
                _grid.Rows[row].Style = cs;
            }

            int inVp = _points.Count(p => p.InViewport);
            _lblCount.Text = _points.Count > 0
                ? string.Format("焊点：{0} 个  视口内：{1} 个", _points.Count, inVp)
                : "焊点：0 个";
        }

        // =========================================================================
        // 标注文字内容计算（按用户选择的命名模式生成）
        // =========================================================================
        private string GetAnnotationLabel(WeldAnnotationPoint pt, int ordinal)
        {
            // ordinal = 1-based 视口内序号（从1开始）
            LabelNamingMode mode = (LabelNamingMode)
                (_cmbLabelMode != null ? _cmbLabelMode.SelectedIndex : 0);
            string name = pt.Name ?? "";
            string pfx = _txtPrefix != null ? (_txtPrefix.Text ?? "") : "";
            string sfx = _txtSuffix != null ? (_txtSuffix.Text ?? "") : "";
            switch (mode)
            {
                case LabelNamingMode.PointName: return name;
                case LabelNamingMode.Prefix: return pfx + ordinal.ToString();
                case LabelNamingMode.Suffix: return ordinal.ToString() + sfx;
                default: return ordinal.ToString();
            }
        }

        // =========================================================================
        // 写入活动 Excel：纯净截图 + 可编辑 Shape 标注
        //   Shape 分三类（dot / line / textbox），每个焊点独立，用户可在 Excel
        //   中对位置、大小、颜色、文字进行任意编辑。
        //   dynamic COM 调用，无需 Microsoft.Office.Interop.Excel 引用。
        // =========================================================================

        private void ExportToActiveExcel(D.Bitmap bmp, AnnotationStyle style)
        {
            string tmp = Path.GetTempFileName() + ".png";
            bmp.Save(tmp, DI.ImageFormat.Png);

            dynamic xlApp = null;
            bool savedScreenUpdating = true;
            try
            {
                try { xlApp = Marshal.GetActiveObject("Excel.Application"); }
                catch { throw new Exception("未检测到已打开的 Excel，请先打开工作簿"); }

                dynamic wb = xlApp.ActiveWorkbook;
                if (wb == null) throw new Exception("Excel 中没有活动工作簿");

                // 关闭屏幕刷新以加速大量 Shape 创建（finally 恢复）
                try { savedScreenUpdating = (bool)xlApp.ScreenUpdating; xlApp.ScreenUpdating = false; }
                catch { }

                // 目标 Sheet
                dynamic ws;
                if (_chkNewSheet.Checked)
                {
                    dynamic sheets = wb.Sheets;
                    ws = sheets.Add(
                        System.Reflection.Missing.Value,
                        sheets[sheets.Count],
                        System.Reflection.Missing.Value,
                        System.Reflection.Missing.Value);
                    ws.Name = "焊点标注_" + DateTime.Now.ToString("MMddHHmm");
                }
                else
                {
                    ws = wb.ActiveSheet;
                }

                // ── 插入纯净截图 ──────────────────────────────────────────────
                dynamic pics = ws.Pictures(System.Reflection.Missing.Value);
                dynamic pic = pics.Insert(tmp);
                dynamic a1 = ws.Cells[1, 1];
                pic.Left = (double)a1.Left;
                pic.Top = (double)a1.Top;

                const double maxWidthPt = 800.0;
                if ((double)pic.Width > maxWidthPt)
                {
                    double r = maxWidthPt / (double)pic.Width;
                    pic.Width = maxWidthPt;
                    pic.Height = (double)pic.Height * r;
                }

                // 截图最终位置/尺寸（Excel 点），以及 像素→点 换算比
                double picLeft = (double)pic.Left;
                double picTop = (double)pic.Top;
                double picWidth = (double)pic.Width;
                double picHeight = (double)pic.Height;
                double sx = picWidth / bmp.Width;
                double sy = picHeight / bmp.Height;

                // Office 常量（手写，避免引用 PIA）
                const int msoShapeOval = 9;
                const int msoTextOrientationHoriz = 1;
                const int msoFalse = 0;
                const int xlHAlignCenter = -4108;
                const int xlVAlignCenter = -4108;
                const int msoConnectorStraight = 1;
                const int xlFreeFloating = 3;   // 图片/Shape 不随单元格尺寸变化移动

                // 截图 Placement 设为 FreeFloating：
                // 防止后续写入数据表时 Columns.AutoFit 改变列宽导致截图和标注移位。
                try { pic.Placement = xlFreeFloating; } catch { }

                int oleDot = D.ColorTranslator.ToOle(style.DotColor);
                int oleLine = D.ColorTranslator.ToOle(style.LineColor);
                int oleBoxFill = D.ColorTranslator.ToOle(style.BoxFillColor);
                int oleBoxBorder = D.ColorTranslator.ToOle(style.BoxBorderColor);
                int oleText = D.ColorTranslator.ToOle(style.TextColor);

                // 标注尺寸（Excel 点，独立于截图分辨率；用户可在 Excel 中自由修改）
                const double DOT_R_PT = 5.0;
                const double BOX_W_PT = 28.0;
                const double BOX_H_PT = 18.0;
                const double OFFSET_PT = 32.0;

                int shapeCount = 0;
                int inVp = _points.Count(p => p.InViewport);
                int processed = 0;
                int ordinal = 0;   // 视口内焊点计数（用于序号命名模式）

                foreach (WeldAnnotationPoint pt in _points)
                {
                    if (!pt.InViewport) continue;
                    ordinal++;

                    // 焊点中心（Excel 点）
                    double cx = picLeft + pt.ScreenX * sx;
                    double cy = picTop + pt.ScreenY * sy;

                    // 编号框位置（右上方偏移；越界翻转到对侧 / 下侧）
                    double bx = cx + OFFSET_PT;
                    double by = cy - OFFSET_PT;
                    if (bx + BOX_W_PT > picLeft + picWidth) bx = cx - OFFSET_PT - BOX_W_PT;
                    if (by < picTop) by = cy + OFFSET_PT;

                    dynamic dotShape = null;
                    dynamic boxShape = null;

                    // 查该点分类对应的 Excel 形状号 + 是否填充
                    string cat;
                    if (!_categories.TryGetValue(pt.Index, out cat) || string.IsNullOrEmpty(cat))
                        cat = DEFAULT_CATEGORY;
                    int shapeId = msoShapeOval;
                    if (style.CategoryShapes != null)
                        style.CategoryShapes.TryGetValue(cat, out shapeId);
                    bool filled = true;
                    if (style.CategoryFilled != null)
                        style.CategoryFilled.TryGetValue(cat, out filled);

                    // ── ① 焊点标记（分类形状；先创建，作为连接器起点） ──────
                    try
                    {
                        dotShape = ws.Shapes.AddShape(
                            shapeId,
                            cx - DOT_R_PT, cy - DOT_R_PT,
                            DOT_R_PT * 2.0, DOT_R_PT * 2.0);
                        if (filled)
                        {
                            // 实心：填充颜色 + 同色细边框
                            try { dotShape.Fill.ForeColor.RGB = oleDot; dotShape.Fill.Solid(); } catch { }
                            try { dotShape.Line.ForeColor.RGB = oleDot; dotShape.Line.Weight = 1.0; } catch { }
                        }
                        else
                        {
                            // 空心：无填充 + 粗描边
                            try { dotShape.Fill.Visible = msoFalse; } catch { }
                            try { dotShape.Line.ForeColor.RGB = oleDot; dotShape.Line.Weight = 1.75; } catch { }
                        }
                        try { dotShape.Placement = xlFreeFloating; } catch { }
                        try { dotShape.Name = "WPDot_" + pt.Index; } catch { }
                        shapeCount++;
                    }
                    catch (Exception ex) { Log("WARN", "AddShape 失败: " + ex.Message); }

                    // ── ② 编号文本框（连接器终点） ──────────────────────────
                    try
                    {
                        boxShape = ws.Shapes.AddTextbox(
                            msoTextOrientationHoriz, bx, by, BOX_W_PT, BOX_H_PT);
                        try { boxShape.Fill.ForeColor.RGB = oleBoxFill; boxShape.Fill.Solid(); } catch { }
                        try { boxShape.Line.ForeColor.RGB = oleBoxBorder; boxShape.Line.Weight = 1.0; } catch { }
                        // 按命名模式生成 label
                        string label = GetAnnotationLabel(pt, ordinal);
                        bool setOk = false;
                        try { boxShape.TextFrame.Characters().Text = label; setOk = true; } catch { }
                        if (!setOk) try { boxShape.TextFrame2.TextRange.Text = label; } catch { }
                        try { boxShape.TextFrame.HorizontalAlignment = xlHAlignCenter; } catch { }
                        try { boxShape.TextFrame.VerticalAlignment = xlVAlignCenter; } catch { }
                        try
                        {
                            boxShape.TextFrame.MarginLeft = 1.0;
                            boxShape.TextFrame.MarginRight = 1.0;
                            boxShape.TextFrame.MarginTop = 1.0;
                            boxShape.TextFrame.MarginBottom = 1.0;
                        }
                        catch { }
                        try
                        {
                            dynamic chars = boxShape.TextFrame.Characters();
                            chars.Font.Bold = true;
                            chars.Font.Size = 10;
                            chars.Font.Color = oleText;
                        }
                        catch { }
                        try { boxShape.Placement = xlFreeFloating; } catch { }
                        try { boxShape.Name = "WPBox_" + pt.Index; } catch { }
                        shapeCount++;
                    }
                    catch (Exception ex) { Log("WARN", "AddTextbox 失败: " + ex.Message); }

                    // ── ③ 连接器（AddConnector + BeginConnect/EndConnect） ─────
                    //   AddConnector 比 AddLine 强：BeginConnect / EndConnect 把
                    //   两端绑定到 dot / textbox 的连接点，Excel 会自动重新布线，
                    //   即使用户拖动 dot 或 textbox，线始终跟随。
                    //   ConnectionSiteIndex=1 表示形状的首个连接点（一般是顶部），
                    //   之后调用 RerouteConnections 让 Excel 选择最近的连接点。
                    if (dotShape != null && boxShape != null)
                    {
                        try
                        {
                            dynamic conn = ws.Shapes.AddConnector(
                                msoConnectorStraight, cx, cy, bx, by);
                            try { conn.ConnectorFormat.BeginConnect(dotShape, 1); } catch (Exception ex) { Log("WARN", "BeginConnect: " + ex.Message); }
                            try { conn.ConnectorFormat.EndConnect(boxShape, 1); } catch (Exception ex) { Log("WARN", "EndConnect: " + ex.Message); }
                            try { conn.RerouteConnections(); } catch { }
                            try { conn.Line.ForeColor.RGB = oleLine; } catch { }
                            try { conn.Line.Weight = (double)style.LineWidth; } catch { }
                            try { conn.Placement = xlFreeFloating; } catch { }
                            try { conn.Name = "WPConn_" + pt.Index; } catch { }
                            shapeCount++;
                        }
                        catch (Exception ex) { Log("WARN", "AddConnector 失败: " + ex.Message); }
                    }

                    processed++;
                    if (inVp > 0 && _progress != null)
                    {
                        try { _progress.Value = 55 + (int)(processed * 40.0 / inVp); } catch { }
                    }
                }

                // ── 可选：焊点数据表（图片下方） ────────────────────────────
                if (_chkWriteList.Checked && _points.Count > 0)
                {
                    int ds = (int)Math.Ceiling(picHeight / 15.0) + 3;
                    string[] hdr = { "#", "焊点名称", "操作名", "X(mm)", "Y(mm)", "Z(mm)", "视口内", "类型" };
                    for (int c = 0; c < hdr.Length; c++)
                    {
                        dynamic cell = ws.Cells[ds, c + 1];
                        cell.Value2 = hdr[c];
                        cell.Font.Bold = true;
                        cell.Interior.Color = D.ColorTranslator.ToOle(D.Color.FromArgb(68, 114, 196));
                        cell.Font.Color = D.ColorTranslator.ToOle(D.Color.White);
                    }
                    for (int i = 0; i < _points.Count; i++)
                    {
                        WeldAnnotationPoint pt = _points[i];
                        int row = ds + 1 + i;
                        string catRow;
                        if (!_categories.TryGetValue(pt.Index, out catRow) || string.IsNullOrEmpty(catRow))
                            catRow = DEFAULT_CATEGORY;
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
                            rng.Interior.Color =
                                D.ColorTranslator.ToOle(D.Color.FromArgb(242, 242, 242));
                        }
                    }
                    dynamic dr2 = ws.Range[ws.Cells[ds, 1], ws.Cells[ds + _points.Count, 8]];
                    dr2.Columns.AutoFit();
                }

                ws.Activate();
                xlApp.Visible = true;
                Log("INFO", string.Format(
                    "[Annotator] Excel 写入完成：{0} 个 Shape，{1} 个视口内点；{2}",
                    shapeCount, inVp,
                    _chkWriteList.Checked ? "附焊点数据表" : "未附数据表"));
            }
            finally
            {
                if (xlApp != null)
                {
                    try { xlApp.ScreenUpdating = savedScreenUpdating; } catch { }
                }
                try { File.Delete(tmp); } catch { }
            }
        }

        // =========================================================================
        // 辅助
        // =========================================================================

        private void SetStatus(string msg)
        {
            _lblStatus.Text = msg;
            _status.Refresh();
        }

        private void Log(string level, string msg)
        {
            if (_logBox == null) return;
            D.Color c = level == "ERR" ? D.Color.Tomato :
                        level == "WARN" ? D.Color.Yellow : D.Color.LightGray;
            _logBox.SelectionColor = c;
            _logBox.AppendText(string.Format("[{0}] {1}: {2}\r\n",
                DateTime.Now.ToString("HH:mm:ss"), level, msg));
            _logBox.ScrollToCaret();
            if (level != "INFO" && !_logVisible)
            {
                _logPanel.Visible = _logVisible = true;
            }
        }
    }

    // =========================================================================
    // 标注样式设置窗口
    // =========================================================================
    public class AnnotationStyleForm : WF.Form
    {
        public AnnotationStyle Result { get; private set; }

        // msoAutoShapeType 常用形状（显示名 → 形状ID）
        private static readonly Tuple<string, int>[] SHAPE_OPTIONS = new[]
        {
            Tuple.Create("圆形",       9),   // msoShapeOval
            Tuple.Create("矩形",       1),   // msoShapeRectangle
            Tuple.Create("三角形",     7),   // msoShapeIsoscelesTriangle
            Tuple.Create("菱形",       4),   // msoShapeDiamond
            Tuple.Create("圆角矩形",   5),   // msoShapeRoundedRectangle
            Tuple.Create("五角星",    12),   // msoShape5pointStar
        };

        public AnnotationStyleForm(AnnotationStyle src)
        {
            // 深拷贝 CategoryShapes，否则直接赋引用会在用户点"取消"后仍然修改外部
            var catCopy = new System.Collections.Generic.Dictionary<string, int>();
            if (src.CategoryShapes != null)
                foreach (var kv in src.CategoryShapes) catCopy[kv.Key] = kv.Value;

            Result = new AnnotationStyle
            {
                DotColor = src.DotColor,
                DotRadius = src.DotRadius,
                LineColor = src.LineColor,
                LineWidth = src.LineWidth,
                BoxBorderColor = src.BoxBorderColor,
                BoxFillColor = src.BoxFillColor,
                TextColor = src.TextColor,
                TextFont = src.TextFont,
                BoxPadding = src.BoxPadding,
                OffsetX = src.OffsetX,
                OffsetY = src.OffsetY,
                CategoryShapes = catCopy
            };
            Text = "标注样式设置";
            FormBorderStyle = WF.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = WF.FormStartPosition.CenterParent;
            Font = new D.Font("Tahoma", 9f);
            // AutoSize：窗口根据控件内容自动撑开，不再被固定 Size 截断
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new D.Size(320, 0);
            Build();
        }

        private void Build()
        {
            TableLayoutPanel t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 10, 12, 10)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            void Row(string lbl, WF.Control ctrl)
            {
                t.Controls.Add(new WF.Label
                {
                    Text = lbl,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 6, 8, 6)
                });
                ctrl.Margin = new Padding(0, 3, 0, 3);
                t.Controls.Add(ctrl);
            }

            // 添加分组标题行（跨两列）
            void Section(string title)
            {
                WF.Label header = new WF.Label
                {
                    Text = title,
                    AutoSize = true,
                    Font = new D.Font("Tahoma", 9f, D.FontStyle.Bold),
                    ForeColor = D.Color.FromArgb(0, 70, 127),
                    Margin = new Padding(0, 10, 0, 4)
                };
                int rowIdx = t.RowCount;
                t.SetColumnSpan(header, 2);
                t.Controls.Add(header, 0, rowIdx);
                t.RowCount = rowIdx + 1;
            }

            WF.Button Clr(D.Color init, Action<D.Color> set)
            {
                WF.Button b = new WF.Button { Width = 80, Height = 22, BackColor = init };
                b.Click += (s, e) =>
                {
                    using (WF.ColorDialog cd = new WF.ColorDialog { Color = b.BackColor })
                        if (cd.ShowDialog() == DialogResult.OK)
                        { b.BackColor = cd.Color; set(cd.Color); }
                };
                return b;
            }

            NumericUpDown Nud(decimal v, decimal mn, decimal mx, decimal inc, int dp, Action<decimal> fn)
            {
                NumericUpDown n = new NumericUpDown
                {
                    Width = 75,
                    Minimum = mn,
                    Maximum = mx,
                    Value = v,
                    Increment = inc,
                    DecimalPlaces = dp
                };
                n.ValueChanged += (s, e) => fn(n.Value);
                return n;
            }

            // ── 基础样式 ──────────────────────────────────────────────────
            Section("基础样式");
            Row("圆点颜色：", Clr(Result.DotColor, c => Result.DotColor = c));
            Row("圆点半径(px)：", Nud(Result.DotRadius, 2, 30, 1, 0, v => Result.DotRadius = (int)v));
            Row("引线颜色：", Clr(Result.LineColor, c => Result.LineColor = c));
            Row("引线宽度(px)：", Nud((decimal)Result.LineWidth, 1, 10, 0.5m, 1, v => Result.LineWidth = (float)v));
            Row("框边框颜色：", Clr(Result.BoxBorderColor, c => Result.BoxBorderColor = c));
            Row("框填充颜色：", Clr(Result.BoxFillColor, c => Result.BoxFillColor = c));
            Row("编号颜色：", Clr(Result.TextColor, c => Result.TextColor = c));
            Row("框内边距(px)：", Nud(Result.BoxPadding, 1, 20, 1, 0, v => Result.BoxPadding = (int)v));
            Row("水平偏移(px)：", Nud(Result.OffsetX, 10, 300, 5, 0, v => Result.OffsetX = (int)v));
            Row("垂直偏移(px)：", Nud(Result.OffsetY, 10, 300, 5, 0, v => Result.OffsetY = (int)v));

            // ── 分类形状 ──────────────────────────────────────────────────
            Section("分类形状（Excel 标记形状）");
            foreach (string cat in new[] { "焊点", "补焊点", "强度校验点" })
            {
                string catLocal = cat;   // 闭包捕获
                WF.ComboBox cmb = new WF.ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = 110
                };
                foreach (var opt in SHAPE_OPTIONS) cmb.Items.Add(opt.Item1);

                int currentId;
                if (!Result.CategoryShapes.TryGetValue(catLocal, out currentId))
                    currentId = 9;   // 默认圆
                int selIdx = 0;
                for (int i = 0; i < SHAPE_OPTIONS.Length; i++)
                    if (SHAPE_OPTIONS[i].Item2 == currentId) { selIdx = i; break; }
                cmb.SelectedIndex = selIdx;

                cmb.SelectedIndexChanged += (s, e) =>
                {
                    int idx = cmb.SelectedIndex;
                    if (idx >= 0 && idx < SHAPE_OPTIONS.Length)
                        Result.CategoryShapes[catLocal] = SHAPE_OPTIONS[idx].Item2;
                };
                Row(catLocal + "：", cmb);
            }

            Controls.Add(t);

            // ── 按钮条 ────────────────────────────────────────────────────
            WF.Panel bp = new WF.Panel { Dock = DockStyle.Bottom, Height = 42 };
            WF.Button ok = new WF.Button
            { Text = "确定", Width = 80, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
            WF.Button can = new WF.Button
            { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
            bp.Resize += (s, e) =>
            {
                can.Left = bp.ClientSize.Width - can.Width - 12;
                can.Top = 8;
                ok.Left = can.Left - ok.Width - 8;
                ok.Top = 8;
            };
            ok.Click += (s, e) => Close();
            can.Click += (s, e) => Close();
            bp.Controls.Add(ok);
            bp.Controls.Add(can);
            Controls.Add(bp);
            AcceptButton = ok;
            CancelButton = can;
        }
    }
}