using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;

namespace TxTools.WeldSpotGrouper
{
    internal sealed class PointSelectionTree : TreeView
    {
        private readonly HashSet<string> _selected = new HashSet<string>();
        private TreeNode _anchor, _dropNode;
        private bool _mouseSelecting, _collapseOnClick, _after;
        public bool PreserveSelection;
        public event EventHandler PointSelectionChanged;
        public PointSelectionTree() { DrawMode = TreeViewDrawMode.OwnerDrawText; }
        public List<SpotItem> SelectedSpots
        {
            get { return WorkflowUi.Nodes(Nodes).Where(n => n.Tag is SpotItem && _selected.Contains(((SpotItem)n.Tag).LocOp.Id)).Select(n => (SpotItem)n.Tag).GroupBy(s => s.LocOp.Id).Select(g => g.First()).ToList(); }
        }
        private void Changed() { Invalidate(); PointSelectionChanged?.Invoke(this, EventArgs.Empty); }
        private void SelectRange(TreeNode node, bool keep)
        {
            if (!keep) _selected.Clear();
            // Walk all expanded rows, including those outside the viewport.
            var visible = new List<TreeNode>();
            for (var n = Nodes.Count == 0 ? null : Nodes[0]; n != null; n = n.NextVisibleNode) visible.Add(n);
            int start = visible.IndexOf(_anchor), end = visible.IndexOf(node);
            if (start < 0) start = end;
            if (end < 0) return;
            for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                if (visible[i].Tag is SpotItem) _selected.Add(((SpotItem)visible[i].Tag).LocOp.Id);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (HitTest(e.Location).Location == TreeViewHitTestLocations.PlusMinus) { base.OnMouseDown(e); return; }
            var node = GetNodeAt(e.Location); var spot = node == null ? null : node.Tag as SpotItem;
            _mouseSelecting = true; _collapseOnClick = false;
            if (spot != null)
            {
                bool control = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
                if (e.Button == MouseButtons.Right && _selected.Contains(spot.LocOp.Id)) { }
                else if (shift) SelectRange(node, control);
                else if (control) { if (!_selected.Add(spot.LocOp.Id)) _selected.Remove(spot.LocOp.Id); _anchor = node; }
                else if (_selected.Contains(spot.LocOp.Id)) _collapseOnClick = _selected.Count > 1;
                else { _selected.Clear(); _selected.Add(spot.LocOp.Id); _anchor = node; }
            }
            else { _selected.Clear(); _anchor = null; }
            try { if (node != null) SelectedNode = node; base.OnMouseDown(e); } finally { _mouseSelecting = false; }
            Changed();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_collapseOnClick && e.Button == MouseButtons.Left && SelectedNode != null && SelectedNode.Tag is SpotItem)
            { _selected.Clear(); _selected.Add(((SpotItem)SelectedNode.Tag).LocOp.Id); _anchor = SelectedNode; Changed(); }
            _collapseOnClick = false; base.OnMouseUp(e);
        }
        protected override void OnItemDrag(ItemDragEventArgs e)
        { _collapseOnClick = false; base.OnItemDrag(e); }
        protected override void OnAfterSelect(TreeViewEventArgs e)
        {
            if (!PreserveSelection && !_mouseSelecting)
            {
                if ((ModifierKeys & Keys.Shift) != 0 && e.Node.Tag is SpotItem) SelectRange(e.Node, (ModifierKeys & Keys.Control) != 0);
                else if ((ModifierKeys & Keys.Control) == 0)
                { _selected.Clear(); if (e.Node.Tag is SpotItem) _selected.Add(((SpotItem)e.Node.Tag).LocOp.Id); _anchor = e.Node; }
                Changed();
            }
            base.OnAfterSelect(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            { foreach (var n in WorkflowUi.Nodes(Nodes)) if (n.Tag is SpotItem) _selected.Add(((SpotItem)n.Tag).LocOp.Id); Changed(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }
        public void RetainSelection()
        {
            var ids = new HashSet<string>(WorkflowUi.Nodes(Nodes).Where(n => n.Tag is SpotItem).Select(n => ((SpotItem)n.Tag).LocOp.Id));
            _selected.IntersectWith(ids);
            var anchor = _anchor == null ? null : _anchor.Tag as SpotItem;
            if (anchor != null) _anchor = WorkflowUi.Nodes(Nodes).FirstOrDefault(n => n.Tag is SpotItem && ((SpotItem)n.Tag).LocOp.Id == anchor.LocOp.Id);
            Invalidate();
        }
        public void DropMarker(TreeNode node, bool after)
        { if (_dropNode == node && _after == after) return; _dropNode = node; _after = after; Invalidate(); }
        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            var spot = e.Node.Tag as SpotItem;
            if (spot == null) e.DrawDefault = true;
            else
            {
                bool selected = _selected.Contains(spot.LocOp.Id);
                using (var brush = new SolidBrush(selected ? SystemColors.Highlight : BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, Font, e.Bounds, selected ? SystemColors.HighlightText : e.Node.ForeColor.IsEmpty ? ForeColor : e.Node.ForeColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            }
            if (e.Node == _dropNode)
                using (var pen = new Pen(SystemColors.Highlight, 2))
                { int y = _after ? e.Bounds.Bottom - 1 : e.Bounds.Top; e.Graphics.DrawLine(pen, e.Bounds.Left, y, ClientSize.Width - 3, y); }
        }
    }
    public class GrouperForm : PickAwareTxForm
    {
        private StructureNode _source;
        private SequencePlan _plan;
        private SequenceDraft _draft = new SequenceDraft();
        private ITxObject _target;
        private object _document;
        private readonly TxObjGridCtrl _scope = new TxObjGridCtrl();
        private readonly TxObjGridCtrl _sourcePick = new TxObjGridCtrl(), _targetPick = new TxObjGridCtrl();
        private readonly TreeView _left = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        private readonly PointSelectionTree _right = new PointSelectionTree { Dock = DockStyle.Fill, HideSelection = false };
        private static readonly object PendingOperation = new object();
        private const string PendingOrderKey = "virtual:pending";
        private readonly TextBox _issues = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly ComboBox _rule = Choice(190, "按零件结构划分", "按绑定零件实例组合");
        private readonly ComboBox _cross = Choice(210, "跨分支点：待人工处理", "跨分支点：最近公共装配", "跨分支点：保留原位");
        private readonly NumericUpDown _depth = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 1, Width = 50 };
        private readonly TextBox _search = new TextBox { Width = 85 };
        private readonly Label _sourceLabel = WorkflowUi.Label("零件结构：未读取");
        private readonly Label _targetLabel = WorkflowUi.Label("目标操作：未选择");
        private readonly Label _status = WorkflowUi.Label("① 选择输入 → ② 设置分组 → ③ 检查分配 → ④ 执行结果");
        private readonly SplitContainer _trees = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1120, 300), SplitterDistance = 450 };
        private readonly Timer _autoPreview = new Timer { Interval = 250 };
        private readonly Timer _watch = new Timer { Interval = 600 };
        private readonly Stack<SequenceEditSnapshot> _undo = new Stack<SequenceEditSnapshot>();
        private Button _apply;
        private bool _refreshing, _scaled, _stale = true, _busy, _finished;
        private string _scopeStamp = "";
        private readonly Size _design = new Size(650, 860);
        public new bool Closed { get; private set; }
        private bool Editable { get { return _plan != null && !_stale && !_busy && !_finished; } }
        private SpotItem SelectedSpot { get { return _right.SelectedNode == null ? null : _right.SelectedNode.Tag as SpotItem; } }
        private SequenceEntry SelectedEntry { get { return _right.SelectedNode == null ? null : (_right.SelectedNode.Tag as SequenceEntry ?? (_right.SelectedNode.Parent == null ? null : _right.SelectedNode.Parent.Tag as SequenceEntry)); } }
        private static ComboBox Choice(int width, params string[] values)
        { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width }; c.Items.AddRange(values); if (values.Length > 0) c.SelectedIndex = 0; return c; }

        public GrouperForm() : this(null) { }
        public GrouperForm(StructureNode source, string description = null)
        {
            FormUiKit.InitStandardForm(this, "快速分序 · 工序组织与点位分配", _design, new Size(650, 780), sizable: true);
            SemiModal = false; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true;
            _document = TxApplication.ActiveDocument; _source = source == null ? null : source.Copy();
            if (source != null) _sourceLabel.Text = "来源：" + (description ?? "结构草稿") + " / " + source.Label;
            var input = WorkflowUi.PickerRow(WorkflowUi.Picker("① 来源零件（单选）", _sourcePick), WorkflowUi.Picker("② 焊点范围（多选）", _scope), WorkflowUi.Picker("③ 目标操作（单选）", _targetPick));
            _sourcePick.EnableMultipleSelection = _targetPick.EnableMultipleSelection = false;
            _sourcePick.ObjectInserted += (s, e) => Run(ReadPickedSource);
            _sourcePick.RowDeleted += (s, e) => { _source = null; RefreshSource(); InvalidatePlan(); };
            _targetPick.ObjectInserted += (s, e) => Run(ReadPickedTarget);
            _targetPick.RowDeleted += (s, e) => { _target = null; InvalidatePlan(); };
            var modes = WorkflowUi.Bar(); modes.Controls.Add(WorkflowUi.Label("④ 分组规则")); modes.Controls.Add(_rule); modes.Controls.Add(_cross);
            var nodeTools = WorkflowUi.Bar();
            nodeTools.Controls.Add(WorkflowUi.Label("零件树 · 勾选工序来源")); nodeTools.Controls.Add(_search); nodeTools.Controls.Add(_depth);
            WorkflowUi.Button(nodeTools, "按层级", () => SetDepth(true));
            _left.CheckBoxes = true;
            _left.AfterCheck += (s, e) => { if (!_refreshing) { var n = (StructureNode)e.Node.Tag; n.IsOperation = Included(n) && e.Node.Checked; RefreshSource(); InvalidatePlan(); } };
            var entries = WorkflowUi.Bar(); entries.Controls.Add(WorkflowUi.Label("操作创建预览 · 与最终结构一致"));
            _trees.Panel1.Controls.Add(_left); _trees.Panel1.Controls.Add(nodeTools);
            _trees.Panel2.Controls.Add(_right); _trees.Panel2.Controls.Add(entries);
            nodeTools.AutoSize = entries.AutoSize = false; nodeTools.Height = entries.Height = 66;
            WorkflowUi.EqualTrees(_trees);
            var menu = new ContextMenuStrip();
            var assignMenu = new ToolStripMenuItem("将焊点分配至"); menu.Items.Add(assignMenu);
            var create = menu.Items.Add("新建工序并接收所选焊点", null, (s, e) => Run(NewEntry));
            var rename = menu.Items.Add("修改工序名称（F2）", null, (s, e) => Run(RenameEntry));
            var keep = menu.Items.Add("移至待分配（保留原位）", null, (s, e) => MovePoints(_right.SelectedSpots, null, null, false));
            var restore = menu.Items.Add("恢复所选焊点自动归属", null, (s, e) => RestorePoint());
            var undo = menu.Items.Add("撤销点位调整", null, (s, e) => UndoAssignment());
            menu.Opening += (s, e) =>
            {
                assignMenu.DropDownItems.Clear(); bool points = Editable && _right.SelectedSpots.Count > 0;
                assignMenu.Enabled = create.Enabled = keep.Enabled = restore.Enabled = points;
                rename.Enabled = Editable && SelectedEntry != null; undo.Enabled = Editable && _undo.Count > 0;
                if (_plan == null) return;
                foreach (var entry in _plan.Entries.Where(o => !o.IsGroup))
                { var key = entry.Key; assignMenu.DropDownItems.Add(entry.Name, null, (sender, args) => MovePoints(_right.SelectedSpots, key, null, false)); }
                assignMenu.DropDownItems.Add("待分配（虚拟，不创建）", null, (sender, args) => MovePoints(_right.SelectedSpots, null, null, false));
            };
            _right.ContextMenuStrip = menu;
            _right.KeyDown += (s, e) => { if (e.KeyCode == Keys.F2) Run(RenameEntry); };
            _right.PointSelectionChanged += (s, e) => { if (!_refreshing) Run(SelectInPs); };
            _right.ShowNodeToolTips = true;
            WireDragDrop();
            var footer = WorkflowUi.Bar(); footer.Dock = DockStyle.Bottom;
            WorkflowUi.Button(footer, "重新扫描场景", Preview); _apply = WorkflowUi.Button(footer, "执行分序", Apply);
            var problems = new GroupBox { Text = "检查结果 · Ctrl/Shift 多选 · 拖到点位上/下半部插入 · 蓝线指示位置", Dock = DockStyle.Bottom, Height = 48 }; problems.Controls.Add(_issues);
            Controls.Add(_trees); Controls.Add(problems); Controls.Add(footer); Controls.Add(WorkflowUi.Stack(input, modes, _status));
            _scope.EnableMultipleSelection = true; _scope.EnableRecurringObjects = false; FormUiKit.GridPickFocus.Wire(_scope);
            _scope.ObjectInserted += (s, e) => InvalidatePlan();
            _scope.RowDeleted += (s, e) => InvalidatePlan();
            _rule.SelectedIndexChanged += (s, e) => { _trees.Panel1Collapsed = _rule.SelectedIndex == 1; _sourcePick.Enabled = _cross.Enabled = _rule.SelectedIndex == 0; InvalidatePlan(); };
            _cross.SelectedIndexChanged += (s, e) => InvalidatePlan(); _search.TextChanged += (s, e) => RefreshSource();
            _autoPreview.Tick += (s, e) => { _autoPreview.Stop(); if (!_busy) Preview(); };
            _watch.Tick += (s, e) => { if (!_busy && !Closed) Run(() =>
            { var stamp = string.Join("|", Scopes().Select(StructureEngine.Id)); if (stamp != _scopeStamp) { _scopeStamp = stamp; InvalidatePlan(); }
                if (!ReferenceEquals(_document, TxApplication.ActiveDocument) && !_stale) InvalidatePlan(); }); };
            FormClosed += (s, e) => { Closed = true; _watch.Stop(); _watch.Dispose(); _autoPreview.Stop(); _autoPreview.Dispose(); };
            RefreshSource(); UpdateActions(); _watch.Start();
            _issues.Text = "选择范围和目标操作。按零件结构划分时，将零件或装配节点设为工序。检查不会修改场景。";
        }
        protected override void OnLoad(EventArgs e) { base.OnLoad(e); FormUiKit.ApplyDpiScaling(this, ref _scaled, _design); WorkflowUi.LimitWidth(this); }
        private void ReadPickedSource()
        {
            InvalidatePlan();
            if (_sourcePick.Count == 0) return;
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument))
            { while (_scope.Count > 0) _scope.DeleteRow(0); while (_targetPick.Count > 0) _targetPick.DeleteRow(0); _target = null; }
            _source = null; RefreshSource();
            StructureNode read;
            try { read = StructureEngine.Read(_sourcePick.GetObject(_sourcePick.Count - 1)); }
            catch { _sourcePick.DeleteRow(_sourcePick.Count - 1); throw; }
            _source = read; _document = TxApplication.ActiveDocument; _draft = new SequenceDraft(); _undo.Clear();
            RefreshSource(); InvalidatePlan();
        }
        private void ReadPickedTarget()
        {
            InvalidatePlan();
            if (_targetPick.Count == 0) return;
            var target = _targetPick.GetObject(_targetPick.Count - 1);
            if (!(target is TxCompoundOperation)) { _targetPick.DeleteRow(_targetPick.Count - 1); throw new InvalidOperationException("目标必须为复合操作。"); }
            _target = target; InvalidatePlan();
        }
        private bool PointDestination(TreeNode node, out string key, out string anchor)
        {
            key = null; anchor = null; if (node == null) return false;
            if (ReferenceEquals(node.Tag, PendingOperation)) return true;
            var entry = node.Tag as SequenceEntry;
            if (entry != null) { key = entry.Key; return true; }
            var existing = node.Tag as TxWeldOperation;
            if (existing != null)
            {
                entry = _plan.Entries.FirstOrDefault(e => e.Name == existing.Name);
                if (entry != null) { key = entry.Key; return true; }
            }
            if (node.Tag is SpotItem || node.Tag is ITxObject)
            {
                if (node.Tag is ITxObject && SpotGrouper.WpFromLocOp((ITxObject)node.Tag) == null) return false;
                if (node.Parent == null) return false;
                entry = node.Parent.Tag as SequenceEntry;
                if (entry == null && !ReferenceEquals(node.Parent.Tag, PendingOperation)) return false;
                key = entry == null ? null : entry.Key;
                anchor = node.Tag is SpotItem ? ((SpotItem)node.Tag).LocOp.Id : ((ITxObject)node.Tag).Id;
                return true;
            }
            return false;
        }
        private void WireDragDrop()
        {
            _right.AllowDrop = true; WorkflowUi.EnableDragScrolling(_right);
            _right.ItemDrag += (s, e) =>
            {
                if (!Editable) return;
                var node = (TreeNode)e.Item;
                if (node.Tag is SpotItem && _right.SelectedSpots.Count > 0) _right.DoDragDrop(_right.SelectedSpots.ToArray(), DragDropEffects.Move);
                else if (node.Tag is SequenceEntry) _right.DoDragDrop(node.Tag, DragDropEffects.Move);
            };
            _right.DragOver += (s, e) =>
            {
                var pos = _right.PointToClient(new Point(e.X, e.Y)); var node = _right.GetNodeAt(pos);
                string key, anchor; bool points = e.Data.GetDataPresent(typeof(SpotItem[]));
                bool valid = Editable && (points ? PointDestination(node, out key, out anchor) : node != null && node.Tag is SequenceEntry && e.Data.GetDataPresent(typeof(SequenceEntry)));
                e.Effect = valid ? DragDropEffects.Move : DragDropEffects.None;
                _right.DropMarker(valid && points && node != null && !(node.Tag is SequenceEntry) && !ReferenceEquals(node.Tag, PendingOperation) ? node : null,
                    node != null && pos.Y >= node.Bounds.Top + node.Bounds.Height / 2);
            };
            _right.DragLeave += (s, e) => _right.DropMarker(null, false);
            _right.QueryContinueDrag += (s, e) => { if (e.EscapePressed || e.Action != DragAction.Continue) _right.DropMarker(null, false); };
            _right.DragDrop += (s, e) => Run(() =>
            {
                _right.DropMarker(null, false);
                var pos = _right.PointToClient(new Point(e.X, e.Y)); var node = _right.GetNodeAt(pos); if (!Editable || node == null) return;
                var spots = e.Data.GetData(typeof(SpotItem[])) as SpotItem[];
                if (spots != null)
                { string key, anchor; if (PointDestination(node, out key, out anchor)) MovePoints(spots, key, anchor, pos.Y >= node.Bounds.Top + node.Bounds.Height / 2); }
                else if (node.Tag is SequenceEntry) DropEntry(e.Data.GetData(typeof(SequenceEntry)) as SequenceEntry, (SequenceEntry)node.Tag);
            });
        }
        private void DropEntry(SequenceEntry source, SequenceEntry target)
        {
            if (!Editable || source == null || source == target) return;
            var block = _plan.Entries.Where(e => e == source).ToList();
            _plan.Entries.RemoveAll(block.Contains); _plan.Entries.InsertRange(_plan.Entries.IndexOf(target), block);
            _draft.Order.Clear(); _draft.Order.AddRange(_plan.Entries.Select(e => e.Key)); Preview();
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { _watch.Stop(); _autoPreview.Stop(); while (_scope.Count > 0) _scope.DeleteRow(0); base.OnFormClosing(e); }
        private void Run(Action action) { if (_busy) return; try { action(); } catch (Exception ex) { _issues.Text = ex.Message; } }
        private void InvalidatePlan()
        { _autoPreview.Stop(); _autoPreview.Start(); _stale = true; _status.Text = "正在更新预览…（输入就绪后自动计算）"; UpdateActions(); }
        private void RefreshSource()
        { _refreshing = true; try { WorkflowUi.Rebuild(_left, () => { if (_source != null) { var n = SourceNode(_source); if (n != null) _left.Nodes.Add(n); } }, tag => ((StructureNode)tag).Key); } finally { _refreshing = false; } }
        private TreeNode SourceNode(StructureNode n)
        {
            var children = n.Children.Select(SourceNode).Where(c => c != null).ToArray();
            if (_search.Text.Length > 0 && n.SourceName.IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase) < 0 && children.Length == 0) return null;
            var node = new TreeNode(n.Label + (!Included(n) ? " [已排除]" : n.IsOperation ? " [建立工序]" : " [继承上级]")) { Tag = n, Checked = n.IsOperation, ForeColor = Included(n) ? SystemColors.WindowText : SystemColors.GrayText };
            node.Nodes.AddRange(children); return node;
        }
        private static bool Included(StructureNode n) { for (; n != null; n = n.Parent) if (!n.Include) return false; return true; }
        private void SetOperation(bool value)
        {
            var n = _left.SelectedNode == null ? null : _left.SelectedNode.Tag as StructureNode;
            if (n == null || !Included(n)) { _issues.Text = "请选择未排除的零件或装配节点。"; return; }
            n.IsOperation = value; RefreshSource(); InvalidatePlan();
        }
        private void SetDepth(bool replace)
        {
            if (_source == null) return;
            var nodes = _source.Walk().Where(n => Included(n) && n.IsContainer && n.Depth == (int)_depth.Value).ToList();
            if (nodes.Count == 0) { _issues.Text = "该层没有有效装配节点，未修改标记。"; return; }
            if (replace) foreach (var n in _source.Walk()) n.IsOperation = false;
            foreach (var n in nodes) n.IsOperation = true;
            RefreshSource(); InvalidatePlan(); _issues.Text = "已设置 " + nodes.Count + " 个工序：" + string.Join("、", nodes.Select(n => n.Label));
        }
        private List<ITxObject> Scopes() { var list = new List<ITxObject>(); for (int i = 0; i < _scope.Count; i++) list.Add(_scope.GetObject(i)); return list; }
        private void Preview()
        {
            if (_busy) return; _autoPreview.Stop(); _stale = true;
            if (_target == null || _scope.Count == 0 || (_rule.SelectedIndex == 0 && (_source == null || !_source.Walk().Any(n => n.IsOperation))))
            { _right.Nodes.Clear(); _status.Text = "选择范围和目标；结构模式还需勾选工序来源。输入就绪后自动预览。"; UpdateActions(); return; }
            try
            {
                if (!ReferenceEquals(_document, TxApplication.ActiveDocument)) throw new InvalidOperationException("文档已切换，请重新打开窗口或读取当前文档的来源。");
                var scopes = Scopes(); if (scopes.Count == 0) throw new InvalidOperationException("请添加焊点范围。");
                _busy = true; UpdateActions();
                var plan = SequenceEngine.Plan(_source, scopes, _target, _rule.SelectedIndex == 1, _cross.SelectedIndex == 1, s => { }, _draft);
                if (_rule.SelectedIndex == 0 && _cross.SelectedIndex == 2)
                    foreach (var spot in plan.Pending.Where(p => p.Value.Contains("跨分支")).Select(p => p.Key).ToList())
                    { plan.Pending.Remove(spot); plan.Kept.Add(spot.LocOp.Id); }
                _plan = plan; _stale = false; _finished = false; _scopeStamp = string.Join("|", scopes.Select(StructureEngine.Id)); Render(); Summary();
            }
            catch (Exception ex) { _right.Nodes.Clear(); _issues.Text = "检查未通过，未修改场景：" + ex.Message; }
            finally { _busy = false; UpdateActions(); }
        }
        private static string NodeKey(object item)
        {
            if (ReferenceEquals(item, PendingOperation)) return PendingOrderKey;
            if (item is SequenceEntry) return ((SequenceEntry)item).Key;
            if (item is SpotItem) return "point:" + ((SpotItem)item).LocOp.Id;
            return "object:" + ((ITxObject)item).Id;
        }
        private TreeNode PreviewNode(OperationPreview model)
        {
            var node = new TreeNode(model.Name) { Tag = model.Item };
            var spot = model.Item as SpotItem; string warning;
            if (spot != null && _plan.Mismatches.TryGetValue(spot.LocOp.Id, out warning))
            { node.ForeColor = Color.Firebrick; node.ToolTipText = warning + "；实际绑定：" + string.Join(" + ", spot.PartNames); }
            else if (spot != null) node.ToolTipText = "绑定零件：" + string.Join(" + ", spot.PartNames);
            foreach (var child in model.Children)
                if (!(child.Item is SpotItem) || (!_plan.Pending.ContainsKey((SpotItem)child.Item) && !_plan.Kept.Contains(((SpotItem)child.Item).LocOp.Id)))
                    node.Nodes.Add(PreviewNode(child));
            return node;
        }
        private void Render()
        {
            bool first = _right.Nodes.Count == 0;
            var oldPending = WorkflowUi.Nodes(_right.Nodes).FirstOrDefault(n => ReferenceEquals(n.Tag, PendingOperation));
            bool pendingWasEmpty = oldPending == null || oldPending.Nodes.Count == 0;
            _refreshing = true; _right.PreserveSelection = true;
            try
            {
                WorkflowUi.Rebuild(_right, () =>
                {
                    if (_plan.Preview == null) return;
                    var root = PreviewNode(_plan.Preview); _right.Nodes.Add(root);
                    var pending = new TreeNode("待分配（虚拟，不创建）") { Tag = PendingOperation, ForeColor = SystemColors.GrayText,
                        ToolTipText = "仅用于预览和手工分配；未分配的点位保留在 PS 原操作中，不创建此操作。" };
                    var spots = _plan.Bindings.Keys.Where(p => _plan.Pending.ContainsKey(p) || _plan.Kept.Contains(p.LocOp.Id)).ToList();
                    List<string> order;
                    if (_draft.PointOrders.TryGetValue(PendingOrderKey, out order))
                    {
                        var indices = order.Select((id, i) => new { id, i }).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
                        spots = spots.OrderBy(p => { int i; return indices.TryGetValue(p.LocOp.Id, out i) ? i : int.MaxValue; }).ToList();
                    }
                    foreach (var spot in spots)
                    { string reason; if (!_plan.Pending.TryGetValue(spot, out reason)) reason = "已决定保留原位";
                        pending.Nodes.Add(new TreeNode(spot.LocOp.Name) { Tag = spot, ToolTipText = reason + "；原操作：" + _plan.OriginalPaths[spot.LocOp.Id] }); }
                    root.Nodes.Add(pending);
                    if (pendingWasEmpty && spots.Count > 0) pending.Expand();
                }, NodeKey);
                if (first && _right.Nodes.Count > 0)
                { _right.Nodes[0].Expand(); if (_right.Nodes[0].Nodes.Count > 0) _right.Nodes[0].Nodes[0].Expand(); }
            }
            finally { _right.PreserveSelection = false; _right.RetainSelection(); _refreshing = false; }
            UpdateActions();
        }
        private void SelectInPs()
        {
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument)) return;
            var spots = _right.SelectedSpots.Where(p => p.LocOp.IsValid()).ToList();
            if (spots.Count == 0 && SelectedSpot == null) return;
            _scope.ListenToPick = _sourcePick.ListenToPick = _targetPick.ListenToPick = false;
            var selected = new TxObjectList(); foreach (var spot in spots) selected.Add(spot.LocOp);
            TxApplication.ActiveSelection.Clear(); TxApplication.ActiveSelection.AddItems(selected); TxApplication.RefreshDisplay();
            _issues.Text = "已选 " + spots.Count + " 个焊点；对应不符 " + spots.Count(p => _plan.Mismatches.ContainsKey(p.LocOp.Id)) + " 个。";
            if (spots.Count == 1) { string warning; _issues.Text = _plan.Mismatches.TryGetValue(spots[0].LocOp.Id, out warning) ? warning : "绑定零件：" + string.Join(" + ", spots[0].PartNames); }
        }
        private void Summary()
        {
            int assigned = _plan.Entries.Sum(e => e.Spots.Count);
            int same = _plan.Entries.Sum(e => e.Existing == null ? 0 : e.Spots.Count(s => StructureEngine.Id(StructureEngine.Parent(s.LocOp)) == e.Existing.Id));
            _status.Text = "③ 检查结果：移动 " + (assigned - same) + " / 已在位 " + same + " / 待处理 " + _plan.Pending.Count + " / 决定保留 " + _plan.Kept.Count + " / 对应不符 " + _plan.Mismatches.Count + " / 阻止项 " + _plan.Blockers.Count;
            _issues.Text = string.Join(Environment.NewLine, _plan.Blockers.Concat(_plan.Mismatches.Select(p => p.Key + "：" + p.Value)).Concat(_plan.Pending.Select(p => p.Key.LocOp.Name + "：" + p.Value)));
            if (_issues.Text.Length == 0) _issues.Text = "检查通过。空工序不会创建；移入已有工序的点位追加到末尾。";
            int missing = _draft.Assignments.Keys.Count(id => !_plan.Locations.Contains(id));
            if (missing > 0) _issues.AppendText(Environment.NewLine + missing + " 项人工决定对应的点位不在当前范围，本次不执行。");
            _apply.Text = !_plan.HasChanges ? "无需变更" : assigned == same ? "执行顺序调整" : "执行分序：移动 " + (assigned - same) + " 点"; UpdateActions();
        }
        private void UpdateActions()
        {
            if (_apply == null) return;
            _apply.Enabled = Editable && _plan.HasChanges && _plan.Blockers.Count == 0 && _plan.Entries.Any(e => e.Spots.Count > 0);
        }
        private void SetPointAssignment(SpotItem spot, string key)
        { if (spot != null) MovePoints(new[] { spot }, key, null, false); }
        private void MovePoints(IEnumerable<SpotItem> selection, string key, string anchor, bool after)
        {
            if (!Editable) return;
            var spots = selection.Where(p => _plan.Locations.Contains(p.LocOp.Id)).GroupBy(p => p.LocOp.Id).Select(g => g.First()).ToList();
            var ids = new HashSet<string>(spots.Select(p => p.LocOp.Id)); if (spots.Count == 0 || (anchor != null && ids.Contains(anchor))) return;
            if (key != null && !_plan.Entries.Any(e => e.Key == key)) return;
            string orderKey = key ?? PendingOrderKey;
            List<string> order;
            if (key == null)
            {
                order = _plan.Bindings.Keys.Where(p => _plan.Pending.ContainsKey(p) || _plan.Kept.Contains(p.LocOp.Id)).Select(p => p.LocOp.Id).ToList();
                List<string> previous;
                if (_draft.PointOrders.TryGetValue(orderKey, out previous))
                {
                    var indices = previous.Select((id, i) => new { id, i }).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
                    order = order.OrderBy(id => { int i; return indices.TryGetValue(id, out i) ? i : int.MaxValue; }).ToList();
                }
            }
            else
            {
                var entry = _plan.Entries.Single(e => e.Key == key);
                var node = _plan.Preview.Children.FirstOrDefault(n => n.Item is SequenceEntry && ((SequenceEntry)n.Item).Key == key);
                if (node == null) node = _plan.Preview.Children.FirstOrDefault(n => n.Item is TxWeldOperation && n.Name == entry.Name);
                order = node == null ? new List<string>() : node.Children.Select(SequenceEngine.PreviewId).ToList();
            }
            order.RemoveAll(ids.Contains); int at = anchor == null ? order.Count : order.IndexOf(anchor);
            if (anchor != null && at < 0) return;
            if (anchor != null && after) at++;
            _undo.Push(_draft.CaptureEdit());
            foreach (var previous in _draft.PointOrders.Values) previous.RemoveAll(ids.Contains);
            foreach (var spot in spots) _draft.Assignments[spot.LocOp.Id] = key;
            order.InsertRange(at, spots.Select(p => p.LocOp.Id)); _draft.PointOrders[orderKey] = order;
            Preview();
        }
        private void RestorePoint()
        {
            if (!Editable || _right.SelectedSpots.Count == 0) return;
            _undo.Push(_draft.CaptureEdit());
            foreach (var spot in _right.SelectedSpots) _draft.Assignments.Remove(spot.LocOp.Id); Preview();
        }
        private void UndoAssignment()
        { if (!Editable || _undo.Count == 0) return; _draft.RestoreEdit(_undo.Pop()); Preview(); }
        private void NewEntry()
        {
            var spots = _right.SelectedSpots;
            if (!Editable || spots.Count == 0) return;
            var name = WorkflowUi.AskName(this, "新建平级焊接工序", "合装焊接"); if (name == null) return;
            var entry = new SequenceEntry { Key = "manual:" + Guid.NewGuid().ToString("N"), Name = StructureEngine.Clean(name) };
            foreach (var spot in spots) entry.ExpectedBindings.Add(_plan.Bindings[spot]);
            _draft.Custom.Add(entry);
            // Make the new destination available without a placeholder in the real preview.
            _plan.Entries.Add(entry); MovePoints(spots, entry.Key, null, false);
        }
        private void RenameEntry()
        {
            if (!Editable || SelectedEntry == null) return; var entry = SelectedEntry;
            var name = WorkflowUi.AskName(this, "修改规划名称（可能由复用改为新建）", entry.Name); if (name == null) return;
            _draft.Names[entry.Key] = StructureEngine.Clean(name); Preview();
        }
        private void Apply()
        {
            _autoPreview.Stop();
            if (!Editable || !_apply.Enabled) return; _busy = true; UpdateActions(); var notes = new List<string>();
            try
            {
                var current = new HashSet<string>(); foreach (var scope in Scopes()) foreach (var spot in SpotGrouper.CollectSpots(scope, s => { })) current.Add(spot.LocOp.Id);
                if (!current.SetEquals(_plan.Locations)) throw new InvalidOperationException("焊点范围已变化，请重新检查。");
                _issues.Text = SequenceEngine.Apply(_plan, notes.Add); _status.Text = "④ 执行完成；当前显示本批结果，再次执行前请重新检查。";
            }
            catch (Exception ex)
            {
                _issues.Text = _plan.Execution.Failure(ex) + Environment.NewLine + string.Join(Environment.NewLine, notes); _status.Text = "④ 执行已停止；预览显示当前实际结构。";
                try { _plan.Preview = ActualPreview(_target); } catch { _plan.Preview = null; }
            }
            finally { _busy = false; _finished = true; Render(); UpdateActions(); }
        }
        private static OperationPreview ActualPreview(ITxObject obj)
        {
            var node = new OperationPreview { Name = obj.Name, Item = obj };
            foreach (var child in StructureEngine.Children(obj)) node.Children.Add(ActualPreview(child));
            return node;
        }
    }
}
