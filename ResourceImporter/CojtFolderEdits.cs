using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;
using TxTools.CrossEnvIO;

namespace TxTools.ResourceImporter
{
    internal sealed class CojtFolderEdits
    {
        private readonly TreeView _tree;
        private readonly Func<bool> _canEdit;
        private readonly Action<string> _report;
        private readonly Action _renamed;
        private readonly HashSet<TreeNode> _drafts = new HashSet<TreeNode>();

        internal CojtFolderEdits(TreeView tree, Func<bool> canEdit, Action<string> report, Action renamed = null)
        {
            _tree = tree;
            _canEdit = canEdit;
            _report = report;
            _renamed = renamed;
            _tree.LabelEdit = true;
            var menu = new ContextMenuStrip { Font = FormUiKit.BaseFont };
            var create = menu.Items.Add("新建文件夹");
            var rename = menu.Items.Add("重命名");
            create.Click += (sender, args) => Create(_tree.SelectedNode);
            rename.Click += (sender, args) => _tree.SelectedNode?.BeginEdit();
            menu.Opening += (sender, args) =>
            {
                var node = _tree.SelectedNode;
                args.Cancel = !_canEdit() || !(node?.Tag is string);
                rename.Enabled = IsDraft(node);
            };
            _tree.ContextMenuStrip = menu;
            _tree.Disposed += (sender, args) => menu.Dispose();
            _tree.MouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                var node = _tree.GetNodeAt(args.Location);
                _tree.SelectedNode = node?.Tag is string ? node : node?.Parent;
            };
            _tree.BeforeLabelEdit += (sender, args) => args.CancelEdit = !_canEdit() || !IsDraft(args.Node);
            _tree.AfterLabelEdit += (sender, args) =>
            {
                if (args.Label == null) return;
                args.CancelEdit = true;
                if (!_canEdit() || !IsDraft(args.Node)) return;
                try
                {
                    string name = args.Label.Trim();
                    string path = Destination(args.Node.Parent, name, args.Node);
                    args.Node.Text = name;
                    UpdatePath(args.Node, path);
                    _renamed?.Invoke();
                }
                catch (Exception error) { _report(error.GetBaseException().Message); }
            };
        }

        internal bool IsDraft(TreeNode node) => node != null && _drafts.Contains(node) && !Directory.Exists((string)node.Tag);

        internal void Create(TreeNode parent)
        {
            if (!_canEdit() || !(parent?.Tag is string path)) return;
            try
            {
                CojtFolderTree.Populate(parent, _report);
                string name = "新建文件夹";
                for (int version = 2; NameInUse(parent, Path.Combine(path, name), null); version++)
                    name = "新建文件夹 (" + version + ")";
                var node = AddDraft(parent, name);
                parent.Expand();
                _tree.SelectedNode = node;
                node.BeginEdit();
            }
            catch (Exception error) { _report(error.GetBaseException().Message); }
        }

        internal void RefreshMaterialized()
        {
            foreach (var folder in _drafts.Where(node => Directory.Exists((string)node.Tag)).ToArray())
            {
                folder.ToolTipText = (string)folder.Tag;
                _drafts.Remove(folder);
            }
        }

        private TreeNode AddDraft(TreeNode parent, string name)
        {
            string path = Destination(parent, name, null);
            var node = new TreeNode(name) { ImageKey = CojtFolderTree.FolderImage, SelectedImageKey = CojtFolderTree.FolderImage };
            _drafts.Add(node);
            UpdatePath(node, path);
            parent.Nodes.Add(node);
            return node;
        }

        private string Destination(TreeNode parent, string name, TreeNode except)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase))
                throw new IOException("请输入有效的文件夹名称。");
            string path = Path.Combine((string)parent.Tag, name);
            LibraryInsertion.CheckDestination(Path.Combine(path, "model.cojt"), (string)_tree.Nodes[0].Tag, allowRoot: true);
            bool same = except != null && string.Equals((string)except.Tag, path, StringComparison.OrdinalIgnoreCase);
            if (!same && NameInUse(parent, path, except)) throw new IOException("此位置已有同名文件或文件夹。");
            return path;
        }

        private static bool NameInUse(TreeNode parent, string path, TreeNode except) => Directory.Exists(path) ||
            File.Exists(path) || parent.Nodes.Cast<TreeNode>().Any(node => node != except && node.Tag is string candidate &&
                string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));

        private void UpdatePath(TreeNode folder, string path)
        {
            folder.Tag = path;
            folder.ToolTipText = path + Environment.NewLine + "待建文件夹";
            foreach (TreeNode child in folder.Nodes)
                if (_drafts.Contains(child)) UpdatePath(child, Path.Combine(path, child.Text));
        }

        private TreeNode Reveal(TreeNode root, string path)
        {
            var folder = CojtFolderTree.Find(root, path, _report);
            if (folder != null) return folder;
            string relative = CojtTransfer.RelativeToRoot(path, (string)root.Tag);
            if (relative == null) return root;
            folder = root;
            foreach (var name in relative.Split(Path.DirectorySeparatorChar))
            {
                CojtFolderTree.Populate(folder, _report);
                var child = folder.Nodes.Cast<TreeNode>().FirstOrDefault(node => node.Tag is string &&
                    string.Equals(node.Text, name, StringComparison.OrdinalIgnoreCase));
                if (child == null && Directory.Exists(Path.Combine((string)folder.Tag, name))) return folder;
                folder = child ?? AddDraft(folder, name);
            }
            return folder;
        }

        internal static string Pick(IWin32Window owner, string library, string selectedPath)
        {
            using var dialog = new TxForm();
            FormUiKit.InitStandardForm(dialog, "选择 COJT 保存目录", new Size(430, 440), new Size(350, 320));
            dialog.SemiModal = false;
            using var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            CojtFolderTree.AddFolderImage(images);
            var tree = new TreeView { Dock = DockStyle.Fill, ShowNodeToolTips = true, HideSelection = false, ImageList = images };
            var path = WorkflowUi.Label("");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(tree, 0, 0);
            layout.Controls.Add(WorkflowUi.Stack(path), 0, 1);
            var actions = WorkflowUi.Bar();
            layout.Controls.Add(actions, 0, 2);
            dialog.Controls.Add(layout);

            var edits = new CojtFolderEdits(tree, () => true, message => path.Text = message,
                () => path.Text = tree.SelectedNode?.Tag as string ?? "");
            tree.BeforeExpand += (sender, args) => CojtFolderTree.Populate(args.Node, message => path.Text = message);
            tree.AfterSelect += (sender, args) => path.Text = args.Node.Tag as string ?? "";
            WorkflowUi.Button(actions, "新建文件夹", () => edits.Create(tree.SelectedNode));
            var confirm = WorkflowUi.Button(actions, "选择", () =>
            {
                if (tree.SelectedNode?.Tag is string) dialog.DialogResult = DialogResult.OK;
            });
            var cancel = WorkflowUi.Button(actions, "取消", () => dialog.DialogResult = DialogResult.Cancel);
            dialog.AcceptButton = confirm;
            dialog.CancelButton = cancel;

            var root = CojtFolderTree.CreateNode(library, "系统库目录");
            tree.Nodes.Add(root);
            root.Expand();
            try { tree.SelectedNode = edits.Reveal(root, selectedPath); }
            catch (Exception error) { tree.SelectedNode = root; path.Text = error.GetBaseException().Message; }
            tree.SelectedNode?.EnsureVisible();
            return dialog.ShowDialog(owner) == DialogResult.OK ? tree.SelectedNode?.Tag as string : null;
        }
    }
}
