using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;
using TxTools.CatiaPartTree.Core;
using TxTools.CatiaPartTree.Ps;

namespace TxTools.CatiaPartTree
{
    public class CatiaPartTreeForm : PickAwareTxForm
    {
        private StructureNode _source;
        private readonly System.Windows.Forms.Timer _autoPreview = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly TxObjGridCtrl _sourcePick = new TxObjGridCtrl(), _targetPick = new TxObjGridCtrl(), _bindingPick = new TxObjGridCtrl();
        private bool _syncPicker;
        private ITxObject _target;
        private object _document;
        private StructurePlan _plan;
        private readonly TreeView _left = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
        private readonly TreeView _right = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        private readonly ListView _problems = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        private readonly TextBox _issues = new TextBox { Dock = DockStyle.Bottom, Height = 42, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly TextBox _search = new TextBox { Width = 100 };
        private readonly ComboBox _candidates = new ComboBox { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _targetLabel = WorkflowUi.Label("目标容器：未选择");
        private readonly Label _sourceLabel = WorkflowUi.Label("来源：未读取");
        private readonly Label _status = WorkflowUi.Label("① 选择输入 → ② 编辑结构 → ③ 检查问题 → ④ 执行结果");
        private readonly Label _selection = WorkflowUi.Label("选中来源节点后可指定对应的 PS 零件实例");
        private readonly CheckBox _includeRoot = new CheckBox { Text = "保留来源根容器", Checked = true, AutoSize = true };
        private readonly CheckBox _createLayer = new CheckBox { Text = "创建此层容器", AutoSize = true, Enabled = false };
        private bool _showingSelection;
        private readonly ComboBox _mode = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        private Button _apply, _result;
        private bool _refreshing, _busy, _scaled, _stale = true, _finished;
        private readonly Size _design = new Size(650, 860);
        private StructureNode SelectedSource { get { return _left.SelectedNode == null ? null : _left.SelectedNode.Tag as StructureNode; } }

        public CatiaPartTreeForm(SynchronizationContext context)
        {
            FormUiKit.InitStandardForm(this, "零件树整理 · 结构与实例归属", _design, new Size(650, 780), sizable: true);
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; SemiModal = false;
            _document = TxApplication.ActiveDocument;
            var input = WorkflowUi.PickerRow(WorkflowUi.Picker("① 来源零件（单选）", _sourcePick), WorkflowUi.Picker("② 目标容器（单选）", _targetPick), WorkflowUi.Picker("③ 绑定实例（选左树叶子）", _bindingPick));
            _sourcePick.EnableMultipleSelection = _targetPick.EnableMultipleSelection = _bindingPick.EnableMultipleSelection = false;
            _sourcePick.ObjectInserted += (s, e) => Run(ReadPickedSource);
            _sourcePick.RowDeleted += (s, e) => { if (!_syncPicker) { _source = null; RefreshSource(); InvalidatePlan(); } };
            _targetPick.ObjectInserted += (s, e) => Run(ReadPickedTarget);
            _targetPick.RowDeleted += (s, e) => { _target = null; InvalidatePlan(); };
            _bindingPick.ObjectInserted += (s, e) => { if (!_syncPicker) Run(() => Bind(_bindingPick.GetObject(_bindingPick.Count - 1))); };
            _bindingPick.RowDeleted += (s, e) => { if (!_syncPicker && SelectedSource != null) Edit(n => n.BoundPart = null); };
            var catia = WorkflowUi.Bar(); WorkflowUi.Button(catia, "读取 CATIA 活动产品", ReadCatia); catia.Controls.Add(_sourceLabel);
            _mode.Items.AddRange(new object[] { "仅创建容器结构", "创建结构并归类已有零件" }); _mode.SelectedIndex = 0;
            var settings = WorkflowUi.Bar(); settings.Controls.Add(WorkflowUi.Label("执行内容")); settings.Controls.Add(_mode); settings.Controls.Add(_includeRoot);
            settings.Controls.Add(WorkflowUi.Label("取消根容器后，从来源根的子级开始"));
            var leftTools = WorkflowUi.Bar(); leftTools.Controls.Add(WorkflowUi.Label("零件树 · 编辑参与范围")); leftTools.Controls.Add(_search);

            var rightTools = WorkflowUi.Bar(); rightTools.Controls.Add(WorkflowUi.Label("零件容器创建预览"));

            rightTools.Controls.Add(_createLayer);

            var split = new SplitContainer { Size = _design, Dock = DockStyle.Fill, SplitterDistance = 470 };
            split.Panel1.Controls.Add(_left); split.Panel1.Controls.Add(leftTools); split.Panel2.Controls.Add(_right); split.Panel2.Controls.Add(rightTools);
            leftTools.AutoSize = rightTools.AutoSize = false; leftTools.Height = rightTools.Height = 70;
            WorkflowUi.EqualTrees(split); WireDragDrop();
            _right.LabelEdit = true;
            _right.AfterLabelEdit += (s, e) => { e.CancelEdit = true; if (e.Label != null) Run(() => { SelectSource(((StructureChange)e.Node.Tag).Node); Edit(n => n.TargetName = StructureEngine.Clean(e.Label)); }); };
            _right.BeforeLabelEdit += (s, e) => { var c = (StructureChange)e.Node.Tag; if (!c.Container) e.CancelEdit = true; else e.Node.Text = c.Name; };
            _right.NodeMouseDoubleClick += (s, e) => { if (((StructureChange)e.Node.Tag).Container) e.Node.BeginEdit(); };
            foreach (var col in new[] { "来源节点", "问题", "来源路径" }) _problems.Columns.Add(col, col == "问题" ? 430 : 270);
            var problemBox = new GroupBox { Text = "问题列表 · 点击定位来源，指定实例或排除分支后重新检查", Dock = DockStyle.Bottom, Height = 95 }; problemBox.Controls.Add(_problems);
            var footer = WorkflowUi.Bar(); footer.Dock = DockStyle.Bottom;
            WorkflowUi.Button(footer, "重新扫描场景", Preview); _apply = WorkflowUi.Button(footer, "创建结构", Apply);
            _result = WorkflowUi.Button(footer, "继续快速分序", () => Run(() => Handoff(!_stale && _plan != null && _plan.Execution.Completed)));
            Controls.Add(split); Controls.Add(problemBox); Controls.Add(_issues); Controls.Add(footer);
            Controls.Add(WorkflowUi.Stack(input, catia, settings, _status));
            _includeRoot.CheckedChanged += (s, e) => InvalidatePlan(); _mode.SelectedIndexChanged += (s, e) => InvalidatePlan();
            _search.TextChanged += (s, e) => RefreshSource();
            _left.AfterCheck += (s, e) =>
            {
                if (_refreshing) return; var n = (StructureNode)e.Node.Tag;
                foreach (var child in n.Walk()) child.Include = e.Node.Checked;
                if (n.Include) for (var p = n.Parent; p != null; p = p.Parent) p.Include = true;
                RefreshSource(); InvalidatePlan();
            };
            _left.AfterSelect += (s, e) => { if (!_refreshing) ShowSelection(); };
            _right.AfterSelect += (s, e) => { var c = e.Node.Tag as StructureChange; if (c != null) SelectSource(c.Node); };
            _right.KeyDown += (s, e) => { if (e.KeyCode == Keys.F2 && _right.SelectedNode != null) { e.Handled = true; _right.SelectedNode.BeginEdit(); } };
            _problems.SelectedIndexChanged += (s, e) => { if (_problems.SelectedItems.Count > 0) SelectSource((StructureNode)_problems.SelectedItems[0].Tag); };
            _candidates.SelectedIndexChanged += (s, e) => UpdateActions();
            _createLayer.CheckedChanged += (s, e) => { if (!_showingSelection) { bool create = _createLayer.Checked; Edit(n => n.SkipLevel = !create); } };
            _autoPreview.Tick += (s, e) => { _autoPreview.Stop(); if (!_busy && _source != null && _target != null) Preview(); };
            FormClosed += (s, e) => _autoPreview.Dispose();
            _issues.Text = "读取来源，选择目标，编辑结构后重新检查。仅创建结构时不会移动零件。"; UpdateActions();
        }
        protected override void OnLoad(EventArgs e) { base.OnLoad(e); FormUiKit.ApplyDpiScaling(this, ref _scaled, _design); WorkflowUi.LimitWidth(this); }
        private void ReadPickedSource()
        {
            InvalidatePlan();
            if (_sourcePick.Count == 0) return;
            _source = null; RefreshSource();
            StructureNode source;
            try { source = StructureEngine.Read(_sourcePick.GetObject(_sourcePick.Count - 1)); }
            catch { _sourcePick.DeleteRow(_sourcePick.Count - 1); throw; }
            SetSource(source, "PS 实际结构");
        }
        private void ReadPickedTarget()
        {
            InvalidatePlan();
            if (_targetPick.Count == 0) return;
            var target = _targetPick.GetObject(_targetPick.Count - 1);
            if (!(target is ITxCompoundPartCreation)) { _targetPick.DeleteRow(_targetPick.Count - 1); throw new InvalidOperationException("目标必须支持创建零件容器。"); }
            _target = target; InvalidatePlan();
        }
        private void WireDragDrop()
        {
            foreach (var tree in new[] { _left, _right })
            {
                tree.AllowDrop = true;
                WorkflowUi.EnableDragScrolling(tree);
                tree.ItemDrag += (s, e) => { var node = (TreeNode)e.Item; var change = node.Tag as StructureChange; tree.DoDragDrop(change == null ? node.Tag : change.Node, DragDropEffects.Move); };
                tree.DragOver += (s, e) => { e.Effect = !_busy && e.Data.GetDataPresent(typeof(StructureNode)) ? DragDropEffects.Move : DragDropEffects.None; };
                tree.DragDrop += (s, e) => Run(() =>
                {
                    var node = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y))); if (node == null) return;
                    var change = node.Tag as StructureChange;
                    var source = e.Data.GetData(typeof(StructureNode)) as StructureNode;
                    var target = change == null ? (StructureNode)node.Tag : change.Node;
                    bool before = tree.PointToClient(new Point(e.X, e.Y)).Y < node.Bounds.Top + node.Bounds.Height / 3;
                    if (before && source != null && source.Parent != null && source.Parent == target.Parent)
                    { var siblings = source.Parent.Children; if (source != target) { siblings.Remove(source); siblings.Insert(siblings.IndexOf(target), source); RefreshSource(); InvalidatePlan(); } }
                    else DropStructure(source, target);
                });
            }
        }
        private void DropStructure(StructureNode source, StructureNode target)
        {
            if (source == null || target == null || source == target) return;
            if (source.Parent == null || source.Walk().Contains(target) || !target.IsContainer)
                throw new InvalidOperationException("根节点不能移动，也不能拖入自身后代或叶子零件。");
            source.Parent.Children.Remove(source); target.Add(source);
            RefreshSource(); InvalidatePlan();
            _issues.Text = "草稿已调整：放到节点文字处移入容器；放到同级节点上沿排在其前。重新检查后执行。";
        }
        private void Run(Action action) { if (_busy) return; try { action(); } catch (Exception ex) { _issues.Text = ex.Message; } }
        private void InvalidatePlan()
        {
            _autoPreview.Stop(); _autoPreview.Start(); _stale = true; _status.Text = _plan == null ? "① 选择来源、目标与执行内容，再检查" : "③ 预览已过期，仅供对照；名称、层级和实例绑定已保留，请重新检查"; UpdateActions();
        }
        private void UpdateActions()
        {
            if (_apply == null) return;
            _apply.Enabled = !_busy && !_stale && !_finished && _plan != null && _plan.HasChanges && _plan.Issues.Count == 0;
            _result.Enabled = !_busy && _source != null;
            var n = SelectedSource; bool canBind = !_busy && n != null && !n.IsContainer && n.Source == null;
            _bindingPick.Enabled = canBind;
        }
        private void SetSource(StructureNode source, string description)
        {
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument))
            { _target = null; while (_targetPick.Count > 0) _targetPick.DeleteRow(0); _targetLabel.Text = "目标容器：未选择"; }
            _source = source; _document = TxApplication.ActiveDocument; _sourceLabel.Text = "来源：" + description + " / " + source.Label;
            _search.Clear(); RefreshSource(); InvalidatePlan();
        }
        private static StructureNode Convert(TreeEditNode n)
        { var result = new StructureNode { SourceName = n.Name, PartNumber = n.PartNumber, IsContainer = n.IsAssembly || n.Children.Count > 0 }; foreach (var child in n.Children) result.Add(Convert(child)); return result; }
        private async void ReadCatia()
        {
            if (_busy) return; var document = TxApplication.ActiveDocument; _busy = true; Enabled = false; _issues.Text = "正在读取 CATIA 产品树…";
            var completion = new System.Threading.Tasks.TaskCompletionSource<TreeEditNode>();
            var thread = new Thread(() => { try { completion.SetResult(new CatiaPartTreeService().ReadCatiaTree(50)); } catch (Exception ex) { completion.SetException(ex); } });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
            try { var model = await completion.Task; if (!IsDisposed) { if (!ReferenceEquals(document, TxApplication.ActiveDocument)) throw new InvalidOperationException("读取期间文档已切换，请重新读取。"); SetSource(Convert(model), "CATIA 产品结构"); _issues.Text = "已读取 CATIA，请选择 PS 目标容器。"; } }
            catch (Exception ex) { if (!IsDisposed) _issues.Text = ex.Message; }
            finally { _busy = false; if (!IsDisposed) { Enabled = true; UpdateActions(); } }
        }
        private void RefreshSource()
        {
            _refreshing = true;
            try { WorkflowUi.Rebuild(_left, () => { if (_source != null) { var node = Build(_source); if (node != null) _left.Nodes.Add(node); } }, tag => ((StructureNode)tag).Key); }
            finally { _refreshing = false; } ShowSelection();
        }
        private TreeNode Build(StructureNode n)
        {
            var children = n.Children.Select(Build).Where(c => c != null).ToArray();
            if (_search.Text.Length > 0 && (n.SourceName + " " + n.PartNumber).IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase) < 0 && children.Length == 0) return null;
            bool partial = n.Include && n.Walk().Any(c => !c.Include);
            var node = new TreeNode(n.SourceName + (partial ? " [部分纳入]" : "") + (n.SkipLevel ? " [不创建此层]" : "") + (n.BoundPart != null ? " [已指定实例]" : "")) { Tag = n, Checked = n.Include };
            node.Nodes.AddRange(children); return node;
        }
        private void Edit(Action<StructureNode> edit)
        { Run(() => { var n = SelectedSource; if (n == null) throw new InvalidOperationException("请先选择来源节点或对应的目标预览节点。"); edit(n); RefreshSource(); InvalidatePlan(); }); }
        private void Rename()
        {
            var n = SelectedSource; if (n == null || !n.IsContainer) throw new InvalidOperationException("请选择需要设置目标名称的装配节点。");
            var name = WorkflowUi.AskName(this, "修改规划名称（可能由复用改为新建）", n.Label); if (name != null) Edit(node => node.TargetName = StructureEngine.Clean(name));
        }
        private void Reorder(int delta)
        {
            Edit(n =>
            {
                if (n.Parent == null) return;
                if (_stale || _plan == null) throw new InvalidOperationException("请先重新检查，再调整新建容器的创建顺序。");
                var change = _plan.Changes.FirstOrDefault(c => c.Node == n && c.Container);
                if (change == null || _plan.Changes.Any(c => c.Container && c.Parent == change.Parent && c.Existing != null)) throw new InvalidOperationException("该层包含复用容器或本层不创建，请在 PS 原生树中调整已有对象。");
                var siblings = n.Parent.Children; int i = siblings.IndexOf(n), j = i + delta;
                if (j >= 0 && j < siblings.Count) { siblings.RemoveAt(i); siblings.Insert(j, n); }
            });
        }
        private void SelectSource(StructureNode model)
        {
            if (_search.Text.Length > 0) _search.Clear();
            var node = WorkflowUi.Nodes(_left.Nodes).FirstOrDefault(n => n.Tag == model);
            if (node != null) { _left.SelectedNode = node; node.EnsureVisible(); ShowSelection(); }
        }
        private sealed class Candidate
        { public ITxObject Value; public override string ToString() { return StructureEngine.Path(Value) + " [" + Value.Id + "]"; } }
        private void ShowSelection()
        {
            var n = SelectedSource; _candidates.Items.Clear();
            bool validBinding = n != null && n.BoundPart != null && n.BoundPart.IsValid();
            _syncPicker = true;
            try { while (_bindingPick.Count > 0) _bindingPick.DeleteRow(0); if (validBinding) _bindingPick.AppendObject(n.BoundPart); } finally { _syncPicker = false; }
            _showingSelection = true;
            try { _createLayer.Enabled = n != null && n.IsContainer && !_busy; _createLayer.Checked = n != null && n.IsContainer && !n.SkipLevel; }
            finally { _showingSelection = false; }
            if (n == null) { _selection.Text = "选择来源节点查看编号、对应实例及候选"; UpdateActions(); return; }
            _selection.Text = n.SourceName + " / 编号：" + (n.PartNumber ?? "—") + (n.BoundPart == null ? "" : validBinding ? " / 已绑定：" + StructureEngine.Path(n.BoundPart) : " / 绑定实例已失效，请重新选择") +
                (n.SkipLevel ? " / 本层不创建，子节点提升到上级有效容器" : "");
            if (_plan != null && !n.IsContainer && n.Source == null)
            {
                var items = new List<ITxObject>(); List<ITxObject> values;
                foreach (var key in new[] { n.SourceName, n.PartNumber }.Where(k => !string.IsNullOrEmpty(k)).Distinct())
                    if (_plan.Candidates.TryGetValue(key, out values)) items.AddRange(values);
                foreach (var item in items.GroupBy(StructureEngine.Id).Select(g => g.First())) _candidates.Items.Add(new Candidate { Value = item });
                if (_candidates.Items.Count > 0) _candidates.SelectedIndex = 0;
            }
            UpdateActions();
        }
        private void Bind(ITxObject part)
        {
            var n = SelectedSource;
            if (n == null || n.IsContainer || n.Source != null) throw new InvalidOperationException("请选择 CATIA 来源中的叶子零件。PS 来源按实际对象归类，无需重新绑定。");
            if (!StructureEngine.IsPart(part) || !part.IsValid()) throw new InvalidOperationException("请选择有效的 PS 零件实例，不能选择容器。");
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument)) throw new InvalidOperationException("文档已切换，请重新读取来源。");
            n.BoundPart = part; RefreshSource(); InvalidatePlan();
        }
        private void Preview()
        {
            if (_busy) return; _autoPreview.Stop(); _stale = true; _busy = true; UpdateActions();
            try
            {
                if (!ReferenceEquals(_document, TxApplication.ActiveDocument)) throw new InvalidOperationException("文档已切换，请重新读取来源。");
                var plan = StructureEngine.Plan(_source, _target, _includeRoot.Checked, _mode.SelectedIndex == 1);
                _plan = plan; _stale = false; _finished = false;
                WorkflowUi.Rebuild(_right, () =>
                {
                    var nodes = new Dictionary<StructureChange, TreeNode>();
                    foreach (var c in plan.Changes) { var node = new TreeNode(c.Name) { Tag = c, ForeColor = c.Error == null ? SystemColors.WindowText : Color.Firebrick }; nodes[c] = node; if (c.Parent == null) _right.Nodes.Add(node); else nodes[c.Parent].Nodes.Add(node); }
                }, tag => ((StructureChange)tag).Node.Key);
                _problems.Items.Clear();
                foreach (var c in plan.Changes.Where(c => c.Error != null))
                { var item = new ListViewItem(c.Node.SourceName) { Tag = c.Node }; item.SubItems.Add(c.Error); item.SubItems.Add(SourcePath(c.Node)); _problems.Items.Add(item); }
                int created = plan.Changes.Count(c => c.Container && c.Existing == null), reused = plan.Changes.Count(c => c.Container && c.Existing != null);
                int moved = plan.Changes.Count(c => !c.Container && !c.Unchanged && c.Error == null), same = plan.Changes.Count(c => !c.Container && c.Unchanged);
                _status.Text = "③ 预计：新建 " + created + " / 复用 " + reused + " / 移动零件 " + moved + " / 已在位 " + same + " / 问题 " + plan.Issues.Count;
                _issues.Text = plan.Issues.Count == 0 ? "检查通过。选中来源或预览节点可调整名称、层级和实例绑定。" : string.Join(Environment.NewLine, plan.Issues);
                _apply.Text = !plan.HasChanges ? "无需变更" : _mode.SelectedIndex == 0 ? "创建容器结构" : "创建结构并移动 " + moved + " 个零件"; ShowSelection();
            }
            catch (Exception ex)
            { _right.Nodes.Clear(); _problems.Items.Clear(); _issues.Text = "检查未通过，未修改场景：" + ex.Message; }
            finally { _busy = false; ShowSelection(); UpdateActions(); }
        }
        private static string SourcePath(StructureNode n) { return n.Parent == null ? n.SourceName : SourcePath(n.Parent) + " / " + n.SourceName; }
        private void Handoff(bool actual)
        {
            if (_source == null) throw new InvalidOperationException("请先读取来源。");
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument)) throw new InvalidOperationException("文档已切换，请重新读取来源。");
            StructureNode source;
            if (actual)
            {
                if (_stale || _plan == null || !_plan.Execution.Completed) throw new InvalidOperationException("请先完整执行整理，再使用整理结果。");
                source = StructureEngine.Read(_target);
            }
            else
            {
                source = _source.Copy(); if (!_includeRoot.Checked) source.SkipLevel = true;
                if (!_stale && _plan != null && !_finished)
                    foreach (var change in _plan.Changes.Where(c => !c.Container && c.Error == null && c.Existing != null))
                    { var node = source.Walk().First(n => n.Key == change.Node.Key); if (node.Source == null) node.BoundPart = change.Existing; }
            }
            new TxTools.WeldSpotGrouper.GrouperForm(source, actual ? "PS 整理目标分支（包含已有内容）" : "零件树结构草稿").Show();
        }
        private void Apply()
        {
            _autoPreview.Stop();
            if (_busy || _stale || _finished || !_apply.Enabled || _plan == null) return; _busy = true; UpdateActions(); var notes = new List<string>();
            try { _issues.Text = StructureEngine.Apply(_plan, notes.Add); _status.Text = "④ 执行完成，可使用整理后的 PS 目标分支继续分序。"; }
            catch (Exception ex) { _issues.Text = _plan.Execution.Failure(ex) + Environment.NewLine + string.Join(Environment.NewLine, notes); _status.Text = "④ 执行已停止，请核对结果后重新检查。"; }
            finally
            {
                _busy = false; _finished = true;
                foreach (var node in WorkflowUi.Nodes(_right.Nodes))
                {
                    var change = (StructureChange)node.Tag;
                    if (change.Result != null) node.Text = change.Name;
                }
                UpdateActions();
            }
        }
    }
}
