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
        public override string Name { get { return "_可达性检查"; } }
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
    // 状态严重性顺序（从重到轻）：
    // Unreachable > Singular > NearLimit > Critical > Reachable > NotChecked
    public enum ReachabilityStatus
    {
        Reachable,    // 正常
        Critical,     // 临界点（低风险信息）— 1/3/5 轴落入品牌特定的 config 翻转临界带
        NearLimit,    // 接近软限位
        Singular,     // 奇异点（J5 ∈ ±10°，腕部奇异）
        Unreachable,  // 不可达 / 超限
        NotChecked
    }

    // 轴级问题标志（与 ReachabilityStatus 相互独立，每个轴单独标记）
    [Flags]
    public enum AxisFlag
    {
        None = 0,
        Critical = 1 << 0,    // 该轴落在临界带
        NearLimit = 1 << 1,   // 该轴距软限位 ≤ 阈值
        Singular = 1 << 2,    // 仅 J5 可能拥有此标志（奇异）
        OverLimit = 1 << 3,   // 该轴超出软限位
    }

    public enum RobotBrand { Auto, KUKA, ABB, FANUC, Other }

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

        // 每个轴的问题标志（J1..J6 对应索引 0..5）
        public AxisFlag[] AxisFlags { get; set; } = new AxisFlag[6];
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
        public int SingularCount => Results.Count(r => r.Status == ReachabilityStatus.Singular);
        public int CriticalCount => Results.Count(r => r.Status == ReachabilityStatus.Critical);
        public double ReachabilityRate =>
            TotalPoints > 0
                ? (double)(ReachableCount + NearLimitCount + Critical_AsReachableCount) / TotalPoints * 100
                : 0;
        // 临界点视为可达（仅信息提示），用于可达率计算
        private int Critical_AsReachableCount => CriticalCount;
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
        private ComboBox _cbBrand;          // 机器人品牌（影响临界点判定规则）
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

        // 用户通过 OP 节点拾取器选中的具体对象实例（而非按名字查找的副本）
        // 解决场景：场景中存在同名 Operation 时，按名字 FindOperationByName 可能返回错的实例，
        //           导致 .Robot 关联到错的机器人副本，IK 整体失败。
        // 用户每次拾取都更新此字段，BtnCheck_Click 优先用它而不是按名字重查。
        private ITxObject _pickedOperation;
        // 缓存每行数据索引（TxFlexGrid 行 → PathPointResult），用于单击跳转
        private readonly Dictionary<int, PathPointResult> _rowToResult = new Dictionary<int, PathPointResult>();

        // PS 标准配色 — 基于 TxColor，使用 .Color 转换为 System.Drawing.Color
        // 主色调
        private static readonly TxColor TxClrAccent = new TxColor(0, 70, 127);
        private static readonly TxColor TxClrSuccess = new TxColor(0, 128, 0);
        private static readonly TxColor TxClrDanger = new TxColor(192, 0, 0);
        private static readonly TxColor TxClrWarning = new TxColor(160, 100, 0);
        // 功能区按钮色
        private static readonly TxColor TxClrBtnCheck = new TxColor(0, 100, 167);
        private static readonly TxColor TxClrBtnAll = new TxColor(0, 120, 90);
        private static readonly TxColor TxClrBtnExport = new TxColor(80, 80, 130);
        private static readonly TxColor TxClrBtnReset = new TxColor(130, 100, 40);
        private static readonly TxColor TxClrBtnClose = new TxColor(130, 50, 50);
        // 表格色
        private static readonly TxColor TxClrGridHeader = new TxColor(218, 227, 243);
        private static readonly TxColor TxClrGridHeaderText = new TxColor(20, 20, 60);
        private static readonly TxColor TxClrGridAlt = new TxColor(242, 244, 248);
        private static readonly TxColor TxClrGridHighlight = new TxColor(189, 215, 238);
        private static readonly TxColor TxClrRowOk = new TxColor(198, 239, 206);
        private static readonly TxColor TxClrRowFail = new TxColor(255, 199, 206);
        private static readonly TxColor TxClrRowWarn = new TxColor(255, 235, 156);
        // 新增：奇异/临界 行底色
        private static readonly TxColor TxClrRowSingular = new TxColor(248, 187, 208);  // 浅紫红
        private static readonly TxColor TxClrRowCritical = new TxColor(207, 216, 220);  // 浅蓝灰
        // 新增：单元格级（轴级）问题高亮色
        private static readonly TxColor TxClrCellOver = new TxColor(198, 40, 40);       // 深红 — 轴超限（白字）
        private static readonly TxColor TxClrCellNear = new TxColor(255, 179, 0);       // 橙黄 — 轴近极限
        private static readonly TxColor TxClrCellSingular = new TxColor(233, 30, 99);   // 紫红 — J5奇异（白字）
        private static readonly TxColor TxClrCellCritical = new TxColor(144, 164, 174); // 蓝灰 — 临界
        // 日志面板
        private static readonly TxColor TxClrLogBg = new TxColor(30, 30, 30);
        private static readonly TxColor TxClrLogText = new TxColor(204, 204, 204);
        private static readonly TxColor TxClrLogErr = new TxColor(255, 100, 100);
        private static readonly TxColor TxClrLogWarn = new TxColor(255, 200, 80);
        private static readonly TxColor TxClrLogOk = new TxColor(80, 220, 120);
        // 点位编辑头栏
        private static readonly TxColor TxClrEditHeader = new TxColor(235, 241, 250);

        // WinForms 快捷引用（.Color 转换）
        private static readonly System.Drawing.Color ClrAccent = TxClrAccent.Color;
        private static readonly System.Drawing.Color ClrSuccess = TxClrSuccess.Color;
        private static readonly System.Drawing.Color ClrDanger = TxClrDanger.Color;
        private static readonly System.Drawing.Color ClrWarning = TxClrWarning.Color;
        private static readonly System.Drawing.Color ClrMuted = SystemColors.GrayText;
        private static readonly System.Drawing.Color ClrText = SystemColors.WindowText;
        private static readonly System.Drawing.Color ClrBg = SystemColors.Control;

        // ── 构造 ──────────────────────────────────────────────────────────────
        public ReachabilityCheckerForm()
        {
            Text = "机器人路径点位检查";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new System.Drawing.Size(1280, 780);
            MinimumSize = new System.Drawing.Size(960, 580);
            InitializeComponent();
            LoadRobotsAndOperations();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _selTimer = new System.Windows.Forms.Timer { Interval = 1500 };
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
                Font = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Regular)
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
                BackColor = System.Drawing.Color.Transparent,
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
                Font = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Bold),
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
                BackColor = System.Drawing.Color.Transparent,
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
                BackColor = System.Drawing.Color.Transparent,
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
                BackColor = System.Drawing.Color.Transparent,
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

            // 行3：机器人品牌（影响临界点判定规则）
            var row3 = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = System.Drawing.Color.Transparent,
                Margin = new Padding(0, 0, 0, 2)
            };
            row3.Controls.Add(MkLabel("品牌"));
            _cbBrand = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = SystemFonts.DefaultFont,
                Margin = new Padding(2, 3, 0, 0)
            };
            _cbBrand.Items.AddRange(new object[] { "自动", "KUKA", "ABB", "FANUC", "其他" });
            _cbBrand.SelectedIndex = 0;
            AutoFitComboBoxWidth(_cbBrand);
            row3.Controls.Add(_cbBrand);
            flow.Controls.Add(row3);

            // 行4：隐藏正常结果项
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
                BackColor = System.Drawing.Color.Transparent,
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
                BackColor = System.Drawing.Color.Transparent,
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
                BackColor = System.Drawing.Color.Transparent,
                Margin = new Padding(0, 0, 0, 2)
            };
            var btnCheck = MkFuncButton("开始检查", TxClrBtnCheck.Color);
            btnCheck.Click += BtnCheck_Click;
            var btnAll = MkFuncButton("检查所有路径", TxClrBtnAll.Color);
            btnAll.Click += BtnCheckAll_Click;
            row1.Controls.AddRange(new Control[] { btnCheck, btnAll });

            // 第二行：3个按钮（结果导出表格 + 重置窗口 + 关闭）
            var row2 = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = System.Drawing.Color.Transparent,
                Margin = new Padding(0, 0, 0, 0)
            };
            var btnExport = MkFuncButton("结果导出", TxClrBtnExport.Color);
            btnExport.Click += BtnExport_Click;
            var btnReset = MkFuncButton("重置窗口", TxClrBtnReset.Color);
            btnReset.Click += (s, e) => ClearResults();
            var btnClose = MkFuncButton("关闭", TxClrBtnClose.Color);
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
        private Button MkFuncButton(string text, System.Drawing.Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = SystemFonts.DefaultFont,
                ForeColor = TxColor.TxColorWhite.Color,
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
            var hdr = new Panel { Dock = DockStyle.Top, Height = 20, BackColor = ClrAccent };
            var lblHdr = new Label
            {
                Text = "运行日志",
                Dock = DockStyle.Fill,
                ForeColor = TxColor.TxColorWhite.Color,
                Font = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };
            var btnClear = new Button
            {
                Text = "清空",
                Dock = DockStyle.Right,
                Width = 44,
                FlatStyle = FlatStyle.Flat,
                BackColor = ClrAccent,
                ForeColor = TxColor.TxColorWhite.Color,
                Font = SystemFonts.DefaultFont
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += (s, e) => _logBox?.Clear();
            hdr.Controls.AddRange(new Control[] { btnClear, lblHdr });

            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = TxClrLogBg.Color,
                ForeColor = TxClrLogText.Color,
                Font = new System.Drawing.Font("Consolas", 8f),
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
                var hdrFont = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Bold);
                for (int i = 0; i < 14; i++)
                {
                    int textW = (int)g.MeasureString(captions[i], hdrFont).Width + 16; // 加上边距
                    if (i == COL_OP) _grid.Cols[i].Width = 140;   // 操作名固定
                    else if (i == COL_PT) _grid.Cols[i].Width = 140;   // 点名固定
                    else if (i == COL_NOTE) _grid.Cols[i].Width = 120;   // 备注列在 Resize 中填充剩余
                    else _grid.Cols[i].Width = Math.Max(textW, 42); // 自适应：至少能放下表头
                }
            }

            // 表头样式
            var hdrStyle = _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Fixed];
            hdrStyle.BackColor = TxClrGridHeader.Color;
            hdrStyle.ForeColor = TxClrGridHeaderText.Color;
            hdrStyle.Font = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Bold);

            // 行色
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Normal].BackColor = SystemColors.Window;
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Alternate].BackColor = TxClrGridAlt.Color;
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Highlight].BackColor = TxClrGridHighlight.Color;
            _grid.Styles[C1.Win.C1FlexGrid.CellStyleEnum.Highlight].ForeColor = SystemColors.WindowText;

            // 单击行 → 跳转机器人到该点位姿态
            _grid.AfterSelChange += Grid_AfterSelChange;

            // 双击行 → 弹出 PS 内置 Robot Jog 对话框
            // 用 MouseDoubleClick 而不是 DoubleClick：C1FlexGrid 在某些 SelectionMode
            // 下 DoubleClick 不会冒泡，MouseDoubleClick 是 WinForms 标准事件，更可靠
            _grid.MouseDoubleClick += Grid_DblClick_Mouse;

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
                            var hdrFont = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Bold);
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

        private void SetStatus(string text, System.Drawing.Color _ignored) => SetStatus(text);

        // 详细日志开关：true 时打印每个点位的方式A/B/C成功细节、限位获取细节等
        // 排查问题时设为 true，正常使用时保持 false 以减少日志噪音
        private bool _logVerbose = false;

        private void Log(string message, string level = "INFO")
        {
            if (_logBox == null || _logBox.IsDisposed) return;

            // DEBUG 级别在非 verbose 模式下静默丢弃（不写日志框）
            if (level == "DEBUG" && !_logVerbose) return;

            if (_logBox.InvokeRequired) { _logBox.BeginInvoke(new Action(() => Log(message, level))); return; }

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            System.Drawing.Color col = level == "ERR" ? TxClrLogErr.Color
                      : level == "WARN" ? TxClrLogWarn.Color
                      : level == "OK" ? TxClrLogOk.Color
                      : TxClrLogText.Color;

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

        // 单击行：将机器人各关节直接驱动到该点位记录的关节值
        //
        // 设计取舍（重要）：
        //   旧实现把点位对象塞进 TxApplication.ActiveSelection，本意是"在 PS 场景里高亮"，
        //   但 ITxRoboticLocationOperation 实际上也是 ITxRoboticOperation 的实现，
        //   会被 OnSelectionTick 600ms 轮询误判为"用户切换了 OP 节点"，
        //   触发 PreviewLocations(单点op) 把表格刷成只剩 1 行。
        //
        //   新实现完全不动 ActiveSelection，改为：
        //     1) 找到该行对应的 TxRobot（用 RobotName 在文档中查）
        //     2) 直接对 robot.Joints 中每个 TxJoint 的 CurrentValue 赋值
        //
        //   这与 CheckReachabilityViaPS 里 "robot.CurrentPose = pd; ReadDrivingJoints(...)"
        //   的整体范式一致，只是单击场景下不需要 IK，直接用记录值即可。
        private void Grid_AfterSelChange(object sender, C1.Win.C1FlexGrid.RangeEventArgs e)
        {
            int row = _grid.RowSel;
            if (row < _grid.Rows.Fixed) return;
            if (!_rowToResult.TryGetValue(row, out var res)) return;

            SetStatus($"[{res.PointName}]  {res.RobotName}  J1={res.J1:F1}° J2={res.J2:F1}° J3={res.J3:F1}°");

            // 未检查/不可达 状态下没有有效关节值，跳过驱动
            if (res.Status == ReachabilityStatus.Unreachable
                || (res.J1 == 0 && res.J2 == 0 && res.J3 == 0
                    && res.J4 == 0 && res.J5 == 0 && res.J6 == 0
                    && res.JointMargin >= 999))
            {
                return; // 还没检查或不可达，不去驱动机器人
            }

            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return;

                // 按 RobotName 找到 TxRobot 实例
                TxRobot robot = FindRobotByName(doc, res.RobotName);
                if (robot == null) return;

                // 取关节集合并按顺序赋值
                // robot.Joints 返回 TxObjectList<TxJoint>（强类型），用 var 自动推断
                var joints = robot.Joints;
                if (joints == null || joints.Count == 0) return;

                double[] target = { res.J1, res.J2, res.J3, res.J4, res.J5, res.J6 };
                int n = Math.Min(joints.Count, target.Length);

                // 单位策略（与 CheckReachabilityViaPS 中存储 J1..J6 的逻辑保持对称）：
                //   存储时：用 maxAbs<=2π+ε 判定整体是否弧度，若是则乘 180/π 转度
                //   读回时：对每个关节单独探测当前值范围，若 |当前值|<=2π+ε 则
                //           认为该关节是旋转关节（PS 内部用弧度），写入时把度数转回弧度
                //           否则按原值（mm 或度数）写入，避免误算平动关节
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        // joints[i] 已经是强类型 TxJoint
                        TxJoint joint = joints[i];
                        if (joint == null) continue;

                        double valDeg = target[i];

                        // 探测当前值，判断该关节是否使用弧度
                        double curVal = 0;
                        bool sawCur = false;
                        try { curVal = joint.CurrentValue; sawCur = true; } catch { }

                        double valToWrite;
                        if (sawCur && Math.Abs(curVal) <= 2 * Math.PI + 0.05)
                        {
                            // PS 内部用弧度 → 度数转弧度
                            valToWrite = valDeg * Math.PI / 180.0;
                        }
                        else
                        {
                            // 平动关节或单位是度，按原值写
                            valToWrite = valDeg;
                        }

                        joint.CurrentValue = valToWrite;
                    }
                    catch { /* 单关节失败不影响其他 */ }
                }
            }
            catch { }
        }

        // 按名称在当前文档中查找 TxRobot 实例
        private TxRobot FindRobotByName(TxDocument doc, string robotName)
        {
            if (doc == null || string.IsNullOrEmpty(robotName)) return null;
            try
            {
                var all = doc.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(TxRobot)));
                foreach (ITxObject o in all)
                {
                    if (o is TxRobot r && r.Name == robotName) return r;
                }
            }
            catch { }
            return null;
        }

        // =====================================================================
        // 双击网格行：选中 PS 中对应点位 + 弹出 PS 内置 Robot Jog 对话框
        //
        // 流程：
        //   1) 从 _rowToResult 拿到当前双击行的 PathPointResult
        //   2) 用 OperationName + PointName 在 PS 中定位到 ITxRoboticLocationOperation
        //   3) 把它设为 PS 的当前选中对象（ActiveSelection）
        //   4) 通过 TxCommandsManager.ExecuteCommand 触发 Robot Jog 命令
        //
        // 关于命令标识符：
        //   PS 的 Robot Jog 命令真实 ID 文档未公开，候选列表为运行时探测，
        //   首次运行时看日志确认哪个 ID 没抛 TxCommandIdentifierDoesNotExistException
        // =====================================================================

        // MouseDoubleClick 入口：用 HitTest 找到双击的行，再委托给主处理
        private void Grid_DblClick_Mouse(object sender, MouseEventArgs e)
        {
            try
            {
                Log($">>> 鼠标双击事件触发 (X={e.X}, Y={e.Y}, Button={e.Button})", "DEBUG");
                if (e.Button != MouseButtons.Left) return;

                // 用 HitTest 把鼠标坐标转成行号
                var ht = _grid.HitTest(e.X, e.Y);
                int row = ht.Row;
                Log($"    HitTest 命中行: {row}, Type={ht.Type}", "DEBUG");

                // 选中该行（保险起见，确保 RowSel 正确）
                if (row >= _grid.Rows.Fixed)
                {
                    _grid.RowSel = row;
                }

                Grid_DblClick_Core(row);
            }
            catch (Exception ex)
            {
                Log($"MouseDoubleClick 包装层异常: {ex.Message}", "ERR");
            }
        }

        private void Grid_DblClick(object sender, EventArgs e)
        {
            // 兼容老的 DoubleClick 事件路径（如果某些场景下 MouseDoubleClick 没触发）
            Log(">>> DoubleClick 事件触发（非 Mouse 版）");
            Grid_DblClick_Core(_grid.RowSel);
        }

        private void Grid_DblClick_Core(int row)
        {
            try
            {
                // ── 1) 取当前选中行（C1FlexGrid 用 RowSel）────────────
                if (row < _grid.Rows.Fixed)
                {
                    Log($"双击在表头/无效行 (row={row})，忽略", "WARN");
                    return;
                }

                if (!_rowToResult.TryGetValue(row, out var res))
                {
                    Log($"行 {row} 未在 _rowToResult 字典中找到对应数据 (字典大小={_rowToResult.Count})", "WARN");
                    return;
                }

                string pointName = res.PointName;
                string opName = res.OperationName;
                if (string.IsNullOrEmpty(pointName))
                {
                    Log("当前行没有点位名，无法定位", "WARN");
                    return;
                }
                Log($"双击点位: [{opName}] / [{pointName}]", "DEBUG");

                // ── 2) 在 PS 文档中找到该点位对象 ─────────────────────
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc == null)
                {
                    Log("ActiveDocument 为 null", "ERR");
                    return;
                }

                ITxObject locObj = FindLocationInDoc(doc, opName, pointName);
                if (locObj == null)
                {
                    Log($"未在 PS 文档中找到点位: {pointName}", "ERR");
                    MessageBox.Show($"未在当前 Study 中找到点位 [{pointName}]，\n" +
                                    "可能该点位已被删除或操作已变更。",
                                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // ── 3) 设置 PS 的当前选中对象（Robot Jog 会读取它）────
                // TxSelection 的真实 API：Clear() / AddItems(TxObjectList) / SetItems(...)
                // 注意：是 AddItems（复数），不是 Add
                try
                {
                    TxSelection sel = TxApplication.ActiveSelection;
                    sel.Clear();

                    TxObjectList list = new TxObjectList();
                    list.Add(locObj);
                    sel.AddItems(list);

                    Log($"已设置 ActiveSelection: {locObj.Name}", "DEBUG");
                }
                catch (Exception exSel)
                {
                    Log($"设置 ActiveSelection 失败: {exSel.Message}", "ERR");
                    return;
                }

                // ── 4) 触发 Robot Jog 对话框 ──────────────────────────
                OpenRobotJogDialog();
            }
            catch (Exception ex)
            {
                Log($"双击处理异常: {ex.Message}", "ERR");
            }
        }

        // ---------------------------------------------------------------------
        // 在 PS 文档中按"操作名 + 点位名"定位到具体的 Location 对象
        // 复用现有 EnumerateLocations 的能力，先定位操作再枚举其下点位
        // ---------------------------------------------------------------------
        private ITxObject FindLocationInDoc(TxDocument doc, string opName, string pointName)
        {
            if (doc == null || string.IsNullOrEmpty(pointName)) return null;

            try
            {
                // 优先策略：先用操作名定位 Operation，再在它下面找 PointName
                if (!string.IsNullOrEmpty(opName))
                {
                    var allOps = doc.OperationRoot.GetAllDescendants(
                        new TxTypeFilter(typeof(ITxObject)));
                    foreach (ITxObject obj in allOps)
                    {
                        if (obj.Name != opName) continue;
                        var locs = EnumerateLocations(obj);
                        foreach (var l in locs)
                        {
                            if ((l as ITxObject)?.Name == pointName)
                                return l as ITxObject;
                        }
                    }
                }

                // 兜底策略：全文档遍历，找名字匹配的 ITxRoboticLocationOperation
                var allDesc = doc.OperationRoot.GetAllDescendants(
                    new TxTypeFilter(typeof(ITxObject)));
                foreach (ITxObject obj in allDesc)
                {
                    if (!(obj is ITxRoboticLocationOperation)) continue;
                    if (obj.Name == pointName) return obj;
                }
            }
            catch (Exception ex)
            {
                Log($"FindLocationInDoc 异常: {ex.Message}", "ERR");
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // 调用 PS 内置 Robot Jog 命令
        //
        // 命令 ID 来源：从 PS 安装目录的 RibbonConfiguration.xml 中查到
        //   <!--Name: Robot Jog-->
        //   <RibbonItem Id="DnProcessSimulateCommands.RobotJog.CApRJRobotJogCmd" />
        //
        // 同目录下其他相关命令的 ID（备查）：
        //   Joint Jog            : DnProcessSimulateCommands.JointJog.CUiKinJointJogCmd
        //   Manipulate Location  : {0A7F9938-20FD-11D4-A4BD-00104B17FDD6}  (GUID形式)
        // ---------------------------------------------------------------------
        private const string CMD_ROBOT_JOG = "DnProcessSimulateCommands.RobotJog.CApRJRobotJogCmd";

        private void OpenRobotJogDialog()
        {
            TxCommandsManager mgr = null;
            try { mgr = TxApplication.CommandsManager; }
            catch (Exception exMgr)
            {
                Log($"获取 CommandsManager 异常: {exMgr.Message}", "ERR");
                return;
            }

            if (mgr == null)
            {
                Log("CommandsManager 为 null", "ERR");
                return;
            }

            // ── 关键：暂停 OnSelectionTick 轮询 ────────────────────────────
            // ExecuteCommand 是同步阻塞的（直到用户关闭 Robot Jog 才返回），
            // 期间 PS 会改 ActiveSelection（Follow mode 会选中点位 / 切换激活操作），
            // 如果不停掉 _selTimer，OnSelectionTick 会把表格刷成那个新选中操作的内容。
            // 同时记录当前 OP 关联，命令返回后强制复位，避免重启 timer 后又被刷掉。
            bool timerWasRunning = _selTimer != null && _selTimer.Enabled;
            ITxRoboticOperation savedOp = _lastSelectedOp;
            string savedOpComboText = _tsOp.SelectedItem?.ToString();

            if (timerWasRunning)
            {
                _selTimer.Stop();
                Log("  已暂停 OnSelectionTick 轮询（防止 Robot Jog 期间表格被刷新）", "DEBUG");
            }

            try
            {
                Log($"  执行命令: {CMD_ROBOT_JOG}", "DEBUG");
                mgr.ExecuteCommand(CMD_ROBOT_JOG);
                Log($"  ✓ Robot Jog 已触发", "DEBUG");
            }
            catch (TxCommandIdentifierDoesNotExistException)
            {
                Log($"  命令ID不存在: {CMD_ROBOT_JOG}（PS 版本可能不同）", "ERR");
                MessageBox.Show(
                    $"Robot Jog 命令未注册到当前 PS 实例。\n\n" +
                    $"已使用的命令ID:\n  {CMD_ROBOT_JOG}\n\n" +
                    $"目标点位已选中，可手动点击工具栏的 Robot Jog 按钮。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (TxCannotActivateCommandException exAct)
            {
                Log($"  ✓ Robot Jog 命令已激活: {exAct.Message}", "OK");
            }
            catch (Exception ex)
            {
                Log($"  执行命令异常: {ex.GetType().Name} - {ex.Message}", "ERR");
            }
            finally
            {
                // ── 命令返回后恢复 timer，但先把 _lastSelectedOp 锁回原值 ────
                // 这样下次 OnSelectionTick 看到 PS 当前选中的 Operation（被 Robot Jog
                // 改过的）跟 _lastSelectedOp 不同时，按现有逻辑会"切换"，但我们
                // 希望保持原 OP 不变，所以反过来把 ActiveSelection 也尝试恢复
                _lastSelectedOp = savedOp;

                // 尝试把 ActiveSelection 中的内容清掉，让 timer 看到"无选中"状态
                // 这样它就不会触发 op 切换；用户后续主动选别的才会触发
                try
                {
                    TxApplication.ActiveSelection.Clear();
                    Log("  已清空 ActiveSelection（防止 timer 误判）", "DEBUG");
                }
                catch (Exception exClr)
                {
                    Log($"  清空 ActiveSelection 异常（忽略）: {exClr.Message}", "WARN");
                }

                if (timerWasRunning)
                {
                    _selTimer.Start();
                    Log("  已恢复 OnSelectionTick 轮询", "DEBUG");
                }
            }
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
                _pickedOperation = null;
                return;
            }

            // 保存用户拾取到的具体对象实例（关键：避免后续按名字查找时拿错副本）
            _pickedOperation = pickedObj;

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
        private List<PathPointResult> CheckReachabilityViaPS(string operationName,
            Action<int, int> progress = null,
            ITxObject preferredOp = null)
        {
            var results = new List<PathPointResult>();
            Log($"开始检查：[{operationName}]");
            try
            {
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc == null) throw new InvalidOperationException("ActiveDocument 为 null");

                // ── 1. 优先使用用户拾取的具体对象实例（避免同名 Operation 冲突）──
                ITxObject operation = null;
                if (preferredOp != null && preferredOp.Name == operationName)
                {
                    operation = preferredOp;
                    Log($"  使用拾取实例: {operation.Name} ({operation.GetType().Name}) HashCode={operation.GetHashCode()}");
                }
                else
                {
                    operation = FindOperationByName(doc, operationName);
                    if (operation != null)
                        Log($"  按名查找实例: {operation.Name} ({operation.GetType().Name}) HashCode={operation.GetHashCode()}", "DEBUG");
                }
                if (operation == null) throw new InvalidOperationException($"未找到操作: {operationName}");

                // ── 2. 从操作自动查找关联机器人 ───────────────────────────
                TxRobot robot = FindAssociatedRobot(operation, doc);
                if (robot == null) throw new InvalidOperationException(
                    $"无法从操作 [{operationName}] 找到关联机器人，请确认操作已分配到机器人");
                Log($"机器人: {robot.Name}", "DEBUG");

                // 探测 DrivingJoints 数量
                int djCount = 0;
                try { djCount = robot.DrivingJoints?.Count ?? 0; } catch { }

                // ── 3. 枚举路径点位 ───────────────────────────────────────
                var locs = EnumerateLocations(operation);
                if (locs.Count == 0)
                    throw new InvalidOperationException($"操作 [{operationName}] 下未找到路径点，请确认操作类型");

                // ── 4. 读取关节限位 ───────────────────────────────────────
                var jointLimits = GetJointLimits(robot);

                // ── 5. 保存初始姿态 ───────────────────────────────────────
                TxPoseData savedPose = null;
                try { savedPose = robot.CurrentPose; }
                catch (Exception ex) { Log($"保存初始姿态失败（非致命）: {ex.Message}", "WARN"); }

                // 当前关节姿态（度），供 IK 多解优选参考
                double[] currentJointsDeg = ReadDrivingJoints(robot);
                if (currentJointsDeg != null && currentJointsDeg.Length > 0)
                {
                    double maxAbs = currentJointsDeg.Max(Math.Abs);
                    if (maxAbs > 0 && maxAbs <= 2 * Math.PI + 0.05)
                        currentJointsDeg = currentJointsDeg.Select(v => v * 180.0 / Math.PI).ToArray();
                }

                // 解析品牌（用于临界点判定）
                RobotBrand brand = ResolveBrand(robot.Name);

                int idx = 1;
                int okA = 0, okB = 0, okC = 0, fail = 0;

                // ── 6. TCP余量检查预备 ─────────────────────────────────
                bool tcpGlobalEnabled = _chkTcpXyz != null && _chkTcpXyz.Checked;

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
                                Log($"  [{res.PointName}] A成功", "DEBUG");
                            }
                            else
                            {
                                Log($"  [{res.PointName}] A: TryExtractPoseValues 返回空", "WARN");
                            }
                        }
                        else
                        {
                            Log($"  [{res.PointName}] A: GetPoseAtLocation 返回 null", "WARN");
                        }
                    }
                    catch (Exception exA)
                    {
                        Log($"  [{res.PointName}] A异常: {exA.GetType().Name} - {exA.Message}", "WARN");
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
                                    Log($"  [{res.PointName}] B成功", "DEBUG");
                                }
                                else { Log($"  [{res.PointName}] B: DrivingJoints 读取为空", "WARN"); }
                            }
                            else
                            {
                                Log($"  [{res.PointName}] B: pd={(pd == null ? "null" : "OK")}, savedPose={(savedPose == null ? "null" : "OK")}", "WARN");
                            }
                        }
                        catch (Exception exB)
                        {
                            Log($"  [{res.PointName}] B异常: {exB.GetType().Name} - {exB.Message}", "WARN");
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
                            Log($"  [{res.PointName}] 尝试IK求解...", "DEBUG");
                            try
                            {
                                var invData = new TxRobotInverseData(locTx);

                                bool hasInv = false;
                                try { hasInv = robot.DoesInverseExist(invData); }
                                catch (Exception exExist) { Log($"  [{res.PointName}] DoesInverseExist异常: {exExist.Message}", "WARN"); }

                                if (!hasInv)
                                {
                                    Log($"  [{res.PointName}] IK无解 (DoesInverseExist=false)", "WARN");
                                    errMsg = "IK无解：超出工作包络或构型奇异";
                                    res.Status = ReachabilityStatus.Unreachable;
                                    res.ErrorMessage = errMsg;
                                    fail++;
                                    results.Add(res);
                                    try { progress?.Invoke(results.Count, locs.Count); } catch { }
                                    continue;
                                }

                                System.Collections.ArrayList solutions = robot.CalcInverseSolutions(invData);
                                Log($"  [{res.PointName}] IK解数量: {solutions?.Count ?? 0}", "DEBUG");

                                if (solutions != null && solutions.Count > 0)
                                {
                                    // 先尝试直接读第一个解的 Values（方式C1）
                                    // 多解优选：挑离当前姿态 L1 距离最近的解
                                    // 这样可以避免选到 J6=+341° 这种与现场 -18° 等价但显得超限的解
                                    TxPoseData firstSol = PickClosestSolution(solutions, currentJointsDeg, djCount);
                                    if (firstSol != null)
                                    {
                                        double[] extracted = TryExtractPoseValues(firstSol, djCount);
                                        if (extracted != null && extracted.Length > 0)
                                        {
                                            joints = extracted;
                                            gotJoints = true;
                                            okC++;
                                            Log($"  [{res.PointName}] C1成功", "DEBUG");
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
                                                    Log($"  [{res.PointName}] C2成功", "DEBUG");
                                                }
                                                else { Log($"  [{res.PointName}] C2: DrivingJoints空", "DEBUG"); }
                                            }
                                            catch (Exception exC2) { Log($"  [{res.PointName}] C2异常: {exC2.Message}", "WARN"); }
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

                        // ── 综合判定：超限 / 奇异 / 近极限 / 临界 ──────────
                        bool jointCheckEnabled = _chkJointMargin != null && _chkJointMargin.Checked;
                        double marginThresh = _nudJointMarginDeg != null
                            ? (double)_nudJointMarginDeg.Value : 10.0;

                        // 同时计算最小余量（用于备注/排序）
                        var (minMargin, _, _) = CalcJointMargins(jointLimits, marginThresh,
                            res.J1, res.J2, res.J3, res.J4, res.J5, res.J6);
                        res.JointMargin = Math.Round(minMargin, 1);

                        string axisNote;
                        res.Status = AnalyzePoint(res, jointLimits, marginThresh,
                            jointCheckEnabled, brand, out axisNote);
                        res.ErrorMessage = axisNote;  // 简短：如 "J5奇异" / "J6近极限(8°)" / "J3临界"

                        // ── 点位 XYZ 余量检查（仅在勾选时执行）──────────────
                        if (tcpGlobalEnabled)
                        {
                            double tcpMarginMm = _nudTcpMargin != null ? (double)_nudTcpMargin.Value : 200.0;
                            string tcpWarn = CheckTcpXyzMargin(robot, loc, tcpMarginMm);
                            if (!string.IsNullOrEmpty(tcpWarn))
                            {
                                // TCP 余量不足升级到 NearLimit（除非已经更严重）
                                if (res.Status == ReachabilityStatus.Reachable
                                    || res.Status == ReachabilityStatus.Critical)
                                    res.Status = ReachabilityStatus.NearLimit;
                                res.ErrorMessage = string.IsNullOrEmpty(res.ErrorMessage)
                                    ? tcpWarn
                                    : res.ErrorMessage + "; " + tcpWarn;
                            }
                        }

                        if (res.Status == ReachabilityStatus.Unreachable) fail++;
                    }
                    else
                    {
                        res.Status = ReachabilityStatus.Unreachable;
                        res.ErrorMessage = string.IsNullOrEmpty(errMsg) ? "无法获取轴值" : errMsg;
                        fail++;
                        Log($"  [{res.PointName}] 所有方式失败: {res.ErrorMessage}", "ERR");
                    }

                    results.Add(res);

                    // 每完成一个点位上报真实进度（让进度条/状态栏即时反馈）
                    try { progress?.Invoke(results.Count, locs.Count); } catch { }
                }

                // 恢复初始姿态
                try { if (savedPose != null) robot.CurrentPose = savedPose; } catch { }

                Log($"检查完成: A={okA} B={okB} C={okC} 失败={fail}", "OK");
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
            Log($"  枚举点位: 操作类型={operation?.GetType().Name ?? "null"}", "DEBUG");

            // 路径1：复合操作（TxRoboticOperation / TxCompoundOperation）
            if (operation is ITxCompoundOperation comp)
            {
                try
                {
                    var objs = comp.GetAllDescendants(
                        new TxTypeFilter(typeof(ITxRoboticLocationOperation)));
                    Log($"  ITxCompoundOperation.GetAllDescendants 返回 {objs?.Count ?? 0} 个对象", "DEBUG");
                    foreach (ITxObject o in objs)
                        if (o is ITxRoboticLocationOperation l) list.Add(l);
                }
                catch (Exception ex) { Log($"  GetAllDescendants 异常: {ex.Message}", "WARN"); }
            }
            else
            {
                Log("  操作不是 ITxCompoundOperation，尝试其他方式...", "DEBUG");
            }

            // 路径2：尝试 dynamic GetDirectDescendants / GetAllDescendants
            if (list.Count == 0)
            {
                try
                {
                    dynamic dop = operation;
                    TxObjectList objs = dop.GetAllDescendants(
                        new TxTypeFilter(typeof(ITxRoboticLocationOperation)));
                    Log($"  dynamic.GetAllDescendants 返回 {objs?.Count ?? 0} 个对象", "DEBUG");
                    foreach (ITxObject o in objs)
                        if (o is ITxRoboticLocationOperation l) list.Add(l);
                }
                catch (Exception ex) { Log($"  dynamic.GetAllDescendants 异常: {ex.Message}", "DEBUG"); }
            }

            // 路径3：操作本身就是一个 LocationOperation
            if (list.Count == 0 && operation is ITxRoboticLocationOperation self)
            {
                Log("  操作本身是 ITxRoboticLocationOperation，作为单点处理", "DEBUG");
                list.Add(self);
            }

            Log($"  枚举结果: {list.Count} 个点位", "DEBUG");
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
            try { dynamic d = loc; var v = d.AbsoluteLocation; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: AbsoluteLocation OK", "DEBUG"); return tx; } } catch { }

            // 策略2：AbsoluteFrame
            try { dynamic d = loc; var v = d.AbsoluteFrame; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: AbsoluteFrame OK", "DEBUG"); return tx; } } catch { }

            // 策略3：LocationInWorld（部分版本）
            try { dynamic d = loc; var v = d.LocationInWorld; if (v is TxTransformation t && t != null) { tx = t; Log($"    GetLocationTransform: LocationInWorld OK", "DEBUG"); return tx; } } catch { }

            // 策略4：通过 ITxLocatableObject 接口（PS 的通用定位接口）
            try
            {
                if (loc is ITxLocatableObject lobj)
                {
                    tx = lobj.AbsoluteLocation;
                    if (tx != null) { Log($"    GetLocationTransform: ITxLocatableObject.AbsoluteLocation OK", "DEBUG"); return tx; }
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

        // ── 获取关节限位（度） ─────────────────────────────────────────────
        //
        // 日志分析：DrivingJoints 上 MinValue/MaxValue/LowerLimit/UpperLimit/Min/Max 全部失败。
        // 新策略：
        //   源1: robot.Joints（ITxKinematicsModellable，运动学关节，与 DrivingJoints 不同类型）
        //   源2: DrivingJoints + 反射枚举属性
        // =====================================================================
        // 奇异/临界/单轴问题判定 工具方法
        // =====================================================================

        // 解析"自动"品牌：如果用户在 UI 选了具体品牌，直接用；否则按机器人名前缀猜
        private RobotBrand ResolveBrand(string robotName)
        {
            // UI 品牌选择优先
            if (_cbBrand != null && _cbBrand.SelectedIndex > 0)
            {
                switch (_cbBrand.SelectedItem?.ToString())
                {
                    case "KUKA": return RobotBrand.KUKA;
                    case "ABB": return RobotBrand.ABB;
                    case "FANUC": return RobotBrand.FANUC;
                    case "其他": return RobotBrand.Other;
                }
            }

            // 自动模式：按名字前缀猜
            string n = (robotName ?? "").ToUpper();
            if (n.Contains("KR") || n.Contains("KUKA")) return RobotBrand.KUKA;
            if (n.Contains("IRB") || n.Contains("ABB")) return RobotBrand.ABB;
            if (n.Contains("FANUC") || n.Contains("R200")) return RobotBrand.FANUC;
            return RobotBrand.Other;
        }

        // 临界带定义：每条 (lo, hi) 闭区间，落入即判为临界
        // 数据来源：用户提供的临界点规则图（KUKA / ABB / FANUC）
        // 用静态字段 + 静态构造函数初始化，避免依赖 C# 6 字典初始化器在
        // 嵌套泛型场景下与 C# 7.3 解析器的兼容性问题
        private static readonly Dictionary<RobotBrand, List<(double lo, double hi)>> CriticalBands
            = BuildCriticalBands();

        private static Dictionary<RobotBrand, List<(double lo, double hi)>> BuildCriticalBands()
        {
            var d = new Dictionary<RobotBrand, List<(double lo, double hi)>>();
            d.Add(RobotBrand.KUKA, new List<(double lo, double hi)>
            {
                (-5.0, 5.0)   // 1/3/5 轴 ±5°
            });
            d.Add(RobotBrand.ABB, new List<(double lo, double hi)>
            {
                (-275, -265), (-185, -175), (-95, -85), (-5, 5),
                (85, 95), (175, 185), (265, 275)
            });
            d.Add(RobotBrand.FANUC, new List<(double lo, double hi)>
            {
                (-185, -175), (175, 185)
            });
            d.Add(RobotBrand.Other, new List<(double lo, double hi)>
            {
                (-5, 5)
            });
            return d;
        }

        // 判断一个轴值是否落入"临界带"
        // axisIdx: 0..5 对应 J1..J6（仅 1/3/5 轴需要判定，其他直接返回 false）
        private bool IsInCriticalBand(double valDeg, int axisIdx, RobotBrand brand)
        {
            // 临界点只关注 1、3、5 轴（图示规则限定）
            if (axisIdx != 0 && axisIdx != 2 && axisIdx != 4) return false;

            List<(double lo, double hi)> bands;
            if (!CriticalBands.TryGetValue(brand, out bands)) return false;
            for (int i = 0; i < bands.Count; i++)
            {
                double lo = bands[i].lo;
                double hi = bands[i].hi;
                if (valDeg >= lo && valDeg <= hi) return true;
            }
            return false;
        }

        // 奇异点：J5 ∈ [-10°, +10°]（腕部奇异，所有品牌通用）
        private bool IsJ5Singular(double j5Deg) => Math.Abs(j5Deg) <= 10.0;

        // 综合分析单个点位：填充 AxisFlags 并返回最终 Status
        //
        // 严重性顺序：Unreachable > Singular > NearLimit > Critical > Reachable
        // —— 每个轴单独打 flag，但点位整体状态取所有轴中最严重的那个
        private ReachabilityStatus AnalyzePoint(PathPointResult res,
            List<(double lo, double hi)> jointLimits, double nearThresh,
            bool jointCheckEnabled, RobotBrand brand,
            out string axisDetail)
        {
            axisDetail = "";
            double[] vals = { res.J1, res.J2, res.J3, res.J4, res.J5, res.J6 };
            res.AxisFlags = new AxisFlag[6];

            // 整体最严重等级（数值越大越严重，最后映射回枚举）
            int worstLevel = 0;  // 0=Reachable, 1=Critical, 2=NearLimit, 3=Singular, 4=Unreachable
            string firstNote = "";

            for (int i = 0; i < 6; i++)
            {
                AxisFlag f = AxisFlag.None;
                double v = vals[i];

                // (1) 轴超限 / 近极限（只在勾选时启用）
                if (i < jointLimits.Count)
                {
                    var (lo, hi) = jointLimits[i];
                    if (lo != hi)  // 限位有效
                    {
                        if (v < lo - 0.001 || v > hi + 0.001)
                        {
                            f |= AxisFlag.OverLimit;
                            if (worstLevel < 4) { worstLevel = 4; firstNote = $"J{i + 1}超限"; }
                        }
                        else if (jointCheckEnabled)
                        {
                            double margin = Math.Min(v - lo, hi - v);
                            if (margin < nearThresh)
                            {
                                f |= AxisFlag.NearLimit;
                                if (worstLevel < 2)
                                {
                                    worstLevel = 2;
                                    firstNote = $"J{i + 1}近极限({margin:F0}°)";
                                }
                            }
                        }
                    }
                }

                // (2) J5 奇异（覆盖临界 — 优先级高）
                if (i == 4 && IsJ5Singular(v))
                {
                    f |= AxisFlag.Singular;
                    if (worstLevel < 3) { worstLevel = 3; firstNote = "J5奇异"; }
                }
                else if (IsInCriticalBand(v, i, brand))
                {
                    // (3) 临界带 — 仅在没有更严重问题时才标
                    f |= AxisFlag.Critical;
                    if (worstLevel < 1) { worstLevel = 1; firstNote = $"J{i + 1}临界"; }
                }

                res.AxisFlags[i] = f;
            }

            axisDetail = firstNote;

            switch (worstLevel)
            {
                case 4: return ReachabilityStatus.Unreachable;
                case 3: return ReachabilityStatus.Singular;
                case 2: return ReachabilityStatus.NearLimit;
                case 1: return ReachabilityStatus.Critical;
                default: return ReachabilityStatus.Reachable;
            }
        }

        // IK 多解优选：从所有解中挑离当前关节姿态 L1 距离最近的一个
        // 这样能避免选到 J6 = +341° 这种与机器人当前 -18° 等价但显得超限的解
        private TxPoseData PickClosestSolution(System.Collections.ArrayList solutions,
            double[] currentDeg, int djCount)
        {
            if (solutions == null || solutions.Count == 0) return null;
            if (solutions.Count == 1 || currentDeg == null) return solutions[0] as TxPoseData;

            TxPoseData best = solutions[0] as TxPoseData;
            double bestDist = double.MaxValue;

            foreach (var item in solutions)
            {
                var pd = item as TxPoseData;
                if (pd == null) continue;

                double[] vals = TryExtractPoseValues(pd, djCount);
                if (vals == null || vals.Length == 0) continue;

                // 单位归一：从 PoseData 取出的可能是弧度，先转度
                double maxAbs = vals.Max(Math.Abs);
                bool isRad = maxAbs > 0 && maxAbs <= 2 * Math.PI + 0.05;
                double[] valsDeg = isRad
                    ? vals.Select(v => v * 180.0 / Math.PI).ToArray()
                    : vals;

                // L1 距离（不加权）
                double dist = 0;
                int n = Math.Min(valsDeg.Length, currentDeg.Length);
                for (int i = 0; i < n; i++)
                    dist += Math.Abs(valsDeg[i] - currentDeg[i]);

                if (dist < bestDist) { bestDist = dist; best = pd; }
            }
            return best;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 读取机器人各轴的软限位
        // 来源优先级：
        //   源1: robot.Joints（运动学关节，最权威）
        //   源2: robot.DrivingJoints（驱动轴反射）
        //   源3: robot.GetParameter / GetAllInstanceParameters
        //   源4: robot.Attributes (自定义属性)
        // ─────────────────────────────────────────────────────────────────────
        private List<(double lo, double hi)> GetJointLimits(TxRobot robot)
        {
            var limits = new List<(double lo, double hi)>();

            // ── 源1：robot.Joints（运动学关节） ────────────────────────
            // 注意：robot.Joints 可能包含外部轴（J7/J8/J9），其限位值单位不同（度制大数值），
            //       会干扰 J1~J6 的弧度制检测。因此仅取前 djCount 个驱动轴。
            int djCount = 0;
            try { djCount = robot.DrivingJoints?.Count ?? 6; } catch { djCount = 6; }
            if (djCount <= 0) djCount = 6;

            // 诊断：打印 robot.Joints 和 robot.DrivingJoints 的实际内容
            try
            {
                Log($"  [诊断] DrivingJoints.Count = {djCount}");
                var diagAll = robot.Joints;
                if (diagAll != null)
                {
                    Log($"  [诊断] Joints.Count = {diagAll.Count}, 内容如下：");
                    int di = 0;
                    foreach (object j in diagAll)
                    {
                        string n = "?", t = "?", val = "?", lo = "?", hi = "?", jt = "?";
                        try { dynamic dj = j; n = dj.Name?.ToString() ?? "(空)"; } catch { }
                        try { t = j.GetType().Name; } catch { }
                        try { dynamic dj = j; val = dj.CurrentValue.ToString("F2"); } catch { }
                        try { dynamic dj = j; lo = dj.LowerLimit.ToString("F2"); } catch { }
                        try { dynamic dj = j; hi = dj.UpperLimit.ToString("F2"); } catch { }
                        try { dynamic dj = j; jt = dj.JointType?.ToString() ?? "?"; } catch { }
                        Log($"    [{di}] Name={n}, Type={t}, JointType={jt}, Cur={val}, Lim=[{lo},{hi}]");
                        di++;
                    }
                }
                var diagDriving = robot.DrivingJoints;
                if (diagDriving != null)
                {
                    Log($"  [诊断] DrivingJoints 内容：");
                    int di = 0;
                    foreach (object j in diagDriving)
                    {
                        string n = "?", t = "?";
                        try { dynamic dj = j; n = dj.Name?.ToString() ?? "(空)"; } catch { }
                        try { t = j.GetType().Name; } catch { }
                        Log($"    [{di}] Name={n}, Type={t}");
                        di++;
                    }
                }
            }
            catch (Exception exDiag)
            {
                Log($"  [诊断] 关节枚举异常: {exDiag.Message}", "WARN");
            }

            try
            {
                var kinJoints = robot.Joints;
                if (kinJoints != null && kinJoints.Count > 0)
                {
                    Log($"  尝试 robot.Joints: 共 {kinJoints.Count} 个关节, 取前 {djCount} 个驱动轴");
                    int jIdx = 0;
                    foreach (object j in kinJoints)
                    {
                        if (jIdx >= djCount) break;   // 仅取驱动轴，跳过外部轴
                        var lim = TryReadJointLimit(j, jIdx);
                        if (lim.HasValue)
                        {
                            limits.Add(lim.Value);
                            Log($"  J{jIdx + 1} 限位(Joints): [{lim.Value.lo:F4}, {lim.Value.hi:F4}]", "DEBUG");
                        }
                        jIdx++;
                    }
                    if (limits.Count > 0 && limits.Any(l => Math.Abs(l.hi - l.lo) < 719))
                    {
                        Log($"  从 robot.Joints 获取限位成功: {limits.Count} 轴", "OK");
                        EnsureDegrees(limits);
                        return limits;
                    }
                    else
                    {
                        Log("  robot.Joints 限位全为默认值或无效，继续尝试其他源", "WARN");
                        limits.Clear();
                    }
                }
            }
            catch (Exception ex) { Log($"  robot.Joints 异常: {ex.Message}", "WARN"); }

            // ── 源2：DrivingJoints + 反射枚举所有属性 ──────────────────
            try
            {
                TxObjectList dj = robot.DrivingJoints;
                if (dj != null && dj.Count > 0)
                {
                    Log($"  尝试 DrivingJoints 反射: {dj.Count} 个关节", "DEBUG");
                    // 先对第一个关节枚举所有属性，找出可能的限位属性名
                    object firstJoint = null;
                    foreach (object jj in dj) { firstJoint = jj; break; }
                    if (firstJoint != null)
                    {
                        var props = firstJoint.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        var sb = new StringBuilder("  DrivingJoint[0] 属性列表: ");
                        foreach (var p in props)
                        {
                            // 只记录可能和限位相关的属性
                            string pn = p.Name;
                            if (pn.Contains("Limit") || pn.Contains("Min") || pn.Contains("Max")
                                || pn.Contains("Value") || pn.Contains("Range") || pn.Contains("Bound")
                                || pn.Contains("Lower") || pn.Contains("Upper") || pn.Contains("Angle"))
                            {
                                try
                                {
                                    object val = p.GetValue(firstJoint);
                                    sb.Append($"{pn}={val}, ");
                                }
                                catch { sb.Append($"{pn}=ERR, "); }
                            }
                        }
                        Log(sb.ToString());
                    }

                    int jIdx = 0;
                    foreach (object j in dj)
                    {
                        var lim = TryReadJointLimit(j, jIdx);
                        if (lim.HasValue)
                        {
                            limits.Add(lim.Value);
                            Log($"  J{jIdx + 1} 限位(DrivingJoints): [{lim.Value.lo:F1}, {lim.Value.hi:F1}]", "DEBUG");
                        }
                        jIdx++;
                    }
                    if (limits.Count > 0 && limits.Any(l => Math.Abs(l.hi - l.lo) < 719))
                    {
                        Log($"  从 DrivingJoints 获取限位成功: {limits.Count} 轴", "OK");
                        EnsureDegrees(limits);
                        return limits;
                    }
                    limits.Clear();
                }
            }
            catch (Exception ex) { Log($"  DrivingJoints 反射异常: {ex.Message}", "WARN"); }

            // ── 源3：robot.GetParameter / GetAllInstanceParameters ──────
            try
            {
                Log("  尝试 robot.GetParameter 方式获取限位...", "DEBUG");
                for (int i = 1; i <= 6; i++)
                {
                    double lo = -360, hi = 360;
                    bool got = false;
                    // 尝试常见参数名
                    string[] loNames = { $"J{i}_Min", $"j{i}_min", $"Joint{i}Min", $"A{i}_Min", $"joint{i}LowerLimit" };
                    string[] hiNames = { $"J{i}_Max", $"j{i}_max", $"Joint{i}Max", $"A{i}_Max", $"joint{i}UpperLimit" };
                    foreach (string n in loNames)
                        try { dynamic v = robot.GetParameter(n); lo = Convert.ToDouble(v); got = true; break; } catch { }
                    foreach (string n in hiNames)
                        try { dynamic v = robot.GetParameter(n); hi = Convert.ToDouble(v); got = true; break; } catch { }
                    if (got) Log($"  J{i} 限位(GetParameter): [{lo:F1}, {hi:F1}]", "DEBUG");
                    limits.Add((lo, hi));
                }
                if (limits.Any(l => Math.Abs(l.hi - l.lo) < 719))
                {
                    Log("  从 GetParameter 获取限位成功", "OK");
                    EnsureDegrees(limits);
                    return limits;
                }
                limits.Clear();
            }
            catch (Exception ex) { Log($"  GetParameter 异常: {ex.Message}", "WARN"); }

            // ── 源4：枚举 GetAllInstanceParameters ──────────────────────
            try
            {
                dynamic allParams = robot.GetAllInstanceParameters();
                if (allParams != null)
                {
                    var paramSb = new StringBuilder("  InstanceParameters: ");
                    int count = 0;
                    foreach (dynamic p in allParams)
                    {
                        try
                        {
                            string pName = p.Name?.ToString() ?? p.ToString();
                            if (pName.ToUpper().Contains("LIMIT") || pName.ToUpper().Contains("JOINT")
                                || pName.ToUpper().Contains("MIN") || pName.ToUpper().Contains("MAX"))
                            {
                                try { paramSb.Append($"{pName}={p.Value}, "); } catch { paramSb.Append($"{pName}, "); }
                                count++;
                            }
                        }
                        catch { }
                    }
                    if (count > 0) Log(paramSb.ToString());
                    else Log("  InstanceParameters 中无限位相关参数", "DEBUG");
                }
            }
            catch (Exception ex) { Log($"  GetAllInstanceParameters 异常: {ex.Message}", "WARN"); }

            // ── 全部失败：返回默认限位并警告 ───────────────────────────
            Log("  ⚠ 所有方式均无法获取关节限位，使用默认值 [-360, 360]", "WARN");
            Log("  请在日志中查看 DrivingJoint 属性列表，将包含限位的属性名反馈给开发者", "DEBUG");
            limits.Clear();
            for (int i = 0; i < 6; i++) limits.Add((-360, 360));
            return limits;
        }

        /// <summary>尝试从单个关节对象读取限位（lo, hi），返回 null 表示全部失败</summary>
        private (double lo, double hi)? TryReadJointLimit(object joint, int index)
        {
            if (joint == null) return null;
            double lo = -360, hi = 360;
            bool gotLo = false, gotHi = false;

            dynamic dj = joint;

            // ── 策略组1：直接属性访问 ────────────────────────────
            if (!gotLo) try { lo = Convert.ToDouble(dj.LowerLimit); gotLo = true; } catch { }
            if (!gotHi) try { hi = Convert.ToDouble(dj.UpperLimit); gotHi = true; } catch { }
            if (!gotLo) try { lo = Convert.ToDouble(dj.MinValue); gotLo = true; } catch { }
            if (!gotHi) try { hi = Convert.ToDouble(dj.MaxValue); gotHi = true; } catch { }
            if (!gotLo) try { lo = Convert.ToDouble(dj.Min); gotLo = true; } catch { }
            if (!gotHi) try { hi = Convert.ToDouble(dj.Max); gotHi = true; } catch { }
            if (!gotLo) try { lo = Convert.ToDouble(dj.MinimumValue); gotLo = true; } catch { }
            if (!gotHi) try { hi = Convert.ToDouble(dj.MaximumValue); gotHi = true; } catch { }
            if (!gotLo) try { lo = Convert.ToDouble(dj.SoftLowerLimit); gotLo = true; } catch { }
            if (!gotHi) try { hi = Convert.ToDouble(dj.SoftUpperLimit); gotHi = true; } catch { }

            // ── 策略组2：Range / Limits 对象 ─────────────────────
            if (!gotLo || !gotHi)
            {
                try
                {
                    dynamic range = dj.Range;
                    if (!gotLo) try { lo = Convert.ToDouble(range.Min); gotLo = true; } catch { }
                    if (!gotHi) try { hi = Convert.ToDouble(range.Max); gotHi = true; } catch { }
                    if (!gotLo) try { lo = Convert.ToDouble(range.Lower); gotLo = true; } catch { }
                    if (!gotHi) try { hi = Convert.ToDouble(range.Upper); gotHi = true; } catch { }
                }
                catch { }
                try
                {
                    dynamic lims = dj.Limits;
                    if (!gotLo) try { lo = Convert.ToDouble(lims.Min); gotLo = true; } catch { }
                    if (!gotHi) try { hi = Convert.ToDouble(lims.Max); gotHi = true; } catch { }
                    if (!gotLo) try { lo = Convert.ToDouble(lims[0]); gotLo = true; } catch { }
                    if (!gotHi) try { hi = Convert.ToDouble(lims[1]); gotHi = true; } catch { }
                }
                catch { }
            }

            // ── 策略组3：通过反射搜索含 "limit"/"min"/"max" 的属性 ──
            if (!gotLo || !gotHi)
            {
                try
                {
                    var props = joint.GetType().GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    foreach (var p in props)
                    {
                        string pn = p.Name.ToUpper();
                        try
                        {
                            object val = p.GetValue(joint);
                            double dv = Convert.ToDouble(val);
                            if (!gotLo && (pn.Contains("LOWER") || (pn.Contains("MIN") && !pn.Contains("MINIMUM"))))
                            { lo = dv; gotLo = true; }
                            if (!gotHi && (pn.Contains("UPPER") || (pn.Contains("MAX") && !pn.Contains("MAXIMUM"))))
                            { hi = dv; gotHi = true; }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return (gotLo || gotHi) ? (lo, hi) : ((-360.0, 360.0));
        }

        /// <summary>统一弧度→度转换</summary>
        private void EnsureDegrees(List<(double lo, double hi)> limits)
        {
            bool allSmall = limits.Count > 0 && limits.All(
                l => Math.Abs(l.lo) <= 2 * Math.PI + 0.5 && Math.Abs(l.hi) <= 2 * Math.PI + 0.5);
            if (allSmall)
            {
                Log("  关节限位判定为弧度制，转换为度", "DEBUG");
                for (int i = 0; i < limits.Count; i++)
                    limits[i] = (limits[i].lo * 180.0 / Math.PI, limits[i].hi * 180.0 / Math.PI);
            }
            for (int i = 0; i < limits.Count; i++)
                Log($"  J{i + 1} 最终限位(度): [{limits[i].lo:F1}, {limits[i].hi:F1}]", "DEBUG");
        }

        /// <summary>
        /// 计算各轴余量，返回 (最小余量, 最小余量所在轴号, 各轴详情字符串)
        /// 余量 = min(角度值 - 下限, 上限 - 角度值)，单位：度
        /// </summary>
        private (double minMargin, int minAxis, string detail) CalcJointMargins(
            List<(double lo, double hi)> limits, double threshold, params double[] angles)
        {
            double minMargin = 9999;
            int minAxis = 0;
            var warnings = new List<string>();

            for (int i = 0; i < Math.Min(angles.Length, limits.Count); i++)
            {
                double lo = limits[i].lo, hi = limits[i].hi;
                double range = hi - lo;
                if (range < 1) continue;  // 无效限位跳过

                double toLo = angles[i] - lo;    // 距下限余量
                double toHi = hi - angles[i];    // 距上限余量
                double margin = Math.Min(toLo, toHi);

                if (margin < threshold)
                {
                    string side = toLo < toHi ? "接近下限" : "接近上限";
                    double closeTo = toLo < toHi ? lo : hi;
                    warnings.Add($"J{i + 1}={angles[i]:F1}°({side} {closeTo:F1}°,余量{margin:F1}°)");
                }

                if (margin < minMargin)
                {
                    minMargin = margin;
                    minAxis = i + 1;
                }
            }

            string detail = warnings.Count > 0 ? string.Join("; ", warnings) : "";
            return (minMargin < 9999 ? minMargin : 9999, minAxis, detail);
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
        // 逻辑：将机器人跳转至该点位，沿 ±X / ±Y / ±Z 六个方向偏移 TCP，
        //        使用 IK（DoesInverseExist）探测每个方向的最大可移动距离（mm），
        //        6个方向的最小值即为该点的 TCP 余量。
        // marginMm = 用户设定的余量阈值(mm)
        // 返回空字符串表示合格，否则返回警告信息
        private string CheckTcpXyzMargin(TxRobot robot, ITxRoboticLocationOperation loc, double marginMm)
        {
            try
            {
                // 获取点位的世界坐标变换矩阵
                TxTransformation baseTx = GetLocationTransform(loc);
                if (baseTx == null) return "";

                // 提取基准位置
                double[] basePos = ExtractTranslation(baseTx);
                if (basePos == null) return "";

                // 6个探测方向：+X, -X, +Y, -Y, +Z, -Z
                double[][] directions = new double[][]
                {
                    new double[] { 1, 0, 0 }, new double[] { -1, 0, 0 },
                    new double[] { 0, 1, 0 }, new double[] { 0, -1, 0 },
                    new double[] { 0, 0, 1 }, new double[] { 0, 0, -1 }
                };
                string[] dirNames = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };

                double minMargin = double.MaxValue;
                string minDir = "";

                for (int d = 0; d < 6; d++)
                {
                    double maxDist = ProbeDirectionMargin(robot, baseTx, basePos, directions[d]);
                    if (maxDist < minMargin)
                    {
                        minMargin = maxDist;
                        minDir = dirNames[d];
                    }
                }

                if (minMargin < double.MaxValue && minMargin < marginMm)
                {
                    return $"TCP余量 {minMargin:F0}mm ({minDir}方向) < 阈值 {marginMm:F0}mm";
                }
            }
            catch (Exception ex)
            {
                Log($"    TCP余量检查异常: {ex.Message}", "WARN");
            }
            return "";
        }

        /// <summary>
        /// 沿指定方向探测 TCP 的最大可移动距离（mm）
        /// 使用粗搜 + 精搜策略：先以大步长快速定位边界，再以小步长精确查找
        /// </summary>
        private double ProbeDirectionMargin(TxRobot robot, TxTransformation baseTx, double[] basePos, double[] dir)
        {
            // 粗搜：步长 50mm，最远探测 2000mm
            double lastOk = 0;
            double coarseStep = 50;
            double maxProbe = 2000;

            for (double dist = coarseStep; dist <= maxProbe; dist += coarseStep)
            {
                if (TestIkAtOffset(robot, baseTx, basePos, dir, dist))
                    lastOk = dist;
                else
                    break;  // 首次失败即停止粗搜
            }

            // 精搜：在 [lastOk, lastOk + coarseStep] 区间内以 5mm 步长细化
            double fineStep = 5;
            double fineEnd = Math.Min(lastOk + coarseStep, maxProbe);
            for (double dist = lastOk + fineStep; dist <= fineEnd; dist += fineStep)
            {
                if (TestIkAtOffset(robot, baseTx, basePos, dir, dist))
                    lastOk = dist;
                else
                    break;
            }

            return lastOk;
        }

        /// <summary>测试 TCP 偏移后 IK 是否有解</summary>
        private bool TestIkAtOffset(TxRobot robot, TxTransformation baseTx, double[] basePos,
                                     double[] dir, double dist)
        {
            try
            {
                // 构造偏移后的目标变换矩阵
                TxTransformation offsetTx = null;
                // 策略1：拷贝构造函数
                try { offsetTx = new TxTransformation(baseTx); } catch { }
                // 策略2：Clone
                if (offsetTx == null)
                    try { dynamic db = baseTx; offsetTx = db.Clone() as TxTransformation; } catch { }
                // 策略3：新建单位阵后手动复制
                if (offsetTx == null)
                {
                    offsetTx = new TxTransformation();
                    dynamic src = baseTx;
                    dynamic dst = offsetTx;
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            try { dst[r, c] = Convert.ToDouble(src[r, c]); } catch { }
                }

                dynamic dt = offsetTx;
                double newX = basePos[0] + dir[0] * dist;
                double newY = basePos[1] + dir[1] * dist;
                double newZ = basePos[2] + dir[2] * dist;

                // 写入平移分量（策略链）
                bool written = false;
                try { dt[0, 3] = newX; dt[1, 3] = newY; dt[2, 3] = newZ; written = true; } catch { }
                if (!written)
                    try { dt.X = newX; dt.Y = newY; dt.Z = newZ; written = true; } catch { }
                if (!written) return false;

                // 使用 DoesInverseExist 快速判断 IK 可行性
                var invData = new TxRobotInverseData(offsetTx);
                return robot.DoesInverseExist(invData);
            }
            catch { return false; }
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

            // ── 诊断：场景中所有同名机器人是否有多台？──────────────────────
            // 如果有，使用 .Robot 属性返回的可能跟用户期望的不是同一台
            try
            {
                if (doc != null)
                {
                    var allRobots = doc.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(TxRobot)));
                    Log($"  [诊断] 场景中机器人总数: {allRobots.Count}");

                    // 按名字分组
                    var byName = new Dictionary<string, List<TxRobot>>();
                    foreach (ITxObject ro in allRobots)
                    {
                        if (ro is TxRobot r)
                        {
                            string n = r.Name ?? "";
                            if (!byName.ContainsKey(n)) byName[n] = new List<TxRobot>();
                            byName[n].Add(r);
                        }
                    }

                    foreach (var kv in byName)
                    {
                        if (kv.Value.Count > 1)
                        {
                            Log($"  [诊断] ⚠ 发现 {kv.Value.Count} 台同名机器人 '{kv.Key}'，详情：", "WARN");
                            for (int i = 0; i < kv.Value.Count; i++)
                            {
                                var r = kv.Value[i];
                                string baseName = "?";
                                string parentName = "?";
                                string poseDesc = "?";
                                int hash = 0;
                                try { hash = r.GetHashCode(); } catch { }
                                try { dynamic d = r; parentName = d.Parent?.Name?.ToString() ?? "(root)"; } catch { }
                                try { dynamic d = r; baseName = d.Baseframe?.Name?.ToString() ?? "?"; } catch { }
                                try
                                {
                                    var joints = r.Joints;
                                    if (joints != null && joints.Count > 0)
                                    {
                                        var sb = new System.Text.StringBuilder();
                                        int ji = 0;
                                        foreach (object jx in joints)
                                        {
                                            if (ji >= 6) break;
                                            try { dynamic dj = jx; sb.Append((dj.CurrentValue * 180.0 / Math.PI).ToString("F0")); }
                                            catch { sb.Append("?"); }
                                            sb.Append(",");
                                            ji++;
                                        }
                                        poseDesc = "[" + sb.ToString().TrimEnd(',') + "]°";
                                    }
                                }
                                catch { }
                                Log($"    #{i}: HashCode={hash}, Parent={parentName}, Base={baseName}, J1-6={poseDesc}");
                            }
                        }
                        else
                        {
                            Log($"  [诊断] 机器人: '{kv.Key}' x1");
                        }
                    }
                }
            }
            catch (Exception exDiag) { Log($"  [诊断] 机器人枚举异常: {exDiag.Message}", "WARN"); }

            // 方式1：直接访问 Robot 属性（TxRoboticOperation / TxWeldOperation 均有此属性）
            try
            {
                dynamic dop = operation;
                var r = dop.Robot as TxRobot;
                if (r != null)
                {
                    // 用 hashcode 帮助区分同名机器人是否是同一个对象实例
                    Log($"  关联机器人(.Robot): '{r.Name}' (HashCode={r.GetHashCode()})");
                    return r;
                }
            }
            catch { }

            // 方式2：Device 属性（部分 PS 版本）
            try { dynamic dop = operation; var r = dop.Device as TxRobot; if (r != null) { Log($"  关联机器人(.Device): {r.Name}", "DEBUG"); return r; } } catch { }

            // 方式3：RobotDevice 属性
            try { dynamic dop = operation; var r = dop.RobotDevice as TxRobot; if (r != null) { Log($"  关联机器人(.RobotDevice): {r.Name}", "DEBUG"); return r; } } catch { }

            // 方式4：向上遍历 Parent 链，找到 TxRobot 类型节点
            try
            {
                dynamic cur = operation;
                for (int depth = 0; depth < 10; depth++)
                {
                    object parent = null;
                    try { parent = cur.Parent; } catch { break; }
                    if (parent == null) break;
                    if (parent is TxRobot rp) { Log($"  关联机器人(Parent链 depth={depth}): {rp.Name}", "DEBUG"); return rp; }
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

            // 方式6：若操作是 TxCompoundOperation，遍历子操作获取 Robot
            // 解决场景：TxCompoundOperation 本身无 Robot，但子 TxWeldOperation 有
            if (operation is TxCompoundOperation compound)
            {
                try
                {
                    var children = compound.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
                    if (children != null)
                    {
                        foreach (ITxObject child in children)
                        {
                            if (child == null) continue;
                            try
                            {
                                dynamic dc = child;
                                var r = dc.Robot as TxRobot;
                                if (r != null) { Log($"  关联机器人(方式6 子操作.Robot [{child.Name}]): {r.Name}"); return r; }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            Log("  所有方式均未能找到关联机器人", "WARN");
            return null;
        }

        private static ITxObject FindOperationByName(TxDocument doc, string name)
        {
            var kids = doc.OperationRoot.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
            return FindOpRecursive(kids, name, 0);
        }

        /// <summary>
        /// 递归查找操作，优先返回 ITxRoboticOperation（有 .Robot 属性的具体操作），
        /// 而非同名的 TxCompoundOperation 父节点。
        /// 解决场景：TxCompoundOperation 与子 TxWeldOperation 同名时，
        ///           从 Compound 获取不到 Robot 导致检查失败。
        /// </summary>
        private static ITxObject FindOpRecursive(TxObjectList nodes, string name, int depth)
        {
            if (nodes == null || depth > 20) return null;
            ITxObject compoundFallback = null;  // 记录同名 Compound，作为后备

            foreach (ITxObject obj in nodes)
            {
                if (obj == null) continue;
                bool isOp = obj is ITxRoboticOperation || obj is TxCompoundOperation
                         || obj is ITxOperation || obj.GetType().Name.Contains("Operation");
                if (!isOp) continue;

                if (obj.Name == name)
                {
                    // 优先返回 ITxRoboticOperation（TxWeldOperation 等有 Robot 属性的类型）
                    if (obj is ITxRoboticOperation)
                        return obj;

                    // TxCompoundOperation 同名 → 先检查子级是否有同名 ITxRoboticOperation
                    if (obj is TxCompoundOperation co)
                    {
                        try
                        {
                            var sub = co.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject)));
                            if (sub != null)
                            {
                                foreach (ITxObject child in sub)
                                {
                                    if (child is ITxRoboticOperation && child.Name == name)
                                        return child;   // 找到同名子 RoboticOperation，优先返回
                                }
                            }
                        }
                        catch { }
                        compoundFallback = obj;   // 子级没有同名 RoboticOperation，记为后备
                    }
                    else
                    {
                        return obj;  // 其他操作类型直接返回
                    }
                }

                // 继续递归搜索子级
                TxObjectList sub2 = null;
                if (obj is TxCompoundOperation co2)
                    try { sub2 = co2.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))); } catch { }
                var found = FindOpRecursive(sub2, name, depth + 1);
                if (found != null) return found;
            }
            return compoundFallback;  // 没有更优选择时返回 Compound
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

                bool savedRedraw = _grid.Redraw;
                _grid.Redraw = false;
                try
                {
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
                }
                finally
                {
                    _grid.Redraw = savedRedraw;
                    _grid.Refresh();
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
            Log($"开始检查任务: {opName}");

            // 保存当前拾取的对象实例引用（仅当名字匹配时使用）
            // 在 BeginInvoke 闭包外捕获，避免后续 _pickedOperation 被改变后影响检查
            ITxObject preferredOpForCheck =
                (_pickedOperation != null && _pickedOperation.Name == opName) ? _pickedOperation : null;

            // 真实进度：在 CheckReachabilityViaPS 里通过 _progressCallback 回调上来
            // 不再用假 timer 累加进度（旧实现 60ms × N 等待，再一次性同步检查）
            //
            // 注意：检查仍在 UI 线程同步执行（PS API 大多不是线程安全的），
            // 但通过 BeginInvoke 让进度更新和重绘穿插在每个点位之间，
            // UI 不会完全卡死，且最后一个点不会再有"延迟到结束才显示"的卡顿
            this.BeginInvoke(new Action(() =>
            {
                List<PathPointResult> results = null;
                try
                {
                    results = CheckReachabilityViaPS(opName, (done, total) =>
                    {
                        if (_tsProgress?.ProgressBar != null)
                        {
                            int pct = total > 0 ? Math.Min(done * 100 / total, 100) : 0;
                            _tsProgress.ProgressBar.Value = pct;
                        }
                        SetStatus($"检查中 [{opName}] {done}/{total}");
                        // 让进度条/状态栏立即刷新
                        Application.DoEvents();
                    }, preferredOpForCheck);
                }
                catch (Exception ex)
                {
                    Log($"检查任务异常: {ex.Message}", "ERR");
                    results = new List<PathPointResult>();
                }

                string robotName = (results != null && results.Count > 0) ? results[0].RobotName : "未知";
                var task = new RobotPathCheckTask
                {
                    RobotName = robotName,
                    PathName = opName,
                    CheckTime = DateTime.Now,
                    Results = results ?? new List<PathPointResult>()
                };
                _tasks.Add(task);
                _currentTask = task;
                RefreshGrid(task.Results);
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
                        $"近极限 {task.NearLimitCount} | 奇异 {task.SingularCount} | 临界 {task.CriticalCount} | " +
                        $"可达率 {task.ReachabilityRate:F1}%",
                        ClrSuccess);
                }
            }));
        }

        private void RefreshGrid(List<PathPointResult> results)
        {
            if (_grid == null) return;
            var filtered = ApplyFilter(results);

            // 关键性能优化：用 Redraw=false 包围批量赋值，避免每个单元格触发重绘
            // 52 行 × 14 列 = 728 次单元格写入，未关闭重绘时累计延迟可达数百毫秒
            bool savedRedraw = _grid.Redraw;
            _grid.Redraw = false;
            try
            {
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
                    string statusText =
                          r.Status == ReachabilityStatus.Reachable ? "正常"
                        : r.Status == ReachabilityStatus.Unreachable ? "不可达"
                        : r.Status == ReachabilityStatus.NearLimit ? "接近极限"
                        : r.Status == ReachabilityStatus.Singular ? "奇异"
                        : r.Status == ReachabilityStatus.Critical ? "临界"
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

                    // 行背景色：按整体状态着色
                    var rowStyle = _grid.Rows[row].Style ?? _grid.Styles.Add($"rs{row}");
                    System.Drawing.Color bg =
                          r.Status == ReachabilityStatus.Reachable ? TxClrRowOk.Color
                        : r.Status == ReachabilityStatus.Unreachable ? TxClrRowFail.Color
                        : r.Status == ReachabilityStatus.NearLimit ? TxClrRowWarn.Color
                        : r.Status == ReachabilityStatus.Singular ? TxClrRowSingular.Color
                        : r.Status == ReachabilityStatus.Critical ? TxClrRowCritical.Color
                        : (i % 2 == 0 ? SystemColors.Window : TxClrGridAlt.Color);
                    rowStyle.BackColor = bg;
                    _grid.Rows[row].Style = rowStyle;

                    // ── 单元格级染色：J1..J6 哪个轴有问题，染该单元格 ──
                    // 优先级：超限 > 奇异 > 近极限 > 临界
                    if (r.AxisFlags != null)
                    {
                        int[] jCols = { COL_J1, COL_J2, COL_J3, COL_J4, COL_J5, COL_J6 };
                        for (int ax = 0; ax < 6 && ax < r.AxisFlags.Length; ax++)
                        {
                            AxisFlag af = r.AxisFlags[ax];
                            if (af == AxisFlag.None) continue;

                            System.Drawing.Color cellBg;
                            System.Drawing.Color cellFg = System.Drawing.Color.Black;
                            if ((af & AxisFlag.OverLimit) != 0)
                            { cellBg = TxClrCellOver.Color; cellFg = System.Drawing.Color.White; }
                            else if ((af & AxisFlag.Singular) != 0)
                            { cellBg = TxClrCellSingular.Color; cellFg = System.Drawing.Color.White; }
                            else if ((af & AxisFlag.NearLimit) != 0)
                            { cellBg = TxClrCellNear.Color; }
                            else if ((af & AxisFlag.Critical) != 0)
                            { cellBg = TxClrCellCritical.Color; }
                            else continue;

                            var cs = _grid.Styles.Add($"cs{row}_{ax}");
                            cs.BackColor = cellBg;
                            cs.ForeColor = cellFg;
                            _grid.SetCellStyle(row, jCols[ax], cs);
                        }
                    }
                }
            }
            finally
            {
                // 恢复重绘并立即整体刷一次
                _grid.Redraw = savedRedraw;
                _grid.Refresh();
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
                      $"奇异 {t.SingularCount}  临界 {t.CriticalCount}  " +
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
                    $"# 总计:{_currentTask.TotalPoints}  可达:{_currentTask.ReachableCount}  不可达:{_currentTask.UnreachableCount}  近极限:{_currentTask.NearLimitCount}  奇异:{_currentTask.SingularCount}  临界:{_currentTask.CriticalCount}  可达率:{_currentTask.ReachabilityRate:F1}%",
                    "",
                    "序号,机器人名,操作名称,点名,点类型,状态,J1(°),J2(°),J3(°),J4(°),J5(°),J6(°),最小余量(°),备注"
                };
                foreach (var r in _currentTask.Results)
                {
                    string st =
                          r.Status == ReachabilityStatus.Reachable ? "可达"
                        : r.Status == ReachabilityStatus.Unreachable ? "不可达"
                        : r.Status == ReachabilityStatus.NearLimit ? "接近极限"
                        : r.Status == ReachabilityStatus.Singular ? "奇异"
                        : r.Status == ReachabilityStatus.Critical ? "临界"
                        : "未检查";
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
            Size = new System.Drawing.Size(480, 400);
            MinimumSize = new System.Drawing.Size(420, 340);
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
                Font = new System.Drawing.Font(SystemFonts.DefaultFont, FontStyle.Bold),
                ForeColor = new TxColor(0, 70, 127).Color,
                BackColor = new TxColor(235, 241, 250).Color
            };

            // 底部状态 + 按钮栏
            var btmPanel = new Panel { Dock = DockStyle.Bottom, Height = 36 };

            _lblStatus = new Label
            {
                Text = "使用控件箭头调整点位位置和姿态",
                AutoSize = false,
                Width = 260,
                Height = 22,
                Location = new System.Drawing.Point(6, 7),
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
                _btnClose.Location = new System.Drawing.Point(btmPanel.Width - 78, 5);
                _btnReset.Location = new System.Drawing.Point(btmPanel.Width - 156, 5);
            };
            _btnClose.Location = new System.Drawing.Point(392, 5);
            _btnReset.Location = new System.Drawing.Point(314, 5);

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