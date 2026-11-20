// AllocatorForm.cs  —  C# 7.3
// 标准 WinForms 设计器骨架：InitializeComponent() + 命名事件处理器(Load/FormClosed) + Dispose(components)。
// 布局：外层 AutoScroll Panel + 垂直 stack(TableLayoutPanel Dock=Top AutoSize)，内容超出滚动不裁切。
// 卡片 MkCard(GroupBox Dock=Top AutoSize)；网格/预览/日志用固定高卡。
// 操作选择：强类型 TxObjGridCtrl(OpGridHost)，ObjectInserted 实时剔除非焊接操作。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.ExportGun;

using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using ComboBox = System.Windows.Forms.ComboBox;
using CheckBox = System.Windows.Forms.CheckBox;
using TextBox = System.Windows.Forms.TextBox;
using RadioButton = System.Windows.Forms.RadioButton;
using Panel = System.Windows.Forms.Panel;

namespace TxTools.WeldSpotAllocator
{
    using Theme = TxTools.Common.FormUiKit.Theme;

    public class AllocatorForm : PickAwareTxForm
    {
        private static AllocatorForm _inst;
        private SynchronizationContext _ctx;
        private bool _inited;
        private bool _scaled;

        private readonly Size _design = new Size(840, 720);

        private List<OpData> _refOps = new List<OpData>();
        private List<OpData> _targetOps = new List<OpData>();
        private AllocPlan _plan;

        private readonly OpGridHost _refHost = new OpGridHost();
        private readonly OpGridHost _tgtHost = new OpGridHost();
        private Panel _refHostPanel, _tgtHostPanel;
        private Panel _scrollPanel;
        private TableLayoutPanel _stackPanel;

        private RadioButton _rbA, _rbB, _rbC;
        private ComboBox _cboFlip;
        private Button _btnPreview, _btnApply;
        private Label _lblRef, _lblTarget, _lblHint;
        private CheckBox _chkByDist, _chkByName, _chkRot, _chkVias, _chkParams, _chkConsume, _chkCountStrict, _chkPosOnly, _chkDiffFrame;
        private CheckBox _chkTargetWorld;
        private TxFrameEditBoxCtrl _referenceFramePicker, _targetFramePicker;
        private Control _frameOptions, _targetFrameField;
        private Button _btnExport;
        private NumericUpDown _numTol;
        private TextBox _txtPrefix, _txtMappedSuffix;
        private DataGridView _grid;
        private TextBox _log;
        private Control _logRegion;
        private Button _btnToggleLog;

        public static void ShowSingleton(SynchronizationContext ctx)
        {
            if (_inst == null || _inst.IsDisposed)
            {
                _inst = new AllocatorForm { _ctx = ctx };
                try { _inst.SemiModal = false; } catch { }
                _inst.Name = typeof(AllocatorForm).FullName;
                _inst.Show();
            }
            else
            {
                if (_inst.WindowState == FormWindowState.Minimized) _inst.WindowState = FormWindowState.Normal;
                _inst.Activate();
            }
        }

        public AllocatorForm()
        {
            InitializeComponent();

            // ⚠ 注意：本窗体 UI 为手写代码，请勿再用 VS 窗体设计器修改此文件，
            //   否则 VS 会重写 InitializeComponent()，导致界面空白/空引用。
            //   关键初始化放构造函数，InitializeComponent 仅保留最小存根。
            this.Load += new System.EventHandler(this.AllocatorForm_Load);
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.AllocatorForm_Closed);

            FormUiKit.InitStandardForm(this, "焊点分配 / 更新", _design,
                new Size(800, 640), sizable: true);
            try { SemiModal = false; } catch { }

            BuildUi();
            _txtMappedSuffix.Text = AllocPlan.DefaultMappedSuffix;
            _txtMappedSuffix.TextChanged += (s, e) =>
            {
                if (_plan == null || !_btnApply.Enabled) return;
                _plan.MappedSuffix = ReadMappedSuffix();
                FillGrid(_plan);
            };
        }

        // PS host 早期钩子（早于 WinForms Load）：UI 已在构造函数建好，此处只做网格初始化。
        public override void OnInitTxForm()
        {
            try { base.OnInitTxForm(); }
            catch (Exception ex) { Log("OnInitTxForm 基类异常：" + ex.Message); }
            EnsureInit();
        }

        private void EnsureInit()
        {
            if (_inited) return;
            // 防御：控件尚未创建时延后到 Load 事件（避免设计器覆盖后空引用）
            if (_refHostPanel == null || _tgtHostPanel == null || _log == null) return;
            _inited = true;
            try
            {
                _refHost.Init(_refHostPanel, IsWeldOp, DropLog, () => { _lblRef.Text = RefText(); InvalidatePreview(); }, Log);
                _tgtHost.Init(_tgtHostPanel, IsWeldOp, DropLog, () => { _lblTarget.Text = TgtText(); InvalidatePreview(); }, Log);
            }
            catch (Exception ex) { Log("网格初始化异常：" + ex.Message); }
        }

        // ── Windows Form Designer 最小存根（单文件形态）─────────────────────
        // VS 若重写此方法，仅影响以下空壳；真正 UI 在构造函数 BuildUi() 中构建。
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.SuspendLayout();
            //
            // AllocatorForm
            //
            this.Name = "AllocatorForm";
            this.ResumeLayout(false);
        }

        private void AllocatorForm_Load(object sender, EventArgs e)
        {
            EnsureInit();
            FormUiKit.ApplyDpiScaling(this, ref _scaled, _design);
        }

        private void AllocatorForm_Closed(object sender, FormClosedEventArgs e)
        {
            _inst = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        // ── 整体：可滚动垂直 stack ─────────────────────────────────────────
        private void BuildUi()
        {
            _scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = false, Padding = new Padding(8), BackColor = FormUiKit.WinBg };
            _stackPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = FormUiKit.WinBg };
            _stackPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++)
                _stackPanel.RowStyles.Add(i == 2 ? new RowStyle(SizeType.Absolute, 224) : i == 5
                    ? new RowStyle(SizeType.Percent, 100) : i == 6
                    ? new RowStyle(SizeType.Absolute, 50) : new RowStyle(SizeType.AutoSize));

            _stackPanel.Controls.Add(BuildModeCard(), 0, 0);
            _stackPanel.Controls.Add(BuildHint(), 0, 1);
            var dataCard = BuildDataCard();
            _stackPanel.Controls.Add(dataCard, 0, 2);
            _stackPanel.Controls.Add(BuildOptionCard(), 0, 3);
            _stackPanel.Controls.Add(BuildActionRow(), 0, 4);
            var previewCard = BuildPreviewCard();
            _stackPanel.Controls.Add(previewCard, 0, 5);
            var logCard = BuildLogCard();
            _stackPanel.Controls.Add(logCard, 0, 6);
            foreach (Control card in new[] { dataCard, previewCard, logCard })
            {
                card.AutoSize = false;
                card.Dock = DockStyle.Fill;
            }

            _scrollPanel.Controls.Add(_stackPanel);
            Controls.Add(_scrollPanel);
            SyncModeUi();
        }

        // ── 卡片工厂（ColoredGroupBox 自绘标题/边框，统一走套件主题）────
        private static GroupBox MkCard(string title, Control inner)
        {
            inner.Dock = DockStyle.Top;
            var g = new FormUiKit.ColoredGroupBox
            {
                Text = title,
                TitleColor = Theme.CardTitle,
                BorderColor = FormUiKit.CardBorder,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 6),
                Font = FormUiKit.BoldFont,
                BackColor = FormUiKit.CardBack
            };
            g.Controls.Add(inner);
            return g;
        }
        private static GroupBox MkFixedCard(string title, int height, Control inner)
        {
            inner.Dock = DockStyle.Fill;
            var g = new FormUiKit.ColoredGroupBox
            {
                Text = title,
                TitleColor = Theme.CardTitle,
                BorderColor = FormUiKit.CardBorder,
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = height,
                Padding = new Padding(8, 4, 8, 8),
                Margin = new Padding(0, 0, 0, 6),
                Font = FormUiKit.BoldFont,
                BackColor = FormUiKit.CardBack
            };
            g.Controls.Add(inner);
            return g;
        }

        private Control BuildModeCard()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            flow.Dock = DockStyle.Top;
            flow.Controls.Add(new Label { Text = "模式：", AutoSize = true, Font = FormUiKit.BoldFont, Margin = new Padding(3, 6, 6, 0) });
            _rbA = NewRb("A 位置更新"); _rbA.Checked = true;
            _rbB = NewRb("B 新焊点分配");
            _rbC = NewRb("C 对称分配");
            _rbA.CheckedChanged += (s, e) => { if (_rbA.Checked) SyncModeUi(); };
            _rbB.CheckedChanged += (s, e) => { if (_rbB.Checked) SyncModeUi(); };
            _rbC.CheckedChanged += (s, e) => { if (_rbC.Checked) SyncModeUi(); };
            flow.Controls.Add(_rbA); flow.Controls.Add(_rbB); flow.Controls.Add(_rbC);
            return flow;
        }

        private Control BuildHint()
        {
            _lblHint = new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Theme.TextFaint, Font = FormUiKit.BaseFont, Margin = new Padding(3, 0, 3, 6), BackColor = Color.Transparent };
            return _lblHint;
        }

        private Control BuildDataCard()
        {
            var two = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = FormUiKit.WinBg };
            two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _refHostPanel = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
            _tgtHostPanel = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
            _lblRef = new Label { Text = RefText(), AutoSize = true, Font = FormUiKit.BaseFont, Margin = new Padding(3, 4, 3, 2), BackColor = Color.Transparent };
            _lblTarget = new Label { Text = TgtText(), AutoSize = true, Font = FormUiKit.BaseFont, Margin = new Padding(3, 4, 3, 2), BackColor = Color.Transparent };

            two.Controls.Add(BuildSide("参考集", _refHostPanel, _lblRef), 0, 0);
            two.Controls.Add(BuildSide("目标集", _tgtHostPanel, _lblTarget), 1, 0);

            var card = MkCard("操作选择（仅焊接操作）", two);
            two.Dock = DockStyle.Fill;
            return card;
        }

        private Control BuildSide(string title, Panel hostPanel, Label info)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(2) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            t.Controls.Add(new Label { Text = title, AutoSize = true, Font = FormUiKit.BoldFont, Margin = new Padding(3, 2, 3, 2), BackColor = Color.Transparent }, 0, 0);
            t.Controls.Add(hostPanel, 0, 1);
            t.Controls.Add(info, 0, 2);
            return t;
        }

        private Control BuildOptionCard()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };

            _chkByDist = NewChk("焊点距离匹配"); _chkByDist.Checked = true;
            _chkByName = NewChk("焊点名匹配");
            _numTol = FormUiKit.MkNumeric(0, 100000, 5);
            _txtPrefix = FormUiKit.MkTextBox("", 110);
            _chkDiffFrame = NewChk("不同车件参考系");
            flow.Controls.Add(_chkByDist);
            flow.Controls.Add(LabeledPair("距离阈值 mm", _numTol));
            flow.Controls.Add(_chkByName);
            flow.Controls.Add(LabeledPair("剥前缀(名)", _txtPrefix));
            flow.Controls.Add(_chkDiffFrame);
            AddBreak(flow);
            _frameOptions = BuildFrameOptions();
            flow.Controls.Add(_frameOptions);
            flow.SetFlowBreak(_frameOptions, true);

            _chkRot = NewChk("复制旋转姿态");
            _chkRot.Checked = true;
            _cboFlip = FormUiKit.MkComboBox(70);
            _cboFlip.Items.AddRange(new object[] { "None", "X", "Y", "Z" }); _cboFlip.SelectedIndex = 0;
            _chkVias = NewChk("镜像过渡点(C)");
            _chkParams = NewChk("复制轨迹参数");
            _chkParams.Checked = true;
            _chkConsume = NewChk("挪动焊点(移入新轨迹,不复制)"); _chkConsume.Checked = true;
            _chkCountStrict = NewChk("操作数量严格(A)"); _chkCountStrict.Checked = true;
            _chkPosOnly = NewChk("仅更新位置(A)");
            _txtMappedSuffix = FormUiKit.MkTextBox("", 110);
            foreach (var option in new[] { _chkByDist, _chkByName, _chkRot, _chkParams, _chkVias, _chkConsume,
                                           _chkDiffFrame, _chkCountStrict, _chkPosOnly })
            {
                option.CheckedChanged += (s, e) =>
                {
                    SyncMatchOptions();
                    InvalidatePreview();
                };
            }
            _numTol.ValueChanged += (s, e) => InvalidatePreview();
            _txtPrefix.TextChanged += (s, e) => InvalidatePreview();
            _cboFlip.SelectedIndexChanged += (s, e) => InvalidatePreview();
            flow.Controls.Add(_chkRot);
            flow.Controls.Add(LabeledPair("镜像翻轴(X前后/Y左右/Z上下)", _cboFlip));
            flow.Controls.Add(_chkVias);
            flow.Controls.Add(_chkParams);
            AddBreak(flow);
            flow.Controls.Add(_chkConsume);
            flow.Controls.Add(LabeledPair("新轨迹后缀", _txtMappedSuffix));
            flow.Controls.Add(_chkCountStrict);
            flow.Controls.Add(_chkPosOnly);
            return MkCard("匹配条件 + 分配后处理", flow);
        }

        private Control BuildFrameOptions()
        {
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            _referenceFramePicker = CreateFramePicker("参考车件坐标");
            _targetFramePicker = CreateFramePicker("目标车件坐标");
            _targetFrameField = LabeledPair("目标车件坐标", _targetFramePicker);
            _chkTargetWorld = NewChk("目标使用世界坐标");
            _chkTargetWorld.CheckedChanged += (s, e) =>
            {
                SyncMatchOptions();
                InvalidatePreview();
            };
            row.Controls.Add(LabeledPair("参考车件坐标", _referenceFramePicker));
            row.Controls.Add(_targetFrameField);
            row.Controls.Add(_chkTargetWorld);
            return row;
        }

        private TxFrameEditBoxCtrl CreateFramePicker(string label)
        {
            var picker = new TxFrameEditBoxCtrl { Width = 150, Height = 28, Font = Font, AccessibleName = label };
            picker.Load += (s, e) => picker.EnableRelativeToWorkingFrameCoordinates = false;
            picker.ValidFrameSet += (s, e) => InvalidatePreview();
            picker.InvalidFrameSet += (s, e) => InvalidatePreview();
            PickFocus.Wire(picker);
            return picker;
        }

        private static double[] ReadFrameOverride(TxFrameEditBoxCtrl picker)
        {
            var location = picker.GetLocation();
            if (location == null && string.IsNullOrWhiteSpace(picker.Text)) return null;
            if (location == null || !location.IsLocationTransformation)
                throw new InvalidOperationException($"请在当前工程重新选取{picker.AccessibleName}，或清空拾取框以自动识别。");
            return PsReader.TxToArr(location);
        }

        private void ClearFrameOverrides()
        {
            if (_referenceFramePicker.IsHandleCreated) _referenceFramePicker.Text = "";
            if (_targetFramePicker.IsHandleCreated) _targetFramePicker.Text = "";
            _chkTargetWorld.Checked = false;
        }

        private Control BuildActionRow()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 4) };
            _btnPreview = NewBtn("预览匹配", Theme.BtnPrimary); _btnPreview.Click += (s, e) => DoPreview();
            _btnApply = NewBtn("执行写入", Theme.BtnSecondary); _btnApply.Enabled = false; _btnApply.Click += (s, e) => DoApply();
            _btnExport = NewBtn("导出Excel", Theme.BtnExport); _btnExport.Click += (s, e) => DoExport();
            var bSwap = NewBtn("交换参考/目标", Theme.BtnMuted); bSwap.Click += (s, e) => DoSwap();
            var bClearAll = NewBtn("清空列表", Theme.BtnMuted);
            bClearAll.Click += (s, e) => { _refHost.Clear(); _tgtHost.Clear(); ClearFrameOverrides(); _lblRef.Text = RefText(); _lblTarget.Text = TgtText(); InvalidatePreview(); };
            flow.Controls.Add(_btnPreview); flow.Controls.Add(_btnApply); flow.Controls.Add(_btnExport); flow.Controls.Add(bSwap); flow.Controls.Add(bClearAll);
            _btnToggleLog = NewBtn("日志", Theme.BtnMuted);
            _btnToggleLog.Click += (s, e) =>
            {
                if (_logRegion != null && PluginLog.Enabled)
                    PluginLog.SetVisible(_logRegion, !_logRegion.Visible);
            };
            flow.Controls.Add(_btnToggleLog);
            return flow;
        }

        private Control BuildPreviewCard()
        {
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            _grid.Columns.Add("op", "新轨迹");
            _grid.Columns.Add("refn", "参考焊点");
            _grid.Columns.Add("tgt", "待分配焊点");
            _grid.Columns.Add("by", "依据");
            _grid.Columns.Add("dist", "距离mm");
            return MkFixedCard("预览结果", 260, _grid);
        }

        private Control BuildLogCard()
        {
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = FormUiKit.MonoFont, BackColor = Theme.LogBg, ForeColor = Theme.LogText };
            var card = MkFixedCard("日志", 70, _log);
            _logRegion = card;
            PluginLog.Register(card, false, _btnToggleLog);
            return card;
        }

        // 工厂（统一收编到 FormUiKit）
        private static RadioButton NewRb(string t) => FormUiKit.MkRadio(t);
        private static Button NewBtn(string t, Color bg) => FormUiKit.MkFuncButton(t, bg);
        private static CheckBox NewChk(string t) => FormUiKit.MkCheckBox(t);
        private static void AddBreak(FlowLayoutPanel f) { var sep = new Label { AutoSize = false, Width = 1, Height = 1, Margin = new Padding(0) }; f.Controls.Add(sep); f.SetFlowBreak(sep, true); }
        private static Control LabeledPair(string text, Control c)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3, 0, 12, 0), WrapContents = false };
            f.Controls.Add(FormUiKit.MkFieldLabel(text));
            f.Controls.Add(c);
            return f;
        }

        private AllocMode Mode => _rbB.Checked ? AllocMode.NewSpot : (_rbC.Checked ? AllocMode.Symmetric : AllocMode.UpdatePosition);
        private string RefText() => $"参考操作 {_refHost.Count} 条";
        private string TgtText() => $"目标操作 {_tgtHost.Count} 条";
        private string ReadMappedSuffix() => string.IsNullOrWhiteSpace(_txtMappedSuffix.Text)
            ? AllocPlan.DefaultMappedSuffix : _txtMappedSuffix.Text.Trim();

        private void SyncModeUi()
        {
            bool assign = Mode != AllocMode.UpdatePosition;
            bool sym = Mode == AllocMode.Symmetric;
            _chkRot.Enabled = assign; _chkParams.Enabled = assign; _chkConsume.Enabled = assign; _chkDiffFrame.Enabled = assign;
            _txtMappedSuffix.Enabled = assign;
            _chkVias.Enabled = sym;
            _chkCountStrict.Enabled = !assign; _chkPosOnly.Enabled = !assign;
            SyncMatchOptions();
            InvalidatePreview();
        }

        private void SyncMatchOptions()
        {
            _txtPrefix.Enabled = _chkByName.Checked;
            _numTol.Enabled = _chkByDist.Checked || !_chkByName.Checked;
            _cboFlip.Enabled = Mode == AllocMode.Symmetric && (_chkRot.Checked || _chkVias.Checked);
            bool differentFrames = Mode != AllocMode.UpdatePosition && _chkDiffFrame.Checked;
            bool showFrames = differentFrames || Mode == AllocMode.Symmetric;
            _frameOptions.Visible = showFrames;
            _targetFrameField.Visible = differentFrames;
            _chkTargetWorld.Visible = differentFrames;
            _targetFramePicker.Enabled = differentFrames && !_chkTargetWorld.Checked;
            if (differentFrames)
                _lblHint.Text = "默认按导插枪规则读取各操作的自身坐标。需要统一某一侧时，在当前工程选取该侧坐标，再预览匹配。";
            else if (Mode == AllocMode.Symmetric)
                _lblHint.Text = "选对侧待分配操作，并在当前工程确认参考车件坐标后预览。对称面为参考车件的局部 XZ 平面。";
            else if (Mode == AllocMode.NewSpot)
                _lblHint.Text = "选参考操作和待分配操作后预览。两侧车件位置或轴向不同时，启用「不同车件参考系」。";
            else
                _lblHint.Text = "选旧版和新版焊接操作后预览。将旧版焊点更新到新版坐标，保留分配。";
        }

        private void InvalidatePreview()
        {
            _plan = null;
            _btnApply.Enabled = false;
            _grid.Rows.Clear();
        }

        // ── 数据 ──────────────────────────────────────────────────────────
        private static bool IsWeldOp(ITxObject o)
        {
            string tn = o.GetType().Name;
            return tn == "TxWeldOperation" || (tn.Contains("Weld") && tn.Contains("Operation") && !tn.Contains("Location"));
        }
        private void DropLog(ITxObject o) => Log($"⚠ 已剔除非焊接操作：{SafeName(o)}");

        private List<OpData> ReadHost(OpGridHost h, bool readSpotFrames)
        {
            var res = new List<OpData>();
            foreach (var o in h.GetObjects())
            {
                var od = SpotReader.ReadOneWeldOp(o, Log, readSpotFrames);
                if (od != null && (od.Spots.Count > 0 || od.Vias.Count > 0)) res.Add(od);
                else Log($"⚠ 跳过非焊接/空操作：{SafeName(o)}");
            }
            return res;
        }

        private bool ResolveOperationFrames(IEnumerable<OpData> operations, string side)
        {
            foreach (var operation in operations)
            {
                if (operation.Spots.Count == 0) continue;
                var info = new OperationInfo { Name = operation.Name, PsObject = operation.Raw };
                PsReader.FillPoints(info, PointType.All, useMfg: true, log: Log);
                var frame = PsReader.ResolveOperationRefFrame(info, fallbackToWorld: true, log: Log);
                operation.OperationFrame = frame.Matrix;
                operation.OperationFrameName = frame.Name;
                if (frame.NeedsUserChoice)
                {
                    var chosen = ExportGunForm.SelectOperationRefFrame(this, frame, $"{side} · {operation.Name}");
                    if (chosen == null) return false;
                    operation.OperationFrameName = chosen.Item1;
                    operation.OperationFrame = chosen.Item2;
                }
                Log($"[坐标] {side}操作「{operation.Name}」使用{operation.OperationFrameName}");
            }
            return true;
        }

        private void DoPreview()
        {
            InvalidatePreview();
            try
            {
                _lblRef.Text = RefText(); _lblTarget.Text = TgtText();
                var mode = Mode;
                var cfg = new MatchSettings
                {
                    ByDistance = _chkByDist.Checked,
                    ByName = _chkByName.Checked,
                    MaxDistMm = (double)_numTol.Value,
                    CountStrict = _chkCountStrict.Checked,
                    DiffFrame = mode != AllocMode.UpdatePosition && _chkDiffFrame.Checked,
                    Names = new NameOptions { StripPrefix = string.IsNullOrWhiteSpace(_txtPrefix.Text) ? null : _txtPrefix.Text.Trim() }
                };
                if (cfg.DiffFrame || mode == AllocMode.Symmetric)
                    cfg.ReferenceFrame = ReadFrameOverride(_referenceFramePicker);
                if (cfg.DiffFrame)
                {
                    cfg.TargetFrame = _chkTargetWorld.Checked ? PsReader.TxToArr(new TxTransformation())
                        : ReadFrameOverride(_targetFramePicker);
                    if (_chkTargetWorld.Checked) cfg.TargetFrameSource = "世界坐标";
                }
                _refOps = ReadHost(_refHost, mode == AllocMode.Symmetric && !cfg.DiffFrame && cfg.ReferenceFrame == null);
                _targetOps = ReadHost(_tgtHost, readSpotFrames: false);
                if (cfg.DiffFrame)
                {
                    if (cfg.ReferenceFrame == null && !ResolveOperationFrames(_refOps, "参考侧")) return;
                    if (cfg.TargetFrame == null && !ResolveOperationFrames(_targetOps, "目标侧")) return;
                }
                if (!cfg.ByDistance && !cfg.ByName) { Log("⚠ 未勾任何匹配条件，按仅距离处理"); cfg.ByDistance = true; }

                _plan = SpotMatchEngine.Build(mode, _refOps, _targetOps, cfg, (FlipAxis)_cboFlip.SelectedIndex,
                    _chkVias.Checked, _chkRot.Checked, _chkParams.Checked, _chkConsume.Checked, Log);
                _plan.MappedSuffix = ReadMappedSuffix();

                FillGrid(_plan);
                foreach (var w in _plan.Warnings) Log("⚠ " + w);
                _btnApply.Enabled = _plan.TotalMatches > 0;
                Log($"预览：匹配 {_plan.TotalMatches} 点，警告 {_plan.Warnings.Count} 条");
            }
            catch (Exception ex)
            {
                Log("预览异常：" + ex.Message);
                MessageBox.Show(ex.Message, "无法预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void FillGrid(AllocPlan plan)
        {
            _grid.Rows.Clear();
            foreach (var om in plan.OpMatches)
            {
                string opName = plan.Mode == AllocMode.UpdatePosition ? (om.TargetOp?.Name ?? om.RefOp?.Name) : plan.GetMappedName(om.RefOp);
                foreach (var mt in om.Matches)
                {
                    string basis = mt.By == MatchBy.Name ? "名称" : mt.By == MatchBy.NameAndPosition ? "名称 + 距离" : "距离";
                    if (mt.Mirrored) basis += plan.DiffFrame ? " · 局部坐标" : " · 镜像";
                    if (double.IsNaN(mt.Dist)) basis += " · 无可比距离";
                    if (plan.CopyRotation && mt.RotationInTargetWorld == null) basis += " · 姿态待补全";
                    int index = _grid.Rows.Add(opName, mt.Ref?.Name, mt.Target?.Name, basis,
                        double.IsNaN(mt.Dist) ? "—" : mt.Dist.ToString("F1"));
                    if (mt.FrameDetails != null) _grid.Rows[index].Cells[3].ToolTipText = mt.FrameDetails;
                }
            }
        }

        private void DoApply()
        {
            var plan = _plan;
            if (plan == null || !_btnApply.Enabled) { Log("请先预览"); return; }
            if (plan.WriteBlockers.Count > 0)
            {
                string details = string.Join("\n", plan.WriteBlockers.Take(5));
                if (plan.WriteBlockers.Count > 5) details += $"\n共 {plan.WriteBlockers.Count} 项，完整明细见日志。";
                MessageBox.Show(details + "\n请补齐所需车件坐标或检查点位姿态后，重新预览。", "暂不能执行写入",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string assignment = plan.Mode == AllocMode.UpdatePosition ? "" :
                $"\n新轨迹后缀为「{plan.MappedSuffix}」，将{(plan.ConsumeTargets ? "移动" : "复制")}匹配的目标点位。";
            var r = MessageBox.Show($"将写入 {plan.TotalMatches} 处更改（{plan.Mode}）。{assignment}\n可用 PS 撤销回退。继续？",
                "确认执行", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK || !ReferenceEquals(_plan, plan)) return;
            _btnApply.Enabled = false;
            try
            {
                var rep = SpotWriter.ApplyPlan(plan, _chkPosOnly.Checked, Log);
                foreach (var e in rep.Errors) Log("✗ " + e);
                Log("完成：" + rep);
                MessageBox.Show(rep.ToString(), "执行完成", MessageBoxButtons.OK, rep.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("执行异常：" + ex.Message); }
        }

        private void DoSwap()
        {
            var a = _refHost.GetObjects();
            var b = _tgtHost.GetObjects();
            _refHost.Clear(); _tgtHost.Clear();
            foreach (var o in b) _refHost.Append(o);
            foreach (var o in a) _tgtHost.Append(o);
            ClearFrameOverrides();
            _lblRef.Text = RefText(); _lblTarget.Text = TgtText();
            InvalidatePreview();
            Log($"已交换参考/目标：参考 {_refHost.Count} 条 ↔ 目标 {_tgtHost.Count} 条");
        }

        // 启动 Excel（late-bound COM，免引用 Interop），新建空白工作簿，注入预览表数据
        private void DoExport()
        {
            if (_grid.Rows.Count == 0) { Log("无匹配结果，请先「预览匹配」"); return; }
            object excel = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Excel.Application");
                if (t == null) { Log("未检测到 Excel（ProgID Excel.Application 缺失）"); return; }
                excel = Activator.CreateInstance(t);
                dynamic app = excel;
                app.Visible = true;
                app.DisplayAlerts = false;

                dynamic wb = app.Workbooks.Add();
                dynamic ws = wb.ActiveSheet;

                int cols = _grid.Columns.Count;
                for (int c = 0; c < cols; c++) ws.Cells[1, c + 1] = _grid.Columns[c].HeaderText;

                int r = 2;
                foreach (DataGridViewRow gr in _grid.Rows)
                {
                    if (gr.IsNewRow) continue;
                    for (int c = 0; c < cols; c++)
                        ws.Cells[r, c + 1] = gr.Cells[c].Value == null ? "" : gr.Cells[c].Value.ToString();
                    r++;
                }

                try { ws.Range[ws.Cells[1, 1], ws.Cells[1, cols]].Font.Bold = true; ws.Columns.AutoFit(); } catch { }
                Log($"已导出 {r - 2} 行到 Excel（新建工作簿）");
            }
            catch (Exception ex)
            {
                Log("导出 Excel 异常：" + ex.Message);
                try { if (excel != null) ((dynamic)excel).Visible = true; } catch { }
            }
        }

        private static string SafeName(ITxObject o) { try { dynamic d = o; return (d.Name as string) ?? o.GetType().Name; } catch { return o.GetType().Name; } }
        private void Log(string s)
        {
            if (_log.InvokeRequired) { _log.BeginInvoke((Action)(() => Log(s))); return; }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine);
        }
    }
}
