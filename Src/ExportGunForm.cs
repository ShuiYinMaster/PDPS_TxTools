// ExportGunForm.cs  —  C# 7.3
//
// 重构说明（基于确认的 API 文档）：
//
// [重构1] 窗体基类：Form → TxForm
// [重构2] 操作拾取：TxObjEditBoxCtrl
//         确认 API：.Object 属性, .ListenToPick 属性, Picked 事件
//         事件参数：TxObjEditBoxCtrl_PickedEventArgs.Object / .IsValidObjectPicked
// [重构3] 参考坐标：TxFrameComboBoxCtrl
//         确认 API：.GetLocation(), .Clear(), .SelectFrame(), .ListenToPick
//         事件：ValidFrameSet / InvalidFrameSet / Picked
//         事件参数：TxFrameComboBoxCtrl_ValidFrameSetEventArgs.Location / .Object
// [重构4] 移除 P/Invoke 置顶，移除 Timer 轮询
//
// 注意：TxPickListener 无 Start/Stop 方法，仅靠构造+事件驱动

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;

namespace MyPlugin.ExportGun
{
    public partial class ExportGunForm : TxForm
    {
        // ── 状态 ──────────────────────────────────────────────────────
        private ExportService _svc;
        private List<OperationInfo> _ops = new List<OperationInfo>();
        private string _refFrameName = "世界坐标系";
        private double[] _refFrameMatrix;
        private string _outputFolder;

        // ── [重构2] PS 原生对象选择 ──────────────────────────────────
        private TxObjEditBoxCtrl _objEditOp;      // .Object 属性获取选中对象
        private ListView lvOps;                   // 显示用

        // ── [重构3] PS 原生坐标选择 ──────────────────────────────────
        private TxFrameComboBoxCtrl _frameCombo;  // .GetLocation() 获取坐标
        private Label lblRefCoordStatus;

        // ── 控件 ──────────────────────────────────────────────────────
        private Label lblListHint;
        private ComboBox cmbPointType;
        private CheckBox chkUseMfgName;
        private Label lblPointCount;
        private CheckBox chkExportTCP;
        private CheckBox chkGunOriginTCP;
        private CheckBox chkCustomGun;
        private TextBox txtGunModel;
        private Button btnBrowseGun;
        private RadioButton rbXml3d;
        private RadioButton rbCATProduct;
        private Button btnExportGun;
        private ComboBox cmbBallTarget;
        private ComboBox cmbBallOption;
        private NumericUpDown nudDiameter;
        private TextBox txtGeomSet;
        private TextBox txtNamePrefix;
        private Button btnExportBall;
        private RichTextBox rtbLog;
        private ProgressBar progressBar;
        private Label lblProgress;
        private Button btnPickOutput;
        private Button btnReset;
        private Button btnClose;
        private Button btnHelp;
        private Button btnExportExcel;
        private Button btnPickFromSel;

        // ── 颜色 ─────────────────────────────────────────────────────
        private static readonly Color C_H1 = Color.FromArgb(65, 150, 170);
        private static readonly Color C_H2G = Color.FromArgb(185, 148, 40);
        private static readonly Color C_H2B = Color.FromArgb(180, 100, 120);
        private static readonly Color C_H3 = Color.FromArgb(60, 148, 80);
        private static readonly Color C_CARD = Color.FromArgb(250, 251, 255); // 统一卡片背景

        // ════════════════════════════════════════════════════════════
        //  构造
        // ════════════════════════════════════════════════════════════
        public ExportGunForm(SynchronizationContext psCtx)
        {
            InitializeComponent();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BuildUI();

            if (System.ComponentModel.LicenseManager.UsageMode ==
                System.ComponentModel.LicenseUsageMode.Designtime) return;
            _svc = new ExportService(psCtx);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_svc == null) return;
            BindFrameComboEvents();
        }

        // ════════════════════════════════════════════════════════════
        //  [重构2] TxObjEditBoxCtrl 事件
        //  确认 API：e.Object = 拾取到的对象, e.IsValidObjectPicked
        // ════════════════════════════════════════════════════════════

        private void OnObjEditPicked(object sender, TxObjEditBoxCtrl_PickedEventArgs e)
        {
            // e.Object: public field, 拾取到的对象
            // e.IsValidObjectPicked: public field, 是否通过验证
            if (e.Object == null) return;

            ITxObject pickedObj = e.Object as ITxObject;
            if (pickedObj == null) return;

            ThreadPool.QueueUserWorkItem(delegate (object s)
            {
                List<OperationInfo> ops = null;
                try
                {
                    _svc.InvokeOnPs(delegate ()
                    {
                        ops = PsReader.ParsePickedObjectToOperations(pickedObj);
                    });
                }
                catch (Exception ex)
                {
                    UI(delegate () { Log("[错误] " + ex.Message); });
                }

                if (ops != null && ops.Count > 0)
                {
                    UI(delegate ()
                    {
                        foreach (var op in ops)
                        {
                            if (!_ops.Exists(o => o.Name == op.Name))
                                _ops.Add(op);
                        }
                        RefreshOpList();
                        UpdatePointCount();
                        Log("[PS] 通过原生选择器添加 " + ops.Count + " 个操作");
                    });
                }
            });
        }

        /// <summary>
        /// "添加到列表" 按钮：读取 TxObjEditBoxCtrl.Object 属性
        /// </summary>
        private void OnAddFromObjEdit(object sender, EventArgs e)
        {
            if (_objEditOp == null) return;

            // 确认 API：TxObjEditBoxCtrl.Object 属性
            ITxObject obj = _objEditOp.Object as ITxObject;
            if (obj == null)
            {
                Log("[PS] 选择器中无对象，请先在PS中选择操作");
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate (object s)
            {
                List<OperationInfo> ops = null;
                try
                {
                    _svc.InvokeOnPs(delegate ()
                    {
                        ops = PsReader.ParsePickedObjectToOperations(obj);
                    });
                }
                catch (Exception ex)
                {
                    UI(delegate () { Log("[错误] " + ex.Message); });
                }

                if (ops != null && ops.Count > 0)
                {
                    UI(delegate ()
                    {
                        foreach (var op in ops)
                        {
                            if (!_ops.Exists(o => o.Name == op.Name))
                                _ops.Add(op);
                        }
                        RefreshOpList();
                        UpdatePointCount();
                        Log("[PS] 添加 " + ops.Count + " 个操作");
                    });
                }
                else
                {
                    UI(delegate () { Log("[PS] 所选对象不包含可识别的操作"); });
                }
            });
        }

        // ════════════════════════════════════════════════════════════
        //  [重构3] TxFrameComboBoxCtrl 事件
        //  确认 API：e.Location = TxTransformation, e.Object = 拾取对象
        //  控件方法：.GetLocation(), .Clear(), .SelectFrame()
        // ════════════════════════════════════════════════════════════

        private void BindFrameComboEvents()
        {
            if (_frameCombo == null) return;
            try
            {
                _frameCombo.ValidFrameSet +=
                    new TxFrameComboBoxCtrl_ValidFrameSetEventHandler(OnFrameValidSet);
                _frameCombo.InvalidFrameSet +=
                    new TxFrameComboBoxCtrl_InvalidFrameSetEventHandler(OnFrameInvalidSet);
                _frameCombo.Picked +=
                    new TxFrameComboBoxCtrl_PickedEventHandler(OnFramePicked);
            }
            catch (Exception ex)
            {
                Log("[警告] TxFrameComboBoxCtrl 事件绑定失败: " + ex.Message);
            }
        }

        private void OnFrameValidSet(object sender, TxFrameComboBoxCtrl_ValidFrameSetEventArgs e)
        {
            // 确认 API：e.Location = TxTransformation, e.Object = 拾取对象
            try
            {
                TxTransformation tx = e.Location as TxTransformation;
                if (tx != null)
                {
                    double[] arr = PsReader.TxToArr(tx);
                    if (!PsReader.IsIdentity(arr))
                    {
                        // 获取名称
                        string name = "用户选定坐标系";
                        if (e.Object != null)
                        {
                            ITxObject txObj = e.Object as ITxObject;
                            if (txObj != null)
                            {
                                try { name = txObj.Name; }
                                catch { name = txObj.GetType().Name; }
                            }
                        }

                        _refFrameName = name;
                        _refFrameMatrix = arr;
                        UpdateRefCoordStatus(name, false);
                        Log("[坐标] 参考坐标已设置：" + name);
                        return;
                    }
                }

                // Location 为 null 或单位矩阵
                string fallbackName = "用户选定坐标系";
                if (e.Object != null)
                {
                    ITxObject txObj = e.Object as ITxObject;
                    if (txObj != null)
                        try { fallbackName = txObj.Name; } catch { }
                }
                _refFrameName = fallbackName;
                _refFrameMatrix = null;
                UpdateRefCoordStatus(fallbackName + " (世界坐标系)", true);
                Log("[坐标] 坐标系: " + fallbackName + " (与世界坐标系等同)");
            }
            catch (Exception ex)
            {
                Log("[坐标] 异常: " + ex.Message);
            }
        }

        private void OnFrameInvalidSet(object sender, TxFrameComboBoxCtrl_InvalidFrameSetEventArgs e)
        {
            Log("[坐标] 所选对象不是有效坐标系");
        }

        private void OnFramePicked(object sender, TxFrameComboBoxCtrl_PickedEventArgs e)
        {
            // ValidFrameSet 会自动触发后续处理
        }

        private void UpdateRefCoordStatus(string name, bool isWorld)
        {
            if (lblRefCoordStatus == null) return;
            lblRefCoordStatus.Text = "当前：" + name;
            lblRefCoordStatus.BackColor = isWorld
                ? Color.FromArgb(210, 252, 210) : Color.FromArgb(180, 220, 255);
            lblRefCoordStatus.ForeColor = isWorld
                ? Color.FromArgb(25, 110, 25) : Color.FromArgb(25, 60, 130);
        }

        // ════════════════════════════════════════════════════════════
        //  界面搭建
        // ════════════════════════════════════════════════════════════
        private void BuildUI()
        {
            SuspendLayout();
            Text = "导出插枪 / 点云到 CATIA";
            ClientSize = new Size(1650, 900);
            MinimumSize = new Size(960, 640);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            BackColor = Color.FromArgb(240, 242, 247);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill; root.ColumnCount = 1; root.RowCount = 3;
            root.Margin = Padding.Empty; root.Padding = new Padding(10, 8, 10, 8);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            root.Controls.Add(BuildHeaderRow(), 0, 0);
            root.Controls.Add(BuildBody(), 0, 1);
            root.Controls.Add(BuildBottom(), 0, 2);
            Controls.Add(root);
            ResumeLayout(false); PerformLayout();
        }

        private Control BuildHeaderRow()
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Fill; row.ColumnCount = 3; row.RowCount = 1;
            row.Margin = new Padding(0, 0, 0, 4); row.Padding = new Padding(4, 2, 4, 2);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            row.Controls.Add(MkHeader("通用信息", C_H1), 0, 0);
            row.Controls.Add(MkHeader("导插枪 / 点球", C_H2G), 1, 0);
            row.Controls.Add(MkHeader("日志信息", C_H3), 2, 0);
            return row;
        }

        private Control BuildBody()
        {
            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill; body.ColumnCount = 3; body.RowCount = 1;
            body.Margin = new Padding(0, 4, 0, 4); body.Padding = new Padding(4);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            body.Controls.Add(BuildCol1(), 0, 0);
            body.Controls.Add(BuildCol2(), 1, 0);
            body.Controls.Add(BuildCol3(), 2, 0);
            return body;
        }

        private Control BuildBottom()
        {
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill; bottom.ColumnCount = 2; bottom.RowCount = 1;
            bottom.Margin = new Padding(0, 4, 0, 0); bottom.Padding = new Padding(4);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            FlowLayoutPanel lf = new FlowLayoutPanel();
            lf.Dock = DockStyle.Fill; lf.FlowDirection = FlowDirection.LeftToRight;
            lf.WrapContents = false; lf.Padding = new Padding(4, 8, 4, 4);
            progressBar = new ProgressBar();
            progressBar.Height = 18; progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Width = 280; progressBar.Margin = new Padding(0, 2, 10, 2);
            lblProgress = new Label(); lblProgress.AutoSize = true;
            lblProgress.ForeColor = Color.FromArgb(75, 80, 92);
            lblProgress.Margin = new Padding(0, 2, 0, 2);
            lf.Controls.Add(progressBar); lf.Controls.Add(lblProgress);

            FlowLayoutPanel rf = new FlowLayoutPanel();
            rf.AutoSize = true; rf.FlowDirection = FlowDirection.LeftToRight;
            rf.WrapContents = false; rf.Padding = new Padding(0, 6, 0, 0);
            rf.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            btnPickOutput = MkBotBtn("输出目录", 80, false);
            btnReset = MkBotBtn("复位", 60, false);
            btnExportExcel = MkBotBtn("导出Excel", 80, false);
            btnClose = MkBotBtn("关闭", 60, false);
            btnHelp = MkBotBtn("?", 36, true);
            btnPickOutput.Click += new EventHandler(OnPickOutput);
            btnReset.Click += new EventHandler(OnReset);
            btnExportExcel.Click += new EventHandler(OnExportExcel);
            btnClose.Click += delegate { Close(); };
            btnHelp.Click += delegate { ShowHelp(); };
            rf.Controls.Add(btnPickOutput); rf.Controls.Add(btnReset);
            rf.Controls.Add(btnExportExcel); rf.Controls.Add(btnClose); rf.Controls.Add(btnHelp);
            bottom.Controls.Add(lf, 0, 0); bottom.Controls.Add(rf, 1, 0);
            return bottom;
        }

        // ════════════════════════════════════════════════════════════
        //  第1列：通用信息（卡片式布局，与第2列风格统一）
        // ════════════════════════════════════════════════════════════
        private Control BuildCol1()
        {
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true;
            scroll.Padding = new Padding(8, 6, 8, 6);

            TableLayoutPanel stack = new TableLayoutPanel();
            stack.Dock = DockStyle.Top; stack.AutoSize = true;
            stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stack.ColumnCount = 1;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.Padding = new Padding(6);

            // ═══════════════════ 卡片1：导出设置 ═══════════════════
            Panel settingsCard = new Panel();
            settingsCard.Dock = DockStyle.Fill; settingsCard.AutoSize = true;
            settingsCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            settingsCard.BackColor = C_CARD;
            settingsCard.Margin = new Padding(0, 0, 0, 10);

            FlowLayoutPanel settingsInner = new FlowLayoutPanel();
            settingsInner.Dock = DockStyle.Top; settingsInner.AutoSize = true;
            settingsInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            settingsInner.FlowDirection = FlowDirection.TopDown;
            settingsInner.WrapContents = false;

            Label lblSettingsTitle = MkCardTitle("导出设置", C_H1);
            settingsInner.Controls.Add(lblSettingsTitle);

            // 点类型
            TableLayoutPanel typeRow = MkFRow("点类型", 30);
            cmbPointType = MkCombo(new[] { "焊点（MFG）", "路径点（Via）", "连续点", "全部类型" });
            cmbPointType.SelectedIndex = 3;
            cmbPointType.SelectedIndexChanged += delegate { UpdatePointCount(); };
            typeRow.Controls.Add(cmbPointType, 1, 0);
            typeRow.Margin = new Padding(0, 5, 0, 5);
            settingsInner.Controls.Add(typeRow);

            chkUseMfgName = new CheckBox();
            chkUseMfgName.Text = "采用MFG名称";
            chkUseMfgName.AutoSize = true;
            chkUseMfgName.Margin = new Padding(0, 5, 0, 5);
            chkUseMfgName.CheckedChanged += delegate { UpdatePointCount(); };
            settingsInner.Controls.Add(chkUseMfgName);

            lblPointCount = new Label();
            lblPointCount.Text = "将导出点数量：0";
            lblPointCount.AutoSize = true;
            lblPointCount.ForeColor = Color.FromArgb(25, 110, 25);
            lblPointCount.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            lblPointCount.Margin = new Padding(0, 5, 0, 8);
            settingsInner.Controls.Add(lblPointCount);

            settingsCard.Controls.Add(settingsInner);
            stack.Controls.Add(settingsCard, 0, 0);

            // ═══════════════════ 卡片2：参考坐标 ═══════════════════
            Panel coordCard = new Panel();
            coordCard.Dock = DockStyle.Fill; coordCard.AutoSize = true;
            coordCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coordCard.BackColor = C_CARD;
            coordCard.Margin = new Padding(0, 0, 0, 10);

            FlowLayoutPanel coordInner = new FlowLayoutPanel();
            coordInner.Dock = DockStyle.Top; coordInner.AutoSize = true;
            coordInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            coordInner.FlowDirection = FlowDirection.TopDown;
            coordInner.WrapContents = false;

            Label lblCoordTitle = MkCardTitle("参考坐标", C_H1);
            coordInner.Controls.Add(lblCoordTitle);

            // PS原生坐标选择控件
            Panel frameCtrlPanel = new Panel();
            frameCtrlPanel.AutoSize = false;
            frameCtrlPanel.Height = 30;
            frameCtrlPanel.Width = 300;
            frameCtrlPanel.Margin = new Padding(0, 5, 0, 5);

            try
            {
                _frameCombo = new TxFrameComboBoxCtrl();
                _frameCombo.Dock = DockStyle.Fill;
                _frameCombo.ListenToPick = true; // 确认 API
                frameCtrlPanel.Controls.Add(_frameCombo);
            }
            catch (Exception ex)
            {
                Label fb = new Label();
                fb.Text = "坐标控件不可用: " + ex.Message;
                fb.Dock = DockStyle.Fill; fb.ForeColor = Color.FromArgb(180, 80, 80);
                fb.Font = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Regular, GraphicsUnit.Point);
                frameCtrlPanel.Controls.Add(fb);
            }
            coordInner.Controls.Add(frameCtrlPanel);

            // 坐标状态
            lblRefCoordStatus = new Label();
            lblRefCoordStatus.Text = "当前：世界坐标系";
            lblRefCoordStatus.AutoSize = false;
            lblRefCoordStatus.Height = 24; lblRefCoordStatus.Width = 300;
            lblRefCoordStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblRefCoordStatus.ForeColor = Color.FromArgb(25, 110, 25);
            lblRefCoordStatus.BackColor = Color.FromArgb(210, 252, 210);
            lblRefCoordStatus.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
            lblRefCoordStatus.Margin = new Padding(0, 0, 0, 5);
            lblRefCoordStatus.Padding = new Padding(6, 0, 6, 0);
            coordInner.Controls.Add(lblRefCoordStatus);

            // 重置按钮
            Button btnClrCoord = MkSmBtn("重置为世界坐标系");
            btnClrCoord.Margin = new Padding(0, 0, 0, 5);
            btnClrCoord.Click += delegate
            {
                _refFrameName = "世界坐标系"; _refFrameMatrix = null;
                if (_frameCombo != null)
                    try { _frameCombo.Clear(); } catch { } // 确认 API
                UpdateRefCoordStatus("世界坐标系", true);
                Log("[坐标] 已重置为世界坐标系");
            };
            coordInner.Controls.Add(btnClrCoord);

            // 提示
            Label lblCoordHint = new Label();
            lblCoordHint.Text = "在PS中选择Component/Frame即可自动获取";
            lblCoordHint.AutoSize = true;
            lblCoordHint.ForeColor = Color.FromArgb(100, 130, 160);
            lblCoordHint.Font = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            lblCoordHint.Margin = new Padding(0, 0, 0, 5);
            coordInner.Controls.Add(lblCoordHint);

            coordCard.Controls.Add(coordInner);
            stack.Controls.Add(coordCard, 0, 1);

            // ═══════════════════ 卡片3：操作选择 ═══════════════════
            Panel opsCard = new Panel();
            opsCard.Dock = DockStyle.Fill; opsCard.AutoSize = true;
            opsCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            opsCard.BackColor = C_CARD;
            opsCard.Margin = new Padding(0, 0, 0, 10);

            FlowLayoutPanel opsInner = new FlowLayoutPanel();
            opsInner.Dock = DockStyle.Top; opsInner.AutoSize = true;
            opsInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            opsInner.FlowDirection = FlowDirection.TopDown;
            opsInner.WrapContents = false;

            Label lblOpsTitle = MkCardTitle("操作选择", C_H1);
            opsInner.Controls.Add(lblOpsTitle);

            // PS原生对象选择控件
            Panel objEditPanel = new Panel();
            objEditPanel.AutoSize = false;
            objEditPanel.Height = 30;
            objEditPanel.Width = 300;
            objEditPanel.Margin = new Padding(0, 5, 0, 5);

            try
            {
                _objEditOp = new TxObjEditBoxCtrl();
                _objEditOp.Dock = DockStyle.Fill;
                _objEditOp.ListenToPick = true;  // 确认 API
                _objEditOp.Picked += new TxObjEditBoxCtrl_PickedEventHandler(OnObjEditPicked);
                objEditPanel.Controls.Add(_objEditOp);
            }
            catch (Exception ex)
            {
                Label fb = new Label();
                fb.Text = "选择控件不可用: " + ex.Message;
                fb.Dock = DockStyle.Fill; fb.ForeColor = Color.FromArgb(180, 80, 80);
                fb.Font = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Regular, GraphicsUnit.Point);
                objEditPanel.Controls.Add(fb);
            }
            opsInner.Controls.Add(objEditPanel);

            // 按钮行
            FlowLayoutPanel pickRow = new FlowLayoutPanel();
            pickRow.AutoSize = true;
            pickRow.FlowDirection = FlowDirection.LeftToRight;
            pickRow.WrapContents = false;
            pickRow.Margin = new Padding(0, 0, 0, 5);

            Button btnAdd = MkFuncBtn("◄ 添加到列表", C_H1);
            btnAdd.Click += new EventHandler(OnAddFromObjEdit);
            btnPickFromSel = MkFuncBtn("◄ 拾取自PS", Color.FromArgb(85, 140, 160));
            btnPickFromSel.Click += new EventHandler(OnPickSel);
            Button btnClear = MkFuncBtn("清空列表", Color.FromArgb(148, 152, 165));
            btnClear.Click += new EventHandler(OnClearOps);
            pickRow.Controls.Add(btnAdd);
            pickRow.Controls.Add(btnPickFromSel);
            pickRow.Controls.Add(btnClear);
            opsInner.Controls.Add(pickRow);

            // 操作列表（嵌入卡片内部）
            Panel pnlList = new Panel();
            pnlList.AutoSize = false;
            pnlList.Height = 220; pnlList.Width = 300;
            pnlList.BackColor = Color.White;
            pnlList.Padding = new Padding(2);
            pnlList.Margin = new Padding(0, 0, 0, 5);
            SetBorder(pnlList, Color.FromArgb(180, 188, 208), 1);

            lvOps = new ListView();
            lvOps.Dock = DockStyle.Fill; lvOps.View = View.Details;
            lvOps.FullRowSelect = true; lvOps.CheckBoxes = true;
            lvOps.GridLines = false; lvOps.BorderStyle = BorderStyle.None;
            lvOps.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvOps.BackColor = Color.White;
            lvOps.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            lvOps.Columns.Add("操作名称", -2);
            lvOps.ItemChecked += delegate { UpdatePointCount(); };

            lblListHint = new Label();
            lblListHint.Text = "列表为空";
            lblListHint.Dock = DockStyle.Bottom; lblListHint.Height = 20;
            lblListHint.TextAlign = ContentAlignment.MiddleCenter;
            lblListHint.ForeColor = Color.FromArgb(135, 140, 152);
            lblListHint.BackColor = Color.FromArgb(245, 247, 252);
            lblListHint.Font = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            pnlList.Controls.Add(lvOps);
            pnlList.Controls.Add(lblListHint);
            opsInner.Controls.Add(pnlList);

            opsCard.Controls.Add(opsInner);
            stack.Controls.Add(opsCard, 0, 2);

            // 标题宽度跟随卡片
            stack.SizeChanged += (s, ev) =>
            {
                int tw = stack.Width - stack.Padding.Horizontal;
                if (tw > 0)
                {
                    lblSettingsTitle.Width = tw;
                    lblCoordTitle.Width = tw;
                    lblOpsTitle.Width = tw;
                    // 内部控件宽度跟随
                    int innerW = tw - 12; // 留一点 padding
                    if (innerW > 100)
                    {
                        frameCtrlPanel.Width = innerW;
                        lblRefCoordStatus.Width = innerW;
                        objEditPanel.Width = innerW;
                        pnlList.Width = innerW;
                    }
                }
            };

            scroll.Controls.Add(stack);
            return scroll;
        }

        // ════════════════════════════════════════════════════════════
        //  第2列：导插枪 + 导点球
        // ════════════════════════════════════════════════════════════
        private Control BuildCol2()
        {
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true;
            scroll.Padding = new Padding(8, 6, 8, 6);

            TableLayoutPanel stack = new TableLayoutPanel();
            stack.Dock = DockStyle.Top; stack.AutoSize = true;
            stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stack.ColumnCount = 1;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.Padding = new Padding(6);

            // 导插枪卡片
            Panel gunCard = new Panel();
            gunCard.Dock = DockStyle.Fill; gunCard.AutoSize = true;
            gunCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            gunCard.BackColor = C_CARD;
            gunCard.Margin = new Padding(0, 0, 0, 10);

            FlowLayoutPanel gunInner = new FlowLayoutPanel();
            gunInner.Dock = DockStyle.Top; gunInner.AutoSize = true;
            gunInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            gunInner.FlowDirection = FlowDirection.TopDown; gunInner.WrapContents = false;

            Label lblGunTitle = MkCardTitle("导插枪", C_H2G);
            gunInner.Controls.Add(lblGunTitle);

            TableLayoutPanel fmtRow = MkFRow("导出为", 30);
            FlowLayoutPanel fmtFlow = new FlowLayoutPanel();
            fmtFlow.AutoSize = true; fmtFlow.FlowDirection = FlowDirection.LeftToRight;
            fmtFlow.WrapContents = false; fmtFlow.Margin = Padding.Empty;
            rbXml3d = new RadioButton(); rbXml3d.Text = "3dxml"; rbXml3d.Checked = true;
            rbXml3d.AutoSize = true; rbXml3d.Margin = new Padding(0, 2, 10, 2);
            rbCATProduct = new RadioButton(); rbCATProduct.Text = "CATProduct";
            rbCATProduct.AutoSize = true; rbCATProduct.Margin = new Padding(0, 2, 0, 2);
            fmtFlow.Controls.Add(rbXml3d); fmtFlow.Controls.Add(rbCATProduct);
            fmtRow.Controls.Add(fmtFlow, 1, 0); fmtRow.Margin = new Padding(0, 5, 0, 5);
            gunInner.Controls.Add(fmtRow);

            FlowLayoutPanel customFlow = new FlowLayoutPanel();
            customFlow.AutoSize = true; customFlow.FlowDirection = FlowDirection.LeftToRight;
            customFlow.WrapContents = false; customFlow.Margin = new Padding(0, 5, 0, 5);
            chkCustomGun = new CheckBox(); chkCustomGun.Text = "自定义焊枪数模";
            chkCustomGun.AutoSize = true; chkCustomGun.Margin = new Padding(0, 4, 10, 4);
            txtGunModel = new TextBox(); txtGunModel.Width = 140;
            txtGunModel.ReadOnly = true; txtGunModel.Enabled = false;
            txtGunModel.Margin = new Padding(0, 4, 10, 4);
            btnBrowseGun = MkSmBtn("选择"); btnBrowseGun.Enabled = false;
            chkCustomGun.CheckedChanged += delegate
            { txtGunModel.Enabled = chkCustomGun.Checked; btnBrowseGun.Enabled = chkCustomGun.Checked; };
            btnBrowseGun.Click += new EventHandler(OnBrowseGun);
            customFlow.Controls.Add(chkCustomGun); customFlow.Controls.Add(txtGunModel);
            customFlow.Controls.Add(btnBrowseGun); gunInner.Controls.Add(customFlow);

            chkGunOriginTCP = new CheckBox(); chkGunOriginTCP.Text = "焊枪以TCP为原点";
            chkGunOriginTCP.AutoSize = true; chkGunOriginTCP.Checked = true;
            chkGunOriginTCP.Margin = new Padding(0, 5, 0, 5); gunInner.Controls.Add(chkGunOriginTCP);

            chkExportTCP = new CheckBox(); chkExportTCP.Text = "导出TCP坐标";
            chkExportTCP.AutoSize = true; chkExportTCP.Margin = new Padding(0, 5, 0, 5);
            gunInner.Controls.Add(chkExportTCP);

            btnExportGun = MkActBtn("导出插枪", C_H2G);
            btnExportGun.Click += new EventHandler(OnExportGun);
            btnExportGun.Margin = new Padding(0, 10, 0, 5); gunInner.Controls.Add(btnExportGun);
            gunCard.Controls.Add(gunInner); stack.Controls.Add(gunCard, 0, 0);

            // 导点球卡片
            Panel ballCard = new Panel();
            ballCard.Dock = DockStyle.Fill; ballCard.AutoSize = true;
            ballCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ballCard.BackColor = C_CARD;
            ballCard.Margin = new Padding(0, 0, 0, 10);

            FlowLayoutPanel ballInner = new FlowLayoutPanel();
            ballInner.Dock = DockStyle.Top; ballInner.AutoSize = true;
            ballInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ballInner.FlowDirection = FlowDirection.TopDown; ballInner.WrapContents = false;

            Label lblBallTitle = MkCardTitle("导点球", C_H2B);
            ballInner.Controls.Add(lblBallTitle);

            TableLayoutPanel tgtRow = MkFRow("导出到", 28);
            cmbBallTarget = MkCombo(new[] { "当前Part文档", "新建Part文档" });
            tgtRow.Controls.Add(cmbBallTarget, 1, 0); tgtRow.Margin = new Padding(0, 5, 0, 5);
            ballInner.Controls.Add(tgtRow);

            TableLayoutPanel optRow = MkFRow("导出选项", 28);
            cmbBallOption = MkCombo(new[] { "轨迹点 + 点球", "仅轨迹点", "仅点球" });
            optRow.Controls.Add(cmbBallOption, 1, 0); optRow.Margin = new Padding(0, 5, 0, 5);
            ballInner.Controls.Add(optRow);

            TableLayoutPanel diamRow = MkFRow("球直径(mm)", 28);
            nudDiameter = new NumericUpDown();
            nudDiameter.Minimum = 1; nudDiameter.Maximum = 500; nudDiameter.Value = 10;
            nudDiameter.Width = 80; nudDiameter.Margin = new Padding(0, 4, 0, 4);
            diamRow.Controls.Add(nudDiameter, 1, 0); diamRow.Margin = new Padding(0, 5, 0, 5);
            ballInner.Controls.Add(diamRow);

            TableLayoutPanel geomRow = MkFRow("几何集", 28);
            txtGeomSet = new TextBox(); txtGeomSet.Text = "Geometry_Spheres";
            txtGeomSet.Dock = DockStyle.Fill; geomRow.Controls.Add(txtGeomSet, 1, 0);
            geomRow.Margin = new Padding(0, 5, 0, 5); ballInner.Controls.Add(geomRow);

            TableLayoutPanel prefixRow = MkFRow("名称前缀", 28);
            txtNamePrefix = new TextBox(); txtNamePrefix.Text = "SPHERE";
            txtNamePrefix.Dock = DockStyle.Fill; prefixRow.Controls.Add(txtNamePrefix, 1, 0);
            prefixRow.Margin = new Padding(0, 5, 0, 5); ballInner.Controls.Add(prefixRow);

            btnExportBall = MkActBtn("导出点球（到Part）", C_H2B);
            btnExportBall.Click += new EventHandler(OnExportBall);
            btnExportBall.Margin = new Padding(0, 10, 0, 5); ballInner.Controls.Add(btnExportBall);
            ballCard.Controls.Add(ballInner); stack.Controls.Add(ballCard, 0, 1);

            stack.SizeChanged += (s, ev) =>
            {
                int tw = stack.Width - stack.Padding.Horizontal;
                if (tw > 0) { lblGunTitle.Width = tw; lblBallTitle.Width = tw; }
            };
            scroll.Controls.Add(stack); return scroll;
        }

        private Control BuildCol3()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill; panel.Padding = new Padding(4);
            panel.MinimumSize = new Size(280, 0);

            rtbLog = new RichTextBox();
            rtbLog.Dock = DockStyle.Fill; rtbLog.BackColor = Color.FromArgb(20, 22, 27);
            rtbLog.ForeColor = Color.FromArgb(180, 210, 180);
            rtbLog.ReadOnly = true; rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.Font = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;

            Button btnClear = new Button();
            btnClear.Text = "清除日志"; btnClear.Dock = DockStyle.Top; btnClear.Height = 26;
            btnClear.FlatStyle = FlatStyle.Flat; btnClear.BackColor = Color.FromArgb(38, 42, 50);
            btnClear.ForeColor = Color.FromArgb(160, 165, 175);
            btnClear.Cursor = Cursors.Hand; btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += delegate { rtbLog.Clear(); };
            panel.Controls.Add(rtbLog); panel.Controls.Add(btnClear);
            return panel;
        }

        // ════════════════════════════════════════════════════════════
        //  PS 数据加载 / 列表管理
        // ════════════════════════════════════════════════════════════
        private void OnPickSel(object sender, EventArgs e)
        {
            Log("[PS] 从PS当前选中拾取...");
            btnPickFromSel.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<OperationInfo> ops = null;
                try { ops = _svc.LoadFromSelection(new Action<string>(Log)); }
                catch (Exception ex) { UI(delegate () { Log("[错误] " + ex.Message); }); }
                var final = ops ?? new List<OperationInfo>();
                UI(delegate ()
                {
                    _ops = final; RefreshOpList(); UpdatePointCount();
                    btnPickFromSel.Enabled = true;
                });
            });
        }

        private void OnClearOps(object sender, EventArgs e)
        {
            lvOps.Items.Clear(); _ops.Clear(); UpdatePointCount();
            // 清空原生选择器：确认 API
            if (_objEditOp != null) _objEditOp.Object = null;
            lblListHint.Text = "列表为空";
            lblListHint.ForeColor = Color.FromArgb(135, 140, 152);
        }

        private void RefreshOpList()
        {
            lvOps.Items.Clear(); lvOps.Columns[0].Width = -2;
            foreach (OperationInfo op in _ops)
            {
                ListViewItem item = new ListViewItem(op.Name);
                item.Checked = true; item.Tag = op;
                lvOps.Items.Add(item);
            }
            lblListHint.Text = _ops.Count > 0 ? "共 " + _ops.Count + " 个操作" : "列表为空";
            lblListHint.ForeColor = _ops.Count > 0
                ? Color.FromArgb(45, 110, 50) : Color.FromArgb(135, 140, 152);
            if (_ops.Count > 0)
            {
                Log("[PS] 加载 " + _ops.Count + " 个操作");
                LogOperationTools();
                if (_refFrameName == "世界坐标系" && _refFrameMatrix == null)
                    AutoLoadRefFrameFromOps();
            }
        }

        private void LogOperationTools()
        {
            var ops = new List<OperationInfo>(_ops);
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (OperationInfo op in ops)
                {
                    string toolName = null;
                    try { _svc.InvokeOnPs(delegate () { toolName = PsReader.GetToolNameFromOperation(op); }); } catch { }
                    string on = op.Name; string tn = toolName;
                    UI(delegate ()
                    { Log("[工具] " + on + " → " + (string.IsNullOrEmpty(tn) ? "未绑定" : tn)); });
                }
            });
        }

        private void AutoLoadRefFrameFromOps()
        {
            var ops = new List<OperationInfo>(_ops);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Tuple<string, double[]> r = null;
                try
                {
                    _svc.InvokeOnPs(delegate ()
                    {
                        foreach (var op in ops) { r = PsReader.GetRefFrameFromOperation(op); if (r != null) break; }
                        if (r == null) r = PsReader.GetReferenceFrame();
                    });
                }
                catch { }
                if (r == null || r.Item1 == "世界坐标系") return;
                var rr = r;
                UI(delegate ()
                {
                    if (_refFrameName == "世界坐标系" && _refFrameMatrix == null)
                    {
                        _refFrameName = rr.Item1; _refFrameMatrix = rr.Item2;
                        UpdateRefCoordStatus(rr.Item1, false);
                        Log("[坐标] 自动获取参考坐标：" + rr.Item1);
                    }
                });
            });
        }

        private List<OperationInfo> GetCheckedOps()
        {
            var list = new List<OperationInfo>();
            foreach (ListViewItem item in lvOps.Items)
                if (item.Checked && item.Tag is OperationInfo)
                    list.Add((OperationInfo)item.Tag);
            return list;
        }

        private void UpdatePointCount()
        {
            try
            {
                var ops = GetCheckedOps();
                if (ops.Count == 0)
                { lblPointCount.Text = "将导出点数量：0（未勾选）"; lblPointCount.ForeColor = Color.FromArgb(135, 70, 70); return; }
                int n = _svc.PreviewPointCount(ops, GetPtType(), chkUseMfgName.Checked, delegate { });
                lblPointCount.Text = "将导出点数量：" + n;
                lblPointCount.ForeColor = n > 0 ? Color.FromArgb(20, 110, 20) : Color.FromArgb(148, 50, 50);
            }
            catch { }
        }

        private PointType GetPtType()
        {
            switch (cmbPointType.SelectedIndex)
            {
                case 0: return PointType.WeldPoint; case 1: return PointType.PathPoint;
                case 2: return PointType.ContinuousPoint; default: return PointType.All;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  事件处理（导出等）
        // ════════════════════════════════════════════════════════════
        private void OnBrowseGun(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择焊枪数模";
                dlg.Filter = "CATIA文件|*.CATProduct;*.CATPart;*.cgr|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                { txtGunModel.Tag = dlg.FileName; txtGunModel.Text = Path.GetFileName(dlg.FileName); }
            }
        }

        private void OnPickOutput(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择导出目录";
                dlg.SelectedPath = _outputFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (dlg.ShowDialog() == DialogResult.OK)
                { _outputFolder = dlg.SelectedPath; Log("[设置] 输出目录：" + _outputFolder); }
            }
        }

        private void OnExportExcel(object sender, EventArgs e)
        {
            var ops = GetCheckedOps();
            if (ops.Count == 0) { MessageBox.Show("请先拾取操作。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SetBusy(true); Log("[Excel] 开始导出，参考坐标：" + _refFrameName);
            _svc.ExportExcelAsync(ops, GetPtType(), chkUseMfgName.Checked,
                _outputFolder ?? DefaultOut(), _refFrameMatrix, _refFrameName,
                new Action<string>(msg => UI(delegate () { Log(msg); })),
                new Action<bool, string>((ok, result) => UI(delegate ()
                {
                    SetBusy(false);
                    if (ok) { Log("✓ Excel：" + result); if (MessageBox.Show("成功：\n" + result + "\n\n打开？", "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) try { System.Diagnostics.Process.Start(result); } catch { } }
                    else { Log("✗ 失败：" + result); MessageBox.Show("失败：\n" + result, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                })));
        }

        private void OnExportGun(object sender, EventArgs e)
        {
            var ops = GetCheckedOps();
            if (ops.Count == 0) { MessageBox.Show("请先勾选操作。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            SetBusy(true);
            var p = new GunExportParams { Operations = ops, ExportTCP = chkExportTCP.Checked,
                GunOriginAtTCP = chkGunOriginTCP.Checked,
                CustomModelPath = chkCustomGun.Checked ? (txtGunModel.Tag as string ?? txtGunModel.Text) : null,
                Format = rbXml3d.Checked ? ExportFormat.Xml3d : ExportFormat.CATProduct,
                OutputPath = _outputFolder ?? DefaultOut(), RefMatrix = _refFrameMatrix, RefName = _refFrameName };
            _svc.ExportGunsAsync(p, new Action<string>(msg => UI(delegate () { Log(msg); })),
                new Action<ExportProgress>(pg => UI(delegate () { SetProgress(pg); })),
                new Action<bool, string>((ok, msg) => UI(delegate ()
                { SetBusy(false); Log(ok ? "✓ " + msg : "✗ " + msg); ShowResult(ok, msg); })));
        }

        private void OnExportBall(object sender, EventArgs e)
        {
            var ops = GetCheckedOps();
            if (ops.Count == 0) { MessageBox.Show("请先勾选操作。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            SetBusy(true);
            var optMap = new[] { BallExportOption.TrajectoryAndBall, BallExportOption.TrajectoryOnly, BallExportOption.BallOnly };
            var p = new BallExportParams { Operations = ops, ExportToCurrentDoc = cmbBallTarget.SelectedIndex == 0,
                Option = optMap[cmbBallOption.SelectedIndex], BallDiameter = (double)nudDiameter.Value,
                OutputPath = _outputFolder ?? DefaultOut(), PointFilter = GetPtType(), UseMfgName = chkUseMfgName.Checked,
                GeomSetName = string.IsNullOrWhiteSpace(txtGeomSet.Text) ? "Geometry_Spheres" : txtGeomSet.Text.Trim(),
                NamePrefix = string.IsNullOrWhiteSpace(txtNamePrefix.Text) ? "SPHERE" : txtNamePrefix.Text.Trim(),
                RefMatrix = _refFrameMatrix, RefName = _refFrameName };
            _svc.ExportBallsAsync(p, new Action<string>(msg => UI(delegate () { Log(msg); })),
                new Action<ExportProgress>(pg => UI(delegate () { SetProgress(pg); })),
                new Action<bool, string>((ok, msg) => UI(delegate ()
                { SetBusy(false); Log(ok ? "✓ " + msg : "✗ " + msg); ShowResult(ok, msg); })));
        }

        private void OnReset(object sender, EventArgs e)
        {
            chkExportTCP.Checked = false; chkGunOriginTCP.Checked = true;
            chkCustomGun.Checked = false; txtGunModel.Text = ""; txtGunModel.Tag = null;
            rbXml3d.Checked = true; cmbPointType.SelectedIndex = 3;
            cmbBallTarget.SelectedIndex = 0; cmbBallOption.SelectedIndex = 0;
            nudDiameter.Value = 10; txtGeomSet.Text = "Geometry_Spheres";
            txtNamePrefix.Text = "SPHERE"; chkUseMfgName.Checked = false;
            _refFrameName = "世界坐标系"; _refFrameMatrix = null;
            UpdateRefCoordStatus("世界坐标系", true);
            if (_frameCombo != null) try { _frameCombo.Clear(); } catch { }  // 确认 API
            if (_objEditOp != null) _objEditOp.Object = null;               // 确认 API
            progressBar.Value = 0; lblProgress.Text = "";
            lvOps.Items.Clear(); _ops.Clear();
            lblListHint.Text = "列表为空"; lblListHint.ForeColor = Color.FromArgb(135, 140, 152);
            Log("[操作] 已复位所有设置");
        }

        // ════════════════════════════════════════════════════════════
        //  UI 辅助
        // ════════════════════════════════════════════════════════════
        private void SetBusy(bool busy)
        {
            btnExportGun.Enabled = !busy; btnExportBall.Enabled = !busy;
            btnPickFromSel.Enabled = !busy; btnExportExcel.Enabled = !busy;
            if (busy) { progressBar.Value = 0; lblProgress.Text = "运行中..."; }
        }

        private void SetProgress(ExportProgress p)
        {
            if (p.Total <= 0) return;
            progressBar.Value = Math.Min((int)((double)p.Current / p.Total * 100), 100);
            lblProgress.Text = p.Current + "/" + p.Total + "  " + p.CurrentItem;
        }

        private void Log(string msg)
        {
            if (rtbLog == null || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), msg); return; }
            Color c;
            if (msg.StartsWith("✓") || msg.Contains("完成")) c = Color.FromArgb(90, 210, 110);
            else if (msg.StartsWith("✗") || msg.Contains("失败") || msg.Contains("错误")) c = Color.FromArgb(228, 88, 88);
            else if (msg.Contains("⚠")) c = Color.FromArgb(228, 180, 70);
            else if (msg.StartsWith("[PS]")) c = Color.FromArgb(110, 180, 228);
            else if (msg.StartsWith("[坐标]")) c = Color.FromArgb(160, 200, 255);
            else if (msg.StartsWith("[Excel]")) c = Color.FromArgb(180, 228, 160);
            else c = Color.FromArgb(178, 200, 178);
            rtbLog.SelectionStart = rtbLog.TextLength; rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = c;
            rtbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            rtbLog.ScrollToCaret();
        }

        private void UI(Action act) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(act); else act(); }
        private void ShowResult(bool ok, string msg) { MessageBox.Show(msg, ok ? "完成" : "失败", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error); }

        private void ShowHelp()
        {
            MessageBox.Show(
                "导出插枪 / 点云到 CATIA\n\n" +
                "【参考坐标】使用PS原生坐标选择控件\n" +
                "【拾取操作】方式1: 上方原生选择器自动拾取并[添加到列表]\n" +
                "          方式2: 在PS选中后点击[拾取自PS]\n" +
                "【导出】勾选操作后点击底栏按钮",
                "帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ════════════════════════════════════════════════════════════
        //  窗体关闭
        // ════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_frameCombo != null)
            {
                try { _frameCombo.ValidFrameSet -= new TxFrameComboBoxCtrl_ValidFrameSetEventHandler(OnFrameValidSet); } catch { }
                try { _frameCombo.InvalidFrameSet -= new TxFrameComboBoxCtrl_InvalidFrameSetEventHandler(OnFrameInvalidSet); } catch { }
                try { _frameCombo.Picked -= new TxFrameComboBoxCtrl_PickedEventHandler(OnFramePicked); } catch { }
            }
            if (_objEditOp != null)
                try { _objEditOp.Picked -= new TxObjEditBoxCtrl_PickedEventHandler(OnObjEditPicked); } catch { }
            if (_svc != null) _svc.Dispose();
            base.OnFormClosing(e);
        }

        private static string DefaultOut() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CatiaExport");

        // ════════════════════════════════════════════════════════════
        //  控件工厂
        // ════════════════════════════════════════════════════════════
        private static Label MkHeader(string text, Color bg)
        {
            var lbl = new Label { Text = text, Dock = DockStyle.Fill, BackColor = bg, ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(2, 0, 2, 0) };
            lbl.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
            return lbl;
        }

        private static Label MkCardTitle(string text, Color accent)
        {
            var lbl = new Label { Text = text, AutoSize = false, Height = 32, Width = 300,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = accent,
                ForeColor = Color.White, Padding = new Padding(8, 0, 0, 0), Margin = new Padding(0) };
            lbl.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            return lbl;
        }

        private static TableLayoutPanel MkFRow(string label, int h)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 3, 0, 3) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lbl = new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(50, 55, 68),
                Margin = new Padding(0, 0, 8, 0), MaximumSize = new Size(88, 0) };
            row.Controls.Add(lbl, 0, 0); return row;
        }

        private static ComboBox MkCombo(string[] items)
        {
            var c = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 0, 3), IntegralHeight = false, MaxDropDownItems = 10, DropDownWidth = 380 };
            c.Items.AddRange(items); c.SelectedIndex = 0; return c;
        }

        private static RoundedButton MkSmBtn(string text)
        {
            var b = new RoundedButton { Text = text, AutoSize = true, MinimumSize = new Size(52, 30), CornerRadius = 6,
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(220, 224, 232), ForeColor = Color.FromArgb(42, 48, 60),
                Cursor = Cursors.Hand, Margin = new Padding(0, 2, 6, 0), Padding = new Padding(8, 4, 8, 4) };
            b.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            b.FlatAppearance.BorderSize = 0; return b;
        }

        private static Button MkFuncBtn(string text, Color accent)
        {
            var b = new RoundedButton { Text = text, AutoSize = true, MinimumSize = new Size(110, 34), CornerRadius = 8,
                FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 10, 0), Padding = new Padding(10, 4, 10, 4) };
            b.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            b.FlatAppearance.BorderSize = 0; return b;
        }

        private static Button MkActBtn(string text, Color accent)
        {
            var b = new RoundedButton { Text = text, AutoSize = true, MinimumSize = new Size(160, 40),
                Dock = DockStyle.Top, CornerRadius = 10, FlatStyle = FlatStyle.Flat, BackColor = accent,
                ForeColor = Color.White, Cursor = Cursors.Hand, Margin = new Padding(0, 4, 0, 6),
                Padding = new Padding(12, 6, 12, 6) };
            b.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            b.FlatAppearance.BorderSize = 0; return b;
        }

        private static Button MkBotBtn(string text, int minW, bool accent)
        {
            var b = new RoundedButton { Text = text, AutoSize = true, Height = 36, MinimumSize = new Size(minW, 36),
                CornerRadius = 8, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0), Padding = new Padding(8, 4, 8, 4) };
            b.BackColor = accent ? Color.FromArgb(46, 114, 174) : Color.FromArgb(225, 228, 236);
            b.ForeColor = accent ? Color.White : Color.FromArgb(42, 48, 60);
            b.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            b.FlatAppearance.BorderSize = 0; return b;
        }

        private static void SetBorder(Control c, Color color, int width)
        { c.Tag = new object[] { color, width }; c.Paint -= OnBorderPaint; c.Paint += OnBorderPaint; }

        private static void OnBorderPaint(object sender, PaintEventArgs e)
        {
            var c = sender as Control; if (c == null) return;
            var arr = c.Tag as object[]; if (arr == null || arr.Length < 2) return;
            using (var pen = new Pen((Color)arr[0], (int)arr[1]))
                e.Graphics.DrawRectangle(pen, 0, 0, c.Width - 1, c.Height - 1);
        }

        private class RoundedButton : Button
        {
            public int CornerRadius { get; set; } = 8;
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
                var rect = new Rectangle(0, 0, Width, Height);
                using (var path = RoundRect(rect, CornerRadius))
                using (Brush b = new SolidBrush(Enabled ? BackColor : Color.FromArgb(200, BackColor.R, BackColor.G, BackColor.B)))
                    e.Graphics.FillPath(b, path);
                TextRenderer.DrawText(e.Graphics, Text, Font, rect,
                    Enabled ? ForeColor : SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            protected override void OnResize(EventArgs e)
            { base.OnResize(e); if (Width > 0 && Height > 0) using (var p = RoundRect(new Rectangle(0, 0, Width, Height), CornerRadius)) Region = new Region(p); }
            private static GraphicsPath RoundRect(Rectangle r, int rad)
            {
                int d = rad * 2; var p = new GraphicsPath(); p.StartFigure();
                p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure(); return p;
            }
        }

        private void InitializeComponent()
        { SuspendLayout(); ClientSize = new Size(1200, 850); Name = "ExportGunForm"; ResumeLayout(false); }
    }
}
