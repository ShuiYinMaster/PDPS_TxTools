using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.CatiaPartTree.Core;
using TxTools.CatiaPartTree.Ps;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;
using TxTools.CrossEnvIO;

namespace TxTools.CatiaPartTree
{
    public class CatiaPartTreeForm : PickAwareTxForm
    {
        private enum SourceKind { CatiaCurrent, PsBranch, ThreeDXml, CatProductFile }
        private readonly Size _design = new Size(650, 780);
        private readonly System.Windows.Forms.Timer _autoPreview = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly TxObjEditBoxCtrl _sourcePick = new TxObjEditBoxCtrl(), _targetPick = new TxObjEditBoxCtrl(), _assignmentPick = new TxObjEditBoxCtrl();
        private readonly ComboBox _sourceKind = new ComboBox { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _mode = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _newRoot = new CheckBox { Text = "新建目标根容器", Checked = true, AutoSize = true };
        private readonly TextBox _rootName = new TextBox { Text = "CATIA导入", Width = 160 };
        private readonly CheckBox _includeRoot = new CheckBox { Text = "保留来源根层", AutoSize = true };
        private readonly TextBox _modelFolder = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox _documentFolder = new CheckBox { Text = "以 CATIA 文档名建子文件夹", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly CheckBox _hierarchyFolder = new CheckBox { Text = "按层级建文件夹", Checked = false, AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly CheckBox _noReduction = new CheckBox { Text = "不减面", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _rightLegend = WorkflowUi.Label("复合零件：新建容器；零件：导入实例");
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000, ShowAlways = true };
        private ImageList _treeImages;
        private readonly TextBox _search = new TextBox { Width = 105 };
        private readonly Label _sourceLabel = WorkflowUi.Label("在 CATIA 中打开文档并保存零件几何，然后读取。");
        private readonly Label _status = WorkflowUi.Label("① 读取来源 → ② 核对预览 → ③ 创建复合零件 / 转换格式并导入 / 分配已有零件");
        private readonly TextBox _issues = new TextBox { Dock = DockStyle.Bottom, Height = 68, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly StructureSourceTree _left = new StructureSourceTree { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, ShowNodeToolTips = true };
        private readonly TreeView _right = new TreeView { Dock = DockStyle.Fill, HideSelection = false, LabelEdit = true, ShowNodeToolTips = true };
        private readonly Control _sourceFrame, _targetFrame, _assignmentFrame, _settings, _input, _modelLocation;
        private readonly Button _read, _check, _apply, _exportJt, _create, _assign, _browseDestination;
        private CatiaExistingAssignment _assignment;
        private bool _assignmentInvalid;
        private StructureNode _source;
        private StructurePlan _plan;
        private CatiaImportSource _catia;
        private CatiaImportSession _import;
        private object _document;
        private long _generation;
        private CancellationTokenSource _cancel;
        private bool _busy, _refreshing, _scaled, _stale = true, _finished, _targetInvalid;
        private SourceKind SelectedKind { get { return (SourceKind)_sourceKind.SelectedIndex; } }
        private bool IsCatia { get { return SelectedKind != SourceKind.PsBranch; } }
        private bool Frozen { get { return _import != null && _import.Stage != CatiaImportStage.Ready; } }
        private StructureNode SelectedSource { get { return _left.SelectedNode == null ? null : _left.SelectedNode.Tag as StructureNode; } }
        private ITxObject ParentTarget { get { return _targetPick.Object as ITxObject; } }

        public CatiaPartTreeForm(SynchronizationContext context)
        {
            FormUiKit.InitStandardForm(this, "零件树整理 · CATIA 转换与导入", _design, new Size(650, 680), sizable: true);
            SemiModal = false; FormBorderStyle = FormBorderStyle.Sizable;
            _document = TxApplication.ActiveDocument; _generation = SceneUndoScope.DocumentGeneration;
            _sourceKind.Items.AddRange(new object[] { "CATIA 当前文档", "PS 零件分支", "3DXML 文件", "CATProduct 文件" });
            _sourceKind.SelectedIndex = (int)SourceKind.CatiaCurrent;
            _mode.Items.AddRange(new object[] { "仅创建容器结构", "创建结构并归类现有零件" }); _mode.SelectedIndex = 0;
            foreach (var picker in new[] { _sourcePick, _targetPick, _assignmentPick })
            { picker.Font = FormUiKit.BaseFont; picker.ListenToPick = true; PickFocus.Wire(picker); }
            _sourceFrame = CrossEnvPickForm.Frame("PS 来源分支 · 单对象选取", _sourcePick);
            _targetFrame = CrossEnvPickForm.Frame("当前 PS · 目标父容器（单选）", _targetPick);
            _assignmentFrame = CrossEnvPickForm.Frame("当前 PS · 待分配复合零件（单选）", _assignmentPick);
            _assignmentFrame.Dock = DockStyle.Fill;
            _tips.SetToolTip(_assignmentPick, "仅分配选中复合零件及其下级容器内的零件，不读取其他分支。");
            _targetPick.Object = TxApplication.ActiveDocument.PhysicalRoot;
            var inputs = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1 };
            inputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _sourceFrame.Dock = _targetFrame.Dock = DockStyle.Fill; inputs.Controls.Add(_sourceFrame, 0, 0); inputs.Controls.Add(_targetFrame, 1, 0); _input = inputs;
            var origin = WorkflowUi.Bar(); origin.Controls.Add(WorkflowUi.Label("① 来源")); origin.Controls.Add(_sourceKind);
            _read = WorkflowUi.Button(origin, "读取 / 重新读取", ReadSource);
            var target = WorkflowUi.Bar(); target.Controls.Add(_newRoot); target.Controls.Add(_rootName);
            target.Dock = DockStyle.None; target.Margin = Padding.Empty; target.Padding = Padding.Empty;
            _rootName.Width = 125; origin.Controls.Add(target);
            _sourceLabel.AutoSize = false; _sourceLabel.AutoEllipsis = true; _sourceLabel.Dock = DockStyle.Fill;
            _sourceLabel.Padding = new Padding(3, 2, 3, 2);
            _sourceLabel.Height = FormUiKit.BaseFont.Height + 8;
            _sourceLabel.MinimumSize = new Size(0, _sourceLabel.Height);
            _sourceLabel.TextChanged += (s, e) => _tips.SetToolTip(_sourceLabel, _sourceLabel.Text);
            var location = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2 };
            location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            location.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            location.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            location.Controls.Add(WorkflowUi.Label("COJT 保存至"), 0, 0); location.Controls.Add(_modelFolder, 1, 0);
            _browseDestination = WorkflowUi.Button(location, "浏览…", BrowseDestination);
            location.SetCellPosition(_browseDestination, new TableLayoutPanelCellPosition(2, 0));
            var saveOptions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 3, 0, 0) };
            saveOptions.Controls.Add(_documentFolder); saveOptions.Controls.Add(_hierarchyFolder); saveOptions.Controls.Add(_noReduction);
            location.Controls.Add(saveOptions, 0, 1); location.SetColumnSpan(saveOptions, 3);
            _modelLocation = location;
            _tips.SetToolTip(_modelFolder, "模型文件的基准保存文件夹，必须在当前 PS 系统库内。默认模糊匹配库内的 product 文件夹；勾选下方选项时再按 CATIA 文档名建立子文件夹。");
            _tips.SetToolTip(_documentFolder, "勾选后在保存文件夹下按当前 CATIA 文档名建立子文件夹；取消则 COJT 直接保存到所选文件夹。");
            _tips.SetToolTip(_hierarchyFolder, "勾选后按本次导入创建的复合零件层级建立子文件夹，COJT 放在所属复合零件的文件夹中；默认不勾选，全部平铺在保存文件夹下。");
            _tips.SetToolTip(_noReduction, "默认沿用 CATPart 内嵌可视化网格的精度，跳过转换减面；取消勾选才按几何与光照误差自动减面。");
            var settings = WorkflowUi.Bar(); settings.Controls.Add(WorkflowUi.Label("整理")); settings.Controls.Add(_mode); _settings = settings;
            settings.Dock = DockStyle.None; settings.Margin = Padding.Empty; settings.Padding = Padding.Empty; origin.Controls.Add(settings);
            var leftTools = WorkflowUi.Bar();
            leftTools.Controls.Add(WorkflowUi.Label("来源树")); leftTools.Controls.Add(_search);
            var leftLegend = WorkflowUi.Label("勾选建层"); leftTools.Controls.Add(leftLegend);
            _tips.SetToolTip(leftLegend, "勾选创建该层复合零件；取消则提升子项。右键排除或恢复零件、分支。");
            var rightTools = WorkflowUi.Bar();
            rightTools.Controls.Add(WorkflowUi.Label("目标预览 · F2 改名")); rightTools.Controls.Add(_rightLegend);
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = _design };
            split.Panel1.Controls.Add(_left); split.Panel1.Controls.Add(leftTools); split.Panel2.Controls.Add(_right); split.Panel2.Controls.Add(rightTools); WorkflowUi.EqualTrees(split);
            var footer = WorkflowUi.Bar(); footer.Dock = DockStyle.Bottom;
            _check = WorkflowUi.Button(footer, "重新检查", Preview);
            _create = WorkflowUi.Button(footer, "创建复合零件", CreateCompounds);
            _apply = WorkflowUi.Button(footer, "转换格式并导入", Advance);
            _exportJt = WorkflowUi.Button(footer, "仅转换 JT", () => Advance(true));
            _assign = WorkflowUi.Button(footer, "分配已有零件", AssignExisting);
            Controls.Add(split); Controls.Add(_issues); Controls.Add(footer);
            var header = WorkflowUi.Stack(origin, _sourceLabel, inputs, location, _status);
            header.RowStyles[1] = new RowStyle(SizeType.Absolute, _sourceLabel.Height + _sourceLabel.Margin.Vertical);
            Controls.Add(header);

            _sourceKind.SelectedIndexChanged += (s, e) => { ClearSource(); ShowSourceMode(); };
            _sourcePick.Picked += (s, e) => { if (!IsCatia) { if (e.IsValidObjectPicked) Run(ReadPs); else ClearSource(); } };
            _sourcePick.TypeValid += (s, e) => { if (!IsCatia) Run(ReadPs); };
            _sourcePick.TypeInvalid += (s, e) => { if (!IsCatia) ClearSource(); };
            _targetPick.Picked += (s, e) => { _targetInvalid = !e.IsValidObjectPicked; InvalidatePlan(); };
            _targetPick.TypeValid += (s, e) => { _targetInvalid = false; InvalidatePlan(); };
            _targetPick.TypeInvalid += (s, e) => { _targetInvalid = true; InvalidatePlan(); _issues.Text = "目标输入无效，请用原生选取框选取零件容器。"; };
            _assignmentPick.Picked += (s, e) => { _assignmentInvalid = !e.IsValidObjectPicked; RefreshAssignment(); };
            _assignmentPick.TypeValid += (s, e) => { _assignmentInvalid = false; RefreshAssignment(); };
            _assignmentPick.TypeInvalid += (s, e) => { _assignmentInvalid = true; RefreshAssignment(); };
            _newRoot.CheckedChanged += (s, e) => { _rootName.Enabled = _newRoot.Checked && !Frozen && !_busy; InvalidatePlan(); };
            _rootName.TextChanged += (s, e) => InvalidatePlan();
            _modelFolder.TextChanged += (s, e) => InvalidatePlan();
            _documentFolder.CheckedChanged += (s, e) => InvalidatePlan();
            _hierarchyFolder.CheckedChanged += (s, e) => InvalidatePlan();
            _includeRoot.CheckedChanged += (s, e) => InvalidatePlan(); _mode.SelectedIndexChanged += (s, e) => InvalidatePlan();
            _search.TextChanged += (s, e) => RefreshSource();
            _left.AfterCheck += (s, e) =>
            {
                if (_refreshing || Frozen || _busy) return;
                var node = (StructureNode)e.Node.Tag;
                if (!node.IsContainer) return;
                if (!node.Include && e.Node.Checked) Include(node, true);
                node.SkipLevel = !e.Node.Checked;
                if (node.Parent == null) _includeRoot.Checked = e.Node.Checked;
                RefreshSource(); InvalidatePlan();
            };
            _right.AfterSelect += (s, e) => { var c = e.Node.Tag as StructureChange; if (c != null) SelectSource(c.Node); };
            WireSourceMenu();
            _right.BeforeLabelEdit += (s, e) =>
            {
                var c = (StructureChange)e.Node.Tag;
                if (_busy || Frozen || (!c.Container && !IsCatia) || (c.Node.Key == "destination" && !_newRoot.Checked)) e.CancelEdit = true;
                else e.Node.Text = c.Name;
            };
            _right.AfterLabelEdit += (s, e) =>
            {
                e.CancelEdit = true; if (e.Label == null) return;
                Run(() => { var c = (StructureChange)e.Node.Tag; if (c.Node.Key == "destination") _rootName.Text = StructureEngine.Clean(e.Label);
                    else { SelectSource(c.Node); Edit(n => n.TargetName = StructureEngine.Clean(e.Label)); } });
            };
            _right.NodeMouseDoubleClick += (s, e) => e.Node.BeginEdit();
            _right.KeyDown += (s, e) => { if (e.KeyCode == Keys.F2 && _right.SelectedNode != null) { e.Handled = true; _right.SelectedNode.BeginEdit(); } };
            WireDragDrop();
            _autoPreview.Tick += (s, e) => { _autoPreview.Stop(); if (!_busy && _source != null && !Frozen) Preview(); };
            FormClosing += (s, e) => { if (_busy) { e.Cancel = true; if (_cancel != null) { _cancel.Cancel(); _issues.Text = "正在取消格式转换，待当前转换程序退出后可关闭。"; } else _issues.Text = "当前步骤正在执行，请等待结束后关闭。"; } };
            FormClosed += (s, e) => { _autoPreview.Dispose(); _tips.Dispose(); _sourceFrame.Dispose(); _assignmentFrame.Dispose(); if (_cancel != null) _cancel.Dispose(); _left.ImageList = _right.ImageList = null; if (_treeImages != null) _treeImages.Dispose(); };
            DpiChanged += (s, e) => InstallImages();
            ShowSourceMode(); UpdateActions();
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); FormUiKit.ApplyDpiScaling(this, ref _scaled, _design); WorkflowUi.LimitWidth(this);
            ((CrossEnvNativeFrame)_sourceFrame).RefreshMetrics(DeviceDpi); ((CrossEnvNativeFrame)_targetFrame).RefreshMetrics(DeviceDpi);
            ((CrossEnvNativeFrame)_assignmentFrame).RefreshMetrics(DeviceDpi);
            InstallImages();
        }
        private void InstallImages()
        {
            var previous = _treeImages; _treeImages = StructureTreeImages.Create(DeviceDpi);
            _left.ImageList = _right.ImageList = _treeImages; _left.RefreshChecks();
            if (previous != null) previous.Dispose();
        }
        private void ShowSourceMode()
        {
            _sourceFrame.Visible = !IsCatia; _settings.Visible = !IsCatia;
            _newRoot.Checked = IsCatia; _includeRoot.Checked = !IsCatia;
            _newRoot.Visible = _rootName.Visible = IsCatia;
            _modelLocation.Visible = IsCatia;
            _assignmentFrame.Visible = IsCatia;
            _rightLegend.Text = IsCatia ? "建层 / 导入" : "建层 / 归类";
            _tips.SetToolTip(_rightLegend, IsCatia ? "复合零件创建容器；零件导入新实例，选取待分配范围后显示已有零件分配。" : "复合零件创建或复用容器；零件归类现有实例。");
            var layout = (TableLayoutPanel)_input;
            layout.SuspendLayout(); layout.Controls.Clear();
            layout.Controls.Add(IsCatia ? _targetFrame : _sourceFrame, 0, 0);
            layout.Controls.Add(IsCatia ? _assignmentFrame : _targetFrame, 1, 0);
            layout.ColumnStyles[0].Width = layout.ColumnStyles[1].Width = 50;
            layout.ResumeLayout(true);
            switch (SelectedKind)
            {
                case SourceKind.CatProductFile: _sourceLabel.Text = "启动 CATIA，点击读取并选择 CATProduct。导入位置以所选根装配为基准。"; break;
                case SourceKind.ThreeDXml: _sourceLabel.Text = "选择 3DXML 文件。读取产品树后，可将包内 CGR 拆分成独立 JT。"; break;
                case SourceKind.PsBranch: _sourceLabel.Text = "在当前 PS 工程的原生选取框中选择来源分支与目标。"; break;
                default: _sourceLabel.Text = "读取 CATIA 当前打开的 CATPart、CATProduct 或 3DXML。"; break;
            }
            _issues.Text = IsCatia ? "先读取 CATIA 并核对预览，可直接创建复合零件或转换格式并导入。分配已有零件前，请在当前 PS 工程选取其所在复合零件。" : "读取 PS 来源，调整左树，核对预览后执行。";
            UpdateActions();
        }
        private void ClearSource()
        {
            _autoPreview.Stop(); _source = null; _catia = null; _import = null; _plan = null; _stale = true; _finished = false;
            _assignment = null;
            _left.Nodes.Clear(); _right.Nodes.Clear(); UpdateActions();
        }
        private void BrowseDestination()
        {
            Run(() =>
            {
                EnsureDocument();
                using (var dialog = new FolderBrowserDialog { Description = "选择 COJT 的基准保存文件夹（当前 PS 系统库内）", ShowNewFolderButton = true })
                {
                    string path = _modelFolder.Text;
                    while (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path)) path = Path.GetDirectoryName(path);
                    dialog.SelectedPath = string.IsNullOrWhiteSpace(path) ? TxApplication.SystemRootDirectory : path;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _modelFolder.Text = dialog.SelectedPath;
                }
            });
        }
        private void Include(StructureNode node, bool include)
        {
            foreach (var child in node.Walk()) child.Include = include;
            if (include) for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.Include = true;
        }
        private void WireSourceMenu()
        {
            var menu = new ContextMenuStrip();
            var exclude = menu.Items.Add("排除选中零件 / 分支");
            var restore = menu.Items.Add("恢复纳入选中零件 / 分支");
            exclude.Click += (s, e) => Edit(n => Include(n, false)); restore.Click += (s, e) => Edit(n => Include(n, true));
            menu.Opening += (s, e) => { var n = SelectedSource; exclude.Enabled = !_busy && !Frozen && n != null && n.Include; restore.Enabled = !_busy && !Frozen && n != null && !n.Include; };
            _left.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) _left.SelectedNode = e.Node; };
            _left.ContextMenuStrip = menu;
        }
        private void EnsureDocument()
        {
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument) || _generation != SceneUndoScope.DocumentGeneration)
                throw new InvalidOperationException("PS 工程已切换或重载，请重新读取来源并重新选择目标。");
        }
        private static Task<CatiaImportSource> ReadCatiaAsync(string assemblyPath = null)
        {
            var completion = new TaskCompletionSource<CatiaImportSource>();
            var thread = new Thread(() =>
            {
                try { completion.SetResult(assemblyPath == null ? CatiaImportSource.ReadActive() : CatiaImportSource.ReadProductFile(assemblyPath)); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start(); return completion.Task;
        }
        private async void ReadSource()
        {
            if (_busy) return;
            if (!IsCatia) { Run(ReadPs); return; }
            ClearSource(); _busy = true; UpdateActions(); _issues.Text = "正在读取 CATIA 活动文档、保存路径及各实例位置…";
            _document = TxApplication.ActiveDocument; _generation = SceneUndoScope.DocumentGeneration;
            try
            {
                CatiaImportSource source;
                if (SelectedKind == SourceKind.ThreeDXml) source = await Read3dXmlFile();
                else if (SelectedKind == SourceKind.CatProductFile) source = await ReadCatProductFile();
                else
                {
                    try { source = await ReadCatiaAsync(); }
                    catch (ThreeDXmlFileRequiredException missing)
                    {
                        _sourceLabel.Text = "CATIA 当前文档：" + missing.FileName + "（待定位）";
                        _issues.Text = missing.Message;
                        source = await Read3dXmlFile(missing);
                    }
                }
                if (source == null) return;
                EnsureDocument(); _catia = source; _source = source.Root;
                _sourceLabel.Text = "来源：" + source.DocumentPath; _sourceLabel.MaximumSize = new Size(610, 0);
                _modelFolder.Text = CatiaLibraryDestination.DefaultFolder(TxApplication.SystemRootDirectory);
                _rootName.Text = StructureEngine.Clean(_source.SourceName); _search.Clear(); RefreshSource(); _stale = true;
            }
            catch (Exception ex) { _issues.Text = ex.GetBaseException().Message + (SelectedKind == SourceKind.CatiaCurrent ? Environment.NewLine + "读取 3DXML 时，也可将来源切换为“3DXML 文件”，选择原始文件读取。" : ""); }
            finally { _busy = false; UpdateActions(); }
            if (_source != null) Preview();
        }
        private async Task<CatiaImportSource> ReadCatProductFile()
        {
            using (var dialog = new OpenFileDialog { Title = "选择作为坐标基准的 CATProduct", Filter = "CATProduct 文件 (*.CATProduct)|*.CATProduct", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                _issues.Text = "正在通过 CATIA 读取装配引用及各零件实例位姿…";
                return await ReadCatiaAsync(dialog.FileName);
            }
        }
        private async Task<CatiaImportSource> Read3dXmlFile(ThreeDXmlFileRequiredException missing = null)
        {
            using (var dialog = new OpenFileDialog { Title = missing == null ? "选择 3DXML 文件" : "定位 CATIA 当前文档的原始 3DXML 文件",
                Filter = "3DXML 文件 (*.3dxml)|*.3dxml", CheckFileExists = true, Multiselect = false })
            {
                if (missing != null)
                {
                    dialog.FileName = missing.FileName;
                    if (missing.Directory != null) dialog.InitialDirectory = missing.Directory;
                }
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                string path = dialog.FileName;
                _issues.Text = "正在读取 3DXML 产品结构、几何引用及实例位置…";
                return await Task.Run(() => ThreeDXmlSource.Read(path));
            }
        }
        private void ReadPs()
        {
            var picked = _sourcePick.Object as ITxObject; ClearSource();
            if (picked == null) throw new InvalidOperationException("请在原生来源选取框中选取一个 PS 零件分支。");
            _source = StructureEngine.Read(picked); _document = TxApplication.ActiveDocument; _generation = SceneUndoScope.DocumentGeneration;
            _rootName.Text = StructureEngine.Clean(_source.SourceName); RefreshSource(); InvalidatePlan();
        }
        private void Run(Action action)
        { if (_busy) return; try { action(); } catch (Exception ex) { _issues.Text = ex.GetBaseException().Message; } }
        private void InvalidatePlan()
        {
            if (Frozen) return;
            _autoPreview.Stop(); _autoPreview.Start(); _stale = true; _plan = null; _import = null; _finished = false; UpdateActions();
        }
        private void RefreshSource()
        {
            _refreshing = true;
            try { WorkflowUi.Rebuild(_left, () => { if (_source != null) { var root = Build(_source); if (root != null) _left.Nodes.Add(root); } }, tag => ((StructureNode)tag).Key); }
            finally { _refreshing = false; } _left.RefreshChecks();
        }
        private TreeNode Build(StructureNode source)
        {
            var children = source.Children.Select(Build).Where(n => n != null).ToArray();
            if (_search.Text.Length > 0 && (source.SourceName + " " + source.PartNumber).IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase) < 0 && children.Length == 0) return null;
            bool create = source.IsContainer && source.Include && !source.SkipLevel && (source.Parent != null || _includeRoot.Checked);
            string kind = source.IsContainer ? "container" : "part";
            var node = new TreeNode(source.SourceName)
            {
                Tag = source, Checked = create, ImageKey = kind, SelectedImageKey = kind,
                ForeColor = source.Include ? SystemColors.WindowText : SystemColors.GrayText,
                ToolTipText = (!source.Include ? "已排除，不参与本批。" : "") + (source.IsContainer ? "复合零件：勾选创建此层容器；取消勾选提升子项。右键可排除整个分支。" : IsCatia ? "零件：导入真实零件实例，不创建空容器。右键可排除或恢复此零件。" : "已有零件：按执行方式保持原位或移动归类。右键可排除或恢复。")
            };
            node.Nodes.AddRange(children); return node;
        }
        private void SelectSource(StructureNode source)
        {
            if (source.Key == "destination") { _left.SelectedNode = null; return; }
            var node = WorkflowUi.Nodes(_left.Nodes).FirstOrDefault(n => ((StructureNode)n.Tag).Key == source.Key);
            if (node != null) { _left.SelectedNode = node; node.EnsureVisible(); }
        }
        private void Edit(Action<StructureNode> edit)
        {
            if (_busy || Frozen) return;
            var source = SelectedSource; if (source == null) return;
            edit(source); RefreshSource(); InvalidatePlan();
        }
        private void WireDragDrop()
        {
            foreach (var tree in new[] { _left, _right })
            {
                tree.AllowDrop = true; WorkflowUi.EnableDragScrolling(tree);
                tree.ItemDrag += (s, e) =>
                {
                    if (_busy || Frozen) return;
                    var tag = ((TreeNode)e.Item).Tag; var change = tag as StructureChange;
                    if (change == null || change.Node.Key != "destination") tree.DoDragDrop(change == null ? tag : change.Node, DragDropEffects.Move);
                };
                tree.DragOver += (s, e) => e.Effect = !_busy && !Frozen && e.Data.GetDataPresent(typeof(StructureNode)) ? DragDropEffects.Move : DragDropEffects.None;
                tree.DragDrop += (s, e) => Run(() =>
                {
                    if (Frozen) return;
                    var point = tree.PointToClient(new Point(e.X, e.Y)); var target = tree.GetNodeAt(point); if (target == null) return;
                    var change = target.Tag as StructureChange; var destination = change == null ? (StructureNode)target.Tag : change.Node;
                    if (destination.Key == "destination") return;
                    var dragged = (StructureNode)e.Data.GetData(typeof(StructureNode));
                    var source = _source.Walk().FirstOrDefault(n => n.Key == dragged.Key);
                    destination = _source.Walk().First(n => n.Key == destination.Key);
                    if (source == null || source == destination) return;
                    if (source.Parent == null || source.Walk().Contains(destination)) throw new InvalidOperationException("不能移动根节点或拖入自身后代。");
                    bool before = point.Y < target.Bounds.Top + target.Bounds.Height / 3;
                    if (before && source.Parent == destination.Parent)
                    { var siblings = source.Parent.Children; siblings.Remove(source); siblings.Insert(siblings.IndexOf(destination), source); }
                    else
                    {
                        if (!destination.IsContainer) throw new InvalidOperationException("请放到容器中；同级节点上沿可调整先后顺序。");
                        source.Parent.Children.Remove(source); destination.Add(source);
                    }
                    RefreshSource(); InvalidatePlan();
                });
            }
        }
        private void Preview()
        {
            if (_busy || _finished) return;
            if (Frozen) { RefreshAssignment(); return; }
            _autoPreview.Stop(); _stale = true;
            try
            {
                EnsureDocument();
                if (_source == null) throw new InvalidOperationException("请先读取来源。");
                var parent = ParentTarget;
                if (_targetInvalid || parent == null || !parent.IsValid() || !(parent is ITxCompoundPartCreation)) throw new InvalidOperationException("请用目标原生选取框选择一个有效的零件容器或物理根。");
                List<StructureChange> changes;
                if (IsCatia)
                {
                    _import = new CatiaImportSession(_catia, _source, parent, _newRoot.Checked, _rootName.Text, _includeRoot.Checked, _modelFolder.Text, _documentFolder.Checked, _hierarchyFolder.Checked);
                    changes = _import.Items; _status.Text = "② 复合零件 " + changes.Count(c => c.Container && c.Existing == null) + " 个；零件实例 " + _import.InstanceCount + " 个。请选择执行操作。";
                    _rightLegend.Text = "建层 / 导入";
                    _issues.Text = "当前预览：转换并导入新实例；选择待分配范围后预览切换为已有零件分配。左树勾选决定创建哪些复合零件层，右键可排除分支。保存位置：" + _import.OutputDirectory;
                }
                else
                {
                    _plan = StructureEngine.Plan(_source, parent, _includeRoot.Checked, _mode.SelectedIndex == 1);
                    changes = _plan.Changes; _issues.Text = _plan.Issues.Count == 0 ? "检查通过。左树修改自动刷新；重新检查用于读取 PS 外部变化。" : string.Join(Environment.NewLine, _plan.Issues);
                    _status.Text = "② 目标预览：" + changes.Count + " 项；问题 " + _plan.Issues.Count;
                }
                if (IsCatia) PrepareAssignment();
                if (IsCatia)
                {
                    int unsupported = _import.Items.Count(c => !c.Container && _catia.Parts[c.Node.Key].ConversionIssue != null);
                    if (unsupported > 0)
                        _issues.AppendText(Environment.NewLine + unsupported + " 个实例缺少可转换的 CGR。悬停目标预览中的零件可查看原因。");
                }
                RenderPreview(IsCatia && _assignment != null ? _assignment.Plan.Changes : changes);
                _stale = false;
            }
            catch (Exception ex) { _right.Nodes.Clear(); _plan = null; _import = null; _issues.Text = "预览未通过：" + ex.GetBaseException().Message; }
            finally { UpdateActions(); }
        }
        private void RenderPreview(List<StructureChange> changes)
        {
            WorkflowUi.Rebuild(_right, () =>
            {
                var nodes = new Dictionary<StructureChange, TreeNode>();
                foreach (var c in changes)
                {
                    string kind = c.Container ? "container" : "part";
                    var node = new TreeNode(c.Name + (c.Error != null ? "【" + c.Error + "】" : !c.Container && c.Existing != null ? "【" + c.Status + "】" : "")) { Tag = c, ImageKey = kind, SelectedImageKey = kind,
                            ToolTipText = c.Error ?? (c.Container ? (c.Existing == null && c.Result == null ? "新建复合零件容器" : "使用已有复合零件容器") : c.Existing != null ? "分配所选范围下的已有零件，保留名称和位置" : IsCatia ? PartPreviewTooltip(_catia.Parts[c.Node.Key]) : "归类已有零件实例"),
                        ForeColor = c.Error == null ? SystemColors.WindowText : Color.Firebrick };
                    nodes.Add(c, node); if (c.Parent == null) _right.Nodes.Add(node); else nodes[c.Parent].Nodes.Add(node);
                }
            }, tag => ((StructureChange)tag).Node.Key);
        }
        private static string PartPreviewTooltip(CatiaSourcePart part)
        {
            if (part.GeometryEntry != null) return part.ConversionIssue ?? "包内 CGR：" + part.GeometryEntry;
            if (part.InstancePath == null) return "导入零件实例；模型文件名保持源 CATIA 文件名";
            return string.Format(CultureInfo.InvariantCulture, "CATProduct 实例位置（mm） X {0:0.###} · Y {1:0.###} · Z {2:0.###}",
                part.Frame[9], part.Frame[10], part.Frame[11]);
        }
        private void PrepareAssignment()
        {
            _assignment = null;
            if (_assignmentInvalid) throw new InvalidOperationException("待分配范围输入无效，请重新选取一个复合零件。");
            var scope = _assignmentPick.Object as ITxObject;
            if (scope == null) return;
            _assignment = new CatiaExistingAssignment(_catia, _import, scope);
            _issues.Text = "待分配范围：" + scope.Name + "（仅此复合零件下）。匹配 " +
                _assignment.Plan.Changes.Count(c => !c.Container && c.Existing != null && c.Error == null) +
                " 项；问题 " + _assignment.Plan.Issues.Count + " 项。" + Environment.NewLine +
                "未匹配或有歧义的来源请在左树排除；范围内其他零件保留原位。" +
                Environment.NewLine + string.Join(Environment.NewLine, _assignment.Plan.Issues);
        }
        private void RefreshAssignment()
        {
            if (_busy || _finished || !IsCatia || _source == null) return;
            if (!Frozen) { InvalidatePlan(); return; }
            try { EnsureDocument(); PrepareAssignment(); RenderPreview(_assignment == null ? _import.Items : _assignment.Plan.Changes); }
            catch (Exception ex) { _assignment = null; RenderPreview(_import.Items); _issues.Text = ex.GetBaseException().Message; }
            UpdateActions();
        }
        private void CreateCompounds()
        {
            if (_busy || !_create.Enabled) return;
            _autoPreview.Stop(); _busy = true; UpdateActions();
            try
            {
                EnsureDocument(); _import.CreateTree();
                _status.Text = "复合零件已创建；可继续转换格式并导入，或选择范围分配已有零件。";
                _issues.Text = "已创建 " + _import.Created + " 个复合零件。";
                PrepareAssignment(); RenderPreview(_assignment == null ? _import.Items : _assignment.Plan.Changes);
            }
            catch (Exception ex) { _issues.Text = _import.Failure(ex); }
            finally { _busy = false; UpdateActions(); }
        }
        private void AssignExisting()
        {
            if (_busy || !_assign.Enabled) return;
            _autoPreview.Stop(); _busy = true; UpdateActions();
            try
            {
                EnsureDocument(); _import.EnsureDocument();
                _issues.Text = _assignment.Apply(message => _issues.AppendText(Environment.NewLine + message));
                _finished = true; _status.Text = "已有零件分配完成。";
                RenderPreview(_assignment.Plan.Changes);
            }
            catch (Exception ex)
            {
                _issues.Text = _assignment.Plan.Execution.Failure(ex);
                _finished = _assignment.Plan.Execution.WriteAttempted;
                if (!_finished) { _stale = true; _assignment = null; }
            }
            finally { _busy = false; UpdateActions(); }
        }
        private void MarkImportIssues(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                StructureChange change;
                if ((change = node.Tag as StructureChange) != null && change.Error != null)
                {
                    node.ForeColor = change.Result == null ? Color.Firebrick : Color.DarkOrange;
                    node.Text = change.Name + "（" + change.Error + "）";
                }
                MarkImportIssues(node.Nodes);
            }
        }
        private void Advance() { Advance(false); }
        private async void Advance(bool jtOnly)
        {
            if (_busy || _stale || !(jtOnly ? _exportJt.Enabled : _apply.Enabled)) return;
            string jtFolder = null;
            if (jtOnly)
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择独立 JT 保存文件夹", ShowNewFolderButton = true,
                    SelectedPath = Path.GetDirectoryName(_catia.DocumentPath) })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    jtFolder = dialog.SelectedPath;
                }
            }
            var session = _import;
            _autoPreview.Stop(); _busy = true; UpdateActions();
            try
            {
                EnsureDocument();
                if (!IsCatia)
                {
                    _finished = true;
                    _issues.Text = StructureEngine.Apply(_plan, message => _issues.AppendText(Environment.NewLine + message));
                    _status.Text = "零件树整理完成。";
                }
                else
                {
                    _cancel = new CancellationTokenSource();
                    if (jtOnly)
                        session = new CatiaImportSession(_catia, _source, ParentTarget, _newRoot.Checked, _rootName.Text, _includeRoot.Checked);
                    else RenderPreview(session.Items);
                    _issues.Text = jtOnly ? "正在将选中的包内 CGR 转换成独立 JT。关闭窗口可请求取消转换。" :
                        "正在转换格式，完成后将自动创建所需复合零件并导入。关闭窗口可请求取消转换。";
                    bool converting = true;
                    var progress = new Progress<string>(message => { if (converting && !IsDisposed) _issues.Text = message; });
                    try { await session.ConvertAsync(_cancel.Token, progress, reduceMesh: !_noReduction.Checked, jtOutputDirectory: jtFolder); }
                    finally { converting = false; }
                    EnsureDocument(); _cancel.Token.ThrowIfCancellationRequested();
                    if (jtOnly)
                    {
                        _status.Text = "已生成 " + session.ConvertedFileCount + " 个独立 JT。";
                        _issues.Text = _status.Text + Environment.NewLine + "保存位置：" + session.OutputDirectory;
                        if (session.Warnings.Count != 0) _issues.AppendText(Environment.NewLine + string.Join(Environment.NewLine, session.Warnings));
                        return;
                    }
                    _import.CreateTree();
                    _import.Import(message => _issues.AppendText(Environment.NewLine + message));
                    _finished = true;
                    _status.Text = _import.Warnings.Count == 0 ? "转换格式并导入完成。" : "转换格式并导入完成，请查看日志中的待核对项。";
                    _issues.Text = _import.ImportSummary + Environment.NewLine + "模型保存在：" + _import.OutputDirectory;
                    if (_import.Warnings.Count != 0)
                        _issues.AppendText(Environment.NewLine + string.Join(Environment.NewLine, _import.Warnings));
                    _issues.SelectionStart = 0;
                    _issues.ScrollToCaret();
                    RenderPreview(_import.Items); MarkImportIssues(_right.Nodes);
                }
            }
            catch (Exception ex)
            {
                if (jtOnly)
                {
                    _status.Text = "独立 JT 转换已停止。";
                    _issues.Text = ex.GetBaseException().Message;
                    if (session?.Warnings.Count > 0) _issues.AppendText(Environment.NewLine + string.Join(Environment.NewLine, session.Warnings));
                    if (session?.DiagnosticLog != null) _issues.AppendText(Environment.NewLine + "诊断日志：" + session.DiagnosticLog);
                    return;
                }
                _issues.Text = IsCatia && _import != null ? _import.Failure(ex) : _plan != null ?
                    _plan.Execution.Failure(ex.GetBaseException()) : ex.GetBaseException().Message;
                _finished = IsCatia ? _import != null && _import.SceneWriteAttempted :
                    _plan != null && _plan.Execution.WriteAttempted;
                if (!_finished) { _stale = true; _import = null; _assignment = null; }
                _status.Text = "执行已停止，请核对结果后重新读取或检查。";
            }
            finally { if (_cancel != null) { _cancel.Dispose(); _cancel = null; } _busy = false; UpdateActions(); }
        }
        private void UpdateActions()
        {
            if (_apply == null) return;
            bool locked = Frozen || _finished; bool edit = !_busy && !locked;
            _sourceKind.Enabled = _read.Enabled = !_busy; _sourcePick.Enabled = !_busy;
            _targetPick.Enabled = _newRoot.Enabled = _includeRoot.Enabled = _mode.Enabled = edit;
            _assignmentPick.Enabled = !_busy && !_finished;
            _rootName.Enabled = edit && _newRoot.Checked; _left.Enabled = edit; _right.Enabled = !_busy; _search.Enabled = !_busy;
            _modelFolder.Enabled = _browseDestination.Enabled = _documentFolder.Enabled = _hierarchyFolder.Enabled = edit;
            _noReduction.Enabled = !_busy && !_finished;
            _check.Enabled = !_busy && !_finished && _source != null;
            bool ready = !_busy && !_stale && !_finished;
            bool catiaReady = ready && IsCatia && _import != null &&
                (_import.Stage == CatiaImportStage.Ready || _import.Stage == CatiaImportStage.TreeCreated);
            _create.Visible = _assign.Visible = IsCatia;
            _create.Enabled = catiaReady && !_import.TreeBuilt;
            _assign.Enabled = catiaReady && _assignment != null && _assignment.Plan.Issues.Count == 0 &&
                _assignment.Plan.Changes.Any(c => !c.Container && c.Existing != null);
            _apply.Enabled = IsCatia ? catiaReady && _import.CanConvert :
                ready && _plan != null && _plan.HasChanges && _plan.Issues.Count == 0;
            _apply.Text = IsCatia ? "转换格式并导入" : "执行整理";
            _exportJt.Visible = SelectedKind == SourceKind.ThreeDXml ||
                (_catia != null && string.Equals(Path.GetExtension(_catia.DocumentPath), ".3dxml", StringComparison.OrdinalIgnoreCase));
            _exportJt.Enabled = catiaReady && _import.CanConvert;
        }
    }
}
