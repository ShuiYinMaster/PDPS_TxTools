// CatiaPartTreeForm.cs — 主窗体：读取 CATIA 目录树、在树内做调整（勾选/增删/改名/排序）、
// 创建 PS 零件树、把已导入零件归类进对应容器。
// 遵循套件统一 GUI 规范（FormUiKit），配色全部来自 Theme。

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.CatiaPartTree.Core;
using TxTools.CatiaPartTree.Ps;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;
using FlatColorButton = TxTools.Common.FormUiKit.FlatColorButton;

namespace TxTools.CatiaPartTree
{
    public partial class CatiaPartTreeForm : TxForm
    {
        private enum LogLevel { Info, Ok, Warn, Error, Ps }

        // ── 数据 ─────────────────────────────────────────────────────────
        private readonly CatiaPartTreeService _svc;
        private TreeEditNode _model;                 // 当前可编辑树（根）
        private static readonly Size _designSize = new Size(1080, 720);
        private bool _dpiApplied;

        // ── 左侧：树编辑 ─────────────────────────────────────────────────
        private TreeView _tree;
        private TextBox _txtFilter;
        private Label _lblStats;

        // ── 右侧：目标与命名 ─────────────────────────────────────────────
        private ComboBox _cmbParent;
        private NumericUpDown _numDepth;
        private CheckBox _chkIncludeRoot;
        private CheckBox _chkPreferPn;
        private CheckBox _chkLeafContainers;

        // ── 右侧：创建 / 归类 ────────────────────────────────────────────
        private ComboBox _cmbScope;
        private CheckBox _chkUnmatched;
        private CheckBox _chkAutoCreate;
        private CheckBox _chkReload;

        // ── 日志 ─────────────────────────────────────────────────────────
        private RichTextBox _rtbLog;

        public CatiaPartTreeForm(SynchronizationContext psCtx)
        {
            SemiModal = false;
            _svc = new CatiaPartTreeService();

            FormUiKit.InitStandardForm(this, "CATIA 目录树 → PS 零件树",
                _designSize, new Size(900, 600), sizable: true);
            this.Padding = new Padding(4);

            BuildBody();
            BuildBottomBar();

            Log("插件已加载。在 CATIA 中打开 .CATProduct 后点 [读取 CATIA 树]，");
            Log("可在左侧调整树（勾选/增删/改名/排序），然后 [创建零件树]，最后 [归类已导入零件]。");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, _designSize);
        }

        // ════════════════════════════════════════════════════════════════
        //  UI — 主体（左树 / 右卡片）+ 底栏
        // ════════════════════════════════════════════════════════════════
        private void BuildBody()
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 2, 0, 2),
                Padding = Padding.Empty
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            body.Controls.Add(BuildTreeCard(), 0, 0);
            body.Controls.Add(BuildRightColumn(), 1, 0);
            this.Controls.Add(body);
        }

        private void BuildBottomBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(4),
                BackColor = FormUiKit.CardBack
            };
            var btnHelp = FormUiKit.MkFuncButton("帮助", Theme.BtnMuted);
            btnHelp.Click += (s, e) => ShowHelp();
            var btnClose = FormUiKit.MkFuncButton("关闭", Theme.BtnDanger);
            btnClose.Click += (s, e) => Close();
            bar.Controls.Add(btnHelp);
            bar.Controls.Add(btnClose);
            this.Controls.Add(bar);
        }

        // ── 左卡片：目录树编辑 ───────────────────────────────────────────
        private Control BuildTreeCard()
        {
            var card = new FormUiKit.ColoredGroupBox
            {
                Text = "CATIA 目录树（可编辑）",
                Dock = DockStyle.Fill,
                HeaderColor = Theme.CardTitle,
                ForeColor = Theme.CardTitle,
                Padding = new Padding(8, 20, 8, 8)
            };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            // 筛选行
            var filterRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 2),
                BackColor = Color.Transparent
            };
            var lblFilter = FormUiKit.MkFieldLabel("筛选:");
            _txtFilter = new TextBox { Width = 200, Font = FormUiKit.BaseFont };
            _txtFilter.TextChanged += (s, e) => RefreshTree();
            _lblStats = new Label
            {
                AutoSize = true,
                Font = FormUiKit.BaseFont,
                ForeColor = Theme.TextDim,
                Margin = new Padding(10, 8, 0, 0)
            };
            filterRow.Controls.Add(lblFilter);
            filterRow.Controls.Add(_txtFilter);
            filterRow.Controls.Add(_lblStats);
            root.Controls.Add(filterRow, 0, 0);

            // 树
            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                CheckBoxes = true,
                LabelEdit = true,
                HideSelection = false,
                FullRowSelect = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.InputBg,
                Font = FormUiKit.BaseFont
            };
            _tree.AfterCheck += OnTreeAfterCheck;
            _tree.AfterLabelEdit += OnTreeAfterLabelEdit;
            root.Controls.Add(_tree, 0, 1);

            // 功能按钮行（原工具条功能，放到树下方）
            var funcRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 2),
                BackColor = Color.Transparent
            };
            AddEditButton(funcRow, "读取 CATIA 树", Theme.BtnPrimary, OnReadTree);
            AddEditButton(funcRow, "展开全部", Theme.BtnMuted, delegate { _tree.ExpandAll(); CaptureExpansion(); });
            AddEditButton(funcRow, "折叠全部", Theme.BtnMuted, delegate { _tree.CollapseAll(); CaptureExpansion(); });
            AddEditButton(funcRow, "全选", Theme.BtnSecondary, delegate { if (_model != null) SetAllChecked(true); });
            AddEditButton(funcRow, "全不选", Theme.BtnSecondary, delegate { if (_model != null) SetAllChecked(false); });
            root.Controls.Add(funcRow, 0, 2);

            // 编辑按钮行
            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0),
                BackColor = Color.Transparent
            };
            AddEditButton(btnRow, "添加子节点", Theme.BtnPrimary, OnAddChild);
            AddEditButton(btnRow, "添加同级", Theme.BtnSecondary, OnAddSibling);
            AddEditButton(btnRow, "重命名", Theme.BtnSecondary, OnRename);
            AddEditButton(btnRow, "删除", Theme.BtnDanger, OnDelete);
            AddEditButton(btnRow, "上移", Theme.BtnMuted, OnMoveUp);
            AddEditButton(btnRow, "下移", Theme.BtnMuted, OnMoveDown);
            AddEditButton(btnRow, "缩进", Theme.BtnMuted, OnIndent);
            AddEditButton(btnRow, "提升", Theme.BtnMuted, OnOutdent);
            root.Controls.Add(btnRow, 0, 3);

            card.Controls.Add(root);
            return card;
        }

        private void AddEditButton(FlowLayoutPanel row, string text, Color color, Action handler)
        {
            var b = FormUiKit.MkFuncButton(text, color);
            b.Click += (s, e) => handler();
            row.Controls.Add(b);
        }

        private void OnTreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            var n = e.Node.Tag as TreeEditNode;
            if (n == null || _model == null) return;
            n.Include = e.Node.Checked;
            UpdateStats();
        }

        private void OnTreeAfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            var n = e.Node.Tag as TreeEditNode;
            if (n == null || string.IsNullOrEmpty(e.Label)) { e.CancelEdit = true; return; }
            n.Name = e.Label;
            e.CancelEdit = true;
            RefreshTree();
        }

        // ── 右列：目标与命名 + 创建/归类 + 日志 ─────────────────────────
        private Control BuildRightColumn()
        {
            var col = FormUiKit.BuildCardColumn(330);
            col.Controls.Add(BuildTargetCard());
            col.Controls.Add(BuildActionCard());
            col.Controls.Add(BuildLogCard());
            return col;
        }

        private Control BuildTargetCard()
        {
            var card = FormUiKit.MkCard("目标与命名", 330, 156, out var content);

            var row1 = FormUiKit.MkRowFlow();
            row1.Controls.Add(FormUiKit.MkFieldLabel("目标父级:"));
            _cmbParent = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                Width = 200,
                Font = FormUiKit.BaseFont
            };
            _cmbParent.Items.Add("自动(智能)");
            _cmbParent.Items.Add("PhysicalRoot");
            _cmbParent.Text = "自动(智能)";
            row1.Controls.Add(_cmbParent);
            content.Controls.Add(row1);

            var row2 = FormUiKit.MkRowFlow();
            row2.Controls.Add(FormUiKit.MkFieldLabel("最大深度:"));
            _numDepth = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 50,
                Value = 20,
                Width = 70,
                Font = FormUiKit.BaseFont
            };
            FormUiKit.AutoFitNumericWidth(_numDepth);
            row2.Controls.Add(_numDepth);
            content.Controls.Add(row2);

            _chkIncludeRoot = new CheckBox { Text = "包含根节点", AutoSize = true, Checked = true, Font = FormUiKit.BaseFont };
            content.Controls.Add(_chkIncludeRoot);
            _chkPreferPn = new CheckBox { Text = "名称优先用 PartNumber", AutoSize = true, Checked = false, Font = FormUiKit.BaseFont };
            content.Controls.Add(_chkPreferPn);
            _chkLeafContainers = new CheckBox { Text = "叶子零件也建空集(默认不建)", AutoSize = true, Checked = false, Font = FormUiKit.BaseFont };
            content.Controls.Add(_chkLeafContainers);
            return card;
        }

        private Control BuildActionCard()
        {
            var card = FormUiKit.MkCard("创建 / 归类", 330, 228, out var content);

            var btnBuild = FormUiKit.MkFuncButton("创建零件树", Theme.BtnPrimary);
            btnBuild.Click += (s, e) => OnBuildTree();
            content.Controls.Add(btnBuild);

            _chkReload = new CheckBox { Text = "建树后保存并重载，刷新 PS 树", AutoSize = true, Checked = true, Font = FormUiKit.BaseFont };
            content.Controls.Add(_chkReload);

            var rowScope = FormUiKit.MkRowFlow();
            rowScope.Controls.Add(FormUiKit.MkFieldLabel("分类范围:"));
            _cmbScope = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 200,
                Font = FormUiKit.BaseFont
            };
            _cmbScope.Items.Add("选中Compound(未选=全部)");
            _cmbScope.Items.Add("零件树(PrLine)");
            _cmbScope.Items.Add("PhysicalRoot");
            _cmbScope.SelectedIndex = 0;
            rowScope.Controls.Add(_cmbScope);
            content.Controls.Add(rowScope);

            _chkUnmatched = new CheckBox { Text = "未匹配零件移入『未分类』容器", AutoSize = true, Font = FormUiKit.BaseFont };
            content.Controls.Add(_chkUnmatched);

            _chkAutoCreate = new CheckBox { Text = "目标容器缺失时按 CATIA 层级自动创建", AutoSize = true, Checked = true, Font = FormUiKit.BaseFont };
            _chkAutoCreate.Name = "_chkAutoCreate";
            content.Controls.Add(_chkAutoCreate);

            var btnClassify = FormUiKit.MkFuncButton("归类已导入零件", Theme.BtnSecondary);
            btnClassify.Click += (s, e) => OnClassify();
            content.Controls.Add(btnClassify);

            var btnClear = FormUiKit.MkFuncButton("清空建树映射", Theme.BtnMuted);
            btnClear.Click += (s, e) => { _svc.ClearMapping(); Log("已清空建树映射。", LogLevel.Info); };
            content.Controls.Add(btnClear);
            return card;
        }

        private Control BuildLogCard()
        {
            var card = FormUiKit.MkCard("日志", 330, 232, out var content);
            _rtbLog = new RichTextBox
            {
                Width = 330 - 18,
                Height = 220,
                BackColor = Theme.LogBg,
                ForeColor = Theme.LogText,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                Font = FormUiKit.BaseFont
            };
            content.Controls.Add(_rtbLog);
            // 宽度跟随卡片内容区，卡片随列宽变化时日志框不会溢出/留白
            FormUiKit.FillWidthInFlow(content, _rtbLog);
            return card;
        }

        // ════════════════════════════════════════════════════════════════
        //  动作
        // ════════════════════════════════════════════════════════════════
        private void OnReadTree()
        {
            var depth = (int)_numDepth.Value;
            SetBusy(true);
            Log("[PS] 正在读取 CATIA 目录树...");
            var t = new Thread(delegate ()
            {
                try
                {
                    var root = _svc.ReadCatiaTree(depth);
                    UI(delegate ()
                    {
                        _model = root;
                        RefreshTree();
                        Log("已读取 CATIA 树: 根=" + root.Name + ", 节点总数=" + (root.DescendantCount() + 1), LogLevel.Ok);
                        if (root.DescendantCount() == 0)
                            Log("警告: 根下没有子节点，请确认 CATIA 活动文档是 .CATProduct。", LogLevel.Warn);
                    });
                }
                catch (Exception ex)
                {
                    UI(delegate () { Log("[错误] 读取 CATIA 树失败: " + ex.Message, LogLevel.Error); });
                }
                finally { UI(delegate () { SetBusy(false); }); }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void OnBuildTree()
        {
            if (_model == null) { Log("请先读取 CATIA 树。", LogLevel.Warn); return; }
            var parent = _cmbParent.Text.Trim();
            if (parent == "自动(智能)" || parent.Length == 0) parent = "";
            _svc.ClearMapping();
            try
            {
                var r = _svc.BuildTree(_model, parent, _chkIncludeRoot.Checked,
                    _chkPreferPn.Checked, _chkLeafContainers.Checked, _chkReload.Checked, Log);
                Log("建树完成: 新建 " + r.Created + " / 跳过 " + r.Skipped
                    + (r.SkippedLeaf > 0 ? "(其中叶子零件 " + r.SkippedLeaf + ")" : "")
                    + " / 失败 " + r.Failed,
                    r.Failed == 0 ? LogLevel.Ok : LogLevel.Warn);
            }
            catch (Exception ex) { Log("[错误] " + ex.Message, LogLevel.Error); }
        }

        private void OnClassify()
        {
            if (_model == null) { Log("请先读取 CATIA 树（用于匹配规则）。", LogLevel.Warn); return; }
            try
            {
                var r = _svc.Classify(_model, _cmbScope.Text, _chkUnmatched.Checked,
                    _chkAutoCreate.Checked, _chkPreferPn.Checked, Log);
                var msg = "归类完成: 已归类 " + r.Matched + " / 未匹配 " + r.Unmatched
                        + (r.InPlace > 0 ? " / 已在位 " + r.InPlace : "")
                        + (r.CreatedContainers > 0 ? " / 新建容器 " + r.CreatedContainers : "")
                        + (r.MovedUnmatched > 0 ? " / 移入未分类 " + r.MovedUnmatched : "")
                        + " / 无容器跳过 " + r.NoContainer + " / 失败 " + r.Failed;
                Log(msg, (r.Failed == 0 && r.Matched > 0) ? LogLevel.Ok : LogLevel.Info);
                if (r.Unmatched > 0)
                    Log("提示: " + r.Unmatched + " 个零件未匹配到树节点，请检查命名/PartNumber 或调整树。", LogLevel.Warn);
            }
            catch (Exception ex) { Log("[错误] " + ex.Message, LogLevel.Error); }
        }

        // ════════════════════════════════════════════════════════════════
        //  树编辑操作（改模型 → 重建 TreeView）
        // ════════════════════════════════════════════════════════════════
        private void OnAddChild()
        {
            if (_model == null) { Log("请先读取 CATIA 树。", LogLevel.Warn); return; }
            var sel = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as TreeEditNode : null;
            var parent = sel ?? _model;
            var node = new TreeEditNode { Name = "新节点", Include = true };
            parent.AddChild(node);
            RefreshTree();
            SelectNode(node);
        }

        private void OnAddSibling()
        {
            if (_model == null) { Log("请先读取 CATIA 树。", LogLevel.Warn); return; }
            var sel = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as TreeEditNode : null;
            if (sel == null || sel.Parent == null) { OnAddChild(); return; }
            var node = new TreeEditNode { Name = "新节点", Include = true };
            sel.Parent.AddChild(node);
            RefreshTree();
            SelectNode(node);
        }

        private void OnRename()
        {
            if (_tree.SelectedNode == null) return;
            _tree.LabelEdit = true;
            _tree.SelectedNode.BeginEdit();
        }

        private void OnDelete()
        {
            var sel = _tree.SelectedNode;
            var n = sel != null ? sel.Tag as TreeEditNode : null;
            if (n == null) return;
            if (n.Parent == null) { _model = null; RefreshTree(); Log("已清空整棵树。", LogLevel.Warn); return; }
            n.Parent.RemoveChild(n);
            RefreshTree();
        }

        private void OnMoveUp()
        {
            var n = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as TreeEditNode : null;
            if (n == null || n.Parent == null) return;
            var sib = n.Parent.Children;
            int i = sib.IndexOf(n);
            if (i <= 0) return;
            sib.RemoveAt(i);
            sib.Insert(i - 1, n);
            RefreshTree();
            SelectNode(n);
        }

        private void OnMoveDown()
        {
            var n = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as TreeEditNode : null;
            if (n == null || n.Parent == null) return;
            var sib = n.Parent.Children;
            int i = sib.IndexOf(n);
            if (i < 0 || i >= sib.Count - 1) return;
            sib.RemoveAt(i);
            sib.Insert(i + 1, n);
            RefreshTree();
            SelectNode(n);
        }

        private void OnIndent()
        {
            var n = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as TreeEditNode : null;
            if (n == null || n.Parent == null) return;
            var sib = n.Parent.Children;
            int i = sib.IndexOf(n);
            if (i <= 0) return;
            var prev = sib[i - 1];
            n.Parent.RemoveChild(n);
            prev.AddChild(n);
            RefreshTree();
            SelectNode(n);
        }

        private void OnOutdent()
        {
            var n = _tree.SelectedNode != null ? _tree.SelectedNode.Tag as TreeEditNode : null;
            if (n == null || n.Parent == null || n.Parent.Parent == null) return;
            var gp = n.Parent.Parent;
            n.Parent.RemoveChild(n);
            gp.AddChild(n);
            RefreshTree();
            SelectNode(n);
        }

        private void SetAllChecked(bool val)
        {
            SetChecked(_model, val);
            RefreshTree();
        }

        private void SetChecked(TreeEditNode n, bool val)
        {
            n.Include = val;
            foreach (var c in n.Children) SetChecked(c, val);
        }

        // ════════════════════════════════════════════════════════════════
        //  树 ↔ 视图同步
        // ════════════════════════════════════════════════════════════════
        private void RefreshTree()
        {
            _tree.BeginUpdate();
            try
            {
                if (_tree.Nodes.Count > 0) CaptureExpansion();
                _tree.Nodes.Clear();
                if (_model != null)
                    foreach (var child in _model.Children)
                        _tree.Nodes.Add(BuildTreeNode(child, _txtFilter.Text));
                UpdateStats();
            }
            finally { _tree.EndUpdate(); }
        }

        private TreeNode BuildTreeNode(TreeEditNode n, string filter)
        {
            var tn = new TreeNode(NodeText(n)) { Tag = n, Checked = n.Include };
            if (n.IsExpanded) tn.Expand();
            foreach (var c in n.Children)
            {
                if (!string.IsNullOrEmpty(filter) && !SubtreeMatches(c, filter)) continue;
                tn.Nodes.Add(BuildTreeNode(c, filter));
            }
            return tn;
        }

        private static string NodeText(TreeEditNode n)
        {
            var s = n.Name ?? "?";
            if (!string.IsNullOrEmpty(n.PartNumber) && n.PartNumber != n.Name)
                s += "  [" + n.PartNumber + "]";
            if (n.IsAssembly) s += "  (装配)";
            return s;
        }

        private static bool SubtreeMatches(TreeEditNode n, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return true;
            if ((n.Name ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((n.PartNumber ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var c in n.Children)
                if (SubtreeMatches(c, keyword)) return true;
            return false;
        }

        private void CaptureExpansion()
        {
            foreach (TreeNode tn in _tree.Nodes) CaptureNode(tn);
        }

        private void CaptureNode(TreeNode tn)
        {
            var n = tn.Tag as TreeEditNode;
            if (n != null) n.IsExpanded = tn.IsExpanded;
            foreach (TreeNode c in tn.Nodes) CaptureNode(c);
        }

        private void SelectNode(TreeEditNode n)
        {
            foreach (TreeNode tn in _tree.Nodes)
            {
                var f = FindNode(tn, n);
                if (f != null)
                {
                    _tree.SelectedNode = f;
                    try { f.EnsureVisible(); } catch { }
                    return;
                }
            }
        }

        private static TreeNode FindNode(TreeNode tn, TreeEditNode n)
        {
            if (ReferenceEquals(tn.Tag, n)) return tn;
            foreach (TreeNode c in tn.Nodes)
            {
                var f = FindNode(c, n);
                if (f != null) return f;
            }
            return null;
        }

        private void UpdateStats()
        {
            if (_model == null) { _lblStats.Text = "未读取"; return; }
            _lblStats.Text = "节点 " + (_model.DescendantCount() + 1) + " | 勾选 " + _model.IncludedCount();
        }

        // ════════════════════════════════════════════════════════════════
        //  日志 / 帮助 / 杂项
        // ════════════════════════════════════════════════════════════════
        private void Log(string msg) { Log(msg, GuessLevel(msg)); }

        private void Log(string msg, LogLevel level)
        {
            if (_rtbLog == null || IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, LogLevel>(Log), msg, level);
                return;
            }
            Color c;
            switch (level)
            {
                case LogLevel.Ok: c = Theme.LogOk; break;
                case LogLevel.Error: c = Theme.LogErr; break;
                case LogLevel.Warn: c = Theme.LogWarn; break;
                case LogLevel.Ps: c = Theme.LogPs; break;
                default: c = Theme.LogText; break;
            }
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionLength = 0;
            _rtbLog.SelectionColor = c;
            _rtbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            try { _rtbLog.ScrollToCaret(); } catch { }
        }

        private static LogLevel GuessLevel(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return LogLevel.Info;
            if (msg.IndexOf("完成", StringComparison.Ordinal) >= 0) return LogLevel.Ok;
            if (msg.IndexOf("失败", StringComparison.Ordinal) >= 0) return LogLevel.Error;
            if (msg.IndexOf("警告", StringComparison.Ordinal) >= 0) return LogLevel.Warn;
            return LogLevel.Info;
        }

        private void UI(Action act)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(act); else act();
        }

        private void SetBusy(bool busy)
        {
            try { Cursor = busy ? Cursors.WaitCursor : Cursors.Default; } catch { }
        }

        private void ShowHelp()
        {
            MessageBox.Show(
                "1. 在 CATIA 中打开 .CATProduct，点击 [读取 CATIA 树]\n" +
                "2. 在左侧树中勾选要导入的分支；可重命名 / 增删 / 排序 / 缩进调整\n" +
                "3. 设置目标父级后点 [创建零件树]，生成 CompoundPart 层级\n" +
                "   · 仅装配节点建空集，CATIA 叶子零件不会建空集(可勾选『叶子零件也建空集』)\n" +
                "   · 勾选『建树后保存并重载刷新 PS 树』可在创建后自动保存并重载刷新树视图\n" +
                "4. 零件已导入 PS 后，点 [归类已导入零件] 按名称/PartNumber 自动归入对应容器\n\n" +
                "· 未勾选的分支不创建；未匹配零件可勾选移入『未分类』\n" +
                "· 所有 PS 操作可 Ctrl+Z 撤销；保存/重载会重建 PS 树视图",
                "帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}