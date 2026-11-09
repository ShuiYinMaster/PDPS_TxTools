// WeldGunWizardForm.cs — C# 7.3
// 布局：AutoScroll Panel + 垂直 stack(TableLayoutPanel Dock=Top AutoSize)
// 卡片 MkCard(GroupBox AutoSize) / MkFixedCard(固定高)
// 参考 AllocatorForm.cs 风格

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.WeldGunDefiner.Core;
using Theme = TxTools.Common.FormUiKit.Theme;
using TxTools.WeldGunDefiner.Math;
using TxTools.Common;

using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using Panel = System.Windows.Forms.Panel;
using TextBox = System.Windows.Forms.TextBox;
using CheckBox = System.Windows.Forms.CheckBox;

namespace TxTools.WeldGunDefiner.UI
{
    public class WeldGunWizardForm : PickAwareTxForm
    {
        // ── 单例 ──────────────────────────────────────────────────────────
        private static WeldGunWizardForm _inst;
        public static void ShowSingleton()
        {
            if (_inst == null || _inst.IsDisposed) { _inst = new WeldGunWizardForm(); _inst.Show(); }
            else { if (_inst.WindowState == FormWindowState.Minimized) _inst.WindowState = FormWindowState.Normal; _inst.Activate(); }
        }

        // ── 数据模型 & 服务 ──────────────────────────────────────────────
        private readonly WeldGunModel _model = new WeldGunModel();
        private WeldGunService _service;

        // ── Step0：焊枪本体 + 建模检测 ──────────────────────────────────
        private Label _lblModelingStatus;
        private Button _btnEnableModeling;
        private CheckBox _chkReuseLinks;
        private bool _restoringSetup;

        // ── Step1：铰点 ──────────────────────────────────────────────────
        private TxObjEditBoxCtrl _gridO, _gridA, _gridC;
        private TxObjEditBoxCtrl _gridStaticTip, _gridMovingTip;
        private TxFrameEditBoxCtrl _tcpEditBox;
        private TxFrameEditBoxCtrl _baseEditBox;
        private TxTransformation _tcpPickedLocation;   // TCP点选位置(若非已有Frame,生成时建TCPF用)

        // ── Step1：Link 几何体 ───────────────────────────────────────────
        private TxObjGridCtrl _gridFixedLink, _gridInputLink, _gridCouplerLink, _gridOutputLink, _gridLnk2;
        private bool _synchronizingTarget, _rejectingPick;  // 需求2：祖先链判断是否可靠(自检结果)，不可靠则不剔除
        // 需求2/3：每个Link grid 关联(grid, 几何体list, 显示名, 高亮色)，用于交叉校验+刷新
        private System.Collections.Generic.List<System.Tuple<TxObjGridCtrl, System.Collections.Generic.List<ITxObject>, string, TxColor>> _linkGrids
            = new System.Collections.Generic.List<System.Tuple<TxObjGridCtrl, System.Collections.Generic.List<ITxObject>, string, TxColor>>();
        private bool _crossCheckActive = false;  // 校验弹窗进行中，防重入

        // ── Step2：参数 ──────────────────────────────────────────────────
        private TextBox _txtPistonStroke, _txtOpenGap, _txtWear;
        private bool _suppressOpenGapEvent = false;  // 程序填开口量时抑制TextChanged
        private bool _openGapUserEdited = false;     // 用户是否手动改过开口量
        private TextBox _txtRAuto, _txtDAuto, _txtDAO, _txtr, _txtL, _txtAlpha;
        private RichTextBox _txtCoordCard;

        // ── Step3：生成 ──────────────────────────────────────────────────
        private TxObjEditBoxCtrl _targetPicker;
        private TextBox _txtJ1, _txtJ2, _txtInputJ1;
        private CheckBox _chkOpenPositive;
        private CheckBox _chkOpenAdapt;   // 需求4：OPEN状态适配勾选
        private RichTextBox _txtFormula2, _txtFormulaInput;
        private RichTextBox _txtResult;

        // ── DPI（FormUiKit 标准方案）────────────────────────────────────────
        private bool _dpiApplied;
        private static readonly Size _designSize = new Size(790, 680);

        // ── 步骤导航 ─────────────────────────────────────────────────────
        private Panel[] _stepPanels;
        private Button _btnBack, _btnNext, _btnGenerate, _btnFinish;
        private Label _lblStepTitle;
        private Label _lblStepProgress;
        private Panel _stepIndicator;
        private int _currentStep = 0;
        private const int PickerHeight = 26;
        private const int PickerRowHeight = 34;

        // ── 构造 ─────────────────────────────────────────────────────────
        public WeldGunWizardForm()
        {
            try { SemiModal = false; } catch { }
            FormUiKit.InitStandardForm(this,
                "X 型焊枪快速定义向导",
                _designSize,
                new Size(790, 420),
                sizable: true);
            StartPosition = FormStartPosition.CenterParent;
            FormClosed += (s, e) =>
            {
                // 取消所有高亮，避免颜色残留在场景里
                try
                {
                    PsSdkHelper.UnhighlightAll(_model.FixedLinkBodies);
                    PsSdkHelper.UnhighlightAll(_model.InputLinkBodies);
                    PsSdkHelper.UnhighlightAll(_model.CouplerLinkBodies);
                    PsSdkHelper.UnhighlightAll(_model.OutputLinkBodies);
                    PsSdkHelper.UnhighlightAll(_model.Lnk2Bodies);
                    PsSdkHelper.Unhighlight(_model.ObjStaticTip);
                    PsSdkHelper.Unhighlight(_model.ObjMovingTip);
                }
                catch { }
                PsSdkHelper.RefreshView();
                _inst = null;
            };
            _service = new WeldGunService(_model);
            _service.ConfirmJointCleanup = names => MessageBox.Show(this,
                "检测到以下已有 Joint / Link：\n\n" + names +
                "\n\n将把已有 Link 内几何移回设备并保留世界位置，然后清理全部 Joint 与 Link（含手工机构）并重新创建。是否继续？",
                "确认清理 Joint 与 Link", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            BuildUi();
        }

        public override void OnInitTxForm() { try { base.OnInitTxForm(); } catch { } try { SemiModal = false; } catch { } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // FormUiKit 标准 DPI 缩放（AutoScaleMode.None + 手动 Scale，ExportGun 同款）
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, _designSize);
            FitStepHeight();
        }

        // ═════════════════════════════════════════════════════════════════
        // 整体布局
        // ═════════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // 外框：步骤指示 + 标题 + 内容区 + 按钮栏
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(6)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // 步骤指示
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 步骤名称已在指示条显示
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 内容
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // 按钮
            Controls.Add(root);

            root.Controls.Add(BuildStepIndicator(), 0, 0);

            _lblStepTitle = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = FormUiKit.BoldFont,
                ForeColor = Theme.CardTitle,
                Padding = new Padding(2, 0, 0, 0)
            };
            root.Controls.Add(_lblStepTitle, 0, 1);

            // 内容区（各 Step 用 Panel 切换）
            var host = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(host, 0, 2);

            _stepPanels = new Panel[]
            {
                BuildStep1(),
                BuildStep2(),
                BuildStep3(),
            };
            foreach (var panel in _stepPanels) WireGenerationInvalidation(panel);
            foreach (var p in _stepPanels) { p.Dock = DockStyle.Fill; p.Visible = false; host.Controls.Add(p); }

            root.Controls.Add(BuildButtonBar(), 0, 3);
            UpdateStepUi();
        }

        // ─────────────────────────────────────────────────────────────────
        // 步骤指示条
        // ─────────────────────────────────────────────────────────────────
        private Panel BuildStepIndicator()
        {
            var steps = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3,
                RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            steps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _stepIndicator = steps;
            // Continuous baseline joins the equal-width step indicators.
            _stepIndicator.Paint += (s, e) =>
            {
                using (var pen = new Pen(FormUiKit.CardBorder))
                    e.Graphics.DrawLine(pen, 0, _stepIndicator.Height - 1,
                                        _stepIndicator.Width, _stepIndicator.Height - 1);
            };
            string[] labels = { "1. 选择与绑定", "2. 参数", "3. 生成" };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
                var label = new Label
                {
                    Text = labels[i],
                    Tag = idx,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty,
                    TextAlign = ContentAlignment.MiddleCenter,
                    BorderStyle = BorderStyle.None
                };
                label.Paint += (s, e) =>
                {
                    int thickness = idx == _currentStep
                        ? System.Math.Max(2, (int)System.Math.Round(3 * FormUiKit.GetLayoutScale(this))) : 1;
                    using (var brush = new SolidBrush(idx == _currentStep ? Color.FromArgb(0, 120, 215) : Color.FromArgb(210, 215, 220)))
                        e.Graphics.FillRectangle(brush, 0, label.Height - thickness, label.Width, thickness);
                };
                steps.Controls.Add(label, i, 0);
            }
            return _stepIndicator;
        }

        private Panel BuildButtonBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 2, 4, 0),
                Margin = Padding.Empty,
                WrapContents = false
            };
            _btnGenerate = NewBtn("生成"); _btnGenerate.Click += (s, e) => DoGenerate(); _btnGenerate.Visible = false;
            _btnNext = NewBtn("下一步 >"); _btnNext.Click += (s, e) => GoNext();
            _btnBack = NewBtn("< 上一步"); _btnBack.Click += (s, e) => GoBack(); _btnBack.Enabled = false;
            // 完成按钮仅在生成页显示，生成成功后启用。
            _btnFinish = NewBtn("完成 ✓"); _btnFinish.Click += (s, e) => Close();
            _btnFinish.EnabledChanged += (sender, e) =>
            {
                if (_btnFinish is FormUiKit.FlatColorButton colored)
                    colored.BgColor = _btnFinish.Enabled ? Theme.BtnSecondary : Color.Gainsboro;
                _btnFinish.ForeColor = _btnFinish.Enabled ? Theme.BtnFore : SystemColors.GrayText;
            };
            _btnFinish.Enabled = false;
            bar.Controls.Add(_btnFinish);
            bar.Controls.Add(_btnGenerate);
            bar.Controls.Add(_btnNext);
            bar.Controls.Add(_btnBack);
            var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            bar.Dock = DockStyle.Right;
            bar.Width = 390;
            _lblStepProgress = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(80, 85, 90), Padding = new Padding(4, 0, 0, 0) };
            footer.Controls.Add(_lblStepProgress);
            footer.Controls.Add(bar);
            return footer;
        }

        // ═════════════════════════════════════════════════════════════════
        // Step 0：选取焊枪本体 + 建模状态检测
        // ═════════════════════════════════════════════════════════════════
        private Control BuildTargetSelection()
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3,
                Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, PickerRowHeight));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            // 设备节点选取（Component 级）
            _targetPicker = NewSinglePicker();
            _targetPicker.PickLevel = TxPickLevel.Component;
            void UpdateTarget(ITxObject obj)
            {
                if (_synchronizingTarget) return;
                _synchronizingTarget = true;
                try
                {
                    if (obj == null || !(obj is ITxComponent) || !(obj is ITxKinematicsModellable))
                    {
                        _targetPicker.Object = null;
                        ResetSelectedSetup();
                        _model.TargetComponent = null; _model.TargetKinematics = null; _model.TargetDevice = null;
                        if (obj != null) MessageBox.Show("请选择支持运动学建模的焊钳组件根节点。", "目标无效");
                    }
                    else
                    {
                        bool targetChanged = _model.TargetKinematics == null || _model.TargetKinematics.Id != obj.Id;
                        if (targetChanged) ResetSelectedSetup();
                        _model.TargetComponent = obj as ITxComponent;
                        _model.TargetKinematics = obj as ITxKinematicsModellable;
                        _model.TargetDevice = obj as ITxDevice;
                        if (targetChanged && _chkReuseLinks.Checked) RestoreExistingSetup();
                    }
                    UpdateModelingStatus();
                }
                finally { _synchronizingTarget = false; }
            }
            _targetPicker.Picked += (sender, e) => UpdateTarget(e.IsValidObjectPicked ? e.Object : null);
            _targetPicker.TypeValid += (sender, e) => UpdateTarget(_targetPicker.Object);
            _targetPicker.TypeInvalid += (sender, e) => UpdateTarget(null);
            content.Controls.Add(_targetPicker, 0, 0);

            // 建模状态 + 启用按钮
            var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _lblModelingStatus = new Label
            {
                Text = "请先选取焊枪设备",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gray
            };
            _btnEnableModeling = NewBtn("启用建模");
            _btnEnableModeling.Dock = DockStyle.Fill;
            _btnEnableModeling.Click += (s, e) => EnableModeling();
            _btnEnableModeling.Enabled = false;
            statusPanel.Controls.Add(_lblModelingStatus, 0, 0);
            statusPanel.Controls.Add(_btnEnableModeling, 1, 0);
            _chkReuseLinks = new CheckBox { Text = "选取本体时恢复已有 Link 与选点", Checked = true,
                AutoSize = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
            content.Controls.Add(statusPanel, 0, 1);
            content.Controls.Add(_chkReuseLinks, 0, 2);
            return MkCard("焊钳本体（组件根节点）", content);
        }

        // 更新建模状态显示
        private void UpdateModelingStatus()
        {
            if (_lblModelingStatus == null) return;
            if (_model.TargetKinematics == null)
            {
                _lblModelingStatus.Text = "请先选取焊枪设备";
                _lblModelingStatus.ForeColor = Color.Gray;
                _btnEnableModeling.Enabled = false;
                return;
            }
            bool open = false;
            try { open = _model.TargetKinematics.IsOpenForKinematicsModeling; } catch { }
            if (open)
            {
                _lblModelingStatus.Text = "已开启建模状态";
                _lblModelingStatus.ForeColor = Color.DarkGreen;
                _btnEnableModeling.Enabled = false;
            }
            else
            {
                _lblModelingStatus.Text = "未开启建模状态";
                _lblModelingStatus.ForeColor = Color.DarkOrange;
                _btnEnableModeling.Enabled = true;
            }
        }

        private void EnableModeling()
        {
            try
            {
                (_model.TargetComponent as ITxComponent)?.SetModelingScope();
                UpdateModelingStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("启用建模状态失败: " + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Step 1：铰点 + Link 几何体
        // ═════════════════════════════════════════════════════════════════
        private Panel BuildStep1()
        {
            var scroll = MkScroll();
            var stack = MkStack(3);

            // 铰点使用单对象编辑框；Link 仍保留多对象列表。
            var hingeLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            hingeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            hingeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int hrow = 0;

            void AddHingeRow(string lbl, ref TxObjEditBoxCtrl grid, string tag)
            {
                string t = tag;
                hingeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, PickerRowHeight));
                hingeLayout.Controls.Add(MakeLabel(lbl), 0, hrow);
                // PickLevel.Entity：允许选子零件/Frame（修复子项无法选择的问题）
                var g = NewSinglePicker(true);
                grid = g;
                Action<ITxObject> setSource = obj =>
                {
                    if (t == "O") _model.HingeO = obj;
                    else if (t == "A") _model.HingeA = obj;
                    else _model.HingeC = obj;
                };
                g.Picked += (sender, e) => { if (!_restoringSetup) setSource(e.IsValidObjectPicked ? e.Object : null); };
                g.TypeValid += (sender, e) => { if (!_restoringSetup) setSource(g.Object); };
                g.TypeInvalid += (sender, e) => { if (!_restoringSetup) setSource(null); };
                BindHingePicker(g,
                    pos => { if (t == "O") { _model.WorldO = pos; _model.HasO = true; } else if (t == "A") { _model.WorldA = pos; _model.HasA = true; } else { _model.WorldC = pos; _model.HasC = true; } TryUpdateErrors(); },
                    () => { if (t == "O") _model.HasO = false; else if (t == "A") { _model.HasA = false; _model.PlaneNormal = default(Vec3); } else _model.HasC = false; },
                    // A点专用：拾取时确定参考平面法向 N
                    t == "A" ? (Action<Vec3>)(n => {
                        _model.PlaneNormal = n;
                        TryUpdateErrors();
                    }) : null);
                hingeLayout.Controls.Add(g, 1, hrow);
                hrow++;
            }

            AddHingeRow("O 动臂转轴:", ref _gridO, "O");
            AddHingeRow("A 活塞铰点:", ref _gridA, "A");
            AddHingeRow("C 连杆铰点:", ref _gridC, "C");

            var hintHinge = new Label { Text = "A点确定参考平面法向（Face法向或Frame的Z轴）；三点投影此平面强制共面", Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(2, 4, 2, 2) };
            var hingeWrap = new Panel { Dock = DockStyle.Top, AutoSize = true };
            hingeWrap.Controls.Add(hintHinge);
            hingeWrap.Controls.Add(hingeLayout);
            stack.Controls.Add(MkCard("三铰点（连杆机构）", hingeWrap), 0, 0);

            // 电极帽使用单对象编辑框。
            var miscLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            miscLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            miscLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int mrow = 0;

            void AddMiscRow(string lbl, ref TxObjEditBoxCtrl grid, Action<ITxObject> onInsert, Action onDelete)
            {
                miscLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, PickerRowHeight));
                miscLayout.Controls.Add(MakeLabel(lbl), 0, mrow);
                var g = NewSinglePicker();
                grid = g;
                ITxObject previous = null;
                Action<ITxObject> update = obj =>
                {
                    if (_restoringSetup) { previous = obj; return; }
                    if (_rejectingPick) return;
                    if (obj != null && !PsSdkHelper.ValidateLinkBody(obj, _model.TargetKinematics, out string reason))
                    {
                        _rejectingPick = true;
                        try { g.Object = null; } finally { _rejectingPick = false; }
                        PsSdkHelper.Unhighlight(previous); previous = null; onDelete(); RefreshAllLinkHighlights();
                        MessageBox.Show(reason, "电极帽选择无效");
                        return;
                    }
                    PsSdkHelper.Unhighlight(previous);
                    previous = obj;
                    if (obj == null) onDelete(); else onInsert(obj);
                    RefreshAllLinkHighlights();
                };
                g.Picked += (sender, e) => update(e.IsValidObjectPicked ? e.Object : null);
                g.TypeValid += (sender, e) => update(g.Object);
                g.TypeInvalid += (sender, e) => update(null);
                miscLayout.Controls.Add(g, 1, mrow);
                mrow++;
            }

            AddMiscRow("静电极帽:", ref _gridStaticTip, o => _model.ObjStaticTip = o, () => _model.ObjStaticTip = null);
            AddMiscRow("动电极帽:", ref _gridMovingTip, o => _model.ObjMovingTip = o, () => _model.ObjMovingTip = null);

            // 焊钳TCP：用 PDPS 坐标选择器 TxFrameEditBoxCtrl
            miscLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, PickerRowHeight));
            miscLayout.Controls.Add(MakeLabel("焊钳 TCP:"), 0, mrow);
            _tcpEditBox = new TxFrameEditBoxCtrl();
            StyleSinglePicker(_tcpEditBox);
            try { _tcpEditBox.ListenToPick = true; } catch { }
            PickFocus.Wire(_tcpEditBox);
            try { _tcpEditBox.PickLevel = TxPickLevel.Entity; } catch { }
            _tcpEditBox.ValidFrameSet += (s, e) =>
            {
                try
                {
                    if (!_restoringSetup) _model.TcpFrame = null;
                    var loc = _tcpEditBox.GetLocation();
                    if (loc != null)
                    {
                        _model.WorldGunTcp = new Vec3(loc[0, 3], loc[1, 3], loc[2, 3]);
                        _tcpPickedLocation = loc;   // 保存完整变换，生成时若需建TCPF用
                        PsSdkHelper.RefreshView();
                    }
                }
                catch { }
            };
            _tcpEditBox.InvalidFrameSet += (sender, e) =>
            {
                if (_restoringSetup) return;
                _model.TcpFrame = null; _model.WorldGunTcp = default(Vec3); _tcpPickedLocation = null;
                PsSdkHelper.RefreshView();
            };
            // Picked：若用户点选的是已有 TxFrame 对象，直接作为 TCP Frame
            try
            {
                _tcpEditBox.Picked += (s, e) =>
                {
                    try
                    {
                        var pe = e as TxFrameEditBoxCtrl_PickedEventArgs;
                        _model.TcpFrame = pe?.Object is TxFrame frame && PsSdkHelper.IsDescendantOf(frame, _model.TargetKinematics) ? frame : null;   // 是Frame就直接用，否则null(生成时建TCPF)
                        if (pe != null && pe.Location != null)
                        {
                            _tcpPickedLocation = pe.Location;
                            _model.WorldGunTcp = new Vec3(pe.Location[0, 3], pe.Location[1, 3], pe.Location[2, 3]);
                            PsSdkHelper.RefreshView();
                        }
                    }
                    catch { }
                };
            }
            catch { }
            miscLayout.Controls.Add(_tcpEditBox, 1, mrow);
            mrow++;
            miscLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, PickerRowHeight));
            miscLayout.Controls.Add(MakeLabel("BASE:"), 0, mrow);
            var baseRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            baseRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            baseRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            baseRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _baseEditBox = new TxFrameEditBoxCtrl { ListenToPick = true, PickLevel = TxPickLevel.Entity,
                EnableRelativeToWorkingFrameCoordinates = false };
            StyleSinglePicker(_baseEditBox);
            PickFocus.Wire(_baseEditBox);
            _baseEditBox.ValidFrameSet += (s, e) => { _model.BaseLocation = _baseEditBox.GetLocation(); PsSdkHelper.RefreshView(); };
            _baseEditBox.InvalidFrameSet += (s, e) => { _model.BaseLocation = null; PsSdkHelper.RefreshView(); };
            _baseEditBox.Picked += (s, e) => PsSdkHelper.RefreshView();
            var clearBase = NewBtn("清空");
            clearBase.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            clearBase.Height = PickerHeight;
            clearBase.Margin = new Padding(4, 4, 0, 4);
            clearBase.Click += (s, e) => { _baseEditBox.Text = string.Empty; _model.BaseLocation = null; PsSdkHelper.RefreshView(); };
            baseRow.Controls.Add(_baseEditBox, 0, 0);
            baseRow.Controls.Add(clearBase, 1, 0);
            miscLayout.Controls.Add(baseRow, 1, mrow++);
            miscLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var baseHint = new Label { Text = "电极帽自动归入对应臂。\nBASE 可选，留空使用焊钳自身坐标。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
            miscLayout.Controls.Add(baseHint, 0, mrow++);
            miscLayout.SetColumnSpan(baseHint, 2);
            miscLayout.RowCount = mrow;
            stack.Controls.Add(MkCard("电极帽、TCP 与 BASE", miscLayout), 0, 1);

            // ── Link 几何体绑定：2×2 网格（避免单列过长）──
            // Component 级别可多选
            var linkGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
            linkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            linkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            linkGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            linkGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            void AddLinkCell(string title, ref TxObjGridCtrl grid, List<ITxObject> list, int col, int row, TxColor color)
            {
                var lst = list;
                var col2 = color;
                var ttl = title;
                var g = new TxObjGridCtrl { Dock = DockStyle.Fill, EnableMultipleSelection = true, PickLevel = TxPickLevel.Entity };
                WireGridPick(g);
                grid = g;
                Action refresh = () => {
                    PsSdkHelper.UnhighlightAll(lst);            // 先取消旧高亮
                    lst.Clear();
                    for (int i = 0; i < g.Count; i++) { var o = g.GetObject(i) as ITxObject; if (o != null) lst.Add(o); }
                    RefreshAllLinkHighlights();
                };
                g.ObjectInserted += (s, e) => {
                    var obj = (e as TxObjGridCtrl_ObjectInsertedEventArgs)?.Obj;
                    if (obj != null && !PsSdkHelper.ValidateLinkBody(obj, _model.TargetKinematics, out string reason))
                    {
                        RemoveObjectFromGrid(g, obj);
                        refresh();
                        MessageBox.Show(reason, "Link 几何选择无效");
                        return;
                    }
                    refresh();
                };
                g.RowDeleted += (s, e) => refresh();
                // 焦点离开时做交叉校验（需求2）
                try { g.Leave += (s, e) => OnLinkGridLeave(g); } catch { }
                _linkGrids.Add(System.Tuple.Create(g, lst, ttl, col2));
                // Keep room for the native header and at least five object rows.
                g.Font = FormUiKit.BaseFont;
                var cell = MkFixedCard(title, 164, g);
                cell.MinimumSize = new Size(0, 164);
                cell.ForeColor = Color.FromArgb(45, 45, 45);
                if (cell is FormUiKit.ColoredGroupBox colored)
                {
                    colored.TitleColor = Color.FromArgb(45, 45, 45);
                    Color[] headerColors = { Color.FromArgb(255, 239, 218), Color.FromArgb(234, 245, 224),
                        Color.FromArgb(255, 248, 214), Color.FromArgb(228, 240, 255), Color.FromArgb(250, 229, 250) };
                    colored.BackColor = headerColors[row * 2 + col];
                    colored.BorderColor = Color.FromArgb(205, 210, 215);
                }
                cell.Margin = new Padding(2);
                linkGrid.Controls.Add(cell, col, row);
            }

            // 改为 2×3 网格（加 lnk2）
            linkGrid.ColumnStyles.Clear();
            linkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            linkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            linkGrid.RowCount = 3;
            linkGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            AddLinkCell("Fixed Link 固定枪体", ref _gridFixedLink, _model.FixedLinkBodies, 0, 0, PsSdkHelper.ColorFixed);
            AddLinkCell("Input Link 活塞缸体", ref _gridInputLink, _model.InputLinkBodies, 1, 0, PsSdkHelper.ColorInput);
            AddLinkCell("Coupler Link 活塞杆/连杆", ref _gridCouplerLink, _model.CouplerLinkBodies, 0, 1, PsSdkHelper.ColorCoupler);
            AddLinkCell("Output Link 动臂", ref _gridOutputLink, _model.OutputLinkBodies, 1, 1, PsSdkHelper.ColorOutput);
            AddLinkCell("lnk2 静臂/磨量补偿", ref _gridLnk2, _model.Lnk2Bodies, 0, 2, PsSdkHelper.ColorLnk2);

            stack.Controls.Add(MkCard("Link 几何体绑定（可多选，Component 级）", linkGrid), 0, 2);

            // 左侧选铰点/电极，右侧绑定 Link；列表内部滚动，整页保持一屏。
            var hingeCard = stack.GetControlFromPosition(0, 0);
            var electrodeCard = stack.GetControlFromPosition(0, 1);
            var oldLinkCard = stack.GetControlFromPosition(0, 2);
            oldLinkCard.Controls.Remove(linkGrid);
            Control linkCard = linkGrid;
            stack.Controls.Clear();
            stack.Dock = DockStyle.Fill;
            stack.AutoSize = false;
            stack.ColumnCount = 2;
            stack.RowCount = 1;
            stack.ColumnStyles.Clear();
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 284));
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stack.RowStyles.Clear();
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (Control card in new[] { hingeCard, electrodeCard, linkCard })
            {
                card.AutoSize = false;
                card.Dock = DockStyle.Fill;
                foreach (Control inner in card.Controls) { inner.AutoSize = false; inner.Dock = DockStyle.Fill; }
            }
            hingeWrap.AutoSize = hingeLayout.AutoSize = miscLayout.AutoSize = linkGrid.AutoSize = false;
            hingeLayout.Dock = miscLayout.Dock = linkGrid.Dock = DockStyle.Fill;
            // Measure the three picker rows independently of the hint and group title.
            hingeWrap.Controls.Clear();
            var hingeContent = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            hingeContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hingeContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 3 * PickerRowHeight));
            hingeContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            hintHinge.AutoSize = false;
            hintHinge.Dock = DockStyle.Fill;
            hingeContent.Controls.Add(hingeLayout, 0, 0);
            hingeContent.Controls.Add(hintHinge, 0, 1);
            hintHinge.Margin = Padding.Empty;
            hingeCard.Controls.Clear();
            hingeWrap.Dispose();
            hingeCard.AutoSize = true;
            hingeCard.Dock = DockStyle.Top;
            hingeCard.Controls.Add(hingeContent);
            linkGrid.RowStyles.Clear();
            for (int i = 0; i < 3; i++) linkGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
            foreach (Control cell in linkGrid.Controls) { cell.AutoSize = false; cell.Dock = DockStyle.Fill; }
            var leftFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Margin = Padding.Empty };
            leftFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            leftFields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftFields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var targetCard = BuildTargetSelection();
            leftFields.Controls.Add(targetCard, 0, 0);
            leftFields.Controls.Add(hingeCard, 0, 1);
            leftFields.Controls.Add(electrodeCard, 0, 2);
            stack.Controls.Add(leftFields, 0, 0);
            stack.Controls.Add(linkCard, 1, 0);
            oldLinkCard.Dispose();
            var selectionHint = new Label
            {
                Dock = DockStyle.Fill, AutoSize = false, Font = FormUiKit.BaseFont,
                Text = "在场景中选择几何体\n支持多选与逐项移除\nLink 绑定随部件一起运动\n\n橙：固定枪体    绿：活塞缸体\n黄：活塞杆 / 连杆\n蓝：动臂    品红：静臂 / 磨量补偿",
                ForeColor = Color.FromArgb(65, 65, 65), Padding = new Padding(4)
            };
            var hintCard = MkFixedCard("选择提示", 164, selectionHint);
            hintCard.Dock = DockStyle.Fill;
            hintCard.Margin = new Padding(2);
            linkGrid.Controls.Add(hintCard, 1, 2);
            var guide = new Panel { Name = "SelectionGuide", Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 0, 6) };
            guide.Resize += (s, e) => guide.Invalidate();
            guide.Paint += (sender, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float scale = System.Math.Min(guide.ClientSize.Height / 230f, guide.ClientSize.Width / 850f);
                g.ScaleTransform(scale, scale);
                g.DrawString("焊钳侧视 · 按刚性部件选择几何体", Font, Brushes.Black, 14, 5);
                using (var fixedPen = new Pen(Color.Orange, 14))
                using (var armPen = new Pen(Color.Blue, 12))
                using (var staticArmPen = new Pen(Color.Magenta, 12))
                using (var cylinderOutline = new Pen(Color.DarkGreen, 26))
                using (var cylinderFill = new Pen(Color.Green, 20))
                using (var rodOutline = new Pen(Color.FromArgb(150, 125, 0), 10))
                using (var rodFill = new Pen(Color.Yellow, 6))
                using (var axisPen = new Pen(Color.DimGray, 1))
                using (var labelFont = new Font(Font, FontStyle.Bold))
                {
                    fixedPen.LineJoin = armPen.LineJoin = staticArmPen.LineJoin =
                        System.Drawing.Drawing2D.LineJoin.Round;
                    // 固定支架承载 A 和 O；静臂从固定侧向电极延伸。
                    g.DrawLines(fixedPen, new[] { new Point(65, 65), new Point(35, 148), new Point(165, 148) });
                    g.DrawLines(staticArmPen, new[] { new Point(165, 148), new Point(250, 180),
                        new Point(345, 161), new Point(345, 135) });
                    // C 位于动臂尾部，动臂绕 O 摆动，前端承载动电极。
                    g.DrawLines(armPen, new[] { new Point(190, 65), new Point(165, 148),
                        new Point(280, 45), new Point(345, 76), new Point(345, 105) });
                    // 先画杆，再画缸体：黄色杆从绿色缸体伸出至 C。
                    g.DrawLine(rodOutline, 115, 65, 190, 65);
                    g.DrawLine(rodFill, 115, 65, 190, 65);
                    g.DrawLine(cylinderOutline, 65, 65, 130, 65);
                    g.DrawLine(cylinderFill, 65, 65, 130, 65);
                    g.FillRectangle(Brushes.Blue, 337, 101, 16, 9);
                    g.FillRectangle(Brushes.Magenta, 337, 130, 16, 9);
                    // 铰点以同心圆表示轴心，不把选面位置画成铰轴。
                    foreach (var point in new[] { new Point(65, 65), new Point(190, 65), new Point(165, 148) })
                    {
                        g.FillEllipse(Brushes.White, point.X - 7, point.Y - 7, 14, 14);
                        g.DrawEllipse(Pens.Black, point.X - 7, point.Y - 7, 14, 14);
                        g.DrawLine(Pens.Black, point.X - 3, point.Y, point.X + 3, point.Y);
                        g.DrawLine(Pens.Black, point.X, point.Y - 3, point.X, point.Y + 3);
                    }
                    g.DrawString("A", labelFont, Brushes.Black, 43, 42);
                    g.DrawString("C", labelFont, Brushes.Black, 198, 46);
                    g.DrawString("O", labelFont, Brushes.Black, 143, 122);
                    g.DrawString("缸体", Font, Brushes.DarkGreen, 80, 33);
                    g.DrawString("活塞杆", Font, Brushes.Black, 132, 80);
                    g.DrawString("动臂", Font, Brushes.Blue, 278, 24);
                    g.DrawString("静臂", Font, Brushes.Magenta, 260, 184);
                    g.DrawString("固定枪体", Font, Brushes.DarkOrange, 22, 169);
                    g.DrawString("动电极", Font, Brushes.Blue, 359, 94);
                    g.DrawString("静电极", Font, Brushes.Magenta, 359, 133);
                    axisPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    g.DrawLine(axisPen, 345, 111, 345, 129);

                    void Legend(Color color, string text, int y)
                    {
                        using (var brush = new SolidBrush(color)) g.FillRectangle(brush, 438, y + 2, 20, 12);
                        g.DrawRectangle(Pens.Gray, 438, y + 2, 20, 12);
                        g.DrawString(text, Font, Brushes.Black, 469, y);
                    }
                    Legend(Color.Orange, "固定枪体 → Fixed Link", 32);
                    Legend(Color.Blue, "动臂 → Output Link（含动电极侧）", 58);
                    Legend(Color.Magenta, "静臂 → lnk2（含静电极侧 / 磨量补偿）", 84);
                    Legend(Color.Green, "活塞缸体 → Input Link", 110);
                    Legend(Color.Yellow, "活塞杆 / 连杆 → Coupler Link", 136);
                    g.DrawString("A 缸体铰轴   C 杆端铰轴   O 动臂主轴", labelFont, Brushes.Black, 438, 168);
                    g.DrawString("铰点选轴心；Link 选随该部件一起运动的几何体。", Font, Brushes.Black, 14, 208);
                    g.DrawString("A 的面法向 / Frame Z 轴沿铰轴。示意非尺寸图。", Font, Brushes.DimGray, 438, 208);
                }
            };
            // 示意图与输入区是不同表格行；小窗口允许整页滚动，不压缩选择框。
            stack.Name = "SelectionFields";
            var page = new TableLayoutPanel { Name = "SelectionPage", Dock = DockStyle.Top, Height = 546,
                ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var toggleGuide = new Button { Text = "▶ 机构示意与选择说明", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(245, 247, 249), ForeColor = Color.FromArgb(45, 45, 45),
                Margin = new Padding(0, 0, 0, 4), UseVisualStyleBackColor = false };
            toggleGuide.FlatAppearance.BorderColor = Color.FromArgb(205, 210, 215);
            bool guideExpanded = false;
            Action resizePage = () =>
            {
                float scale = FormUiKit.GetLayoutScale(this);
                page.RowStyles[1].Height = guideExpanded ? (float)System.Math.Round(230 * scale) : 0;
                page.Height = System.Math.Max((int)System.Math.Round((546 + (guideExpanded ? 230 : 0)) * scale),
                    scroll.ClientSize.Height - scroll.Padding.Vertical);
            };
            guide.Visible = false;
            toggleGuide.Click += (s, e) =>
            {
                guideExpanded = !guideExpanded;
                guide.Visible = guideExpanded;
                toggleGuide.Text = (guideExpanded ? "▼" : "▶") + " 机构示意与选择说明";
                resizePage();
            };
            page.Controls.Add(toggleGuide, 0, 0);
            page.Controls.Add(guide, 0, 1);
            page.Controls.Add(stack, 0, 2);
            scroll.AutoScroll = true;
            scroll.Controls.Add(page);
            scroll.Resize += (s, e) => resizePage();
            var p = new Panel { Dock = DockStyle.Fill };
            p.Controls.Add(scroll);
            return p;
        }
        // ═════════════════════════════════════════════════════════════════
        private Panel BuildStep2()
        {
            var scroll = MkScroll();
            var stack = MkStack(3);

            // 自动推算（只读）
            var autoLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            autoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            autoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            autoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            int ar = 0;
            TextBox AddAutoRow(string lbl, string unit)
            {
                autoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                autoLayout.Controls.Add(new Label { Text = lbl, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 4, 0) }, 0, ar);
                var tb = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.ReadOnlyBg };
                autoLayout.Controls.Add(tb, 1, ar);
                autoLayout.Controls.Add(new Label { Text = unit, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray }, 2, ar);
                ar++; autoLayout.RowCount = ar; return tb;
            }
            _txtRAuto = AddAutoRow("R  臂长(动TCP→O):", "mm");
            _txtDAuto = AddAutoRow("最大张开距离:", "mm");
            _txtDAO = AddAutoRow("d_AO  活塞铰->轴:", "mm");
            _txtr = AddAutoRow("r  连杆铰->轴:", "mm");
            _txtL = AddAutoRow("L  初始伸出量:", "mm");
            _txtAlpha = AddAutoRow("Alpha 结构角:", "deg");
            stack.Controls.Add(MkCard("自动推算参数（只读）", autoLayout), 0, 0);

            // 用户输入
            var userLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            userLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            userLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            userLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            int ur = 0;
            TextBox AddUserRow(string lbl, string defVal, string unit)
            {
                userLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                userLayout.Controls.Add(new Label { Text = lbl, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 4, 0) }, 0, ur);
                var tb = new TextBox { Dock = DockStyle.Fill, Text = defVal };
                userLayout.Controls.Add(tb, 1, ur);
                userLayout.Controls.Add(new Label { Text = unit, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray }, 2, ur);
                ur++; userLayout.RowCount = ur; return tb;
            }
            // s0(活塞闭合长) = |AC| - 行程，自动算，无需用户输入
            _txtPistonStroke = AddUserRow("活塞行程:", "100", "mm");
            _txtOpenGap = AddUserRow("焊钳开口量:", "185", "mm");
            _txtWear = AddUserRow("磨损补偿量:", "10", "mm");
            _txtPistonStroke.TextChanged += (s, e) => { OnParamChanged(); SyncOpenGapFromCalc(); };
            _txtOpenGap.TextChanged += (s, e) => { if (_suppressOpenGapEvent) return; _openGapUserEdited = true; OnParamChanged(); };
            _txtWear.TextChanged += (s, e) => OnParamChanged();
            stack.Controls.Add(MkCard("焊钳参数（用户填写）", userLayout), 0, 1);

            // RPRR 坐标卡
            _txtCoordCard = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Theme.InputBg,
                Font = FormUiKit.MonoFont,
                Text = "（请先完成铰点选取，参数将自动填入）"
            };
            var coordinates = MkFixedCard("RPRR 向导坐标参考", 120, _txtCoordCard);
            coordinates.Dock = DockStyle.Fill;
            stack.Controls.Add(coordinates, 0, 2);
            stack.Dock = DockStyle.Fill;
            stack.AutoSize = false;
            stack.RowStyles[2].SizeType = SizeType.Percent;
            stack.RowStyles[2].Height = 100;

            scroll.Controls.Add(stack);
            var p = new Panel { Dock = DockStyle.Fill };
            p.Controls.Add(scroll);
            return p;
        }

        // ═════════════════════════════════════════════════════════════════
        // Step 3：生成
        // ═════════════════════════════════════════════════════════════════
        private Panel BuildStep3()
        {
            var scroll = MkScroll();
            scroll.AutoScroll = true;
            var stack = MkStack(4);

            // 关节命名（设备已在Step1选取）
            var nameLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            nameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int nr = 0;
            TextBox AddNameRow(string lbl, string def)
            {
                nameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                nameLayout.Controls.Add(new Label { Text = lbl, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 4, 0) }, 0, nr);
                var tb = new TextBox { Dock = DockStyle.Fill, Text = def };
                nameLayout.Controls.Add(tb, 1, nr);
                nr++; nameLayout.RowCount = nr; return tb;
            }
            _txtJ1 = AddNameRow("J1 主动关节:", "j1");
            _txtJ2 = AddNameRow("J2 磨量补偿:", "j2");
            _txtInputJ1 = AddNameRow("input_j1 动臂:", "input_j1");
            _txtJ1.TextChanged += (s, e) => { _model.J1_Name = _txtJ1.Text; RefreshFormulaPreview(); };
            _txtJ2.TextChanged += (s, e) => { _model.J2_Name = _txtJ2.Text; };
            _txtInputJ1.TextChanged += (s, e) => { _model.InputJ1_Name = _txtInputJ1.Text; RefreshFormulaPreview(); };

            // 需求4：OPEN状态适配勾选
            _chkOpenAdapt = new CheckBox
            {
                Text = "适配 OPEN 初始模型",
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = FormUiKit.BoldFont,
                Margin = Padding.Empty
            };
            _chkOpenAdapt.CheckedChanged += (s, e) => { _model.OpenStateAdapt = _chkOpenAdapt.Checked; };
            _chkOpenPositive = new CheckBox
            {
                Text = "OPEN 使用正值",
                Dock = DockStyle.Fill, AutoSize = false,
                Font = FormUiKit.BoldFont, Margin = Padding.Empty
            };
            _chkOpenPositive.CheckedChanged += (sender, e) =>
            {
                _model.OpenPositive = _chkOpenPositive.Checked;
                RefreshFormulaPreview();
            };
            stack.Controls.Add(MkCard("关节命名", nameLayout), 0, 0);

            var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Margin = Padding.Empty };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Control BuildOption(CheckBox check, string description)
            {
                var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                    Padding = new Padding(10, 2, 8, 6), Margin = new Padding(3),
                    BorderStyle = BorderStyle.FixedSingle, Font = FormUiKit.BaseFont };
                card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var hint = new Label { Text = description, Dock = DockStyle.Fill,
                    Margin = new Padding(18, 0, 0, 0), Font = FormUiKit.BaseFont,
                    ForeColor = Color.FromArgb(75, 85, 95) };
                void UpdateAppearance()
                {
                    card.BackColor = check.Checked ? Color.FromArgb(226, 240, 253) : Color.FromArgb(244, 247, 250);
                    check.ForeColor = check.Checked ? Color.FromArgb(0, 85, 155) : Color.FromArgb(35, 50, 65);
                }
                check.CheckedChanged += (s, e) => UpdateAppearance();
                card.Controls.Add(check, 0, 0);
                card.Controls.Add(hint, 0, 1);
                UpdateAppearance();
                return card;
            }
            options.Controls.Add(BuildOption(_chkOpenAdapt,
                "模型处于最大开口时启用。\n生成时先回到闭合状态，并设为零位。\n闭合状态模型请保持关闭。"), 0, 0);
            options.Controls.Add(BuildOption(_chkOpenPositive,
                "启用后：OPEN 使用正值，CLOSE = 0，\n磨量补偿使用负值。\n关闭时沿用默认方向。"), 1, 0);
            var optionsCard = MkFixedCard("生成选项（生成前确认）", 132, options);
            ((FormUiKit.ColoredGroupBox)optionsCard).TitleColor = Color.FromArgb(35, 65, 95);
            stack.Controls.Add(optionsCard, 0, 1);

            // 公式预览
            _txtFormula2 = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.ReadOnlyBg, Font = FormUiKit.MonoFont };
            _txtFormulaInput = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.ReadOnlyBg, Font = FormUiKit.MonoFont };
            var fmlLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            fmlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            fmlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fmlLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fmlLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fmlLayout.Controls.Add(new Label { Text = "J2 公式:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 0, 0);
            fmlLayout.Controls.Add(_txtFormula2, 1, 0);
            fmlLayout.Controls.Add(new Label { Text = "input_j1:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 0, 1);
            fmlLayout.Controls.Add(_txtFormulaInput, 1, 1);
            stack.Controls.Add(MkFixedCard("Joint Dependency 公式预览（可复制）", 110, fmlLayout), 0, 2);

            // 结果
            _txtResult = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.InputBg, Font = FormUiKit.BaseFont };
            var resultCard = MkFixedCard("生成结果", 100, _txtResult);
            resultCard.Dock = DockStyle.Fill;
            stack.Controls.Add(resultCard, 0, 3);
            stack.Dock = DockStyle.Top;
            stack.AutoSize = false;
            stack.RowStyles[3].SizeType = SizeType.Percent;
            stack.RowStyles[3].Height = 100;
            Action resizeContent = () => stack.Height = System.Math.Max(
                (int)System.Math.Round(490 * FormUiKit.GetLayoutScale(this)),
                scroll.ClientSize.Height - scroll.Padding.Vertical);
            scroll.Resize += (s, e) => resizeContent();

            scroll.Controls.Add(stack);
            var p = new Panel { Dock = DockStyle.Fill };
            p.Controls.Add(scroll);
            return p;
        }

        // ═════════════════════════════════════════════════════════════════
        // 辅助：卡片工厂（参考 AllocatorForm）
        // ═════════════════════════════════════════════════════════════════
        private static GroupBox MkCard(string title, Control inner)
        {
            // Prevent the group title's bold font from propagating into form fields.
            if (!(inner is RichTextBox)) inner.Font = FormUiKit.BaseFont;
            inner.Dock = DockStyle.Top;
            var g = new FormUiKit.ColoredGroupBox
            {
                Text = title,
                TitleColor = Theme.CardTitle,
                BorderColor = FormUiKit.CardBorder,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 8),
                Margin = new Padding(0, 0, 0, 6),
                Font = FormUiKit.BoldFont,
                BackColor = FormUiKit.CardBack
            };
            g.Controls.Add(inner);
            return g;
        }
        private static GroupBox MkFixedCard(string title, int height, Control inner)
        {
            if (!(inner is RichTextBox)) inner.Font = FormUiKit.BaseFont;
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
        private static Panel MkScroll()
            => new Panel { Dock = DockStyle.Fill, AutoScroll = false, Padding = new Padding(6) };
        private static TableLayoutPanel MkStack(int rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = rows };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return t;
        }

        /// <summary>从 grid 移除指定对象（用 DeleteRow，按行匹配）</summary>
        private void RemoveObjectFromGrid(TxObjGridCtrl g, ITxObject target)
        {
            if (g == null || target == null) return;
            try
            {
                for (int i = 0; i < g.Count; i++)
                {
                    var o = g.GetObject(i) as ITxObject;
                    if (o == target) { g.DeleteRow(i); return; }
                }
            }
            catch { }
        }

        /// <summary>用对象列表重置 grid 内容（清空后 Objects 整体赋值）</summary>
        private void ResetGridObjects(TxObjGridCtrl g, System.Collections.Generic.List<ITxObject> objs)
        {
            if (g == null) return;
            try
            {
                var list = new TxObjectList();
                if (objs != null) foreach (var o in objs) if (o != null) list.Add(o);
                g.Objects = list;
            }
            catch { }
        }

        /// <summary>
        /// 自检：祖先链判断在当前环境/此焊钳是否可靠。
        /// 取焊钳几个已知后代，若 IsDescendantOf 能正确识别它们属于焊钳，则可靠；
        /// 否则(API在此环境走不通)返回false，调用方据此禁用外部剔除，避免误删本体。
        /// </summary>


        /// 若离开的 grid 与其他 grid 有共同几何体，弹窗三选项：
        ///   (1) 保留到此 grid，从其他 grid 移除
        ///   (2) 不保留交叉项（从此 grid 移除，其他保留）
        ///   (3) 取消，焦点返回此 grid
        /// </summary>
        private void OnLinkGridLeave(TxObjGridCtrl leftGrid)
        {
            if (_crossCheckActive) return;
            if (leftGrid == null) return;

            // 找到离开grid对应的list和名字
            System.Collections.Generic.List<ITxObject> leftList = null;
            string leftName = null;
            foreach (var t in _linkGrids)
                if (t.Item1 == leftGrid) { leftList = t.Item2; leftName = t.Item3; break; }
            if (leftList == null || leftList.Count == 0) return;

            // 收集与其他grid的交叉项
            var crossing = new System.Collections.Generic.List<ITxObject>();
            var otherInfo = new System.Collections.Generic.List<string>();
            foreach (var t in _linkGrids)
            {
                if (t.Item1 == leftGrid) continue;
                foreach (var o in t.Item2)
                {
                    if (o != null && leftList.Contains(o) && !crossing.Contains(o))
                    {
                        crossing.Add(o);
                        if (!otherInfo.Contains(t.Item3)) otherInfo.Add(t.Item3);
                    }
                }
            }
            if (crossing.Count == 0) return;

            _crossCheckActive = true;
            try
            {
                string msg = $"检测到 {crossing.Count} 个几何体同时存在于：\n" +
                             $"「{leftName}」与「{string.Join("、", otherInfo)}」\n\n" +
                             "请选择处理方式：\n" +
                             "  [是]  保留到「" + leftName + "」，从其他Link移除\n" +
                             "  [否]  不保留交叉项（从「" + leftName + "」移除）\n" +
                             "  [取消]  什么都不做，返回继续编辑";
                var r = MessageBox.Show(msg, "Link几何体交叉",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (r == DialogResult.Yes)
                {
                    // 保留到本grid，从其他grid移除交叉项
                    foreach (var t in _linkGrids)
                    {
                        if (t.Item1 == leftGrid) continue;
                        bool changed = false;
                        foreach (var o in crossing)
                            if (t.Item2.Contains(o)) { RemoveObjectFromGrid(t.Item1, o); changed = true; }
                        if (changed)
                        {
                            t.Item2.RemoveAll(o => crossing.Contains(o));
                        }
                    }
                    RefreshAllLinkHighlights();   // 问题3：刷新高亮颜色
                }
                else if (r == DialogResult.No)
                {
                    // 从本grid移除交叉项（其他保留）
                    foreach (var o in crossing) RemoveObjectFromGrid(leftGrid, o);
                    leftList.RemoveAll(o => crossing.Contains(o));
                    RefreshAllLinkHighlights();   // 问题3：刷新高亮颜色
                }
                else
                {
                    // 取消：焦点返回本grid
                    try { BeginInvoke((Action)(() => ActivateGridPick(leftGrid))); } catch { }
                }
            }
            finally { _crossCheckActive = false; }
        }

        /// <summary>问题3：交叉处理后重新着色所有Link几何体(先全清再按各自颜色上色)</summary>
        private void RefreshAllLinkHighlights()
        {
            try
            {
                _model.SynchronizeElectrodeBindings();
                foreach (var t in _linkGrids) PsSdkHelper.UnhighlightAll(t.Item2);
                foreach (var t in _linkGrids) PsSdkHelper.HighlightAll(t.Item2, t.Item4);
                PsSdkHelper.Highlight(_model.ObjStaticTip, PsSdkHelper.ColorLnk2);
                PsSdkHelper.Highlight(_model.ObjMovingTip, PsSdkHelper.ColorOutput);
            }
            catch { }
            PsSdkHelper.RefreshView();
        }

        /// <summary>
        /// 统一处理 grid 拾取激活，解决"点击选择框后鼠标不变十字"的焦点问题。
        /// 根因：TxObjGridCtrl 首次显示时 PS 拾取提供者未注册，需在控件获得焦点/
        /// 点击时主动 Focus() + 重设 ListenToPick 来触发 PS 重新注册拾取监听。
        /// （复用 FormUiKit.GridPickFocus 统一助手：启动抢焦点 + 点击重获焦点 + ESC 取消焦点）
        /// </summary>
        private static void WireGridPick(TxObjGridCtrl g)
        {
            FormUiKit.GridPickFocus.Wire(g);
            g.Picked += (s, e) => PsSdkHelper.RefreshView();
        }

        private static void ActivateGridPick(TxObjGridCtrl g)
        {
            FormUiKit.GridPickFocus.Activate(g);
        }
        private static Button NewBtn(string t)
        {
            var button = FormUiKit.MkFuncButton(t, Theme.BtnSecondary);
            button.Font = FormUiKit.BaseFont;
            return button;
        }
        private static Label MakeLabel(string t) => new Label { Text = t, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 6, 0), Font = FormUiKit.BaseFont };

        // ═════════════════════════════════════════════════════════════════
        // 铰点 Grid：双路坐标（Frame原点 / Picked点击位置）
        // ═════════════════════════════════════════════════════════════════
        private static TxObjEditBoxCtrl NewSinglePicker(bool allowFace = false)
        {
            var picker = new TxObjEditBoxCtrl
            {
                ListenToPick = true,
                PickLevel = TxPickLevel.Entity, PickOnly = true, PickAndClear = true,
                ValidatorType = allowFace ? TxValidatorType.AnyLocatableObjectAndFace : TxValidatorType.AnyLocatableObject
            };
            StyleSinglePicker(picker);
            PickFocus.Wire(picker);
            return picker;
        }

        private static void StyleSinglePicker(Control picker)
        {
            picker.Dock = DockStyle.None;
            picker.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            picker.Height = PickerHeight;
            picker.Margin = new Padding(0, 4, 0, 4);
            picker.Font = FormUiKit.BaseFont;
            picker.BackColor = SystemColors.Window;
            picker.ForeColor = SystemColors.WindowText;
        }

        private void BindHingePicker(TxObjEditBoxCtrl picker, Action<Vec3> onSet,
            Action onClear, Action<Vec3> onNormal = null)
        {
            Action<TxTransformation> setLocation = loc =>
            {
                if (loc == null) { onClear(); return; }
                onSet(new Vec3(loc[0, 3], loc[1, 3], loc[2, 3]));
                if (onNormal != null)
                {
                    var normal = new Vec3(loc[0, 2], loc[1, 2], loc[2, 2]);
                    if (normal.Length > 0.5) onNormal(normal.Normalized());
                }
            };
            picker.Picked += (sender, e) =>
            {
                if (_restoringSetup) return;
                onClear(); // 替换前清理旧坐标，非法拾取不能继续使用上次的轴心。
                if (e.IsValidObjectPicked)
                    setLocation(e.Object is TxFrame frame ? frame.AbsoluteLocation : e.Location);
                PsSdkHelper.RefreshView();
            };
            picker.TypeValid += (sender, e) =>
            {
                if (_restoringSetup) return;
                onClear();
                // 通过名称设置 Frame 时使用原点；实体轴心必须从图形窗口拾取。
                if (picker.Object is TxFrame frame) setLocation(frame.AbsoluteLocation);
                PsSdkHelper.RefreshView();
            };
            picker.TypeInvalid += (sender, e) => { if (!_restoringSetup) onClear(); PsSdkHelper.RefreshView(); };
        }

        private void TryUpdateErrors()
        {
            if (_model.AllHingesReady) _service.UpdateHingePoints(out string error);
        }

        // ═════════════════════════════════════════════════════════════════
        // 参数变更
        // ═════════════════════════════════════════════════════════════════
        private void OnParamChanged()
        {
            if (_restoringSetup) return;
            double v;
            if (double.TryParse(_txtPistonStroke.Text, out v)) _model.PistonStroke = v;
            if (double.TryParse(_txtOpenGap.Text, out v)) _model.OpenGap = v;
            if (double.TryParse(_txtWear.Text, out v)) _model.WearAllowance = v;
            RefreshParameterDisplay();
        }

        /// <summary>
        /// 活塞行程变化时，把计算出的最大开口量自动填入"焊钳开口量"框。
        /// 用户一旦手动改过开口量，则不再自动覆盖（尊重用户输入）。
        /// </summary>
        private void SyncOpenGapFromCalc()
        {
            if (_restoringSetup) return;
            if (_openGapUserEdited) return;       // 用户改过就不自动填
            if (_model.MaxOpenGap <= 0) return;
            _suppressOpenGapEvent = true;
            try
            {
                _txtOpenGap.Text = $"{_model.MaxOpenGap:F1}";
                _model.OpenGap = _model.MaxOpenGap;
            }
            finally { _suppressOpenGapEvent = false; }
        }

        private void RefreshParameterDisplay()
        {
            if (_model.Mechanism == null) return;
            var m = _model.Mechanism;
            m.S0 = _model.S0; m.PistonStroke = _model.PistonStroke;
            m.OpenGap = _model.OpenGap; m.WearAllowance = _model.WearAllowance;

            // ── 臂长 R = TCP(静电极尖端)→ O 投影距离 ──
            // 注意：电极帽自身坐标在【尾端】不可靠，TCP固定在静电极【尖端】
            // X型焊钳闭合时两电极尖端接触在TCP位置，动臂张开时动电极尖绕O摆动，
            // 所以摆动有效半径 = |TCP → O|，这也是臂长
            double armLen = 0;
            bool hasTcp = _model.WorldGunTcp.Length > 1e-6;
            if (_model.Plane != null && hasTcp)
            {
                Vec3 projTcp = _model.Plane.Project(_model.WorldGunTcp);
                Vec3 projO = _model.Plane.Project(_model.WorldO);
                armLen = (projTcp - projO).Length;
                _model.ArmLength = armLen;
            }
            else if (_model.Plane != null && _model.ObjMovingTip != null)
            {
                // 兜底：TCP未选时用动电极帽（精度较低）
                Vec3 wMov;
                if (PsSdkHelper.TryGetWorldPosition(_model.ObjMovingTip, out wMov))
                {
                    Vec3 projMov = _model.Plane.Project(wMov);
                    Vec3 projO = _model.Plane.Project(_model.WorldO);
                    armLen = (projMov - projO).Length;
                    _model.ArmLength = armLen;
                }
            }

            // ── 最大张开距离：动臂摆角 × 有效半径(TCP→O) ──
            // output_j1 = -(coup_output_j1 + fixed_Input_j1)，动臂相对基座转角
            // 开口 = 2 × |TCP→O| × sin(摆角/2)，TCP=静电极尖端=闭合时电极接触点
            double maxOpen = 0;
            if (armLen > 0 && m.d_AO > 0 && m.r > 0)
            {
                // s0 = 活塞闭合长 = |AC| - 行程（m.L 即 |AC|）
                double a = m.d_AO, b = m.r;
                double s0 = m.L - _model.PistonStroke;
                if (s0 <= 1e-6) s0 = m.L;
                if (s0 > 1e-6)
                {
                    double innerConst = b * b - a * a;
                    // fixed_Input_j1 角（余弦定理）
                    System.Func<double, double> fixedInput = d =>
                    {
                        double cv = (a * a + d * d - b * b) / (2 * a * d);
                        return System.Math.Acos(System.Math.Max(-1, System.Math.Min(1, cv)));
                    };
                    double offsetA = fixedInput(s0);
                    double offsetC = System.Math.Atan2(a * System.Math.Sin(offsetA),
                                                       (s0 * s0 + innerConst) / (2 * s0));
                    // coup_output_j1 角
                    System.Func<double, double> coupOutput = d =>
                    {
                        double y = a * System.Math.Sin(fixedInput(d));
                        double x = (d * d + innerConst) / (2 * d);
                        return System.Math.Atan2(y, x) - offsetC;
                    };
                    // output_j1 角 = -(coup + (fixed-offsetA))
                    System.Func<double, double> outputAngle = d =>
                        -(coupOutput(d) + (fixedInput(d) - offsetA));

                    double out0 = outputAngle(s0);
                    double outMax = outputAngle(s0 + _model.PistonStroke);
                    double swing = System.Math.Abs(outMax - out0);
                    maxOpen = 2 * armLen * System.Math.Sin(swing / 2);
                    _model.MaxOpenGap = maxOpen;
                }
            }

            _txtRAuto.Text = armLen > 0 ? $"{armLen:F2}" : "--";
            _txtDAuto.Text = maxOpen > 0 ? $"{maxOpen:F2}" : "--";
            _txtDAO.Text = $"{m.d_AO:F2}";
            _txtr.Text = $"{m.r:F2}";
            _txtL.Text = $"{m.L:F2}";
            _txtAlpha.Text = $"{m.Alpha * 180.0 / System.Math.PI:F2}";
            _txtCoordCard.Text = m.FormatHingePointsForWizard(_model.WorldO, _model.WorldA, _model.WorldC);
        }

        private void RefreshFormulaPreview()
        {
            if (_model.Mechanism == null) return;
            _model.Mechanism.PistonStroke = _model.PistonStroke;
            _model.Mechanism.OpenGap = _model.OpenGap;
            _model.Mechanism.WearAllowance = _model.WearAllowance;
            var formulas = _model.Mechanism.BuildFormulas(_model.J1_Name, _model.J2_Name, _model.InputJ1_Name, _model.OpenPositive);
            _txtFormula2.Text = formulas.J2_Formula;
            _txtFormulaInput.Text = formulas.InputJ1_Formula;
            _model.Formulas = formulas;
        }

        // ═════════════════════════════════════════════════════════════════
        // 生成
        // ═════════════════════════════════════════════════════════════════
        private void DoGenerate()
        {
            _btnFinish.Enabled = false;
            try
            {
                if (!ReadParameters(out string parameterError)) { MessageBox.Show(parameterError, "参数无效"); return; }
                if (!_model.AllHingesReady || _model.ObjStaticTip == null || _model.ObjMovingTip == null || _model.Plane == null)
                { MessageBox.Show("选点或电极帽已失效，请返回第一步重新确认。", "输入不完整"); return; }
                if (!ReadOptionalBase(out string baseError))
                {
                    MessageBox.Show(baseError, "BASE 坐标", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                using (var undoScope = TxTools.Common.SceneUndoScope.Begin("生成焊钳及 TCPF"))
                {
                    if (_model.TargetKinematics == null)
                    {
                        MessageBox.Show("请在「目标焊枪设备节点」中选取焊枪根节点。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // 6. 建模状态检测
                    if (!_model.TargetKinematics.IsOpenForKinematicsModeling)
                    {
                        var dr = MessageBox.Show(
                            "焊枪设备尚未开启运动学建模状态。\n\n是否立即启用？",
                            "需要建模状态", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (dr != DialogResult.Yes) return;
                        try { (_model.TargetComponent as ITxComponent)?.SetModelingScope(); }
                        catch (Exception ex)
                        {
                            MessageBox.Show("启用建模状态失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                    _model.J1_Name = _txtJ1?.Text ?? "j1";
                    _model.J2_Name = _txtJ2?.Text ?? "j2";
                    _model.InputJ1_Name = _txtInputJ1?.Text ?? "input_j1";

                    // 需求3：TCP Frame 准备
                    // 若用户点选的是已有Frame(_model.TcpFrame已设)，直接用；
                    // 否则在点选位置创建名为 TCPF 的 Frame
                    if (_model.TcpFrame == null && _tcpPickedLocation != null && _model.TargetComponent != null)
                    {
                        string fErr;
                        var tcpf = PsSdkHelper.CreateFrame(_model.TargetComponent, "TCPF", _tcpPickedLocation, out fErr);
                        if (tcpf != null) _model.TcpFrame = tcpf;
                        else MessageBox.Show("创建TCPF失败: " + fErr + "\n焊钳定义的TCP将为空。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }

                    var result = _service.GenerateAll();
                    _txtResult.Text = result.Summary();
                    _txtResult.ForeColor = result.Success ? Color.DarkGreen : Color.DarkRed;

                    // 生成成功后启用完成按钮
                    if (result.Success)
                    {
                        // 问题5：OPEN适配成功后，几何已被驱动并置零为CLOSE状态。
                        // 自动取消勾选，避免再次点生成时重复驱动+置零导致过冲。
                        if (_model.OpenStateAdapt)
                        {
                            _model.OpenStateAdapt = false;
                            try { if (_chkOpenAdapt != null) _chkOpenAdapt.Checked = false; } catch { }
                        }
                    }
                    _btnFinish.Enabled = result.Success;

                }

            }
            catch (Exception ex)
            { MessageBox.Show("生成已停止：" + ex.Message + "\n如有部分变更，请在当前 PS 工程按 Ctrl+Z 撤销。", "焊钳生成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ═════════════════════════════════════════════════════════════════
        // 步骤导航
        // ═════════════════════════════════════════════════════════════════
        private void WireGenerationInvalidation(Control parent)
        {
            void InvalidateResult() { if (_btnFinish != null) _btnFinish.Enabled = false; }
            if (parent is TxObjGridCtrl grid)
            {
                grid.ObjectInserted += (sender, e) => InvalidateResult();
                grid.RowDeleted += (sender, e) => InvalidateResult();
                grid.Picked += (sender, e) => InvalidateResult();
            }
            else if (parent is TxObjEditBoxCtrl single)
            {
                single.Picked += (sender, e) => InvalidateResult();
                single.TypeValid += (sender, e) => InvalidateResult();
                single.TypeInvalid += (sender, e) => InvalidateResult();
            }
            else if (parent is TxFrameEditBoxCtrl frame)
            {
                frame.ValidFrameSet += (sender, e) => InvalidateResult();
                frame.InvalidFrameSet += (sender, e) => InvalidateResult();
            }
            else if (parent is TextBox text && !text.ReadOnly) text.TextChanged += (sender, e) => InvalidateResult();
            else if (parent is CheckBox check) check.CheckedChanged += (sender, e) => InvalidateResult();
            // SDK 控件内部有编辑框；只监听其公开事件。
            if (parent is TxObjGridCtrl || parent is TxObjEditBoxCtrl || parent is TxFrameEditBoxCtrl) return;
            foreach (Control child in parent.Controls) WireGenerationInvalidation(child);
        }

        private void RestoreExistingSetup()
        {
            if (!_service.TryRestoreSetup(out string restoreStatus)) return;
            _restoringSetup = true;
            try
            {
                _gridO.Object = _model.HingeO; _gridA.Object = _model.HingeA; _gridC.Object = _model.HingeC;
                _gridStaticTip.Object = _model.ObjStaticTip;
                _gridMovingTip.Object = _model.ObjMovingTip;
                _tcpEditBox.SetLocation(_model.TcpFrame.AbsoluteLocation);
                _tcpPickedLocation = _model.TcpFrame.AbsoluteLocation;
                if (_model.BaseLocation != null) _baseEditBox.SetLocation(_model.BaseLocation);
                else _baseEditBox.Text = string.Empty;
                _txtPistonStroke.Text = _model.PistonStroke.ToString();
                _txtOpenGap.Text = _model.OpenGap.ToString();
                _txtWear.Text = _model.WearAllowance.ToString();
                _chkOpenAdapt.Checked = _model.OpenStateAdapt;
                _chkOpenPositive.Checked = _model.OpenPositive;
            }
            finally { _restoringSetup = false; }
            foreach (var entry in _linkGrids) ResetGridObjects(entry.Item1, new List<ITxObject>(entry.Item2));
            _openGapUserEdited = true;
            RefreshParameterDisplay();
            RefreshAllLinkHighlights();
        }

        private void GoNext()
        {
            if (!ValidateStep(out string err))
            { MessageBox.Show(err, "请完成当前步骤", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            if (_currentStep == 0)
            {
                _model.PistonStroke = 100; _model.OpenGap = 185; _model.WearAllowance = 10;
                double v;
                if (double.TryParse(_txtPistonStroke?.Text, out v)) _model.PistonStroke = v;
                if (double.TryParse(_txtOpenGap?.Text, out v)) _model.OpenGap = v;
                if (double.TryParse(_txtWear?.Text, out v)) _model.WearAllowance = v;
                string calcErr;
                if (!_service.ComputeMechanismFromPoints(out calcErr))
                { MessageBox.Show("机构参数计算失败:\n" + calcErr, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                RefreshParameterDisplay();
                // 首次进入参数页：把计算的最大开口量自动填入开口量框
                SyncOpenGapFromCalc();
            }
            if (_currentStep == 1) RefreshFormulaPreview();

            _currentStep = System.Math.Min(_currentStep + 1, _stepPanels.Length - 1);
            UpdateStepUi();
        }

        private void GoBack() { _currentStep = System.Math.Max(_currentStep - 1, 0); UpdateStepUi(); }

        private bool ReadParameters(out string error)
        {
            error = null;
            if (!double.TryParse(_txtPistonStroke.Text, out double stroke) || !(stroke > 0) || double.IsInfinity(stroke) ||
                !double.TryParse(_txtOpenGap.Text, out double gap) || !(gap > 0) || double.IsInfinity(gap) ||
                !double.TryParse(_txtWear.Text, out double wear) || !(wear >= 0) || double.IsInfinity(wear))
            { error = "行程、开口必须是有限正数，磨量必须是有限非负数，请检查参数输入。"; return false; }
            _model.PistonStroke = stroke; _model.OpenGap = gap; _model.WearAllowance = wear;
            return true;
        }

        private void ResetSelectedSetup()
        {
            _restoringSetup = true;
            try
            {
                PsSdkHelper.Unhighlight(_model.ObjStaticTip); PsSdkHelper.Unhighlight(_model.ObjMovingTip);
                _model.ObjStaticTip = null; _model.ObjMovingTip = null;
                foreach (var entry in _linkGrids)
                { PsSdkHelper.UnhighlightAll(entry.Item2); entry.Item2.Clear(); ResetGridObjects(entry.Item1, new List<ITxObject>()); }
                _model.SynchronizeElectrodeBindings();
                _model.HasO = _model.HasA = _model.HasC = false;
                _model.HingeO = _model.HingeA = _model.HingeC = null;
                _model.Plane = null; _model.PlaneNormal = default(Vec3); _model.Mechanism = null; _model.Formulas = null;
                _model.WorldGunTcp = default(Vec3); _model.TcpFrame = null; _model.BaseLocation = null; _tcpPickedLocation = null;
                _openGapUserEdited = false;
                foreach (var picker in new[] { _gridO, _gridA, _gridC, _gridStaticTip, _gridMovingTip }) picker.Object = null;
                _tcpEditBox.Text = string.Empty; _baseEditBox.Text = string.Empty;
                foreach (var text in new[] { _txtRAuto, _txtDAuto, _txtDAO, _txtr, _txtL, _txtAlpha }) text.Clear();
                _txtCoordCard.Clear(); _txtFormula2.Clear(); _txtFormulaInput.Clear(); _txtResult.Clear();
                _btnFinish.Enabled = false;
            }
            finally { _restoringSetup = false; PsSdkHelper.RefreshView(); }
        }

        private bool ValidateStep(out string err)
        {
            err = null;
            if (_currentStep >= 1 && (!ReadParameters(out err) || !ReadOptionalBase(out err))) return false;
            if (_currentStep == 0)
            {
                // Step0：必须选取焊枪本体且开启建模状态
                if (_model.TargetKinematics == null) { err = "请先选取焊枪设备根节点"; return false; }
                bool open = false;
                try { open = _model.TargetKinematics.IsOpenForKinematicsModeling; } catch { }
                if (!open) { err = "焊枪未开启运动学建模状态，请点击「启用建模」按钮"; return false; }
                if (!ReadOptionalBase(out err)) return false;
                if (!_model.HasO) { err = "请设置 O 点坐标"; return false; }
                if (!_model.HasA) { err = "请设置 A 点坐标（A点同时确定参考平面法向）"; return false; }
                if (!_model.HasC) { err = "请设置 C 点坐标"; return false; }
                if (_model.ObjStaticTip == null) { err = "请选取静电极帽"; return false; }
                if (_model.ObjMovingTip == null) { err = "请选取动电极帽"; return false; }
                if (_model.ObjStaticTip.Equals(_model.ObjMovingTip)) { err = "静、动电极帽不能选择同一个对象"; return false; }
                if (_model.TcpFrame == null && _tcpPickedLocation == null) { err = "请选取焊钳TCP（静电极尖端坐标，用于臂长和开口计算）"; return false; }
                // 投影偏移已确定必须执行，不再提示
            }
            return true;
        }

        private bool ReadOptionalBase(out string error)
        {
            error = null;
            if (_baseEditBox == null || string.IsNullOrWhiteSpace(_baseEditBox.Text))
            {
                _model.BaseLocation = null;
                return true;
            }
            try
            {
                _model.BaseLocation = _baseEditBox.GetLocation();
                if (_model.BaseLocation != null) return true;
            }
            catch (Exception ex) { error = "无法读取 BASE 坐标：" + ex.Message; }
            error = error ?? "BASE 坐标无效，请重新选择，或点击清空以使用焊钳自身坐标。";
            return false;
        }

        private void SetStepPicking(Control parent, bool suspended)
        {
            if (parent is TxObjGridCtrl || parent is TxObjEditBoxCtrl || parent is TxFrameEditBoxCtrl)
            {
                PickFocus.SetSuspended(parent, suspended);
                return;
            }
            foreach (Control child in parent.Controls) SetStepPicking(child, suspended);
        }

        private void UpdateStepUi()
        {
            string[] titles = {
                "Step 1/3  --  焊钳、铰点与 Link 绑定",
                "Step 2/3  --  确认机构参数",
                "Step 3/3  --  生成运动学定义",
            };
            // 参数页及生成页禁止继续拾取；返回选择页时只恢复该页的拾取资格。
            for (int index = 0; index < _stepPanels.Length; index++)
                SetStepPicking(_stepPanels[index], index != _currentStep || _currentStep >= 1);
            _lblStepTitle.Text = titles[_currentStep];
            for (int i = 0; i < _stepPanels.Length; i++) _stepPanels[i].Visible = i == _currentStep;
            _btnBack.Enabled = _currentStep > 0;
            _btnNext.Visible = _currentStep < _stepPanels.Length - 1;
            _btnGenerate.Visible = _currentStep == _stepPanels.Length - 1;
            _lblStepProgress.Text = "步骤 " + (_currentStep + 1) + " / 3";
            _btnFinish.Visible = _currentStep == _stepPanels.Length - 1;
            // 返回前置步骤后需重新生成。
            if (_currentStep < _stepPanels.Length - 1) _btnFinish.Enabled = false;

            foreach (Control c in _stepIndicator.Controls)
            {
                if (c is Label lbl && lbl.Tag is int idx)
                {
                    string[] stepLabels = { "1. 选择与绑定", "2. 参数", "3. 生成" };
                    lbl.Text = (idx < _currentStep ? "✓  " : "") + stepLabels[idx];
                    lbl.Font = idx == _currentStep ? FormUiKit.BoldFont : FormUiKit.BaseFont;
                    lbl.BackColor = idx == _currentStep ? Color.FromArgb(229, 241, 252) : Color.White;
                    lbl.ForeColor = idx == _currentStep ? Color.FromArgb(0, 85, 155)
                        : idx < _currentStep ? Color.FromArgb(60, 100, 65) : Color.FromArgb(105, 110, 115);
                    lbl.Invalidate();
                }
            }
            FitStepHeight();
            if (_currentStep >= 1 && IsHandleCreated)
                BeginInvoke((Action)(() =>
                {
                    if (!IsDisposed && _currentStep >= 1 && _btnNext.Visible)
                    { ActiveControl = _btnNext; _btnNext.Focus(); }
                    else if (!IsDisposed && _currentStep >= 1 && _btnGenerate.Visible)
                    { ActiveControl = _btnGenerate; _btnGenerate.Focus(); }
                }));
        }

        private void FitStepHeight()
        {
            if (!_dpiApplied) return;
            int[] designHeights = { 680, 640, 640 };
            int target = (int)System.Math.Round(designHeights[_currentStep] * FormUiKit.GetLayoutScale(this));
            int available = Screen.FromControl(this).WorkingArea.Height - 16;
            Height = System.Math.Min(available, System.Math.Max(MinimumSize.Height, target));
        }
    }
}
