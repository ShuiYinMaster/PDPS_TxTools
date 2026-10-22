// CrossEnvIOForm.cs  --  C# 8.0
// 跨环境 I/O 主窗体：资源 / 零件 / 焊点 三个标签页 + 共享日志。
// UI 全部走 TxTools.Common.FormUiKit（主题配色、DPI 缩放、布局助手）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Agent.Core;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;

namespace TxTools.CrossEnvIO
{
    public class CrossEnvIOForm : PickAwareTxForm
    {
        internal static CrossEnvIOForm Instance;

        private readonly SynchronizationContext _psCtx;
        private RichTextBox _rtbLog;
        private TabControl _tabs;
        private Label _lblStatus;
        private ProgressBar _progress;
        private bool _busy;
        private int _refreshGeneration;
        private readonly Dictionary<int, string> _peerRoots = new Dictionary<int, string>();

        // ── 资源/零件页（合并，模式切换共享一套控件）──
        private TextBox _txtSrcLib;
        private TextBox _txtDstLib;
        private TextBox _txtFile;
        private TxObjGridCtrl _gridRoot;
        private Label _lblHint;
        private Label _lblFile;
        private RadioButton _rbRes;
        private RadioButton _rbPart;
        private bool _partMode;

        // ════════════════════════════════════════════════════════════
        //  焊点页
        // ════════════════════════════════════════════════════════════

        // ── 焊点页 ──
        private TextBox _txtWeldFile;
        private CheckBox _chkWeldProject;
        private CheckBox _chkWeldBindOnly;
        private TxObjGridCtrl _gridWeldSel;   // 焊点操作多选过滤

        // ── 撤销用：记录上次复制的 cojt 目录（静态字段，跨实例共享）──
        private static CopyResult _lastResCopy;
        private static CopyResult _lastPartCopy;

        // ── 重建原点（资源/零件共用，用于两个场景的世界坐标同步）──
        private TxFrameEditBoxCtrl _frameOrigin;
        private double _originX;
        private double _originY;
        private double _originZ;

        // ── 对端实例选择（多实例可切换）──
        private ComboBox _cmbPeer;
        private readonly List<TxTools.Agent.Core.PsInstanceInfo> _peerList =
            new List<TxTools.Agent.Core.PsInstanceInfo>();

        private bool _scaled;
        private readonly Size _designSize = new Size(760, 640);
        private readonly Size _minSize = new Size(740, 600);

        private static readonly string DefaultDir = TxToolsTemp.DirectoryFor("CrossEnvIO");

        public CrossEnvIOForm(SynchronizationContext psCtx)
        {
            _psCtx = psCtx;
            SemiModal = false;
            FormUiKit.InitStandardForm(this,
                "跨环境互传 I/O（资源 / 零件 / 焊点）",
                _designSize, _minSize, sizable: true);
            BuildUi();
            FormClosing += (s, e) =>
            {
                if (_busy) { e.Cancel = true; Log("[提示] 操作进行中，请等待结果后关闭窗口"); }
            };
            Log("插件已启动。数据文件默认目录: " + DefaultDir);
            Log("本环境库根自动识别: " + (EnvLibrary.CurrentRoot() ?? "<无>"));
            DetectLibraryRoots();
            Log("流程（双向）：任一端【导出】落盘 → 另一端【重建】。不区分主控/被控。");
        }

        public override void OnInitTxForm()
        {
            base.OnInitTxForm();
            SemiModal = false;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, _designSize);
        }

        /// <summary>用户在 PS 场景中拾取坐标系/对象作为重建原点，提取其平移 XYZ 作为偏移量。</summary>
        private void OnOriginFrameSet(object sender, TxFrameEditBoxCtrl_ValidFrameSetEventArgs e)
        {
            try
            {
                TxTransformation loc = _frameOrigin.GetLocation();
                if (loc != null)
                {
                    _originX = loc.Translation.X;
                    _originY = loc.Translation.Y;
                    _originZ = loc.Translation.Z;
                    Log("[原点] 重建原点已设置: X=" + _originX + " Y=" + _originY + " Z=" + _originZ);
                }
            }
            catch (Exception ex)
            {
                Log("[原点] 获取坐标失败: " + ex.Message);
                _originX = 0; _originY = 0; _originZ = 0;
            }
        }

        private void BuildUi()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var tab = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = FormUiKit.BaseFont,
                Padding = new Point(6, 4),
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(100, 28),
                Appearance = TabAppearance.Normal
            };
            _tabs = tab;
            tab.DrawItem += (s, e) =>
            {
                var tc = (TabControl)s;
                bool selected = (e.Index == tc.SelectedIndex);
                // 选中：深色标题背景 + 白色文字；未选中：浅灰背景 + 深色文字
                Color bgColor = selected ? FormUiKit.TitleBack : Theme.BtnMuted;
                Color foreColor = selected ? Theme.HeaderFore : Color.Black;
                using (var brush = new SolidBrush(bgColor))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text, tc.Font, e.Bounds, foreColor, bgColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            };
            tab.TabPages.Add(BuildTransferTab());
            tab.TabPages.Add(BuildWeldTab());

            var logBox = BuildLogBox();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(6, 4, 6, 4),
                BackColor = FormUiKit.CardBack
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            root.Controls.Add(tab, 0, 0);
            var activity = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            activity.Controls.Add(_progress);
            activity.Controls.Add(_lblStatus);
            root.Controls.Add(activity, 0, 1);
            root.Controls.Add(logBox, 0, 2);
            PluginLog.Register(logBox);

            Controls.Add(root);
        }

// ════════════════════════════════════════════════════════════
        //  资源/零件页（合并：模式切换共享导出根/库根/文件/动作）
        // ════════════════════════════════════════════════════════════

        private TabPage BuildTransferTab()
        {
            var page = new TabPage("资源 / 零件");
            FlowLayoutPanel content;
            var card = FormUiKit.MkCard("资源 / 零件跨环境传输", 500, 800, out content);
            card.Dock = DockStyle.Fill;   // 关键：卡片填满标签页，消除右侧空白
            content.AutoScroll = false;

            // 模式切换（资源 / 零件）
            var rowMode = FormUiKit.MkRowFlow();
            _rbRes = FormUiKit.MkRadio("资源", true);
            _rbPart = FormUiKit.MkRadio("零件", false);
            _rbRes.CheckedChanged += (s, e) => { if (_rbRes.Checked) ApplyMode(false); };
            _rbPart.CheckedChanged += (s, e) => { if (_rbPart.Checked) ApplyMode(true); };
            rowMode.Controls.Add(_rbRes);
            rowMode.Controls.Add(_rbPart);
            content.Controls.Add(rowMode);

            _lblHint = FormUiKit.MkLabel("导出根：多选资源/零件；选择为空时导出整个场景树。", false);
            content.Controls.Add(_lblHint);

            // 导出根多选拾取（带边框）
            var gridPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                Margin = new Padding(0, 0, 0, 2),
                BackColor = SystemColors.Window,
                Padding = new Padding(1),
                BorderStyle = BorderStyle.FixedSingle,
            };
            _gridRoot = new TxObjGridCtrl
            {
                Dock = DockStyle.Fill,
                Font = FormUiKit.BaseFont,
                ListenToPick = true,
                EnableMultipleSelection = true,
                EnableRecurringObjects = false,
            };
            FormUiKit.GridPickFocus.Wire(_gridRoot);
            gridPanel.Controls.Add(_gridRoot);
            content.Controls.Add(gridPanel);
            FormUiKit.FillWidthInFlow(content, gridPanel);

            // 重建原点（PS 原生坐标选择框）
            var originHint = FormUiKit.MkLabel("重建原点（XYZ 偏移）", false);
            content.Controls.Add(originHint);
            var rowOrigin = FormUiKit.MkRowFlow();
            _frameOrigin = new TxFrameEditBoxCtrl
            {
                Width = 190,
                Height = 22,
                Font = FormUiKit.BaseFont,
                ListenToPick = true,
                Margin = new Padding(0, 0, 4, 0)
            };
            PickFocus.Wire(_frameOrigin);
            try { _frameOrigin.PickLevel = TxPickLevel.Entity; } catch { }
            _frameOrigin.ValidFrameSet += OnOriginFrameSet;
            _frameOrigin.InvalidFrameSet += (s, e) => { _originX = 0; _originY = 0; _originZ = 0; };
            rowOrigin.Controls.Add(_frameOrigin);
            var btnResetOrigin = FormUiKit.MkBtn("重置坐标", Theme.BtnSecondary, 60, 26);
            btnResetOrigin.Click += (s, e) =>
            {
                try
                {
                    _frameOrigin.SetLocation(new TxTransformation());
                    _originX = 0; _originY = 0; _originZ = 0;
                    Log("[原点] 已重置为世界原点");
                }
                catch (Exception ex) { Log("[原点] 重置失败，保留原偏移: " + ex.Message); }
            };
            rowOrigin.Controls.Add(btnResetOrigin);
            content.Controls.Add(rowOrigin);
            FormUiKit.FillWidthInFlow(content, rowOrigin);

            // 工具行：清空选择 + 识别库根
            var rowTools = FormUiKit.MkRowFlow();
            var btnClear = FormUiKit.MkBtn("清空选择", Theme.BtnSecondary, 90, 26);
            btnClear.Click += (s, e) => { try { _gridRoot.Objects = new TxObjectList(); } catch { } };
            rowTools.Controls.Add(btnClear);
            var btnDetect = FormUiKit.MkBtn("识别库根", Theme.BtnSecondary, 90, 26);
            btnDetect.Click += (s, e) => DetectLibraryRoots();
            rowTools.Controls.Add(btnDetect);
            content.Controls.Add(rowTools);

            // 对端实例选择（多实例可切换）
            var rowPeer = FormUiKit.MkRowFlow();
            rowPeer.Controls.Add(FormUiKit.MkFieldLabel("对端实例:"));
            _cmbPeer = FormUiKit.MkComboBox(180, false);
            _cmbPeer.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbPeer.SelectedIndexChanged += (s, e) => OnPeerChanged();
            rowPeer.Controls.Add(_cmbPeer);
            var btnRefreshPeer = FormUiKit.MkBtn("刷新", Theme.BtnSecondary, 60, 26);
            btnRefreshPeer.Click += (s, e) => RefreshPeerList();
            rowPeer.Controls.Add(btnRefreshPeer);
            content.Controls.Add(rowPeer);
            FormUiKit.FillWidthInFlow(content, rowPeer);

            // 本端库根 / 对端库根 / 结构文件
            var rowSrc = FormUiKit.MkRowFlow();
            rowSrc.Controls.Add(FormUiKit.MkFieldLabel("本端库根:"));
            rowSrc.Controls.Add(_txtSrcLib = FormUiKit.MkTextBox("", 330));
            content.Controls.Add(rowSrc);
            FormUiKit.FillWidthInFlow(content, rowSrc);

            var rowDst = FormUiKit.MkRowFlow();
            rowDst.Controls.Add(FormUiKit.MkFieldLabel("对端库根:"));
            rowDst.Controls.Add(_txtDstLib = FormUiKit.MkTextBox("", 330));
            content.Controls.Add(rowDst);
            FormUiKit.FillWidthInFlow(content, rowDst);

            _lblFile = FormUiKit.MkFieldLabel("结构文件:");
            _txtFile = FormUiKit.MkTextBox(Path.Combine(DefaultDir, "structure.txt"), 330);
            var rowFile = FormUiKit.MkRowFlow();
            rowFile.Controls.Add(_lblFile);
            rowFile.Controls.Add(_txtFile);
            content.Controls.Add(rowFile);
            FormUiKit.FillWidthInFlow(content, rowFile);

            var rowBrowse = FormUiKit.MkRowFlow();
            rowBrowse.Controls.Add(MkBrowseBtn("浏览本端", "FolderPicker", () => _txtSrcLib));
            rowBrowse.Controls.Add(MkBrowseBtn("浏览对端", "FolderPicker", () => _txtDstLib));
            rowBrowse.Controls.Add(MkBrowseBtn("打开结构文件", "FilePicker", () => _txtFile));
            content.Controls.Add(rowBrowse);
            var rowSaveFile = FormUiKit.MkRowFlow();
            rowSaveFile.Controls.Add(MkBrowseBtn("设置导出路径", "SaveFilePicker", () => _txtFile));
            content.Controls.Add(rowSaveFile);

            // 操作按钮：3×2 铺满宽度
            //   导出本端结构 / 复制cojt到对端 / 在本端重建对端结构
            //   在对端重建本端结构 / 一键复制cojt并重建 / 撤销复制
            var tblActions = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 2,
                AutoSize = false,
                Dock = DockStyle.Top,
                Width = 440,
                Height = 64,
                Margin = new Padding(0, 4, 0, 0)
            };
            tblActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            tblActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int c = 0; c < 3; c++)
                tblActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

            tblActions.Controls.Add(FormUiKit.MkBtn("导出本端结构", Theme.BtnPrimary, 140, 32), 0, 0);
            tblActions.Controls[0].Click += (s, e) => RunExport();
            tblActions.Controls.Add(FormUiKit.MkBtn("复制cojt到对端", Theme.BtnSecondary, 140, 32), 1, 0);
            tblActions.Controls[1].Click += (s, e) => RunCopyCojts();
            tblActions.Controls.Add(FormUiKit.MkBtn("在本端重建", Theme.BtnPrimary, 140, 32), 2, 0);
            tblActions.Controls[2].Click += (s, e) => RunRebuild();
            tblActions.Controls.Add(FormUiKit.MkBtn("在对端重建", Theme.BtnPrimary, 140, 32), 0, 1);
            tblActions.Controls[3].Click += (s, e) => RunRebuildRemote();
            tblActions.Controls.Add(FormUiKit.MkBtn("一键复制并重建", Theme.BtnPrimary, 140, 32), 1, 1);
            tblActions.Controls[4].Click += (s, e) => RunPushToPeer();
            tblActions.Controls.Add(FormUiKit.MkBtn("撤销复制", Theme.BtnDanger, 140, 32), 2, 1);
            tblActions.Controls[5].Click += (s, e) => RunUndoCopy();
            content.Controls.Add(tblActions);
            FormUiKit.FillWidthInFlow(content, tblActions);

            var tip = FormUiKit.MkLabel("提示：复制cojt后需刷新对端库浏览器；推送会同时在对端重建。", false);
            FormUiKit.WrapLabelInFlow(content, tip);
            content.Controls.Add(tip);

            // 左侧选取对象/原点，右侧设置对端/路径；底部保持同一组执行操作。
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
                Padding = new Padding(8, 6, 8, 4), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
                Margin = new Padding(0, 0, 12, 0) };
            for (int i = 0; i < 6; i++) selection.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
            selection.Controls.Add(rowMode, 0, 0);
            selection.Controls.Add(_lblHint, 0, 1);
            gridPanel.Dock = DockStyle.Fill;
            selection.Controls.Add(gridPanel, 0, 2);
            selection.Controls.Add(originHint, 0, 3);
            selection.Controls.Add(rowOrigin, 0, 4);
            selection.Controls.Add(rowTools, 0, 5);
            _lblHint.AutoSize = true;
            selection.SizeChanged += (s, e) => _lblHint.MaximumSize = new Size(Math.Max(20, selection.ClientSize.Width - 8), 0);

            var paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) paths.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            paths.Controls.Add(rowPeer, 0, 0);
            TableLayoutPanel PathRow(Control label, TextBox input, Control browse)
            {
                var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = browse == null ? 2 : 3,
                    RowCount = 1, Margin = new Padding(0, 2, 0, 2) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                if (browse != null) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                label.Anchor = AnchorStyles.Left;
                input.Width = 120;
                input.Dock = DockStyle.Fill;
                row.Controls.Add(label, 0, 0);
                row.Controls.Add(input, 1, 0);
                if (browse != null) row.Controls.Add(browse, 2, 0);
                return row;
            }
            var browseSource = rowBrowse.Controls[0];
            var browseTarget = rowBrowse.Controls[1];
            var browseFile = rowBrowse.Controls[2];
            browseSource.Text = browseTarget.Text = "浏览";
            paths.Controls.Add(PathRow(rowSrc.Controls[0], _txtSrcLib, browseSource), 0, 1);
            paths.Controls.Add(PathRow(rowDst.Controls[0], _txtDstLib, browseTarget), 0, 2);
            paths.Controls.Add(PathRow(_lblFile, _txtFile, null), 0, 3);
            var fileActions = FormUiKit.MkRowFlow();
            fileActions.Controls.Add(browseFile);
            fileActions.Controls.Add(rowSaveFile.Controls[0]);
            paths.Controls.Add(fileActions, 0, 4);
            rowOrigin.Dock = rowTools.Dock = rowPeer.Dock = fileActions.Dock = DockStyle.Top;
            layout.Controls.Add(selection, 0, 0);
            layout.Controls.Add(paths, 1, 0);
            tblActions.Dock = DockStyle.Fill;
            layout.Controls.Add(tblActions, 0, 1);
            layout.SetColumnSpan(tblActions, 2);
            layout.Controls.Add(tip, 0, 2);
            layout.SetColumnSpan(tip, 2);
            layout.SizeChanged += (s, e) => tip.MaximumSize = new Size(Math.Max(20, layout.ClientSize.Width - 20), 0);
            card.Controls.Remove(content);
            card.Controls.Add(layout);
            card.Controls.SetChildIndex(layout, 0);
            content.Dispose();

            page.Controls.Add(card);
            return page;
        }

        /// <summary>资源/零件模式切换：更新提示文字、文件行标签与默认文件名。</summary>
        private void ApplyMode(bool part)
        {
            _partMode = part;
            if (_lblHint != null)
                _lblHint.Text = part
                    ? "零件树导出含COJT列；选择为空时导出整个场景树。"
                    : "导出根：多选资源/零件；选择为空时导出整个场景树。";
            if (_lblFile != null)
                _lblFile.Text = part ? "零件树文件:" : "结构文件:";
            if (_txtFile != null)
            {
                string cur = GetText(_txtFile);
                string otherDefault = part
                    ? Path.Combine(DefaultDir, "structure.txt")
                    : Path.Combine(DefaultDir, "part_hierarchy.txt");
                if (string.Equals(cur, otherDefault, StringComparison.OrdinalIgnoreCase))
                    _txtFile.Text = part
                        ? Path.Combine(DefaultDir, "part_hierarchy.txt")
                        : Path.Combine(DefaultDir, "structure.txt");
            }
        }

        // ════════════════════════════════════════════════════════════
        //  焊点页
        // ════════════════════════════════════════════════════════════

        private TabPage BuildWeldTab()
        {
            var page = new TabPage("焊点");
            FlowLayoutPanel content;
            var card = FormUiKit.MkCard("焊点跨环境传输", 500, 380, out content);
            card.Dock = DockStyle.Fill;   // 填满标签页，消除右侧空白
            content.AutoScroll = false;

            content.Controls.Add(FormUiKit.MkLabel("导出选中的焊点操作；重建按ParentOpPath建操作树并创建焊点。", false));

            // 焊点操作多选（带边框）
            content.Controls.Add(FormUiKit.MkLabel("焊点操作（可多选，不选则导出全部）：", false));
            var gridPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                Margin = new Padding(0, 0, 0, 2),
                BackColor = SystemColors.Window,
                Padding = new Padding(1),
                BorderStyle = BorderStyle.FixedSingle,
            };
            _gridWeldSel = new TxObjGridCtrl
            {
                Dock = DockStyle.Fill,
                Font = FormUiKit.BaseFont,
                ListenToPick = true,
                EnableMultipleSelection = true,
                EnableRecurringObjects = false,
            };
            FormUiKit.GridPickFocus.Wire(_gridWeldSel);
            gridPanel.Controls.Add(_gridWeldSel);
            content.Controls.Add(gridPanel);
            FormUiKit.FillWidthInFlow(content, gridPanel);
            var rowClearWeld = FormUiKit.MkRowFlow();
            var btnClearWeld = FormUiKit.MkBtn("清空选择", Theme.BtnSecondary, 90, 26);
            btnClearWeld.Click += (s, e) => { try { _gridWeldSel.Objects = new TxObjectList(); } catch { } };
            rowClearWeld.Controls.Add(btnClearWeld);
            content.Controls.Add(rowClearWeld);

            var rowWeldFile = FormUiKit.MkRowFlow();
            rowWeldFile.Controls.Add(FormUiKit.MkFieldLabel("焊点文件:"));
            rowWeldFile.Controls.Add(_txtWeldFile = FormUiKit.MkTextBox(Path.Combine(DefaultDir, "weldpoints.txt"), 330));
            content.Controls.Add(rowWeldFile);
            FormUiKit.FillWidthInFlow(content, rowWeldFile);

            var rowBrowse = FormUiKit.MkRowFlow();
            rowBrowse.Controls.Add(MkBrowseBtn("打开焊点文件", "FilePicker", () => _txtWeldFile));
            rowBrowse.Controls.Add(MkBrowseBtn("设置导出路径", "SaveFilePicker", () => _txtWeldFile));
            content.Controls.Add(rowBrowse);

            _chkWeldProject = FormUiKit.MkCheckBox("重建后投影到零件表面", false);
            _chkWeldBindOnly = FormUiKit.MkCheckBox("仅绑定已有焊点（不新建）", false);
            var rowOpts = FormUiKit.MkRowFlow();
            rowOpts.Controls.Add(_chkWeldProject);
            rowOpts.Controls.Add(_chkWeldBindOnly);
            content.Controls.Add(rowOpts);

            // 操作按钮（一行铺满，宽度充裕）
            var tblActions = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = false,
                Dock = DockStyle.Top,
                Width = 440,
                Height = 40,
                Margin = new Padding(0, 4, 0, 0)
            };
            tblActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tblActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            tblActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            tblActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

            tblActions.Controls.Add(FormUiKit.MkBtn("导出本端焊点", Theme.BtnPrimary, 140, 32), 0, 0);
            tblActions.Controls[0].Click += (s, e) => RunExportWeld();
            tblActions.Controls.Add(FormUiKit.MkBtn("在本端重建焊点", Theme.BtnPrimary, 140, 32), 1, 0);
            tblActions.Controls[1].Click += (s, e) => RunRebuildWeld();
            tblActions.Controls.Add(FormUiKit.MkBtn("在对端重建焊点", Theme.BtnPrimary, 140, 32), 2, 0);
            tblActions.Controls[2].Click += (s, e) => RunPushWeldToPeer();
            content.Controls.Add(tblActions);
            FormUiKit.FillWidthInFlow(content, tblActions);

            page.Controls.Add(card);
            return page;
        }

        // ════════════════════════════════════════════════════════════
        //  日志
        // ════════════════════════════════════════════════════════════

        private Control BuildLogBox()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = FormUiKit.CardBack,
                Padding = new Padding(6, 0, 6, 4)
            };
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Theme.LogBg,
                ForeColor = Theme.LogText,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle
            };
            panel.Controls.Add(_rtbLog);
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 6,
                Style = ProgressBarStyle.Marquee, Visible = false };
            _lblStatus = new Label { Dock = DockStyle.Top, Height = 22, Text = "就绪",
                Font = FormUiKit.BaseFont, ForeColor = Theme.LogText };
            panel.Controls.Add(_progress);
            panel.Controls.Add(_lblStatus);
            return panel;
        }

        // ════════════════════════════════════════════════════════════
        //  动作
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 自动识别本端 + 对端库根：
        ///   本端 = TxApplication.SystemRootDirectory（PS 主线程直接读）；
        ///   对端 = 后台线程枚举其它 PDPS 实例（PsRpc fast-ping 取 systemRoot），取第一个存在的。
        /// 后台线程避免 UI 因远程超时卡住。
        /// </summary>
        private void DetectLibraryRoots()
        {
            int generation = ++_refreshGeneration;
            string current = null;
            try { current = EnvLibrary.CurrentRoot(); }
            catch { }

            if (!string.IsNullOrEmpty(current))
            {
                SetText(_txtSrcLib, current);
                Log("[库根] 本端: " + current);
            }
            else
            {
                Log("[库根] 本端识别失败（SystemRootDirectory 为空）");
            }

            // 后台枚举对端（管道 IO，不在 PS 主线程跑，避免卡 UI）
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var envs = EnvLibrary.ListEnvironments(MarshalLog, current);
                    MarshalLog("[库根] 检测到 " + envs.Count + " 个环境:");
                    foreach (var e in envs)
                        MarshalLog("[库根]   " + (e.IsSelf ? "本端" : "对端") + " " + e.Name
                            + (string.IsNullOrEmpty(e.SystemRoot) ? "  <无库根>" : "  " + e.SystemRoot));

                    // 填充对端实例下拉框（多实例可切换）
                    MarshalRefreshPeer(envs, generation);
                }
                catch (Exception ex)
                {
                    MarshalLog("[库根] 识别异常: " + ex.Message);
                }
            });
        }

        /// <summary>后台线程把对端实例列表刷新进下拉框（封送回 UI 线程）。</summary>
        private void MarshalRefreshPeer(List<EnvInfo> envs, int generation)
        {
            try
            {
                if (IsDisposed) return;
                Action apply = () =>
                {
                    if (!IsDisposed && !_busy && generation == _refreshGeneration) PopulatePeerCombo(envs);
                };
                if (InvokeRequired) BeginInvoke(apply); else apply();
            }
            catch { }
        }

        /// <summary>把对端实例填充进下拉框，保留当前选中。</summary>
        private void PopulatePeerCombo(List<EnvInfo> envs)
        {
            try
            {
                if (_cmbPeer == null) return;
                int keepPid = SelectedPeerPid();
                _peerList.Clear();
                _peerRoots.Clear();
                _cmbPeer.Items.Clear();
                foreach (var e in envs)
                {
                    if (e == null || e.IsSelf) continue;
                    var info = new TxTools.Agent.Core.PsInstanceInfo
                    {
                        Pid = e.Pid,
                        Name = e.Name,
                        Study = e.Study
                    };
                    _peerList.Add(info);
                    _peerRoots[e.Pid] = e.SystemRoot;
                    _cmbPeer.Items.Add(e.Name + "  [" + (e.Study ?? "无工程") + "] [pid " + e.Pid + "]");
                }
                if (_cmbPeer.Items.Count > 0)
                {
                    int sel = -1;
                    for (int i = 0; i < _peerList.Count; i++)
                        if (_peerList[i].Pid == keepPid) { sel = i; break; }
                    _cmbPeer.SelectedIndex = sel >= 0 ? sel : (keepPid == 0 ? 0 : -1);
                    if (_cmbPeer.SelectedIndex < 0)
                    { _txtDstLib.Text = ""; Log("[对端] 原目标已失效，请重新选择实例"); }
                }
                else
                {
                    _txtDstLib.Text = "";
                    Log("[对端] 没有可用的对端实例");
                }
            }
            catch (Exception ex) { Log("[对端] 填充实例失败: " + ex.Message); }
        }

        /// <summary>手动刷新对端实例列表（独立线程，避免卡 UI）。</summary>
        private void RefreshPeerList()
        {
            int generation = ++_refreshGeneration;
            string current = EnvLibrary.CurrentRoot();
            Log("[对端] 刷新实例列表…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var envs = EnvLibrary.ListEnvironments(MarshalLog, current);
                    MarshalRefreshPeer(envs, generation);
                }
                catch (Exception ex) { MarshalLog("[对端] 刷新异常: " + ex.Message); }
            });
        }

        private void OnPeerChanged()
        {
            try
            {
                var peer = SelectedPeer();
                if (peer == null) return;
                string root;
                _txtDstLib.Text = _peerRoots.TryGetValue(peer.Pid, out root) ? root ?? "" : "";
                Log("[对端] 已切换对端实例: " + peer.Name + " [pid " + peer.Pid + "]");
            }
            catch (Exception ex) { Log("[对端] 切换异常: " + ex.Message); }
        }

        /// <summary>当前下拉框选中的对端实例（无则返回 null）。</summary>
        private TxTools.Agent.Core.PsInstanceInfo SelectedPeer()
        {
            try
            {
                int i = _cmbPeer != null ? _cmbPeer.SelectedIndex : -1;
                if (i >= 0 && i < _peerList.Count) return _peerList[i];
            }
            catch { }
            return null;
        }

        private int SelectedPeerPid()
        {
            var p = SelectedPeer();
            return p != null ? p.Pid : 0;
        }

        private void MarshalLog(string msg)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action<string>(Log), msg);
                else Log(msg);
            }
            catch { }
        }

        private static void MarshalText(TextBox tb, string text)
        {
            try
            {
                if (tb == null || tb.IsDisposed) return;
                if (tb.InvokeRequired) tb.BeginInvoke(new Action(() => tb.Text = text ?? ""));
                else tb.Text = text ?? "";
            }
            catch { }
        }

        private static void SetText(TextBox tb, string text)
        {
            try { if (tb != null) tb.Text = text ?? ""; } catch { }
        }

        /// <summary>从多选框取选中对象；为空则返回 PhysicalRoot（导出全树）。</summary>
        private List<ITxObject> GetGridRoots(TxObjGridCtrl grid, ITxObject fallback)
        {
            var list = new List<ITxObject>();
            if (grid == null) { if (fallback != null) list.Add(fallback); return list; }
            try
            {
                int n = grid.Count;
                for (int i = 0; i < n; i++)
                {
                    var o = grid.GetObject(i);
                    if (o == null) throw new InvalidOperationException("选择对象已失效，请重新拾取");
                    list.Add(o);
                }
            }
            catch (Exception ex) { throw new InvalidOperationException("无法读取导出范围: " + ex.Message, ex); }
            if (list.Count == 0 && fallback != null) list.Add(fallback);
            return list;
        }

        private string ModeName() => _partMode ? "零件树" : "资源结构";

        private async void RunOperation(string title, Func<System.Threading.Tasks.Task> operation)
        {
            if (_busy) { Log("[提示] 上一次操作尚未结束"); return; }
            _busy = true;
            _tabs.Enabled = false;
            _lblStatus.Text = title + "…";
            _progress.Visible = true;
            try
            {
                await System.Threading.Tasks.Task.Yield();
                await operation(); _lblStatus.Text = title + "结束，详见日志";
            }
            catch (Exception ex) { _lblStatus.Text = title + "失败"; Log("[错误] " + ex.Message); }
            finally
            {
                _busy = false;
                if (!IsDisposed) { _tabs.Enabled = true; _progress.Visible = false; }
            }
        }

        private static System.Threading.Tasks.Task Finished() => System.Threading.Tasks.Task.CompletedTask;

        private void EnsureDocument(TxDocument document)
        {
            if (!Equals(document, TxApplication.ActiveDocument))
                throw new InvalidOperationException("本端工程已切换，操作已中止");
        }

        private int ExportStructureFile(string file)
        {
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("无活动文档");
            var roots = GetGridRoots(_gridRoot, doc.PhysicalRoot);
            if (roots.Any(r => !(r is TxPhysicalRoot) && !(r is TxCompoundResource) && !(r is TxCompoundPart)
                && !(r is ITxComponent)))
                throw new InvalidOperationException("导出选择包含非资源/零件对象，请检查选择列表");
            // 同时选中父、子时只遍历父，避免重复数据行。
            var descendants = new HashSet<string>();
            foreach (var root in roots)
            {
                var collection = root as ITxObjectCollection;
                if (collection == null) continue;
                foreach (ITxObject child in collection.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))))
                    descendants.Add(child.Id);
            }
            roots = roots.Where(r => !descendants.Contains(r.Id)).GroupBy(r => r.Id).Select(g => g.First()).ToList();
            Log("=== 导出" + ModeName() + " === 范围: " +
                string.Join("、", roots.Select(SafeName)));
            int count = StructureIO.ExportStructureMulti(roots, file, true, Log);
            if (count == 0) throw new InvalidOperationException("未导出有效结构，已中止后续操作");
            return count;
        }

        private int ExportWeldFile(string file)
        {
            if (TxApplication.ActiveDocument == null) throw new InvalidOperationException("无活动文档");
            HashSet<string> ids = null;
            if (_gridWeldSel.Count > 0)
            {
                ids = new HashSet<string>();
                for (int i = 0; i < _gridWeldSel.Count; i++)
                {
                    var obj = _gridWeldSel.GetObject(i);
                    if (!(obj is TxWeldLocationOperation))
                        throw new InvalidOperationException("请选择焊点操作；列表包含其他类型的对象");
                    ids.Add(obj.Id);
                }
            }
            Log("=== 导出焊点 === " + (ids == null ? "范围: 全部焊点操作" : "范围: " + ids.Count + " 个指定对象"));
            int count = WeldPointIO.ExportWeldPoints(file, Log, ids);
            if (count == 0) throw new InvalidOperationException("未导出有效焊点，已中止后续操作");
            return count;
        }

        private void RunExport() => RunOperation("导出结构", () =>
        {
            ExportStructureFile(GetText(_txtFile)); return Finished();
        });

        private void RunExportWeld() => RunOperation("导出焊点", () =>
        {
            ExportWeldFile(GetText(_txtWeldFile)); return Finished();
        });

        private async System.Threading.Tasks.Task<CopyResult> CopyCojts(
            string file, string source, string destination, bool part, int targetPid)
        {
            var paths = StructureIO.ReadCojtList(file, Log);
            if (paths.Count == 0) { Log("[复制] 数据中没有 cojt，无需复制"); return null; }
            var result = await System.Threading.Tasks.Task.Run(() =>
                CojtTransfer.CopyCojtList(paths, source, destination, MarshalLog));
            result.TargetPids.Add(targetPid);
            var previous = part ? _lastPartCopy : _lastResCopy;
            if (previous != null) result.Merge(previous);
            if (part) _lastPartCopy = result; else _lastResCopy = result;
            Log("[复制结果] " + result);
            if (result.Fail > 0) throw new IOException("部分 cojt 复制失败，已停止重建");
            return result;
        }

        private void RunCopyCojts() => RunOperation("复制 cojt", async () =>
        {
            var peer = FindPeerInstance();
            var doc = TxApplication.ActiveDocument;
            var state = await InspectPeer(peer);
            EnsureDocument(doc);
            string destination = ValidatePeerRoot(state, GetText(_txtDstLib));
            await CopyCojts(GetText(_txtFile), GetText(_txtSrcLib), destination, _partMode, peer.Pid);
        });

        private void RunRebuild() => RunOperation("本端重建结构", () =>
        {
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("无活动文档");
            var copy = _partMode ? _lastPartCopy : _lastResCopy;
            if (copy != null) copy.SceneMayReferenceFiles = true;
            var rep = StructureIO.RebuildStructure(GetText(_txtFile), doc.PhysicalRoot, !_partMode, Log,
                _originX, _originY, _originZ);
            foreach (var error in rep.Errors) Log("[警告] " + error);
            Log("[结果] " + rep);
            Log("[Undo] 在当前 PS 工程按 Ctrl+Z 可撤销本批次；未自动保存/重载。");
            TryRefresh();
            if (rep.Failed > 0 || rep.Errors.Count > 0) throw new InvalidOperationException("重建未完整成功，请检查日志");
            return Finished();
        });

        private void RunRebuildWeld() => RunOperation("本端重建焊点", () =>
        {
            var rep = WeldPointIO.RebuildWeldPoints(GetText(_txtWeldFile),
                _chkWeldProject.Checked, _chkWeldBindOnly.Checked, Log);
            foreach (var error in rep.Errors) Log("[警告] " + error);
            Log("[结果] " + rep); TryRefresh();
            if (rep.Failed > 0 || rep.Errors.Count > 0) throw new InvalidOperationException("焊点重建未完整成功，请检查日志");
            return Finished();
        });

        private void RunUndoCopy() => RunOperation("清理复制文件", async () =>
        {
            var result = _partMode ? _lastPartCopy : _lastResCopy;
            if (result == null || result.CopiedDirs.Count == 0) { Log("[清理] 无复制记录"); return; }
            if (result.SceneMayReferenceFiles) { result.Undo(Log); return; }
            if (result.TargetPids.Count == 0) throw new InvalidOperationException("复制记录缺少目标实例信息，已保留文件");
            var doc = TxApplication.ActiveDocument;
            var removable = new HashSet<string>(result.CopiedDirs, StringComparer.OrdinalIgnoreCase);
            foreach (int pid in result.TargetPids)
            {
                var peer = TxTools.Agent.Core.PsInstanceRegistry.Live().FirstOrDefault(p => p.Pid == pid && !p.IsSelf);
                if (peer == null) throw new InvalidOperationException("复制目标实例已不可用，无法核验其工程引用，已保留文件");
                var state = await InspectPeer(peer, new Newtonsoft.Json.Linq.JArray(result.CopiedDirs));
                var references = state["references"] as Newtonsoft.Json.Linq.JObject;
                foreach (var directory in result.CopiedDirs)
                    if (references == null || references[directory]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean
                        || (bool)references[directory]) removable.Remove(directory);
            }
            EnsureDocument(doc);
            // 只在两端都明确证明未引用时清理；清单摘要仍由 CopyResult 再校验。
            result.Undo(Log, directory =>
                removable.Contains(directory) && !ComponentIO.IsPathReferencedByScene(directory));
        });

        private void RunPushToPeer() => PushToPeer(_partMode ? "part" : "resource", true);
        private void RunRebuildRemote() => PushToPeer(_partMode ? "part" : "resource", false);
        private void RunPushWeldToPeer() => PushToPeer("weld", false);

        private void PushToPeer(string kind, bool copyCojts) => RunOperation("推送并对端重建", async () =>
        {
            var doc = TxApplication.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("无活动文档");
            var peer = FindPeerInstance();
            var state = await InspectPeer(peer);
            EnsureDocument(doc);
            string destination = ValidatePeerRoot(state, GetText(_txtDstLib));
            string source = EnvLibrary.CurrentRoot();
            string configuredSource = GetText(_txtSrcLib);
            if (kind != "weld" && !SamePath(source, configuredSource))
                throw new InvalidOperationException("本端库根与活动工程不一致，请重新识别库根");

            // 每次调用使用独立快照，原导出文件即使存在也不会被用作失败后的替代数据。
            string configuredFile = kind == "weld" ? GetText(_txtWeldFile) : GetText(_txtFile);
            string file = TransferFile.SnapshotPath(configuredFile);
            if (kind == "weld") ExportWeldFile(file); else ExportStructureFile(file);
            CopyResult copied = null;
            if (copyCojts)
            {
                copied = await CopyCojts(file, source, destination, kind == "part", peer.Pid);
            }
            EnsureDocument(doc);
            var input = new Newtonsoft.Json.Linq.JObject
            {
                ["kind"] = kind, ["file"] = file, ["source_library_root"] = source,
                ["expected_study"] = (string)state["study"],
                ["expected_document"] = (string)state["document"],
                ["expected_library_root"] = destination,
                ["origin_x"] = _originX, ["origin_y"] = _originY, ["origin_z"] = _originZ
            };
            if (copied != null) input["cojt_map"] = Newtonsoft.Json.Linq.JObject.FromObject(copied.TargetPaths);
            if (kind == "weld")
            {
                input["project"] = _chkWeldProject.Checked;
                input["bind_only"] = _chkWeldBindOnly.Checked;
            }
            else
            {
                // 调用可能超时或部分失败，依赖保护必须在发出 RPC 前设置。
                if (_lastResCopy != null) _lastResCopy.SceneMayReferenceFiles = true;
                if (_lastPartCopy != null) _lastPartCopy.SceneMayReferenceFiles = true;
            }
            Log("[远程] " + peer.Name + " [pid " + peer.Pid + "] 工程=" + state["study"] +
                " 库根=" + destination + "\n[快照] " + file);
            var response = await System.Threading.Tasks.Task.Run(() =>
                TxTools.Agent.Core.PsRpcClient.Invoke(peer, "crossenv_rebuild", input));
            if (!response.Ok) throw new IOException("远程调用失败或结果未知，请先核对对端工程: " + response.Error);
            string output = response.Output ?? "";
            Log("[远程结果] " + output);
            if (output.StartsWith("Error:", StringComparison.Ordinal))
                throw new InvalidOperationException("对端重建未完整成功，详见结果日志");
        });

        private async System.Threading.Tasks.Task<Newtonsoft.Json.Linq.JObject> InspectPeer(
            TxTools.Agent.Core.PsInstanceInfo peer, Newtonsoft.Json.Linq.JArray directories = null)
        {
            var input = new Newtonsoft.Json.Linq.JObject();
            if (directories != null) input["directories"] = directories;
            var response = await System.Threading.Tasks.Task.Run(() =>
                TxTools.Agent.Core.PsRpcClient.Invoke(peer, "crossenv_inspect", input));
            if (!response.Ok || string.IsNullOrEmpty(response.Output) || response.Output.StartsWith("Error:", StringComparison.Ordinal))
                throw new IOException("对端预检失败，请确认对端已加载新版插件: " + (response.Error ?? response.Output));
            return Newtonsoft.Json.Linq.JObject.Parse(response.Output);
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private string ValidatePeerRoot(Newtonsoft.Json.Linq.JObject state, string configured)
        {
            string actual = (string)state["systemRoot"];
            if (string.IsNullOrWhiteSpace(actual) || !Directory.Exists(actual))
                throw new IOException("对端库根无效: " + actual);
            if (!SamePath(actual, configured))
                throw new InvalidOperationException("对端库根与所选实例不一致，请刷新实例列表后重试。实际库根: " + actual);
            return actual;
        }

        private TxTools.Agent.Core.PsInstanceInfo FindPeerInstance()
        {
            var selected = SelectedPeer();
            if (selected == null) throw new InvalidOperationException("请先选择目标实例");
            var live = TxTools.Agent.Core.PsInstanceRegistry.Live().FirstOrDefault(p => !p.IsSelf && p.Pid == selected.Pid);
            if (live == null) throw new InvalidOperationException("选中的实例已失效，请刷新后重新选择");
            return live;
        }

        private Button MkBrowseBtn(string text, string kind, Func<TextBox> get)
        {
            var b = FormUiKit.MkBtn(text, Theme.BtnSecondary, 130, 26);
            b.Click += (s, e) =>
            {
                var tb = get();
                string v = Browse(kind, GetText(tb));
                if (v != null) tb.Text = v;
            };
            return b;
        }

        private static string Browse(string kind, string initial)
        {
            try
            {
                if (kind == "FolderPicker")
                {
                    using (var dlg = new FolderBrowserDialog())
                    {
                        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                            dlg.SelectedPath = initial;
                        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
                    }
                }
                if (kind == "SaveFilePicker")
                {
                    using (var dlg = new SaveFileDialog { Filter = "TSV 数据文件 (*.txt;*.tsv)|*.txt;*.tsv|所有文件 (*.*)|*.*",
                        DefaultExt = "txt", FileName = Path.GetFileName(initial ?? "") })
                    {
                        if (!string.IsNullOrWhiteSpace(initial)) dlg.InitialDirectory = Path.GetDirectoryName(initial);
                        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
                    }
                }
                using (var dlg = new OpenFileDialog { Filter = "TSV 数据文件 (*.txt;*.tsv)|*.txt;*.tsv|所有文件 (*.*)|*.*" })
                {
                    if (!string.IsNullOrWhiteSpace(initial)) dlg.InitialDirectory = Path.GetDirectoryName(initial);
                    return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
                }
            }
            catch { return null; }
        }

        private static string GetText(TextBox tb)
        {
            try { return tb == null ? "" : (tb.Text ?? "").Trim(); }
            catch { return ""; }
        }

        private static string SafeName(ITxObject o)
        {
            try { return o.Name; } catch { return "?"; }
        }

        private static void TryRefresh()
        {
            try { TxApplication.RefreshDisplay(); } catch { }
        }

        public void Log(string msg)
        {
            if (_rtbLog == null || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), msg); } catch { }
                return;
            }
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n";
            _rtbLog.AppendText(line);
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();
        }
    }
}
