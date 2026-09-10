// CrossEnvIOForm.cs  --  C# 8.0
// 跨环境 I/O 主窗体：资源 / 零件 / 焊点 三个标签页 + 共享日志。
// UI 全部走 TxTools.Common.FormUiKit（主题配色、DPI 缩放、布局助手）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;

namespace TxTools.CrossEnvIO
{
    public class CrossEnvIOForm : TxForm
    {
        internal static CrossEnvIOForm Instance;

        private readonly SynchronizationContext _psCtx;
        private RichTextBox _rtbLog;

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
        private readonly Size _designSize = new Size(500, 800);
        private readonly Size _minSize = new Size(500, 800);

        private static readonly string DefaultDir = @"C:\TxAgentImport";

        public CrossEnvIOForm(SynchronizationContext psCtx)
        {
            _psCtx = psCtx;
            SemiModal = false;
            FormUiKit.InitStandardForm(this,
                "跨环境互传 I/O（资源 / 零件 / 焊点）",
                _designSize, _minSize, sizable: true);
            BuildUi();
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
            FormBorderStyle = FormBorderStyle.FixedSingle;
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
                RowCount = 2,
                Margin = new Padding(6, 4, 6, 4),
                BackColor = FormUiKit.CardBack
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 74));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
            root.Controls.Add(tab, 0, 0);
            root.Controls.Add(logBox, 0, 1);

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
            content.AutoScroll = true;

            // 模式切换（资源 / 零件）
            var rowMode = FormUiKit.MkRowFlow();
            _rbRes = FormUiKit.MkRadio("资源", true);
            _rbPart = FormUiKit.MkRadio("零件", false);
            _rbRes.CheckedChanged += (s, e) => { if (_rbRes.Checked) ApplyMode(false); };
            _rbPart.CheckedChanged += (s, e) => { if (_rbPart.Checked) ApplyMode(true); };
            rowMode.Controls.Add(_rbRes);
            rowMode.Controls.Add(_rbPart);
            content.Controls.Add(rowMode);

            _lblHint = FormUiKit.MkLabel("导出根：在场景中多选复合资源，遍历其子集导出。", false);
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
            content.Controls.Add(FormUiKit.MkLabel("重建原点：拾取坐标系，其 XYZ 为重建偏移量。", false));
            var rowOrigin = FormUiKit.MkRowFlow();
            _frameOrigin = new TxFrameEditBoxCtrl
            {
                Width = 280,
                Height = 22,
                Font = FormUiKit.BaseFont,
                ListenToPick = true,
                Margin = new Padding(0, 0, 4, 0)
            };
            try { _frameOrigin.PickLevel = TxPickLevel.Entity; } catch { }
            _frameOrigin.ValidFrameSet += OnOriginFrameSet;
            _frameOrigin.InvalidFrameSet += (s, e) => { _originX = 0; _originY = 0; _originZ = 0; };
            rowOrigin.Controls.Add(_frameOrigin);
            var btnResetOrigin = FormUiKit.MkBtn("重置坐标", Theme.BtnSecondary, 60, 26);
            btnResetOrigin.Click += (s, e) => { _originX = 0; _originY = 0; _originZ = 0; Log("[原点] 已重置坐标"); };
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
            _cmbPeer = FormUiKit.MkComboBox(250, false);
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
            rowSrc.Controls.Add(_txtSrcLib = FormUiKit.MkTextBox(DefaultDir, 330));
            content.Controls.Add(rowSrc);
            FormUiKit.FillWidthInFlow(content, rowSrc);

            var rowDst = FormUiKit.MkRowFlow();
            rowDst.Controls.Add(FormUiKit.MkFieldLabel("对端库根:"));
            rowDst.Controls.Add(_txtDstLib = FormUiKit.MkTextBox(DefaultDir, 330));
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
            rowBrowse.Controls.Add(MkBrowseBtn("浏览文件", "FilePicker", () => _txtFile));
            content.Controls.Add(rowBrowse);

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
                Margin = new Padding(0, 4, 0, 0)
            };
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

            page.Controls.Add(card);
            return page;
        }

        /// <summary>资源/零件模式切换：更新提示文字、文件行标签与默认文件名。</summary>
        private void ApplyMode(bool part)
        {
            _partMode = part;
            if (_lblHint != null)
                _lblHint.Text = part
                    ? "零件树导出含COJT列；重建用TxCompoundPart容器+组件挂载。"
                    : "导出根：在场景中多选复合资源，遍历其子集导出。";
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
            content.AutoScroll = true;

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
            rowBrowse.Controls.Add(MkBrowseBtn("浏览焊点文件", "FilePicker", () => _txtWeldFile));
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
                Margin = new Padding(0, 4, 0, 0)
            };
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
                    var envs = EnvLibrary.ListEnvironments(MarshalLog);
                    if (envs.Count <= 1)
                    {
                        MarshalLog("[库根] 未发现其它 PDPS 实例（对端库根请手动浏览选择）");
                        return;
                    }
                    MarshalLog("[库根] 检测到 " + envs.Count + " 个环境:");
                    foreach (var e in envs)
                        MarshalLog("[库根]   " + (e.IsSelf ? "本端" : "对端") + " " + e.Name
                            + (string.IsNullOrEmpty(e.SystemRoot) ? "  <无库根>" : "  " + e.SystemRoot));

                    // 填充对端实例下拉框（多实例可切换）
                    MarshalRefreshPeer(envs);

                    string remote = EnvLibrary.ResolveRemoteRoot(envs, MarshalLog);
                    if (!string.IsNullOrEmpty(remote))
                    {
                        MarshalText(_txtDstLib, remote);
                        MarshalLog("[库根] 对端: " + remote);
                    }
                    else
                    {
                        MarshalLog("[库根] 对端库根未解析到（对端 TxAgent 未启动或库根不存在，请手动浏览）");
                    }
                }
                catch (Exception ex)
                {
                    MarshalLog("[库根] 识别异常: " + ex.Message);
                }
            });
        }

        /// <summary>后台线程把对端实例列表刷新进下拉框（封送回 UI 线程）。</summary>
        private void MarshalRefreshPeer(List<EnvInfo> envs)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) { BeginInvoke(new Action<List<EnvInfo>>(PopulatePeerCombo), envs); return; }
                PopulatePeerCombo(envs);
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
                    _cmbPeer.Items.Add(e.Name + "  [pid " + e.Pid + "]");
                }
                if (_cmbPeer.Items.Count > 0)
                {
                    int sel = -1;
                    for (int i = 0; i < _peerList.Count; i++)
                        if (_peerList[i].Pid == keepPid) { sel = i; break; }
                    _cmbPeer.SelectedIndex = sel >= 0 ? sel : 0;
                    Log("[对端] 可切换实例 " + _peerList.Count + " 个，当前: " + _peerList[Math.Max(0, _cmbPeer.SelectedIndex)].Name);
                }
                else
                {
                    Log("[对端] 没有可用的对端实例");
                }
            }
            catch (Exception ex) { Log("[对端] 填充实例失败: " + ex.Message); }
        }

        /// <summary>手动刷新对端实例列表（独立线程，避免卡 UI）。</summary>
        private void RefreshPeerList()
        {
            Log("[对端] 刷新实例列表…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var envs = EnvLibrary.ListEnvironments(MarshalLog);
                    MarshalRefreshPeer(envs);
                    var remote = EnvLibrary.ResolveRemoteRoot(envs, MarshalLog);
                    if (!string.IsNullOrEmpty(remote)) MarshalText(_txtDstLib, remote);
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
                    try { var o = grid.GetObject(i); if (o != null) list.Add(o); }
                    catch { }
                }
            }
            catch { }
            if (list.Count == 0 && fallback != null) list.Add(fallback);
            return list;
        }

        private string ModeName() => _partMode ? "零件树" : "资源结构";

        private void RunExport()
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) { Log("[错误] 无活动文档"); return; }
                var roots = GetGridRoots(_gridRoot, doc.PhysicalRoot);
                string file = GetText(_txtFile);
                Log("=== 导出" + ModeName() + " ===  导出根 " + roots.Count + " 个");
                foreach (var r in roots) Log("  根: " + SafeName(r));
                // includeCojt=true：组件记录 cojt 磁盘路径，重建时可自动插入组件
                int n = StructureIO.ExportStructureMulti(roots, file, true, Log);
                Log("[完成] 导出 " + n + " 行 → " + file);
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        private void RunCopyCojts()
        {
            try
            {
                string src = GetText(_txtSrcLib);
                string dst = GetText(_txtDstLib);
                Log("=== 复制 cojt ===");
                // 基于导出结构的 COJT 列复制（与导出范围一致，避免整个库全部复制）
                string file = GetText(_txtFile);
                var cojts = StructureIO.ReadCojtList(file, Log);
                if (cojts.Count == 0)
                {
                    Log("[完成] 导出文件无 COJT 列或为空，请先【导出】。");
                    return;
                }
                var rep = CojtTransfer.CopyCojtList(cojts, src, dst, Log);
                if (_partMode) _lastPartCopy = rep; else _lastResCopy = rep;
                Log("[完成] " + rep);
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        private void RunRebuild()
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) { Log("[错误] 无活动文档"); return; }
                string file = GetText(_txtFile);
                Log("=== 重建" + ModeName() + " ===");
                var rep = StructureIO.RebuildStructure(file, doc.PhysicalRoot, !_partMode, Log,
                    _originX, _originY, _originZ);
                foreach (var e in rep.Errors) Log("[警告] " + e);
                // 统一收尾：保存 + 改 psz 路径 + 重载刷新（与对端重建同一套逻辑）
                Log("[修路径] 重建完成，统一保存+改psz+重载");
                ComponentIO.FixPszAfterInsert(Log);
                Log("[完成] " + rep);
                TryRefresh();
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        private void RunUndoCopy()
        {
            Log("=== 撤销 cojt 复制 ===");
            var rep = _partMode ? _lastPartCopy : _lastResCopy;
            if (rep == null || rep.CopiedDirs.Count == 0)
            { Log("[撤销] 无可撤销的复制记录"); return; }
            rep.Undo(Log);
        }

        private void RunExportWeld()
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) { Log("[错误] 无活动文档"); return; }
                string file = GetText(_txtWeldFile);
                // 多选过滤：从拾取的焊点操作中提取名称集合（空=导出全部）
                HashSet<string> selectedNames = null;
                try
                {
                    if (_gridWeldSel != null && _gridWeldSel.Count > 0)
                    {
                        selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < _gridWeldSel.Count; i++)
                        {
                            try { var obj = _gridWeldSel.GetObject(i); if (obj != null) selectedNames.Add(obj.Name); } catch { }
                        }
                    }
                }
                catch { }
                Log("=== 导出焊点 ===" + (selectedNames != null && selectedNames.Count > 0 ? " (过滤 " + selectedNames.Count + " 个)" : " (导出全部)"));
                int n = WeldPointIO.ExportWeldPoints(file, Log, selectedNames);
                Log("[完成] 导出焊点 " + n + " 条 → " + file);
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        private void RunRebuildWeld()
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) { Log("[错误] 无活动文档"); return; }
                string file = GetText(_txtWeldFile);
                bool proj = _chkWeldProject != null && _chkWeldProject.Checked;
                bool bindOnly = _chkWeldBindOnly != null && _chkWeldBindOnly.Checked;
                Log("=== 重建焊点 === 投影=" + proj + " 仅绑定=" + bindOnly);
                var rep = WeldPointIO.RebuildWeldPoints(file, proj, bindOnly, Log);
                foreach (var e in rep.Errors) Log("[警告] " + e);
                Log("[完成] " + rep);
                TryRefresh();
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        // ════════════════════════════════════════════════════════════
        //  推送到对端并远程重建（一键）
        //  流程:① 本端导出 TSV → ② 复制 cojt 到对端库 → ③ RPC 调对端 crossenv_rebuild
        //  这样无需手动切到对端窗口点「重建」，直接在对端进程内完成重建。
        // ════════════════════════════════════════════════════════════

        private void RunPushToPeer()
        {
            PushToPeer(_partMode ? "part" : "resource", RunExport, RunCopyCojts);
        }

        /// <summary>在对端重建本端结构：本端导出 → 对端 RPC 重建（不复制 cojt，cojt 复制是单独按钮）。</summary>
        private void RunRebuildRemote()
        {
            PushToPeer(_partMode ? "part" : "resource", RunExport, null);
        }

        private void RunPushWeldToPeer()
        {
            PushToPeer("weld", RunExportWeld, null);
        }

        private void PushToPeer(string kind, Action localExport, Action localCopyCojt)
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) { Log("[错误] 无活动文档"); return; }

                // 数据文件路径（先解析，导出后校验）
                string file = kind == "weld" ? GetText(_txtWeldFile) : GetText(_txtFile);
                if (string.IsNullOrWhiteSpace(file))
                { Log("[错误] 数据文件路径为空"); return; }

                // ① 本端导出 TSV（必须先落盘，对端重建读的就是这份文件）
                if (localExport != null)
                {
                    localExport();
                    if (!File.Exists(file))
                    {
                        Log("[错误] 导出未生成文件: " + file + "（本端无活动文档或导出失败，中止推送）");
                        return;
                    }
                }

                // ② 复制 cojt 到对端库（资源/零件才有）—— 先确认两端库根已配置
                if (localCopyCojt != null)
                {
                    string srcLib = GetText(_txtSrcLib);
                    string dstLib = GetText(_txtDstLib);
                    if (string.IsNullOrWhiteSpace(srcLib) || !Directory.Exists(srcLib))
                    { Log("[错误] 本端库根未设置或不存在: " + srcLib + "（先点「识别库根」）"); return; }
                    if (string.IsNullOrWhiteSpace(dstLib))
                    { Log("[错误] 对端库根未设置（先点「识别库根」）"); return; }
                    localCopyCojt();
                }

                // ③ 找对端实例并远程重建
                var peer = FindPeerInstance();
                if (peer == null) { Log("[错误] 未找到对端实例（对端 TxAgent 未启动或不在同一机器）"); return; }

                Log("=== 推送并对端重建 === kind=" + kind + " 对端=" + peer.Name);
                Log("[远程] 文件: " + file);

                // RPC 调用可能耗时（对端场景大/正在仿真），放后台线程，避免卡 UI
                var input = new Newtonsoft.Json.Linq.JObject
                {
                    ["kind"] = kind,
                    ["file"] = file,
                    ["origin_x"] = _originX,
                    ["origin_y"] = _originY,
                    ["origin_z"] = _originZ
                };
                if (kind == "weld")
                {
                    input["project"] = _chkWeldProject != null && _chkWeldProject.Checked;
                    input["bind_only"] = _chkWeldBindOnly != null && _chkWeldBindOnly.Checked;
                }

                var target = peer;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    var r = TxTools.Agent.Core.PsRpcClient.Invoke(target, "crossenv_rebuild", input);
                    if (r.Ok)
                        MarshalLog("[远程完成] [" + target.Name + "]\n" + (r.Output ?? ""));
                    else
                        MarshalLog("[远程失败] [" + target.Name + "] " + (r.Error ?? ""));
                });
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        /// <summary>
        /// 取对端实例。优先返回下拉框当前选中的实例（多实例可切换）；
        /// 未选或选中已失效时回退到第一个非本进程的活实例。
        /// </summary>
        private TxTools.Agent.Core.PsInstanceInfo FindPeerInstance()
        {
            try
            {
                var selected = SelectedPeer();
                if (selected != null)
                {
                    // 校验选中实例还活着（心跳未过期且进程存在）
                    foreach (var i in TxTools.Agent.Core.PsInstanceRegistry.Live())
                        if (i.Pid == selected.Pid) return i;
                }
                // 回退：取第一个非本进程的活实例
                foreach (var i in TxTools.Agent.Core.PsInstanceRegistry.Live())
                    if (!i.IsSelf) return i;
            }
            catch { }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  工具
        // ════════════════════════════════════════════════════════════

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
                using (var dlg = new OpenFileDialog())
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
