using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;

namespace TxTools.Common.TreeWorkflow
{
    internal static class WorkflowUi
    {
        // SDK rows are 21 logical pixels: header + five objects + pick row.
        public static Control Picker(string title, TxObjGridCtrl grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.ListenToPick = true;
            grid.EnableRecurringObjects = false;
            FormUiKit.GridPickFocus.Wire(grid);
            var host = new GroupBox { Text = title, Height = 172, Dock = DockStyle.Fill,
                Padding = new Padding(6, 4, 6, 6), Margin = new Padding(4) };
            // Reserve a separate cell so the native control cannot cover the title.
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Margin = Padding.Empty;
            body.Controls.Add(grid); host.Controls.Add(body);
            return host;
        }
        public static Control PickerRow(params Control[] pickers)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 180, ColumnCount = pickers.Length, RowCount = 1, Margin = Padding.Empty };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < pickers.Length; i++) { row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / pickers.Length)); row.Controls.Add(pickers[i], i, 0); }
            return row;
        }
        public static void LimitWidth(Form form)
        {
            float scale;
            using (var graphics = form.CreateGraphics()) scale = graphics.DpiX / 96f;
            int width = (int)Math.Round(650 * scale);
            int height = form.Height, minimumHeight = form.MinimumSize.Height;
            form.MinimumSize = Size.Empty;
            form.MaximumSize = new Size(width, int.MaxValue);
            form.MinimumSize = new Size(width, minimumHeight);
            form.Size = new Size(width, Math.Max(height, minimumHeight));
            form.MaximizeBox = false;
        }
        public static void EqualTrees(SplitContainer split)
        {
            split.IsSplitterFixed = true;
            split.SizeChanged += (s, e) => { if (split.Width > 200) split.SplitterDistance = (split.ClientSize.Width - split.SplitterWidth) / 2; };
        }
        public static void EnableDragScrolling(TreeView tree)
        {
            // DragOver is not raised repeatedly while the mouse stays still.
            // Keep scrolling on a timer until drop, leave or cancellation.
            var timer = new Timer { Interval = 80 };
            Point position = Point.Empty;
            TreeNode hovered = null;
            DateTime hoverSince = DateTime.UtcNow;
            DragEventHandler track = (s, e) =>
            {
                position = tree.PointToClient(new Point(e.X, e.Y));
                var node = tree.GetNodeAt(position);
                if (node != hovered) { hovered = node; hoverSince = DateTime.UtcNow; }
                timer.Start();
            };
            tree.DragEnter += track; tree.DragOver += track;
            timer.Tick += (s, e) =>
            {
                if (tree.IsDisposed || !tree.Visible) { timer.Stop(); return; }
                int edge = Math.Max(20, tree.ItemHeight + 4);
                var pointed = tree.GetNodeAt(position);
                if (pointed != hovered) { hovered = pointed; hoverSince = DateTime.UtcNow; }
                var top = tree.TopNode;
                if (top == null) return;
                if (position.Y < edge && top.PrevVisibleNode != null) tree.TopNode = top.PrevVisibleNode;
                else if (position.Y >= tree.ClientSize.Height - edge && top.NextVisibleNode != null) tree.TopNode = top.NextVisibleNode;
                else if (hovered != null && !hovered.IsExpanded && hovered.Nodes.Count > 0 && (DateTime.UtcNow - hoverSince).TotalMilliseconds >= 650)
                    hovered.Expand();
            };
            tree.DragLeave += (s, e) => { timer.Stop(); hovered = null; };
            tree.DragDrop += (s, e) => { timer.Stop(); hovered = null; };
            tree.QueryContinueDrag += (s, e) => { if (e.Action != DragAction.Continue || e.EscapePressed) timer.Stop(); };
            tree.Disposed += (s, e) => timer.Dispose();
        }
        public static FlowLayoutPanel Bar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4) };
        }
        public static Button Button(Control host, string text, Action action)
        {
            var button = FormUiKit.MkFuncButton(text, FormUiKit.Theme.BtnSecondary);
            button.Click += (s, e) => action(); host.Controls.Add(button); return button;
        }
        public static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(3, 7, 3, 3) };
        }
        public static TableLayoutPanel Stack(params Control[] controls)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var control in controls) { panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.Controls.Add(control); }
            panel.SizeChanged += (s, e) =>
            {
                foreach (var label in controls.OfType<Label>())
                    label.MaximumSize = new Size(Math.Max(1, panel.ClientSize.Width - label.Margin.Horizontal), 0);
            };
            return panel;
        }
        public static IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            { yield return node; foreach (var child in Nodes(node.Nodes)) yield return child; }
        }
        public static void Rebuild(TreeView tree, Action fill, Func<object, string> key)
        {
            var expanded = new HashSet<string>(Nodes(tree.Nodes).Where(n => n.IsExpanded).Select(n => key(n.Tag)));
            var selected = tree.SelectedNode == null ? null : key(tree.SelectedNode.Tag);
            var top = tree.TopNode == null ? null : key(tree.TopNode.Tag);
            bool first = tree.Nodes.Count == 0;
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear(); fill();
                foreach (var node in Nodes(tree.Nodes))
                {
                    var id = key(node.Tag);
                    if (expanded.Contains(id)) node.Expand();
                    if (id == selected) tree.SelectedNode = node;
                }
                if (first && tree.Nodes.Count > 0) tree.Nodes[0].Expand();
                var topNode = Nodes(tree.Nodes).FirstOrDefault(n => key(n.Tag) == top);
                if (topNode != null) tree.TopNode = topNode;
            }
            finally { tree.EndUpdate(); }
        }
        public static string AskName(IWin32Window owner, string title, string initial)
        {
            using (var dialog = new Form { Text = title, Width = 460, Height = 180, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false })
            {
                var text = new TextBox { Text = initial, Dock = DockStyle.Top, Margin = new Padding(8) };
                var bar = Bar(); bar.Dock = DockStyle.Bottom;
                var ok = Button(bar, "确定", () => { if (!string.IsNullOrWhiteSpace(text.Text)) dialog.DialogResult = DialogResult.OK; });
                var cancel = Button(bar, "取消", () => dialog.DialogResult = DialogResult.Cancel);
                dialog.Controls.Add(Stack(Label("目标名称（不修改来源名称或已有 PS 对象名称）"), text));
                dialog.Controls.Add(bar); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                return dialog.ShowDialog(owner) == DialogResult.OK ? text.Text : null;
            }
        }
    }
}
