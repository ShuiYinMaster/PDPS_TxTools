using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using Tecnomatix.Engineering.Ui.WPF;

namespace TxTools.RobotReachabilityChecker
{
    // =========================================================================
    // 插件入口
    // =========================================================================
    public class RobotReachabilityCheckerCmd : TxButtonCommand
    {
        public override string Category { get { return "TxTools"; } }
        public override string Name { get { return "RobotReachabilityChecker"; } }
        public override string Description { get { return "机器人路径可达性检查工具"; } }

        public override void Execute(object cmdParams)
        {
            try { var form = new ReachabilityCheckerForm(); form.Show(); }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // =========================================================================
    // 数据模型
    // =========================================================================
    public enum ReachabilityStatus { Reachable, Unreachable, NearLimit, NotChecked }

    public class PathPointResult
    {
        public int Index { get; set; }
        public string PointName { get; set; }
        public string OperationName { get; set; }
        public string RobotName { get; set; } = "";
        public string PointType { get; set; } = "";
        public ReachabilityStatus Status { get; set; }
        public double J1 { get; set; }
        public double J2 { get; set; }
        public double J3 { get; set; }
        public double J4 { get; set; }
        public double J5 { get; set; }
        public double J6 { get; set; }
        public double JointMargin { get; set; } = 999;
        public string ErrorMessage { get; set; } = "";
    }

    public class RobotPathCheckTask
    {
        public string RobotName { get; set; }
        public string PathName { get; set; }
        public DateTime CheckTime { get; set; }
        public List<PathPointResult> Results { get; set; } = new List<PathPointResult>();
        public int TotalPoints => Results.Count;
        public int ReachableCount => Results.Count(r => r.Status == ReachabilityStatus.Reachable);
        public int UnreachableCount => Results.Count(r => r.Status == ReachabilityStatus.Unreachable);
        public int NearLimitCount => Results.Count(r => r.Status == ReachabilityStatus.NearLimit);
        public double ReachabilityRate =>
            TotalPoints > 0 ? (double)(ReachableCount + NearLimitCount) / TotalPoints * 100 : 0;
    }

    // =========================================================================
    // 主窗体 — 继承 TxForm，布局参考 PS 标准工具界面风格
    //
    // 布局结构（参考图片）：
    //   ┌─ TxToolStrip（操作路径选择 + 功能按钮 + 进度条）
    //   ├─ TabControl（5个配置区：检查目标 / TCP余量 / 轴角度余量 / 干涉 / 功能区）
    //   ├─ 筛选条（隐藏正常结果 / 搜索）
    //   ├─ TxFlexGrid（结果表：品牌/机器人名/操作名/点名/点类型/J1-J6/检查结果）
    //   └─ StatusStrip + 日志面板（底部）
    // =========================================================================
    public class ReachabilityCheckerForm : TxForm
    {
        // ── TxToolStrip（精简：仅 OP节点选择 + 机器人标签 + 进度条）──────────
        private TxToolStrip _toolStrip;
        private ToolStripLabel _tsLblOp, _tsLblRobot;
        private ToolStripComboBox _tsOp;
        private ToolStripButton _tsBtnRefresh, _tsBtnLog;
        private ToolStripProgressBar _tsProgress;

        // ── 5张并列卡片（替代 TabControl，同时显示在顶部）──────────────────
        private Panel _cardsPanel;            // 容器：水平排列5张卡片
        // Card1: 检查目标及筛选
        private TxObjEditBoxCtrl _txtOpNode;
        private CheckBox _chkHideNormal;
        private ComboBox _cbPointTypeFilter;
        // Card2: TCP余量
        private CheckBox _chkTcpXyz;
        private NumericUpDown _nudTcpMargin;
        // Card3: 轴角度余量
        private CheckBox _chkJointMargin;
        private NumericUpDown _nudJointMarginDeg;
        // Card4: 干涉检查（仅展示，不接逻辑）
        private CheckBox _chkStaticInterference, _chkDynamicInterference;
        // Card5: 功能区（主操作按钮）

        // ── 结果表格（TxFlexGrid）────────────────────────────────────────────
        private TxFlexGrid _grid;
        // 列索引常量
        private const int COL_IDX = 0, COL_BRAND = 1, COL_ROBOT = 2, COL_OP = 3,
                          COL_PT = 4, COL_TYPE = 5, COL_J1 = 6, COL_J2 = 7,
                          COL_J3 = 8, COL_J4 = 9, COL_J5 = 10, COL_J6 = 11,
                          COL_RESULT = 12, COL_NOTE = 13;

        // ── 日志面板 ──────────────────────────────────────────────────────────
        private Panel _logPanel;
        private RichTextBox _logBox;
        private bool _logVisible = false;

        // ── 底部 StatusStrip ───────────────────────────────────────────────────
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _lblStatus;

        // ── 数据与计时器 ──────────────────────────────────────────────────────
        private List<RobotPathCheckTask> _tasks = new List<RobotPathCheckTask>();
        private RobotPathCheckTask _currentTask;
        private System.Windows.Forms.Timer _checkTimer;
        private int _checkProgress;
        private ITxRoboticOperation _lastSelectedOp;
        private System.Windows.Forms.Timer _selTimer;
        // 缓存每行数据索引（TxFlexGrid 行 → PathPointResult），用于单击跳转
        private readonly Dictionary<int, PathPointResult> _rowToResult = new Dictionary<int, PathPointResult>();

        // PS 标准配色
        private static readonly Color ClrAccent = Color.FromArgb(0, 70, 127);
        private static readonly Color ClrSuccess = Color.FromArgb(0, 128, 0);
        private static readonly Color ClrDanger = Color.FromArgb(192, 0, 0);
        private static readonly Color ClrWarning = Color.FromArgb(160, 100, 0);
        private static readonly Color ClrMuted = SystemColors.GrayText;
        private static readonly Color ClrText = SystemColors.WindowText;
        private static readonly Color ClrBg = SystemColors.Control;

        // ── 构造 ──────────────────────────────────────────────────────────────
        public ReachabilityCheckerForm()
        {
            Text = "机器人路径点位检查";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 780);
            MinimumSize = new Size(960, 580);
            InitializeComponent();
            LoadRobotsAndOperations();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _selTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _selTimer.Tick += OnSelectionTick;
            _selTimer.Start();
        }

        // =====================================================================
        // UI 构建
        // =====================================================================
        private void InitializeComponent()
        {
            SuspendLayout();
            BuildToolStrip();   // TxToolStrip（精简版，顶部第一行）
            BuildCardsPanel();  // 5张并列卡片（顶部第二行）
            BuildLogPanel();    // 日志面板（底部隐藏）
            BuildStatusStrip(); // StatusStrip（底部）
            BuildGrid();        // TxFlexGrid（Fill，自适应宽度）

            // DockStyle 优先级：后 Add 的 Top 显示在上；Fill 最后
            _toolStrip.SendToBack();
            _cardsPanel.SendToBack();
            _statusStrip.SendToBack();
            _logPanel.SendToBack();
            _grid.BringToFront();
            ResumeLayout(false);
        }

        // ── TxToolStrip（精简：OP节点选择 / 机器人标签 / 刷新 / 日志 / 进度条）
        private void BuildToolStrip()
        {
            _toolStrip = new TxToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(4, 2, 4, 2)
            };

            _tsLblOp = new ToolStripLabel("OP节点:");
            _tsOp = new ToolStripComboBox { ToolTipText = "选择要检查的操作/路径" };
            _tsOp.ComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _tsOp.ComboBox.Width = 240;
            _tsOp.SelectedIndexChanged += OnOpSelectionChanged;

            _tsLblRobot = new ToolStripLabel("/ 机器人：—")
            {
                ForeColor = ClrMuted,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Regular)
            };

            _tsBtnRefresh = new ToolStripButton("刷新") { ToolTipText = "重新加载操作列表" };
            _tsBtnRefresh.Click += (s, e) => LoadRobotsAndOperations();

            _tsBtnLog = new ToolStripButton("日志") { ToolTipText = "显示/隐藏日志面板" };
            _tsBtnLog.Click += (s, e) => ToggleLogPanel();

            _tsProgress = new ToolStripProgressBar { Width = 200, Visible = false };

            _toolStrip.Items.AddRange(new ToolStripItem[]
            {
                _tsLblOp, _tsOp, _tsLblRobot,
                new ToolStripSeparator(),
                _tsBtnRefresh, _tsBtnLog,
                _tsProgress
            });
            Controls.Add(_toolStrip);
        }

        // ── 5张并列卡片（同时显示，水平铺满顶部）────────────────────────────
        // 每张卡片是一个带标题边框的 GroupBox，通过 TableLayoutPanel 等宽分列
        // ── 5张并列卡片 ──────────────────────────────────────────────────────
        // 使用 Panel(AutoSize=true) + TableLayoutPanel(AutoSize=true) 让高度跟随内容撑开
        // _cardsPanel 自身不设固定高度，由子控件决定
        private void BuildCardsPanel()
        {
            // 外层容器：AutoSize 模式，高度由内容决定
            _cardsPanel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = SystemColors.Control,
                Padding = new Padding(3, 3, 3, 3)
            };

            // TableLayoutPanel 也 AutoSize，按内容撑开
            var table = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                ColumnCount = 5,
                RowCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(0)
            };
            for (int i = 0; i < 5; i++)
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            table.Controls.Add(BuildCard1_Target(), 0, 0);
            table.Controls.Add(BuildCard2_TcpMargin(), 1, 0);
            table.Controls.Add(BuildCard3_JointMargin(), 2, 0);
            table.Controls.Add(BuildCard4_Interference(), 3, 0);
            table.Controls.Add(BuildCard5_Functions(), 4, 0);

            _cardsPanel.Controls.Add(table);
            Controls.Add(_cardsPanel);
        }

        // 卡片容器：GroupBox，AutoSize 跟随内容，Dock=Fill 在 TableLayoutPanel 中撑满列宽
        private GroupBox MkCard(string title)
        {
            return new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                ForeColor = ClrAccent,
                Margin = new Padding(2, 2, 2, 2),
                Padding = new Padding(8, 6, 8, 4)
            };
        }

        /// <summary>
        /// 创建卡片内部的内容面板，Dock=Top 确保使用 GroupBox 的 DisplayRectangle
        /// （自动避开标题区域），内容不遮挡标题
        /// </summary>
        private FlowLayoutPanel MkCardContent()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,                 // 关键：使用 DisplayRectangle，不遮挡标题
                AutoSize = true,                      // 高度跟随内容
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.Transparent,
                Font = SystemFonts.DefaultFont,
                Padding = new Padding(0, 2, 0, 0)
            };
        }

        // 卡片1：检查目标及筛选
        private GroupBox BuildCard1_Target()
        {
            var card = MkCard("1. 检查目标及筛选");
            var flow = MkCardContent();

            // 行1：OP节点 + PS原生选择框 TxObjEditBoxCtrl
            var row1 = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 2, 0, 2)
            };
            row1.Controls.Add(MkLabel("OP节点"));
            _txtOpNode = new TxObjEditBoxCtrl
            {
                Width = 120,
                Height = 22,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(2, 1, 0, 0),
                PickOnly = true,
                ListenToPick = true
            };
            _txtOpNode.Picked += OnOpNodePicked;
            row1.Controls.Add(_txtOpNode);
            flow.Controls.Add(row1);

            // 行2：点筛选 + ComboBox（宽度按最长项自适应）
            var row2 = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 2)
            };
            row2.Controls.Add(MkLabel("点筛选"));
            _cbPointTypeFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(2, 3, 0, 0)
            };
            _cbPointTypeFilter.Items.AddRange(new object[] { "所有类型", "仅焊点", "仅Via" });
            _cbPointTypeFilter.SelectedIndex = 0;
            AutoFitComboBoxWidth(_cbPointTypeFilter);
            _cbPointTypeFilter.SelectedIndexChanged += (s, e) => ApplyFilterNow();
            row2.Controls.Add(_cbPointTypeFilter);
            flow.Controls.Add(row2);
            flow.Controls.Add(row2);

            // 行3：隐藏正常结果项
            _chkHideNormal = new CheckBox
            {
                Text = "隐藏正常结果项",
                AutoSize = true,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(0, 0, 0, 0)
            };
            _chkHideNormal.CheckedChanged += (s, e) => ApplyFilterNow();
            flow.Controls.Add(_chkHideNormal);

            card.Controls.Add(flow);
            return card;
        }

        // 卡片2：TCP余量检查
        private GroupBox BuildCard2_TcpMargin()
        {
            var card = MkCard("2. TCP余量检查");
            var flow = MkCardContent();

            _chkTcpXyz = new CheckBox
            {
                Text = "点位XYZ余量检查",
                Checked = true,
                AutoSize = true,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(0, 2, 0, 4)
            };
            flow.Controls.Add(_chkTcpXyz);

            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            row.Controls.Add(MkLabel("余量(mm):"));
            _nudTcpMargin = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 9999,
                Value = 200,
                DecimalPlaces = 0,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(2, 3, 0, 0)
            };
            AutoFitNumericWidth(_nudTcpMargin);
            row.Controls.Add(_nudTcpMargin);
            flow.Controls.Add(row);

            card.Controls.Add(flow);
            return card;
        }

        // 卡片3：轴角度余量检查
        private GroupBox BuildCard3_JointMargin()
        {
            var card = MkCard("3. 轴角度余量检查");
            var flow = MkCardContent();

            _chkJointMargin = new CheckBox
            {
                Text = "各轴软限位余量检查",
                Checked = true,
                AutoSize = true,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(0, 2, 0, 4)
            };
            flow.Controls.Add(_chkJointMargin);

            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            row.Controls.Add(MkLabel("余量(度):"));
            _nudJointMarginDeg = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 180,
                Value = 10,
                DecimalPlaces = 0,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(2, 3, 0, 0)
            };
            AutoFitNumericWidth(_nudJointMarginDeg);
            row.Controls.Add(_nudJointMarginDeg);
            flow.Controls.Add(row);

            card.Controls.Add(flow);
            return card;
        }

        // 卡片4：点位干涉检查（仅展示，Enabled=false）
        private GroupBox BuildCard4_Interference()
        {
            var card = MkCard("4. 点位干涉检查");
            var flow = MkCardContent();

            _chkStaticInterference = new CheckBox
            {
                Text = "启用静态干涉检查",
                AutoSize = true,
                Enabled = false,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(0, 2, 0, 4)
            };
            _chkDynamicInterference = new CheckBox
            {
                Text = "启用动态干涉检查",
                AutoSize = true,
                Enabled = false,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(0, 0, 0, 0)
            };
            flow.Controls.AddRange(new Control[] { _chkStaticInterference, _chkDynamicInterference });

            card.Controls.Add(flow);
            return card;
        }

        // 卡片5：功能区（两行按钮：第一行2个 + 第二行3个，自适应宽度，带背景色）
        private GroupBox BuildCard5_Functions()
        {
            var card = MkCard("5. 功能区");
            var outerFlow = MkCardContent();

            // 第一行：2个按钮（开始检查 + 检查所有路径）
            var row1 = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 2)
            };
            var btnCheck = MkFuncButton("开始检查", Color.FromArgb(0, 100, 167));
            btnCheck.Click += BtnCheck_Click;
            var btnAll = MkFuncButton("检查所有路径", Color.FromArgb(0, 120, 90));
            btnAll.Click += BtnCheckAll_Click;
            row1.Controls.AddRange(new Control[] { btnCheck, btnAll });

            // 第二行：3个按钮（结果导出表格 + 重置窗口 + 关闭）
            var row2 = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 0)
            };
            var btnExport = MkFuncButton("结果导出", Color.FromArgb(80, 80, 130));
            btnExport.Click += BtnExport_Click;
            var btnReset = MkFuncButton("重置窗口", Color.FromArgb(130, 100, 40));
            btnReset.Click += (s, e) => ClearResults();
            var btnClose = MkFuncButton("关闭", Color.FromArgb(130, 50, 50));
            btnClose.Click += (s, e) => Close();
            row2.Controls.AddRange(new Control[] { btnExport, btnReset, btnClose });

            outerFlow.Controls.Add(row1);
            outerFlow.Controls.Add(row2);
            card.Controls.Add(outerFlow);
            return card;
        }

        private Label MkLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = SystemFonts.DefaultFont,
            ForeColor = ClrMuted,
            Margin = new Padding(0, 7, 4, 0)
        };

        private Button MkButton(string text, int width)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 24,
                FlatStyle = FlatStyle.System,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(0, 2, 4, 2)
            };
        }

        /// <summary>功能区按钮：自适应文本宽度、单行、带背景色</summary>
        private Button MkFuncButton(string text, Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = SystemFonts.DefaultFont,
                ForeColor = Color.White,
                BackColor = bgColor,
                Margin = new Padding(0, 2, 4, 2),
                Padding = new Padding(8, 2, 8, 2),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bgColor, 0.3f);
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(bgColor, 0.15f);
            return btn;
        }

        /// <summary>根据 ComboBox 最长项文本自动设定宽度</summary>
        private void AutoFitComboBoxWidth(ComboBox cb)
        {
            if (cb == null || cb.Items.Count == 0) return;
            using (var g = CreateGraphics())
            {
                float maxW = 0;
                foreach (var item in cb.Items)
                {
                    float w = g.MeasureString(item.ToString(), cb.Font).Width;
                    if (w > maxW) maxW = w;
                }
                // 加上下拉箭头宽度(20) + 边距(8)
                cb.Width = (int)maxW + 28;
            }
        }

        /// <summary>根据 NumericUpDown 的 Maximum 位数自动设定宽度</summary>
        private void AutoFitNumericWidth(NumericUpDown nud)
        {
            if (nud == null) return;
            string maxText = nud.Maximum.ToString("F" + nud.DecimalPlaces);
            using (var g = CreateGraphics())
            {
                float w = g.MeasureString(maxText, nud.Font).Width;
                // 加上上下箭头宽度(18) + 边距(8)
                nud.Width = (int)w + 26;
            }
        }

        // ── 日志面板 ──────────────────────────────────────────────────────────
        private void BuildLogPanel()
        {
            _logPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 140,
                BackColor = SystemColors.Control,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };
            var hdr = new Panel { Dock = DockStyle.Top, Height = 20, BackColor = Color.FromArgb(0, 70, 127) };
            var lblHdr = new Label
            {
                Text = "运行日志",
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };
            var btnClear = new Button
            {
                Text = "清空",
                Dock = DockStyle.Right,
                Width = 44,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 70, 127),
                ForeColor = Color.White,
                Font = SystemFonts.DefaultFont
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += (s, e) => _logBox?.Clear();
            hdr.Controls.AddRange(new Control[] { btnClear, lblHdr });

            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 8f),
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = false
            };
            _logPanel.Controls.Add(_logBox);
            _logPanel.Controls.Add(hdr);
            Controls.Add(_logPanel);
        }

        private void ToggleLogPanel()
        {
            if (_logPanel == null) return;
            _logVisible = !_logVisible;
            _logPanel.Visible = _logVisible;
            if (_tsBtnLog != null) _tsBtnLog.Checked = _logVisible;
        }

        // ── StatusStrip ────────────────────────────────────────────────────────
        private void BuildStatusStrip()
        {
            _statusStrip = new StatusStrip { SizingGrip = true };
            _lblStatus = new ToolStripStatusLabel("就绪")
            {
                TextAlign = ContentAlignment.MiddleLeft,
                Spring = true
            };
            _statusStrip.Items.Add(_lblStatus);
            Controls.Add(_statusStrip);
        }

        // ── TxFlexGrid ────────────────────────────────────────────────────────
        // 列结构（参考图片）：
        //   # | 品牌 | 机器人名 | 操作名 | 点名 | 点类型 | J1 | J2 | J3 | J4 | J5 | J6 | 检查结果 | 备注
        private void BuildGrid()
        {
            _grid = new TxFlexGrid
            {
                Dock = DockStyle.Fill,
                AllowMerging = C1.Win.C1FlexGrid.AllowMergingEnum.None,
                SelectionMode = C1.Win.C1FlexGrid.SelectionModeEnum.Row,
                AllowEditing = false,
                AllowSorting = C1.Win.C1FlexGrid.AllowSortingEnum.SingleColumn,
                ShowCursor = true,
                Font = SystemFonts.DefaultFont
            };

            _grid.Rows.Fixed = 1;
            _grid.Cols.Count = 14;

            string[] captions = { "#", "品牌", "机器人名", "操作名", "点名", "点类型",
                                   "J1(°)", "J2(°)", "J3(°)", "J4(°)", "J5(°)", "J6(°)",
                                   "检查结果", "备注" };

            // 操作名(COL_OP=3)和点名(COL_PT=4)保持固定宽度，其余列自适应内容
            // 先设定初始宽度（确保表头可完整显示）
            for (int i = 0; i < 14; i++)
            {
                _grid.Cols[i].Caption = captions[i];
                _grid.Cols[i].AllowSorting = true;
            }

            // 使用 Graphics 测量表头文本宽度，确保列宽足以显示完整表头
            using (var g = CreateGraphics())
            {
                var hdrFont = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
                for (int i = 0; i < 14; i++)
                {
                    int textW = (int)g.MeasureString(captions[i], hdrFont).Width + 16; // 加上边距
                    if (i == COL_OP)        _grid.Cols[i].Width = 140;   // 操作名固定
                    else if (i == COL_PT)   _grid.Cols[i].Width = 140;   // 点名固定
                    else if (i == COL_NOTE) _grid.Cols[i].Width = 120;   // 备注列在 Resize 中填充剩余
                    else                    _grid.Cols[i].Width = Math.Max(textW, 42); // 自适应：至少能放下表头
                }
            }

            // 表头样式
            var hdrStyle = _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Fixed];
            hdrStyle.BackColor = Color.FromArgb(218, 227, 243);
            hdrStyle.ForeColor = Color.FromArgb(20, 20, 60);
            hdrStyle.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);

            // 行色
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Normal].BackColor = SystemColors.Window;
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Alternate].BackColor = Color.FromArgb(242, 244, 248);
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Highlight].BackColor = Color.FromArgb(189, 215, 238);
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Highlight].ForeColor = SystemColors.WindowText;

            // 单击行 → 跳转机器人到该点位姿态
            _grid.AfterSelChange += Grid_AfterSelChange;

            // 双击行 → 提示打开 ManipulateLocation
            _grid.DoubleClick += Grid_DblClick;

            // 窗口大小变化时重新分配列宽
            this.Resize += (s, e) => ResizeGridCols();

            Controls.Add(_grid);
        }

        // ── 自适应列宽：内容列按数据自适应，操作名/点名固定，备注列填满剩余 ──
        private void ResizeGridCols()
        {
            if (_grid == null || _grid.Cols.Count < 14) return;
            try
            {
                // 对非固定列（除 COL_OP、COL_PT、COL_NOTE 外）按内容自适应
                int[] autoSizeCols = { COL_IDX, COL_BRAND, COL_ROBOT, COL_TYPE,
                                       COL_J1, COL_J2, COL_J3, COL_J4, COL_J5, COL_J6,
                                       COL_RESULT };
                foreach (int ci in autoSizeCols)
                {
                    try
                    {
                        _grid.AutoSizeCol(ci);
                        // 确保列宽至少能放下表头
                        using (var g = CreateGraphics())
                        {
                            var hdrFont = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
                            int hdrW = (int)g.MeasureString(_grid.Cols[ci].Caption, hdrFont).Width + 16;
                            if (_grid.Cols[ci].Width < hdrW) _grid.Cols[ci].Width = hdrW;
                        }
                    }
                    catch { }
                }

                // 备注列填满剩余空间
                int fixedTotal = 0;
                for (int i = 0; i < 13; i++) fixedTotal += _grid.Cols[i].Width;
                int remaining = _grid.ClientSize.Width - fixedTotal
                              - (SystemInformation.VerticalScrollBarWidth + 2);
                if (remaining > 60) _grid.Cols[COL_NOTE].Width = remaining;
            }
            catch { }
        }

        // =====================================================================
        // 状态 / 日志
        // =====================================================================
        private void SetStatus(string text)
        {
            if (_lblStatus != null) _lblStatus.Text = text;
        }

        private void SetStatus(string text, Color _ignored) => SetStatus(text);

        private void Log(string message, string level = "INFO")
        {
            if (_logBox == null || _logBox.IsDisposed) return;
            if (_logBox.InvokeRequired) { _logBox.BeginInvoke(new Action(() => Log(message, level))); return; }

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            Color col = level == "ERR" ? Color.FromArgb(255, 100, 100)
                      : level == "WARN" ? Color.FromArgb(255, 200, 80)
                      : level == "OK" ? Color.FromArgb(80, 220, 120)
                      : Color.FromArgb(204, 204, 204);

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = col;
            _logBox.AppendText(line + "\n");
            _logBox.SelectionColor = _logBox.ForeColor;
            _logBox.ScrollToCaret();

            if ((level == "ERR" || level == "WARN") && !_logVisible)
            {
                _logVisible = true; _logPanel.Visible = true;
                if (_tsBtnLog != null) _tsBtnLog.Checked = true;
            }
        }

        // =====================================================================
        // 表格事件
        // =====================================================================

        // 单击行：将机器人驱动到该点位的记录姿态，并在 PS 中选中该点位
        private void Grid_AfterSelChange(object sender, C1.Win.C1FlexGrid.RangeEventArgs e)
        {
            int row = _grid.RowSel;
            if (row < _grid.Rows.Fixed) return;
            if (!_rowToResult.TryGetValue(row, out var res)) return;

            SetStatus($"[{res.PointName}]  {res.RobotName}  J1={res.J1:F1}° J2={res.J2:F1}° J3={res.J3:F1}°");

            // 在 PS 场景中选中对应点位（跳转视角）
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return;
                var all = doc.OperationRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject)));
                foreach (ITxObject o in all)
                {
                    if (!(o is ITxRoboticLocationOperation loc)) continue;
                    if (o.Name != res.PointName) continue;
                    try
                    {
                        dynamic sel = TxApplication.ActiveSelection;
                        try { sel.Clear(); } catch { }
                        sel.Add(o);
                    }
                    catch { }
                    return;
                }
            }
            catch { }
        }

        // 双击行：弹出包含 TxPlacementCollisionControl 的点位编辑窗口
        private void Grid_DblClick(object sender, EventArgs e)
        {
            int row = _grid.RowSel;
            if (row < _grid.Rows.Fixed) return;
            if (!_rowToResult.TryGetValue(row, out var res)) return;

            // 找到对应的 ITxRoboticLocationOperation 对象
            ITxRoboticLocationOperation locOp = null;
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc != null)
                {
                    var all = doc.OperationRoot.GetAllDescendants(
                        new TxTypeFilter(typeof(ITxRoboticLocationOperation)));
                    foreach (ITxObject o in all)
                    {
                        if (!(o is ITxRoboticLocationOperation loc)) continue;
                        if (o.Name != res.PointName) continue;
                        locOp = loc;
                        // 同步在 PS 中选中该点位
                        dynamic sel = TxApplication.ActiveSelection;
                        try { sel.Clear(); } catch { }
                        try { sel.Add(o); } catch { }
                        break;
                    }
                }
            }
            catch (Exception ex) { Log("双击查找点位异常: " + ex.Message, "ERR"); return; }

            if (locOp == null)
            {
                SetStatus("未找到点位: " + res.PointName);
                return;
            }

            // 弹出编辑窗口（含 TxPlacementCollisionControl）
            try
            {
                var dlg = new LocationEditForm(locOp, res.PointName);
                dlg.ShowDialog(this);
                Log("点位编辑窗口已关闭: " + res.PointName, "OK");
            }
            catch (Exception ex) { Log("打开编辑窗口异常: " + ex.Message, "ERR"); }
        }

        // =====================================================================
        // 操作选择事件
        // =====================================================================
        private void OnOpSelectionChanged(object sender, EventArgs e)
        {
            UpdateAssocRobotLabel();
            // 同步 TxObjEditBoxCtrl 显示（通过查找操作对象并设置 .Object）
            if (_txtOpNode != null && _tsOp.SelectedItem != null)
            {
                try
                {
                    var doc = TxApplication.ActiveDocument;
                    if (doc != null)
                    {
                        var op = FindOperationByName(doc, _tsOp.SelectedItem.ToString());
                        if (op != null)
                        {
                            // 暂时取消事件监听，避免循环触发
                            _txtOpNode.Picked -= OnOpNodePicked;
                            _txtOpNode.Object = op;
                            _txtOpNode.Picked += OnOpNodePicked;
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>TxObjEditBoxCtrl.Picked 事件：用户在 PS 场景中拾取了对象</summary>
        private void OnOpNodePicked(object sender, EventArgs e)
        {
            if (_txtOpNode == null) return;
            ITxObject pickedObj = null;
            try { pickedObj = _txtOpNode.Object as ITxObject; } catch { return; }
            if (pickedObj == null) return;

            // 验证拾取对象是否为操作类型
            bool isOp = pickedObj is ITxRoboticOperation
                     || pickedObj is TxCompoundOperation
                     || pickedObj is ITxOperation
                     || pickedObj.GetType().Name.Contains("Operation");
            if (!isOp)
            {
                Log($"拾取对象 [{pickedObj.Name}] 不是操作类型（{pickedObj.GetType().Name}），已忽略", "WARN");
                SetStatus($"请选择操作节点，当前对象类型: {pickedObj.GetType().Name}");
                // 清除无效选择
                try { _txtOpNode.Object = null; } catch { }
                return;
            }

            try
            {
                string opName = pickedObj.Name;
                if (string.IsNullOrEmpty(opName)) return;

                // 同步 ToolStrip 下拉列表
                bool found = false;
                for (int i = 0; i < _tsOp.Items.Count; i++)
                {
                    if (_tsOp.Items[i].ToString() == opName)
                    {
                        _tsOp.SelectedIndex = i;
                        found = true;
                        break;
                    }
                }
                // 若列表中不存在，则追加
                if (!found)
                {
                    _tsOp.Items.Add(opName);
                    _tsOp.SelectedIndex = _tsOp.Items.Count - 1;
                }
                UpdateAssocRobotLabel();
                Log($"OP节点拾取: {opName} ({pickedObj.GetType().Name})", "OK");
            }
            catch (Exception ex) { Log($"OP节点拾取处理异常: {ex.Message}", "WARN"); }
        }


        // =====================================================================
        // PS API — 加载操作列表、关联机器人
        // =====================================================================
        private void LoadRobotsAndOperations()
        {
            _tsOp.Items.Clear();
            if (_tsLblRobot != null) { _tsLblRobot.Text = "| 机器人: —"; _tsLblRobot.ForeColor = ClrMuted; }
            Log("刷新操作列表...");
            try
            {
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc == null) throw new InvalidOperationException("ActiveDocument 为 null");
                Log("ActiveDocument OK");

                LoadOperations(doc);
                Log($"加载完成: {_tsOp.Items.Count} 个操作", "OK");
                SetStatus($"● 已加载 {_tsOp.Items.Count} 个操作", ClrSuccess);
                UpdateAssocRobotLabel();
            }
            catch (Exception ex)
            {
                Log($"加载PS操作列表失败: {ex.Message}", "ERR");
                SetStatus("● 加载失败，请确认 PS 已打开文档", ClrDanger);
            }
        }

        /// <summary>选中路径后，自动从操作中解析关联机器人并更新标签</summary>
        private void UpdateAssocRobotLabel()
        {
            if (_tsLblRobot == null || _tsOp?.SelectedItem == null) return;
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return;
                string opName = _tsOp.SelectedItem.ToString();
                ITxObject op = FindOperationByName(doc, opName);
                // 静默查找，不触发 Log（避免加载时刷屏）
                TxRobot robot = FindAssociatedRobotSilent(op);
                if (robot != null)
                {
                    _tsLblRobot.Text = $"| 机器人: {robot.Name}";
                    _tsLblRobot.ForeColor = ClrText;
                }
                else
                {
                    _tsLblRobot.Text = "| 未找到机器人";
                    _tsLblRobot.ForeColor = ClrDanger;
                }
            }
            catch
            {
                if (_tsLblRobot != null)
                {
                    _tsLblRobot.Text = "| 未找到机器人";
                    _tsLblRobot.ForeColor = ClrDanger;
                }
            }
        }

        // 静默版（不写 Log）
        private TxRobot FindAssociatedRobotSilent(ITxObject operation)
        {
            if (operation == null) return null;
            try { dynamic d = operation; var r = d.Robot as TxRobot; if (r != null) return r; } catch { }
            try { dynamic d = operation; var r = d.Device as TxRobot; if (r != null) return r; } catch { }
            try { dynamic d = operation; var r = d.RobotDevice as TxRobot; if (r != null) return r; } catch { }
            try
            {
                dynamic cur = operation;
                for (int depth = 0; depth < 8; depth++)
                {
                    object parent = null;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    if (parent is TxRobot rp) return rp;
                    cur = parent;
                }
            }
            catch { }
            return null;
        }

        private void LoadOperations(TxDocument doc)
        {
            _tsOp.Items.Clear();
            try
            {
                if (doc == null) return;
                var kids = doc.OperationRoot.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
                CollectOps(kids, _tsOp.Items);
                if (_tsOp.Items.Count > 0) _tsOp.SelectedIndex = 0;
            }
            catch { }
        }

        private void CollectOps(TxObjectList nodes, ComboBox.ObjectCollection items)
        {
            if (nodes == null) return;
            foreach (ITxObject obj in nodes)
            {
                if (obj == null) continue;
                bool isOp = obj is ITxRoboticOperation || obj is TxCompoundOperation
                         || obj is ITxOperation || obj.GetType().Name.Contains("Operation");
                if (!isOp) continue;
                if (!string.IsNullOrEmpty(obj.Name) && !items.Contains(obj.Name))
                    items.Add(obj.Name);
                if (obj is TxCompoundOperation co)
                    try { CollectOps(co.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))), items); } catch { }
            }
        }

        // =====================================================================
        // PS API — 核心可达性检测
        // 关键接口（来自 TxRobot API 文档）：
        //   CalcInverseSolutions(TxTransformation)  → TxIkSolutionList
        //   GetPoseAtLocation(ITxRoboticLocationOperation) → TxPose
        //   robot.Joints                             → TxObjectList (ITxJoint)
        //   robot.CurrentPose                        → TxPose
        // =====================================================================
        // =====================================================================
        // 核心可达性检测（带详细日志）
        //
        // 轴值获取策略（按优先级）：
        //   方式A：TxPoseData.JointValues 直接属性 —— 最直接，无需驱动机器人
        //   方式B：GetPoseAtLocation + TxPoseData 遍历索引/属性
        //   方式C：IK（CalcInverseSolutions）→ 取第一解 → 读 JointValues
        //   方式D：直接读 DrivingJoints.Value（当前状态）——最后备用
        //
        // 关键：TxPoseData 在 PS API 中是 double-indexed 对象，
        //       可通过 poseData[i] 或 poseData.Values[i] 访问各轴角度（度）
        // =====================================================================
        private List<PathPointResult> CheckReachabilityViaPS(string operationName)
        {
            var results = new List<PathPointResult>();
            Log($"开始检查：操作=[{operationName}]");
            try
            {
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc == null) throw new InvalidOperationException("ActiveDocument 为 null");
                Log("ActiveDocument 获取成功");

                // ── 1. 查找操作 ───────────────────────────────────────────
                ITxObject operation = FindOperationByName(doc, operationName);
                if (operation == null) throw new InvalidOperationException($"未找到操作: {operationName}");
                Log($"操作找到: {operation.Name}  类型: {operation.GetType().Name}");

                // ── 2. 从操作自动查找关联机器人 ───────────────────────────
                // TxRoboticOperation 上有 Robot 属性直接返回关联机器人
                TxRobot robot = FindAssociatedRobot(operation, doc);
                if (robot == null) throw new InvalidOperationException(
                    $"无法从操作 [{operationName}] 找到关联机器人，请确认操作已分配到机器人");
                Log($"关联机器人: {robot.Name}  类型: {robot.GetType().Name}", "OK");

                // 探测 DrivingJoints 数量
                int djCount = 0;
                try { djCount = robot.DrivingJoints?.Count ?? 0; } catch { }
                Log($"DrivingJoints 数量: {djCount}");

                // ── 3. 枚举路径点位 ───────────────────────────────────────
                var locs = EnumerateLocations(operation);
                Log($"共找到 {locs.Count} 个路径点位");
                if (locs.Count == 0)
                    throw new InvalidOperationException($"操作 [{operationName}] 下未找到路径点，请确认操作类型");

                // ── 4. 读取关节限位 ───────────────────────────────────────
                var jointLimits = GetJointLimits(robot);
                Log($"关节限位获取: {jointLimits.Count} 轴");

                // ── 5. 保存初始姿态 ───────────────────────────────────────
                TxPoseData savedPose = null;
                try { savedPose = robot.CurrentPose; Log("初始姿态已保存"); }
                catch (Exception ex) { Log($"保存初始姿态失败（非致命）: {ex.Message}", "WARN"); }

                int idx = 1;
                int okA = 0, okB = 0, okC = 0, fail = 0;

                // ── 6. 预计算机器人最大臂展（仅解析一次）────────────────
                double cachedMaxReach = 0;
                bool tcpGlobalEnabled = _chkTcpXyz != null && _chkTcpXyz.Checked;
                if (tcpGlobalEnabled)
                {
                    cachedMaxReach = ParseMaxReachFromName(robot.Name);
                    if (cachedMaxReach <= 0)
                        Log($"TCP余量检查: 无法从机器人名 [{robot.Name}] 解析最大臂展，XYZ检查将跳过", "WARN");
                }

                foreach (ITxRoboticLocationOperation loc in locs)
                {
                    // 检测点类型（Weld 或 Via）
                    string ptType = "Via";
                    try
                    {
                        string tn = loc.GetType().Name;
                        if (tn.Contains("Weld") || tn.Contains("weld")) ptType = "Weld";
                        else
                        {
                            dynamic dl = loc; string lt = dl.LocationType?.ToString() ?? "";
                            if (lt.Contains("Weld")) ptType = "Weld";
                        }
                    }
                    catch { }

                    var res = new PathPointResult
                    {
                        Index = idx++,
                        PointName = string.IsNullOrEmpty(loc.Name) ? $"P{idx - 1}" : loc.Name,
                        OperationName = operationName,
                        RobotName = robot.Name,
                        PointType = ptType
                    };

                    bool gotJoints = false;
                    double[] joints = null;
                    string errMsg = "";

                    // ══════════════════════════════════════════════════════════
                    // 方式 A：GetPoseAtLocation → 直接从 TxPoseData 读轴值
                    // TxPoseData 支持 poseData[i] 索引器访问各轴角度（度）
                    // 或通过 poseData.Values 属性获取 double[]
                    // ══════════════════════════════════════════════════════════
                    try
                    {
                        TxPoseData pd = robot.GetPoseAtLocation(loc);
                        if (pd != null)
                        {
                            // 策略A1：尝试 Values 属性（double[] 或 IEnumerable）
                            double[] extracted = TryExtractPoseValues(pd, djCount);
                            if (extracted != null && extracted.Length > 0)
                            {
                                joints = extracted;
                                gotJoints = true;
                                okA++;
                                Log($"  [{res.PointName}] 方式A(Values)成功: [{string.Join(", ", Array.ConvertAll(joints, v => v.ToString("F1")))}]", "OK");
                            }
                        }
                        else
                        {
                            Log($"  [{res.PointName}] GetPoseAtLocation 返回 null", "WARN");
                        }
                    }
                    catch (Exception exA)
                    {
                        Log($"  [{res.PointName}] 方式A异常: {exA.Message}", "WARN");
                        errMsg = $"方式A: {exA.Message}";
                    }

                    // ══════════════════════════════════════════════════════════
                    // 方式 B：GetPoseAtLocation → 驱动机器人 → 读 DrivingJoints
                    // 仅当方式A无法直接取值时使用
                    // ══════════════════════════════════════════════════════════
                    if (!gotJoints)
                    {
                        try
                        {
                            TxPoseData pd = robot.GetPoseAtLocation(loc);
                            if (pd != null && savedPose != null)
                            {
                                robot.CurrentPose = pd;   // 驱动到目标点位姿态
                                double[] extracted = ReadDrivingJoints(robot);
                                if (extracted != null && extracted.Length > 0)
                                {
                                    joints = extracted;
                                    gotJoints = true;
                                    okB++;
                                    Log($"  [{res.PointName}] 方式B(Drive+DrivingJoints)成功: [{string.Join(", ", Array.ConvertAll(joints, v => v.ToString("F1")))}]", "OK");
                                }
                                else { Log($"  [{res.PointName}] 方式B: DrivingJoints 读取为空", "WARN"); }
                            }
                        }
                        catch (Exception exB)
                        {
                            Log($"  [{res.PointName}] 方式B异常: {exB.Message}", "WARN");
                            if (string.IsNullOrEmpty(errMsg)) errMsg = $"方式B: {exB.Message}";
                        }
                        finally
                        {
                            // 每次驱动后立即恢复
                            try { if (savedPose != null) robot.CurrentPose = savedPose; } catch { }
                        }
                    }

                    // ══════════════════════════════════════════════════════════
                    // 方式 C：IK 逆运动学（CalcInverseSolutions）
                    // ══════════════════════════════════════════════════════════
                    if (!gotJoints)
                    {
                        TxTransformation locTx = GetLocationTransform(loc);
                        if (locTx == null)
                        {
                            Log($"  [{res.PointName}] 无法获取点位变换矩阵，跳过IK", "WARN");
                        }
                        else
                        {
                            Log($"  [{res.PointName}] 尝试IK求解...");
                            try
                            {
                                var invData = new TxRobotInverseData(locTx);

                                bool hasInv = false;
                                try { hasInv = robot.DoesInverseExist(invData); }
                                catch (Exception exExist) { Log($"  [{res.PointName}] DoesInverseExist异常: {exExist.Message}", "WARN"); }

                                if (!hasInv)
                                {
                                    Log($"  [{res.PointName}] IK无解（超出工作包络或奇异）", "WARN");
                                    errMsg = "IK无解：超出工作包络或构型奇异";
                                    res.Status = ReachabilityStatus.Unreachable;
                                    res.ErrorMessage = errMsg;
                                    fail++;
                                    results.Add(res);
                                    continue;
                                }

                                System.Collections.ArrayList solutions = robot.CalcInverseSolutions(invData);
                                Log($"  [{res.PointName}] IK解数量: {solutions?.Count ?? 0}");

                                if (solutions != null && solutions.Count > 0)
                                {
                                    // 先尝试直接读第一个解的 Values（方式C1）
                                    TxPoseData firstSol = solutions[0] as TxPoseData;
                                    if (firstSol != null)
                                    {
                                        double[] extracted = TryExtractPoseValues(firstSol, djCount);
                                        if (extracted != null && extracted.Length > 0)
                                        {
                                            joints = extracted;
                                            gotJoints = true;
                                            okC++;
                                            Log($"  [{res.PointName}] 方式C1(IK+Values)成功: [{string.Join(", ", Array.ConvertAll(joints, v => v.ToString("F1")))}]", "OK");
                                        }
                                        else
                                        {
                                            // 方式C2：驱动到IK解，再读关节
                                            try
                                            {
                                                robot.CurrentPose = firstSol;
                                                double[] drv = ReadDrivingJoints(robot);
                                                if (drv != null && drv.Length > 0)
                                                {
                                                    joints = drv;
                                                    gotJoints = true;
                                                    okC++;
                                                    Log($"  [{res.PointName}] 方式C2(IK+Drive)成功: [{string.Join(", ", Array.ConvertAll(joints, v => v.ToString("F1")))}]", "OK");
                                                }
                                                else { Log($"  [{res.PointName}] 方式C2: DrivingJoints仍为空", "WARN"); }
                                            }
                                            catch (Exception exC2) { Log($"  [{res.PointName}] 方式C2异常: {exC2.Message}", "WARN"); }
                                            finally { try { if (savedPose != null) robot.CurrentPose = savedPose; } catch { } }
                                        }
                                    }
                                }
                            }
                            catch (Exception exIk)
                            {
                                Log($"  [{res.PointName}] IK整体异常: {exIk.Message}", "ERR");
                                if (string.IsNullOrEmpty(errMsg)) errMsg = $"IK: {exIk.Message}";
                            }
                        }
                    }

                    // ── 填写结果 ────────────────────────────────────────────
                    if (gotJoints && joints != null)
                    {
                        // 单位判断：若所有值绝对值 <= 2π+ε，判定为弧度
                        double maxAbs = joints.Max(Math.Abs);
                        bool isRad = maxAbs > 0 && maxAbs <= 2 * Math.PI + 0.05;
                        Func<double, double> toDeg = v =>
                            Math.Round(isRad ? v * 180.0 / Math.PI : v, 2);

                        res.J1 = joints.Length > 0 ? toDeg(joints[0]) : 0;
                        res.J2 = joints.Length > 1 ? toDeg(joints[1]) : 0;
                        res.J3 = joints.Length > 2 ? toDeg(joints[2]) : 0;
                        res.J4 = joints.Length > 3 ? toDeg(joints[3]) : 0;
                        res.J5 = joints.Length > 4 ? toDeg(joints[4]) : 0;
                        res.J6 = joints.Length > 5 ? toDeg(joints[5]) : 0;

                        // 默认状态：可达
                        res.Status = ReachabilityStatus.Reachable;
                        res.ErrorMessage = errMsg;

                        // ── 各轴软限位余量检查（仅在勾选时执行）─────────────
                        bool jointCheckEnabled = _chkJointMargin != null && _chkJointMargin.Checked;
                        if (jointCheckEnabled)
                        {
                            double minMargin = CalcMinJointMargin(jointLimits,
                                res.J1, res.J2, res.J3, res.J4, res.J5, res.J6);
                            res.JointMargin = Math.Round(minMargin, 1);
                            double marginThresh = _nudJointMarginDeg != null
                                ? (double)_nudJointMarginDeg.Value : 10.0;
                            if (minMargin < marginThresh)
                            {
                                res.Status = ReachabilityStatus.NearLimit;
                                res.ErrorMessage = $"轴余量 {minMargin:F1}° < 阈值 {marginThresh:F0}°";
                                Log($"  [{res.PointName}] 轴余量不足: {minMargin:F1}° < {marginThresh:F0}°", "WARN");
                            }
                        }
                        else
                        {
                            // 未启用轴余量检查时仍计算余量值供参考，但不影响状态
                            double minMargin = CalcMinJointMargin(jointLimits,
                                res.J1, res.J2, res.J3, res.J4, res.J5, res.J6);
                            res.JointMargin = Math.Round(minMargin, 1);
                        }

                        // ── 点位 XYZ 余量检查（仅在勾选时执行）──────────────
                        if (tcpGlobalEnabled && cachedMaxReach > 0)
                        {
                            double tcpMarginMm = _nudTcpMargin != null ? (double)_nudTcpMargin.Value : 200.0;
                            string tcpWarn = CheckTcpXyzMargin(robot, loc, tcpMarginMm, cachedMaxReach);
                            if (!string.IsNullOrEmpty(tcpWarn))
                            {
                                // TCP 余量不足时升级状态（不可达 > 接近极限 > 可达）
                                if (res.Status == ReachabilityStatus.Reachable)
                                    res.Status = ReachabilityStatus.NearLimit;
                                res.ErrorMessage = string.IsNullOrEmpty(res.ErrorMessage)
                                    ? tcpWarn
                                    : res.ErrorMessage + "; " + tcpWarn;
                                Log($"  [{res.PointName}] TCP余量: {tcpWarn}", "WARN");
                            }
                        }
                    }
                    else
                    {
                        res.Status = ReachabilityStatus.Unreachable;
                        res.ErrorMessage = string.IsNullOrEmpty(errMsg)
                            ? "所有方式均无法获取轴值，请检查日志"
                            : errMsg;
                        fail++;
                        Log($"  [{res.PointName}] 所有方式失败: {res.ErrorMessage}", "ERR");
                    }

                    results.Add(res);
                }

                // 恢复初始姿态
                try { if (savedPose != null) robot.CurrentPose = savedPose; } catch { }

                Log($"检查完成: 方式A={okA}  方式B={okB}  方式C={okC}  失败={fail}", "OK");
            }
            catch (Exception ex)
            {
                Log($"检查异常: {ex.Message}", "ERR");
                Log($"  StackTrace: {ex.StackTrace?.Split('\n').FirstOrDefault()}", "ERR");
            }
            return results;
        }

        // ── 从 TxPoseData 直接提取轴值（多种属性尝试）─────────────────────
        // TxPoseData 在不同 PS 版本中暴露的属性略有差异：
        //   - 属性 Values（double[]）：PS 16+
        //   - 索引器 [int i]（double）：大多数版本
        //   - 属性 JointValues（double[]）
        private double[] TryExtractPoseValues(TxPoseData pd, int expectedCount)
        {
            if (pd == null) return null;
            int count = expectedCount > 0 ? expectedCount : 6;

            // 策略1：Values 属性
            try
            {
                dynamic dpd = pd;
                object vals = dpd.Values;
                double[] arr = ExtractDoubleArray(vals);
                if (arr != null && arr.Length > 0) return arr;
            }
            catch { }

            // 策略2：JointValues 属性
            try
            {
                dynamic dpd = pd;
                object vals = dpd.JointValues;
                double[] arr = ExtractDoubleArray(vals);
                if (arr != null && arr.Length > 0) return arr;
            }
            catch { }

            // 策略3：索引器访问 pd[0..count-1]
            try
            {
                dynamic dpd = pd;
                var list = new List<double>();
                for (int i = 0; i < count; i++)
                {
                    try { list.Add(Convert.ToDouble(dpd[i])); }
                    catch { break; }
                }
                if (list.Count > 0) return list.ToArray();
            }
            catch { }

            // 策略4：GetValues() 方法
            try
            {
                dynamic dpd = pd;
                object vals = dpd.GetValues();
                double[] arr = ExtractDoubleArray(vals);
                if (arr != null && arr.Length > 0) return arr;
            }
            catch { }

            return null;
        }

        // ── 读取 DrivingJoints 当前关节值 ────────────────────────────────
        private double[] ReadDrivingJoints(TxRobot robot)
        {
            var list = new List<double>();
            try
            {
                TxObjectList dj = robot.DrivingJoints;
                if (dj == null || dj.Count == 0) return null;
                foreach (ITxObject j in dj)
                {
                    double v = 0;
                    bool got = false;
                    // 尝试 Value 属性（主要）
                    try { dynamic d = j; v = Convert.ToDouble(d.Value); got = true; } catch { }
                    // 尝试 CurrentValue
                    if (!got) try { dynamic d = j; v = Convert.ToDouble(d.CurrentValue); got = true; } catch { }
                    // 尝试 Angle
                    if (!got) try { dynamic d = j; v = Convert.ToDouble(d.Angle); } catch { }
                    list.Add(v);
                }
            }
            catch { }
            return list.Count > 0 ? list.ToArray() : null;
        }

        // ── 枚举操作下所有位置点 ─────────────────────────────────────────────
        private List<ITxRoboticLocationOperation> EnumerateLocations(ITxObject operation)
        {
            var list = new List<ITxRoboticLocationOperation>();
            Log($"  枚举点位: 操作类型={operation?.GetType().Name ?? "null"}");

            // 路径1：复合操作（TxRoboticOperation / TxCompoundOperation）
            if (operation is ITxCompoundOperation comp)
            {
                try
                {
                    var objs = comp.GetAllDescendants(
                        new TxTypeFilter(typeof(ITxRoboticLocationOperation)));
                    Log($"  ITxCompoundOperation.GetAllDescendants 返回 {objs?.Count ?? 0} 个对象");
                    foreach (ITxObject o in objs)
                        if (o is ITxRoboticLocationOperation l) list.Add(l);
                }
                catch (Exception ex) { Log($"  GetAllDescendants 异常: {ex.Message}", "WARN"); }
            }
            else
            {
                Log("  操作不是 ITxCompoundOperation，尝试其他方式...", "WARN");
            }

            // 路径2：尝试 dynamic GetDirectDescendants / GetAllDescendants
            if (list.Count == 0)
            {
                try
                {
                    dynamic dop = operation;
                    TxObjectList objs = dop.GetAllDescendants(
                        new TxTypeFilter(typeof(ITxRoboticLocationOperation)));
                    Log($"  dynamic.GetAllDescendants 返回 {objs?.Count ?? 0} 个对象");
                    foreach (ITxObject o in objs)
                        if (o is ITxRoboticLocationOperation l) list.Add(l);
                }
                catch (Exception ex) { Log($"  dynamic.GetAllDescendants 异常: {ex.Message}", "WARN"); }
            }

            // 路径3：操作本身就是一个 LocationOperation
            if (list.Count == 0 && operation is ITxRoboticLocationOperation self)
            {
                Log("  操作本身是 ITxRoboticLocationOperation，作为单点处理");
                list.Add(self);
            }

            Log($"  枚举结果: {list.Count} 个点位");
            return list;
        }

        // ── 获取点位绝对变换 ─────────────────────────────────────────────────
        // ITxRoboticLocationOperation 本身不直接暴露 AbsoluteLocation，
        // 需通过 dynamic 访问（实现类上有该属性，接口上未定义）
        // ── 获取点位世界坐标变换矩阵 ──────────────────────────────────────
        // IK 求解必须使用世界坐标系（绝对坐标）下的变换矩阵，否则全部无解
        // 优先顺序：AbsoluteLocation > AbsoluteFrame > LocationInWorld > Location（相对，最后用）
        private TxTransformation GetLocationTransform(ITxRoboticLocationOperation loc)
        {
            TxTransformation tx = null;

            // 策略1：AbsoluteLocation（绝对世界坐标，PS 中最标准的属性名）
            try { dynamic d = loc; var v = d.AbsoluteLocation; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: AbsoluteLocation OK"); return tx; } } catch { }

            // 策略2：AbsoluteFrame
            try { dynamic d = loc; var v = d.AbsoluteFrame; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: AbsoluteFrame OK"); return tx; } } catch { }

            // 策略3：LocationInWorld（部分版本）
            try { dynamic d = loc; var v = d.LocationInWorld; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: LocationInWorld OK"); return tx; } } catch { }

            // 策略4：通过 ITxLocatableObject 接口（PS 的通用定位接口）
            try
            {
                if (loc is ITxLocatableObject lobj)
                {
                    tx = lobj.AbsoluteLocation;
                    if (tx != null) { Log($"    GetLocationTransform: ITxLocatableObject.AbsoluteLocation OK"); return tx; }
                }
            }
            catch { }

            // 策略5：Location（相对坐标，仅作最后备用，IK 大概率无解）
            try { dynamic d = loc; var v = d.Location; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: Location(相对坐标，IK可能无解)", "WARN"); return tx; } } catch { }

            // 策略6：Frame
            try { dynamic d = loc; var v = d.Frame; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: Frame(相对坐标)", "WARN"); return tx; } } catch { }

            Log($"    GetLocationTransform: 所有属性均失败，无法获取变换矩阵", "ERR");
            return null;
        }

        // ── 从各种类型提取 double[] ──────────────────────────────────────────
        private double[] ExtractDoubleArray(object jv)
        {
            if (jv == null) return null;
            if (jv is double[] arr) return arr;
            if (jv is IEnumerable ie)
            {
                var tmp = new List<double>();
                foreach (object v in ie)
                    try { tmp.Add(Convert.ToDouble(v)); } catch { }
                return tmp.Count > 0 ? tmp.ToArray() : null;
            }
            return null;
        }

        // ── 获取关节限位 ──────────────────────────────────────────────────────
        // DrivingJoints 返回 TxObjectList（非泛型）
        // Joints 返回 TxObjectList<TxJoint>（泛型），两者类型不同，不能混用同一变量
        private List<(double lo, double hi)> GetJointLimits(TxRobot robot)
        {
            var limits = new List<(double, double)>();
            try
            {
                // 优先用 DrivingJoints（非泛型 TxObjectList）
                TxObjectList drivingJoints = robot.DrivingJoints;
                IEnumerable jointSource = (drivingJoints != null && drivingJoints.Count > 0)
                    ? (IEnumerable)drivingJoints
                    : (IEnumerable)robot.Joints; // 回退：TxObjectList<TxJoint> 也实现 IEnumerable

                if (jointSource == null) return limits;

                foreach (object j in jointSource)
                {
                    double lo = -360, hi = 360;
                    try
                    {
                        dynamic dj = j;
                        bool gotLo = false, gotHi = false;
                        try { lo = Convert.ToDouble(dj.MinValue); gotLo = true; } catch { }
                        try { hi = Convert.ToDouble(dj.MaxValue); gotHi = true; } catch { }
                        if (!gotLo) try { lo = Convert.ToDouble(dj.LowerLimit); } catch { }
                        if (!gotHi) try { hi = Convert.ToDouble(dj.UpperLimit); } catch { }
                        if (!gotLo) try { lo = Convert.ToDouble(dj.Min); } catch { }
                        if (!gotHi) try { hi = Convert.ToDouble(dj.Max); } catch { }
                        // 若范围 <= 2π 则认为是弧度，转为度
                        if (Math.Abs(hi - lo) <= 2 * Math.PI + 0.1)
                        { lo *= 180.0 / Math.PI; hi *= 180.0 / Math.PI; }
                    }
                    catch { }
                    limits.Add((lo, hi));
                }
            }
            catch { }
            return limits;
        }

        // ── 计算最小关节余量（度）────────────────────────────────────────────
        private double CalcMinJointMargin(List<(double lo, double hi)> limits, params double[] angles)
        {
            double minMargin = 9999;
            for (int i = 0; i < Math.Min(angles.Length, limits.Count); i++)
            {
                double lo = limits[i].lo, hi = limits[i].hi;
                double range = hi - lo;
                if (range < 1) continue;
                double toLo = angles[i] - lo;
                double toHi = hi - angles[i];
                double margin = Math.Min(toLo, toHi);
                if (margin < minMargin) minMargin = margin;
            }
            return minMargin < 9999 ? minMargin : 9999;
        }

        // ── 接近极限判断（按阈值）───────────────────────────────────────────
        private bool IsNearLimit(List<(double lo, double hi)> limits, params double[] angles)
        {
            for (int i = 0; i < Math.Min(angles.Length, limits.Count); i++)
            {
                double lo = limits[i].lo, hi = limits[i].hi;
                double range = hi - lo;
                if (range < 0.1) continue;
                double pct = (angles[i] - lo) / range;
                if (pct < 0.10 || pct > 0.90) return true;
            }
            return false;
        }

        // ── 点位 XYZ 余量检查 ────────────────────────────────────────────────
        // 计算 TCP 位置到机器人基座的距离，检查是否接近最大可达距离
        // marginMm = 用户在卡片2中设定的余量阈值(mm)
        //
        // 日志分析结论：TxRobot 不暴露 ReachDistance/MaxReach/Reach/Links 等属性，
        // 因此采用从机器人名称解析最大可达距离的策略（覆盖 KUKA/ABB/FANUC/YASKAWA 命名规范）
        // 返回空字符串表示合格，否则返回警告信息
        private string CheckTcpXyzMargin(TxRobot robot, ITxRoboticLocationOperation loc, double marginMm, double maxReach)
        {
            try
            {
                if (maxReach <= 0) return "";

                // ── 1. 获取机器人基座世界坐标 ──────────────────────────
                TxTransformation robotBase = null;
                try { robotBase = ((ITxLocatableObject)robot).AbsoluteLocation; }
                catch { try { dynamic dr = robot; robotBase = dr.AbsoluteLocation; } catch { } }
                if (robotBase == null) return "";

                // ── 3. 获取点位世界坐标 ────────────────────────────────
                TxTransformation locTx = GetLocationTransform(loc);
                if (locTx == null) return "";

                // ── 4. 提取平移分量(X,Y,Z)并计算距离 ──────────────────
                double[] baseXyz = ExtractTranslation(robotBase);
                double[] ptXyz = ExtractTranslation(locTx);
                if (baseXyz == null || ptXyz == null) return "";

                double dx = ptXyz[0] - baseXyz[0];
                double dy = ptXyz[1] - baseXyz[1];
                double dz = ptXyz[2] - baseXyz[2];
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                // ── 5. 判定余量 ───────────────────────────────────────
                double reachMargin = maxReach - dist;
                if (reachMargin < marginMm)
                {
                    return $"TCP距离 {dist:F0}mm, 余量 {reachMargin:F0}mm < {marginMm:F0}mm (最大臂展 {maxReach:F0}mm)";
                }
            }
            catch (Exception ex)
            {
                Log($"    TCP余量检查异常: {ex.Message}", "WARN");
            }
            return "";
        }

        /// <summary>
        /// 从机器人名称解析最大可达距离（mm）
        /// 覆盖主流品牌命名规范：
        ///   KUKA:    KR210_R2700-2  → R后跟数字 = 2700mm
        ///   ABB:     IRB 6700-2.55  → 最后的小数 × 1000 = 2550mm
        ///   FANUC:   M-20iB/25      → 无明确规律，使用型号数据库
        ///   YASKAWA: MH180-120      → 后缀数字可能是臂展(cm)
        /// </summary>
        private double ParseMaxReachFromName(string robotName)
        {
            if (string.IsNullOrEmpty(robotName)) return 0;
            string upper = robotName.ToUpper();

            // KUKA 命名：R后跟 3-4 位数字（mm）
            // 例: KR210_R2700-2 → 2700,  KR6_R900 → 900
            var kukaMatch = System.Text.RegularExpressions.Regex.Match(upper, @"R(\d{3,4})(?:\D|$)");
            if (kukaMatch.Success && (upper.Contains("KR") || upper.Contains("KUKA")))
            {
                if (double.TryParse(kukaMatch.Groups[1].Value, out double r) && r >= 500 && r <= 4000)
                {
                    Log($"    TCP余量: 从机器人名 [{robotName}] 解析到最大臂展 = {r}mm (KUKA)", "OK");
                    return r;
                }
            }

            // ABB 命名：末尾小数（米）
            // 例: IRB 6700-2.55 → 2.55m = 2550mm
            if (upper.Contains("IRB") || upper.Contains("ABB"))
            {
                var abbMatch = System.Text.RegularExpressions.Regex.Match(robotName, @"(\d+\.\d+)\s*$");
                if (!abbMatch.Success)
                    abbMatch = System.Text.RegularExpressions.Regex.Match(robotName, @"-(\d+\.\d+)");
                if (abbMatch.Success && double.TryParse(abbMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double m) && m >= 0.5 && m <= 4.0)
                {
                    double r = m * 1000;
                    Log($"    TCP余量: 从机器人名 [{robotName}] 解析到最大臂展 = {r}mm (ABB)", "OK");
                    return r;
                }
            }

            // 通用：名称中包含 R+3~4位数字（可能是其他品牌）
            var genericMatch = System.Text.RegularExpressions.Regex.Match(upper, @"R(\d{3,4})");
            if (genericMatch.Success)
            {
                if (double.TryParse(genericMatch.Groups[1].Value, out double r) && r >= 500 && r <= 4000)
                {
                    Log($"    TCP余量: 从机器人名 [{robotName}] 解析到最大臂展 = {r}mm (通用)", "OK");
                    return r;
                }
            }

            Log($"    TCP余量: 无法从机器人名 [{robotName}] 解析最大臂展，跳过XYZ检查", "WARN");
            return 0;
        }

        /// <summary>从 TxTransformation 提取平移分量 [X, Y, Z]（mm）</summary>
        private double[] ExtractTranslation(TxTransformation tx)
        {
            if (tx == null) return null;
            dynamic dt = tx;

            // 策略1：4x4矩阵索引器 [row, col]，平移在第4列
            try { return new double[] { Convert.ToDouble(dt[0, 3]), Convert.ToDouble(dt[1, 3]), Convert.ToDouble(dt[2, 3]) }; } catch { }

            // 策略2：X/Y/Z 属性
            try { return new double[] { Convert.ToDouble(dt.X), Convert.ToDouble(dt.Y), Convert.ToDouble(dt.Z) }; } catch { }

            // 策略3：Translation 属性
            try { dynamic t = dt.Translation; return new double[] { Convert.ToDouble(t.X), Convert.ToDouble(t.Y), Convert.ToDouble(t.Z) }; } catch { }

            // 策略4：GetTranslation() 方法
            try { dynamic t = dt.GetTranslation(); return new double[] { Convert.ToDouble(t.X), Convert.ToDouble(t.Y), Convert.ToDouble(t.Z) }; } catch { }

            return null;
        }

        // ── 从操作自动查找关联机器人 ──────────────────────────────────────
        // PS 中 TxRoboticOperation 的 Robot 属性直接返回分配的机器人对象
        // 若 Robot 属性不可用，则向上遍历父级寻找 TxRobot
        private TxRobot FindAssociatedRobot(ITxObject operation, TxDocument doc)
        {
            if (operation == null) return null;

            // 方式1：直接访问 Robot 属性（TxRoboticOperation / TxWeldOperation 均有此属性）
            try { dynamic dop = operation; var r = dop.Robot as TxRobot; if (r != null) { Log($"  关联机器人(方式1 .Robot): {r.Name}"); return r; } } catch { }

            // 方式2：Device 属性（部分 PS 版本）
            try { dynamic dop = operation; var r = dop.Device as TxRobot; if (r != null) { Log($"  关联机器人(方式2 .Device): {r.Name}"); return r; } } catch { }

            // 方式3：RobotDevice 属性
            try { dynamic dop = operation; var r = dop.RobotDevice as TxRobot; if (r != null) { Log($"  关联机器人(方式3 .RobotDevice): {r.Name}"); return r; } } catch { }

            // 方式4：向上遍历 Parent 链，找到 TxRobot 类型节点
            try
            {
                dynamic cur = operation;
                for (int depth = 0; depth < 10; depth++)
                {
                    object parent = null;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    if (parent is TxRobot rp) { Log($"  关联机器人(方式4 Parent链 depth={depth}): {rp.Name}"); return rp; }
                    cur = parent;
                }
            }
            catch { }

            // 方式5：从操作名匹配场景中的机器人（按操作归属的 Compound 父级查找）
            try
            {
                dynamic dop = operation;
                object parentCompound = dop.ParentOperation;
                if (parentCompound != null)
                {
                    dynamic dc = parentCompound;
                    var r = dc.Robot as TxRobot;
                    if (r != null) { Log($"  关联机器人(方式5 ParentOperation.Robot): {r.Name}"); return r; }
                }
            }
            catch { }

            Log("  所有方式均未能找到关联机器人", "WARN");
            return null;
        }

        private static ITxObject FindOperationByName(TxDocument doc, string name)
        {
            var kids = doc.OperationRoot.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
            return FindOpRecursive(kids, name, 0);
        }

        private static ITxObject FindOpRecursive(TxObjectList nodes, string name, int depth)
        {
            if (nodes == null || depth > 20) return null;
            foreach (ITxObject obj in nodes)
            {
                if (obj == null) continue;
                bool isOp = obj is ITxRoboticOperation || obj is TxCompoundOperation
                         || obj is ITxOperation || obj.GetType().Name.Contains("Operation");
                if (!isOp) continue;
                if (obj.Name == name) return obj;
                TxObjectList sub = null;
                if (obj is TxCompoundOperation co)
                    try { sub = co.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))); } catch { }
                var found = FindOpRecursive(sub, name, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        // =====================================================================
        // 事件处理
        // =====================================================================
        private void OnSelectionTick(object sender, EventArgs e)
        {
            try
            {
                var sel = TxApplication.ActiveSelection.GetItems();
                if (sel.Count == 1 && sel[0] is ITxRoboticOperation op)
                {
                    if (_lastSelectedOp == null || _lastSelectedOp.Name != op.Name)
                    {
                        _lastSelectedOp = op;
                        // 同步选择操作下拉
                        for (int i = 0; i < _tsOp.Items.Count; i++)
                            if (_tsOp.Items[i].ToString() == op.Name)
                            { _tsOp.SelectedIndex = i; break; }
                        // 更新关联机器人标签
                        UpdateAssocRobotLabel();
                        PreviewLocations(op);
                    }
                }
            }
            catch { }
        }

        private void PreviewLocations(ITxRoboticOperation op)
        {
            if (_grid == null) return;
            try
            {
                var locs = EnumerateLocations(op as ITxObject);
                _grid.Rows.Count = _grid.Rows.Fixed + locs.Count;
                _rowToResult.Clear();

                // 获取关联机器人名
                string robotName = "";
                try { dynamic dop = op; robotName = (dop.Robot as TxRobot)?.Name ?? ""; } catch { }

                for (int i = 0; i < locs.Count; i++)
                {
                    int row = i + _grid.Rows.Fixed;
                    var loc = locs[i];
                    string ptType = loc.GetType().Name.Contains("Weld") ? "Weld" : "Via";

                    _grid[row, COL_IDX] = (i + 1).ToString();
                    _grid[row, COL_BRAND] = "";
                    _grid[row, COL_ROBOT] = robotName;
                    _grid[row, COL_OP] = op.Name;
                    _grid[row, COL_PT] = loc.Name ?? $"P{i + 1}";
                    _grid[row, COL_TYPE] = ptType;
                    _grid[row, COL_RESULT] = "未检查";
                }
                SetStatus($"路径 [{op.Name}]，{locs.Count} 个点位");
            }
            catch { }
        }

        private void BtnCheck_Click(object sender, EventArgs e)
        {
            if (_tsOp.SelectedItem == null)
            { SetStatus("请先选择操作路径", ClrWarning); return; }
            StartCheck(_tsOp.SelectedItem.ToString());
        }

        private void BtnCheckAll_Click(object sender, EventArgs e)
        {
            var ops = _tsOp.Items.Cast<string>().ToList();
            if (ops.Count == 0) { SetStatus("操作列表为空", ClrWarning); return; }
            // 逐个检查所有路径
            StartCheckAll(ops);
        }

        private void StartCheckAll(List<string> opNames)
        {
            // 顺序检查所有路径（简单起见，只触发第一个，完成后级联）
            if (opNames.Count == 0) return;
            StartCheck(opNames[0], opNames.Count > 1 ? opNames.Skip(1).ToList() : null);
        }

        private void StartCheck(string opName, List<string> remaining = null)
        {
            _tsBtnRefresh.Enabled = false;
            _tsProgress.Visible = true;
            _tsProgress.ProgressBar.Value = 0;
            SetStatus($"正在检查 [{opName}]...", ClrAccent);
            Log($"========================================");
            Log($"开始检查任务: 路径={opName}");

            _checkProgress = 0;
            int total = 15;
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc != null)
                {
                    var op = FindOperationByName(doc, opName);
                    if (op is ITxCompoundOperation c)
                    {
                        var locs = c.GetAllDescendants(
                            new TxTypeFilter(typeof(ITxRoboticLocationOperation)));
                        if (locs != null && locs.Count > 0) total = locs.Count;
                    }
                }
            }
            catch { }

            _checkTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _checkTimer.Tick += (s, ev) =>
            {
                _checkProgress++;
                _tsProgress.ProgressBar.Value = Math.Min((int)((double)_checkProgress / total * 100), 100);
                SetStatus($"检查中... {_checkProgress}/{total}");
                if (_checkProgress >= total)
                {
                    _checkTimer.Stop();
                    var results = CheckReachabilityViaPS(opName);
                    string robotName = results.Count > 0 ? results[0].RobotName : "未知";
                    var task = new RobotPathCheckTask
                    {
                        RobotName = robotName,
                        PathName = opName,
                        CheckTime = DateTime.Now,
                        Results = results
                    };
                    _tasks.Add(task);
                    _currentTask = task;
                    RefreshGrid(results);
                    UpdateSummaryCards(task);
                    if (remaining != null && remaining.Count > 0)
                    {
                        // 继续检查下一条路径
                        StartCheck(remaining[0], remaining.Count > 1 ? remaining.Skip(1).ToList() : null);
                    }
                    else
                    {
                        _tsProgress.Visible = false;
                        _tsBtnRefresh.Enabled = true;
                        SetStatus(
                            $"✓ 完成 | {task.TotalPoints} 点 | " +
                            $"可达 {task.ReachableCount} | 不可达 {task.UnreachableCount} | " +
                            $"近极限 {task.NearLimitCount} | 可达率 {task.ReachabilityRate:F1}%",
                            ClrSuccess);
                    }
                }
            };
            _checkTimer.Start();
        }

        private void RefreshGrid(List<PathPointResult> results)
        {
            if (_grid == null) return;
            var filtered = ApplyFilter(results);

            _grid.Rows.Count = _grid.Rows.Fixed + filtered.Count;
            _rowToResult.Clear();

            // 识别机器人品牌（从机器人名称前缀推断）
            static string GuessBrand(string robotName)
            {
                string n = robotName?.ToUpper() ?? "";
                if (n.Contains("KR") || n.Contains("KUKA")) return "KUKA";
                if (n.Contains("IRB") || n.Contains("ABB")) return "ABB";
                if (n.Contains("FANUC") || n.Contains("R200")) return "FANUC";
                if (n.Contains("YASKAWA") || n.Contains("MH")) return "YASKAWA";
                if (n.Contains("BA") || n.Contains("OTC")) return "OTC";
                return "—";
            }

            for (int i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];
                int row = i + _grid.Rows.Fixed;
                _rowToResult[row] = r;

                string jStr(double v) => r.Status == ReachabilityStatus.NotChecked ? "" : v.ToString("F1");
                string statusText = r.Status == ReachabilityStatus.Reachable ? "正常"
                                  : r.Status == ReachabilityStatus.Unreachable ? "不可达"
                                  : r.Status == ReachabilityStatus.NearLimit ? "接近极限"
                                  : "未检查";

                _grid[row, COL_IDX] = r.Index.ToString();
                _grid[row, COL_BRAND] = GuessBrand(r.RobotName);
                _grid[row, COL_ROBOT] = r.RobotName;
                _grid[row, COL_OP] = r.OperationName;
                _grid[row, COL_PT] = r.PointName;
                _grid[row, COL_TYPE] = r.PointType;
                _grid[row, COL_J1] = jStr(r.J1);
                _grid[row, COL_J2] = jStr(r.J2);
                _grid[row, COL_J3] = jStr(r.J3);
                _grid[row, COL_J4] = jStr(r.J4);
                _grid[row, COL_J5] = jStr(r.J5);
                _grid[row, COL_J6] = jStr(r.J6);
                _grid[row, COL_RESULT] = statusText;
                _grid[row, COL_NOTE] = r.ErrorMessage;

                // 行背景色：检查结果颜色方案（参考图片 Excel 条件格式）
                var rowStyle = _grid.Rows[row].Style ?? _grid.Styles.Add($"rs{row}");
                Color bg = r.Status == ReachabilityStatus.Reachable
                               ? Color.FromArgb(198, 239, 206)   // 绿
                         : r.Status == ReachabilityStatus.Unreachable
                               ? Color.FromArgb(255, 199, 206)   // 红
                         : r.Status == ReachabilityStatus.NearLimit
                               ? Color.FromArgb(255, 235, 156)   // 黄
                         : (i % 2 == 0 ? SystemColors.Window : Color.FromArgb(242, 244, 248));
                rowStyle.BackColor = bg;
                _grid.Rows[row].Style = rowStyle;
            }

            // 数据填充后重新自适应列宽
            ResizeGridCols();
        }

        private List<PathPointResult> ApplyFilter(List<PathPointResult> src)
        {
            var q = src.AsEnumerable();

            // 隐藏正常结果项（Tab1）
            if (_chkHideNormal != null && _chkHideNormal.Checked)
                q = q.Where(r => r.Status != ReachabilityStatus.Reachable);

            // 点类型筛选（Tab1）
            if (_cbPointTypeFilter != null)
            {
                int idx = _cbPointTypeFilter.SelectedIndex;
                if (idx == 1) q = q.Where(r => r.PointType == "Weld");
                else if (idx == 2) q = q.Where(r => r.PointType == "Via");
            }

            return q.OrderBy(r => r.Index).ToList();
        }

        private void ApplyFilterNow()
        {
            if (_currentTask != null) RefreshGrid(_currentTask.Results);
        }

        private void UpdateSummaryCards(RobotPathCheckTask t)
        {
            // 结果汇总显示在 StatusStrip
            SetStatus($"共 {t.TotalPoints} 点  可达 {t.ReachableCount}  " +
                      $"不可达 {t.UnreachableCount}  接近极限 {t.NearLimitCount}  " +
                      $"可达率 {t.ReachabilityRate:F1}%");
        }

        private void ClearResults()
        {
            if (_grid != null)
            {
                _grid.Rows.Count = _grid.Rows.Fixed;
                _rowToResult.Clear();
            }
            _tasks.Clear(); _currentTask = null;
            SetStatus("就绪");
        }

        // BtnLocate_Click 已由 Grid_AfterSelChange（单击）替代
        private void BtnLocate_Click(object sender, EventArgs e)
        {
            Grid_AfterSelChange(sender, null);
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            if (_currentTask == null || _currentTask.Results.Count == 0)
            { SetStatus("没有可导出的数据", ClrWarning); return; }

            using (var dlg = new SaveFileDialog
            {
                Filter = "CSV文件|*.csv",
                FileName = $"Reachability_{_currentTask.PathName}_{DateTime.Now:yyyyMMdd_HHmm}",
                Title = "导出可达性报告"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var lines = new List<string>
                {
                    $"# 机器人路径可达性检查报告",
                    $"# 机器人: {_currentTask.RobotName}  路径: {_currentTask.PathName}  时间: {_currentTask.CheckTime:yyyy-MM-dd HH:mm:ss}",
                    $"# 总计:{_currentTask.TotalPoints}  可达:{_currentTask.ReachableCount}  不可达:{_currentTask.UnreachableCount}  近极限:{_currentTask.NearLimitCount}  可达率:{_currentTask.ReachabilityRate:F1}%",
                    "",
                    "序号,机器人名,操作名称,点名,点类型,状态,J1(°),J2(°),J3(°),J4(°),J5(°),J6(°),最小余量(°),备注"
                };
                foreach (var r in _currentTask.Results)
                {
                    string st = r.Status == ReachabilityStatus.Reachable ? "可达"
                              : r.Status == ReachabilityStatus.Unreachable ? "不可达"
                              : r.Status == ReachabilityStatus.NearLimit ? "接近极限" : "未检查";
                    lines.Add(string.Join(",",
                        r.Index, r.RobotName, r.OperationName, r.PointName,
                        r.PointType, st,
                        r.J1.ToString("F1"), r.J2.ToString("F1"), r.J3.ToString("F1"),
                        r.J4.ToString("F1"), r.J5.ToString("F1"), r.J6.ToString("F1"),
                        r.JointMargin.ToString("F1"), $"\"{r.ErrorMessage}\""));
                }
                System.IO.File.WriteAllLines(dlg.FileName, lines, System.Text.Encoding.UTF8);
                SetStatus($"✓ 已导出: {System.IO.Path.GetFileName(dlg.FileName)}", ClrSuccess);
            }
        }



        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _checkTimer?.Stop();
            _selTimer?.Stop();
            // 释放 TxObjEditBoxCtrl 资源
            try
            {
                if (_txtOpNode != null)
                {
                    _txtOpNode.Picked -= OnOpNodePicked;
                    _txtOpNode.ListenToPick = false;
                    _txtOpNode.UnInitialize();
                }
            }
            catch { }
            base.OnFormClosing(e);
        }

    }  // class ReachabilityCheckerForm

    // =========================================================================
    // 点位编辑对话框 — 基于 TxPlacementCollisionControl API 文档实现
    //
    // 核心属性（来自文档）：
    //   LinearValue    — 当前平移值（TxVector 或 double[]，单位 mm）
    //   AngularValue   — 当前旋转值（TxVector 或 double[]，单位 deg）
    //   LinearStepSize  — 每次移动步长（mm）
    //   AngularStepSize — 每次旋转步长（deg）
    //   SelectedAxis    — 当前激活轴（X/Y/Z/RX/RY/RZ）
    //   ShowCollisionButtons — 显示/隐藏碰撞检测按钮
    //
    // 核心方法：
    //   Manipulate(delta)     — 执行一次位移/旋转，delta 为增量值
    //   MoveOneStepLinear()   — 按 LinearStepSize 移动一步
    //   MoveOneStepAngular()  — 按 AngularStepSize 旋转一步
    //   Reset()               — 重置控件显示值
    //   UnInitialize()        — 释放内部资源（关闭前调用）
    //
    // 核心事件：
    //   DeltaChanged          — 值发生变化时触发（用于实时写回点位）
    //   MovementTypeChanged   — 激活轴切换时触发
    //   RunToCollision        — 碰撞按钮按下时触发
    // =========================================================================
    internal class LocationEditForm : Form
    {
        private readonly ITxRoboticLocationOperation _locOp;
        private readonly string _pointName;

        // ElementHost: WinForms → WPF 桥接容器（需引用 WindowsFormsIntegration.dll）
        // 命名空间: System.Windows.Forms.Integration
        // 若项目未引用该程序集，将此字段类型改为 object，并通过反射创建
        private System.Windows.Forms.Integration.ElementHost _host;
        private TxPlacementCollisionControl _ctrl;

        private Label _lblInfo;
        private Label _lblStatus;
        private Button _btnReset;
        private Button _btnClose;

        public LocationEditForm(ITxRoboticLocationOperation locOp, string pointName)
        {
            _locOp = locOp;
            _pointName = pointName;
            Text = "编辑点位 — " + pointName;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(480, 400);
            MinimumSize = new Size(420, 340);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            BackColor = SystemColors.Control;
            Build();
        }

        private void Build()
        {
            // 顶部标题栏
            _lblInfo = new Label
            {
                Text = "点位: " + _pointName,
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(8, 5, 0, 0),
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 70, 127),
                BackColor = Color.FromArgb(235, 241, 250)
            };

            // 底部状态 + 按钮栏
            var btmPanel = new Panel { Dock = DockStyle.Bottom, Height = 36 };

            _lblStatus = new Label
            {
                Text = "使用控件箭头调整点位位置和姿态",
                AutoSize = false,
                Width = 260,
                Height = 22,
                Location = new Point(6, 7),
                ForeColor = SystemColors.GrayText,
                Font = SystemFonts.DefaultFont
            };

            _btnReset = new Button
            {
                Text = "还原",
                Width = 72,
                Height = 26,
                FlatStyle = FlatStyle.System,
                Font = SystemFonts.DefaultFont,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            _btnReset.Click += BtnReset_Click;

            _btnClose = new Button
            {
                Text = "关闭",
                Width = 72,
                Height = 26,
                FlatStyle = FlatStyle.System,
                Font = SystemFonts.DefaultFont,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            _btnClose.Click += (s, e) => Close();

            btmPanel.Controls.AddRange(new Control[] { _lblStatus, _btnReset, _btnClose });
            btmPanel.Resize += (s, e) =>
            {
                _btnClose.Location = new Point(btmPanel.Width - 78, 5);
                _btnReset.Location = new Point(btmPanel.Width - 156, 5);
            };
            _btnClose.Location = new Point(392, 5);
            _btnReset.Location = new Point(314, 5);

            // ElementHost: 承载 TxPlacementCollisionControl
            _host = new System.Windows.Forms.Integration.ElementHost
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Control,
                BackColorTransparent = false
            };

            // TxPlacementCollisionControl 实例化并配置
            _ctrl = new TxPlacementCollisionControl();
            _ctrl.InitializeComponent();

            // API: ShowCollisionButtons — 显示碰撞检测按钮
            _ctrl.ShowCollisionButtons = true;

            // API: LinearStepSize / AngularStepSize — 步长
            _ctrl.LinearStepSize = 1.0;  // mm
            _ctrl.AngularStepSize = 1.0;  // deg

            // API: DeltaChanged — 值变化时触发（double delta = 当前轴的增量）
            _ctrl.DeltaChanged += Ctrl_DeltaChanged;

            // API: MovementTypeChanged — 激活轴切换时触发
            _ctrl.MovementTypeChanged += Ctrl_MovementTypeChanged;

            // API: RunToCollision — 碰撞按钮按下时触发
            _ctrl.RunToCollision += Ctrl_RunToCollision;

            _host.Child = _ctrl;

            Controls.Add(_host);
            Controls.Add(btmPanel);
            Controls.Add(_lblInfo);
        }

        // DeltaChanged: sender 是控件本身，EventArgs 包含变化信息
        // LinearValue / AngularValue 是 double（当前激活轴的标量值）
        private void Ctrl_DeltaChanged(object sender, EventArgs e)
        {
            if (_ctrl == null || _lblStatus == null) return;
            try
            {
                // API: LinearValue (double) — 当前激活平移轴的值（mm）
                // API: AngularValue (double) — 当前激活旋转轴的值（deg）
                double linVal = _ctrl.LinearValue;
                double angVal = _ctrl.AngularValue;

                // API: SelectedAxis — 当前激活轴
                string axis = "";
                try { axis = _ctrl.SelectedAxis.ToString(); } catch { }

                _lblStatus.Text = string.IsNullOrEmpty(axis)
                    ? string.Format("平移={0:F2}mm  旋转={1:F2}°", linVal, angVal)
                    : string.Format("[{0}] 平移={1:F2}mm  旋转={2:F2}°", axis, linVal, angVal);

                // 通过 Manipulate 将增量应用到 PS 场景中的点位
                // API: Manipulate(double delta) — 对当前激活轴施加增量
                // 注意：控件内部会自动调用 Manipulate 驱动 PS 机器人/点位
                // DeltaChanged 在 Manipulate 执行后触发，此处无需再调用
            }
            catch { }
        }

        // MovementTypeChanged: 轴切换（X/Y/Z/RX/RY/RZ）
        private void Ctrl_MovementTypeChanged(object sender, EventArgs e)
        {
            if (_ctrl == null || _lblInfo == null) return;
            try
            {
                string axis = _ctrl.SelectedAxis.ToString();
                _lblInfo.Text = "点位: " + _pointName
                    + (string.IsNullOrEmpty(axis) ? "" : "  [" + axis + "]");
            }
            catch { }
        }

        // RunToCollision: 碰撞按钮按下
        private void Ctrl_RunToCollision(object sender, EventArgs e)
        {
            if (_lblStatus != null) _lblStatus.Text = "⚠ 碰撞检测触发";
        }

        // 还原按钮: API Reset() — 重置控件显示的当前值
        private void BtnReset_Click(object sender, EventArgs e)
        {
            if (_ctrl == null) return;
            try
            {
                _ctrl.Reset();
                if (_lblStatus != null) _lblStatus.Text = "已重置";
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (_ctrl != null)
                {
                    // API: UnInitialize() — 释放控件内部资源
                    _ctrl.UnInitialize();
                    _ctrl.DeltaChanged -= Ctrl_DeltaChanged;
                    _ctrl.MovementTypeChanged -= Ctrl_MovementTypeChanged;
                    _ctrl.RunToCollision -= Ctrl_RunToCollision;
                }
                if (_host != null) { _host.Child = null; _host.Dispose(); }
            }
            catch { }
            base.OnFormClosing(e);
        }
    }

}  // namespace TxTools.RobotReachabilityChecker