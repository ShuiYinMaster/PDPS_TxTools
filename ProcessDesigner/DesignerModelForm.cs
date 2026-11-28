using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.CatiaPartTree.Core;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;
using TxTools.CrossEnvIO;
using TxTools.ResourceImporter;

namespace TxTools.ProcessDesigner
{
    internal sealed partial class DesignerModelForm : TxForm
    {
        private const string ResourceDragFormat = "TxTools.ProcessDesigner.Resources";
        private readonly bool _parts;
        private readonly TreeView _structure = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false,
            LabelEdit = true, ShowNodeToolTips = true };
        private readonly ListView _files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, LabelEdit = true, ShowItemToolTips = true, MultiSelect = true, AllowDrop = true };
        private readonly TreeView _folders = new TreeView { Dock = DockStyle.Fill, AllowDrop = true, HideSelection = false,
            ShowNodeToolTips = true, LabelEdit = true };
        private readonly SplitContainer _storage = new SplitContainer { Dock = DockStyle.Fill, Width = 418, SplitterDistance = 208 };
        private readonly Dictionary<string, TreeNode> _assignedFolders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        private readonly TextBox _name = FormUiKit.MkTextBox("", 120);
        private readonly TextBox _cojtFolder = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox _documentFolder = FormUiKit.MkCheckBox("以来源文档名建子文件夹", true);
        private readonly CheckBox _geometry = FormUiKit.MkCheckBox("导入零件几何", true);
        private readonly CheckBox _keepRoot = FormUiKit.MkCheckBox("保留来源根层", false);
        private readonly Label _status = WorkflowUi.Label("");
        private readonly FlowLayoutPanel _sourceActions = WorkflowUi.Bar(), _options = WorkflowUi.Bar();
        private readonly TableLayoutPanel _location;
        private readonly Button _prepare, _cancel, _clearAssignment;
        private CatiaImportSource _source;
        private CancellationTokenSource _work;

        internal DesignerModelForm(bool parts)
        {
            _parts = parts;
            FormUiKit.InitStandardForm(this, parts ? "零件导入" : "资源导入",
                new Size(456, 560), new Size(440, 480), sizable: true);
            SemiModal = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 7 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < content.RowCount; i++)
                content.RowStyles.Add(i == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            content.Controls.Add(_sourceActions, 0, 0);
            content.Controls.Add(_options, 0, 1);
            _location = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            _location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cojtFolder.Text = parts ? "product" : "TxToolsImports";
            _location.Controls.Add(WorkflowUi.Label("默认 COJT 保存至"), 0, 0);
            _location.Controls.Add(_cojtFolder, 1, 0);
            var browse = WorkflowUi.Button(_location, "浏览…", BrowseCojtFolder);
            _location.SetCellPosition(browse, new TableLayoutPanelCellPosition(2, 0));
            var checks = WorkflowUi.Bar();
            checks.WrapContents = false;
            checks.Visible = parts;
            if (parts) checks.Controls.AddRange(new Control[] { _keepRoot, _geometry, _documentFolder });
            _geometry.CheckedChanged += (sender, args) =>
            {
                _location.Visible = _geometry.Checked;
                _documentFolder.Enabled = _geometry.Checked;
                _storage.Panel2Collapsed = !_geometry.Checked;
                _clearAssignment.Visible = _geometry.Checked;
            };
            content.Controls.Add(checks, 0, 2);
            content.Controls.Add(_location, 0, 3);
            content.Controls.Add(_storage, 0, 4);
            ConfigureStorage(parts ? (Control)_structure : _files);
            var submit = WorkflowUi.Bar();
            submit.WrapContents = false;
            _prepare = WorkflowUi.Button(submit, "准备 eBOP 导入", async () => await Prepare());
            _cancel = WorkflowUi.Button(submit, "取消准备", () => _work?.Cancel());
            _clearAssignment = WorkflowUi.Button(submit, "清除分配", () => AssignFolder(SelectedAssets(), null, clear: true));
            _cancel.Enabled = false;
            content.Controls.Add(submit, 0, 5);
            content.Controls.Add(WorkflowUi.Stack(_status), 0, 6);
            Controls.Add(content);

            _options.Controls.Add(WorkflowUi.Label(parts ? "复合零件名称" : "复合资源名称"));
            _options.Controls.Add(_name);
            if (parts) ConfigureParts();
            else ConfigureResources();
            Shown += (sender, args) =>
            {
                LoadPreviewImages();
                LoadFolders();
            };
            FormClosing += (sender, args) =>
            {
                if (_work == null || args.CloseReason != CloseReason.UserClosing) return;
                args.Cancel = true;
                _work.Cancel();
            };
            UpdateActions();
        }

        protected override void OnLoad(EventArgs args)
        {
            base.OnLoad(args);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
        }

        private void ConfigureParts()
        {
            WorkflowUi.Button(_sourceActions, "读取当前 CATIA 装配", async () =>
                await ReadParts(() => RunSta(CatiaImportSource.ReadActive)));
            var open = WorkflowUi.Button(_sourceActions, "打开装配文件…", () =>
            {
                using (var picker = new OpenFileDialog { Filter = "装配文件|*.CATProduct;*.3dxml;*.step;*.stp", CheckFileExists = true })
                {
                    if (picker.ShowDialog(this) == DialogResult.OK) ReadPartFile(picker.FileNames);
                }
            });
            _structure.AfterLabelEdit += SourceLabelEdited;
            _structure.AfterCheck += (sender, args) => RefreshFolderPreview();
            _structure.KeyDown += RenameOnF2;
            _structure.AllowDrop = true;
            WorkflowUi.EnableDragScrolling(_structure);
            _structure.ItemDrag += (sender, args) =>
            {
                if (args.Item is TreeNode node && args.Button == MouseButtons.Left)
                {
                    _structure.SelectedNode = node;
                    var effects = node.Parent == null ? DragDropEffects.Copy : DragDropEffects.Move | DragDropEffects.Copy;
                    _structure.DoDragDrop(node, effects);
                }
            };
            _structure.DragOver += (sender, args) =>
            {
                args.Effect = TryMoveBranch(args, apply: false) ? DragDropEffects.Move : DragDropEffects.None;
            };
            _structure.DragDrop += (sender, args) => TryMoveBranch(args, apply: true);
            FileDropInput.Wire(() => _work == null, ReadPartFile, message => _status.Text = message, _structure, _sourceActions, open);
            _status.Text = "可将一个 CATProduct、3DXML 或 STEP 拖入左侧来源树。读取后可将零件或分支拖到右侧分配目录。";
        }

        private bool ReadPartFile(string[] paths)
        {
            if (paths.Length == 1 && File.Exists(paths[0]))
            {
                string path = paths[0];
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".catproduct":
                        _ = ReadParts(() => RunSta(() => CatiaImportSource.ReadProductFile(path)));
                        return true;
                    case ".3dxml":
                        _ = ReadParts(() => Task.Run(() => ThreeDXmlSource.Read(path, _work.Token, allowXmlGeometry: true), _work.Token));
                        return true;
                    case ".step":
                    case ".stp":
                        _ = ReadParts(() => Task.Run(() => StepSource.Read(path, _work.Token), _work.Token));
                        return true;
                }
            }
            _status.Text = "请拖入一个 CATProduct、3DXML 或 STEP 文件。";
            return false;
        }

        private bool TryMoveBranch(DragEventArgs args, bool apply)
        {
            if (_work != null || !args.Data.GetDataPresent(typeof(TreeNode))) return false;
            var dragged = args.Data.GetData(typeof(TreeNode)) as TreeNode;
            var point = _structure.PointToClient(new Point(args.X, args.Y));
            var target = _structure.GetNodeAt(point);
            if (dragged?.TreeView != _structure || dragged.Parent == null || target == null) return false;
            for (var ancestor = target; ancestor != null; ancestor = ancestor.Parent)
                if (ancestor == dragged) return false;
            bool insertBefore = dragged.Parent == target.Parent && point.Y < target.Bounds.Top + target.Bounds.Height / 3;
            if (!insertBefore && !((StructureNode)target.Tag).IsContainer) return false;
            if (!apply) return true;

            dragged.Remove();
            if (insertBefore) target.Parent.Nodes.Insert(target.Index, dragged);
            else
            {
                target.Nodes.Add(dragged);
                target.Expand();
            }
            _structure.SelectedNode = dragged;
            RefreshFolderPreview();
            return true;
        }

        private void ConfigureResources()
        {
            _name.Text = "导入资源";
            var add = WorkflowUi.Button(_sourceActions, "添加模型…", () =>
            {
                using (var picker = new OpenFileDialog { Filter = ResourceConversion.FileFilter, CheckFileExists = true, Multiselect = true })
                {
                    if (picker.ShowDialog(this) == DialogResult.OK) AddResourceFiles(picker.FileNames);
                }
            });
            WorkflowUi.Button(_sourceActions, "移除所选模型", () =>
            {
                foreach (var item in _files.SelectedItems.Cast<ListViewItem>().ToArray())
                {
                    _assignedFolders.Remove((string)item.Tag);
                    _resourceTypes.Remove((string)item.Tag);
                    _files.Items.Remove(item);
                }
                RefreshFolderPreview();
                UpdateActions();
            });
            var prototype = WorkflowUi.Button(_sourceActions, "批量设原型", () =>
                ShowPrototypeMenu(_files.SelectedItems.Cast<ListViewItem>().ToArray(), Cursor.Position));
            _files.Columns.Add("资源名称", 105);
            _files.Columns.Add("资源原型", 91);
            _files.AfterLabelEdit += (sender, args) =>
            {
                args.CancelEdit = args.Label != null && string.IsNullOrWhiteSpace(args.Label);
                if (args.Label != null && !args.CancelEdit) BeginInvoke((Action)RefreshFolderPreview);
            };
            _files.MouseClick += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || (ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None) return;
                var hit = _files.HitTest(args.Location);
                if (hit.Item != null && hit.SubItem == hit.Item.SubItems[1])
                    ShowPrototypeMenu(new[] { hit.Item }, _files.PointToScreen(new Point(hit.SubItem.Bounds.Left, hit.SubItem.Bounds.Bottom)));
            };
            _files.SelectedIndexChanged += (sender, args) => prototype.Enabled = _files.SelectedItems.Count != 0;
            prototype.Enabled = false;
            _files.KeyDown += RenameOnF2;
            FileDropInput.Wire(() => _work == null, AddResourceFiles, message => _status.Text = message, _files, _sourceActions, add);
            _files.ItemDrag += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left) return;
                string[] files = SelectedAssets().ToArray();
                if (files.Length != 0)
                    _files.DoDragDrop(new DataObject(ResourceDragFormat, files), DragDropEffects.Copy);
            };
            _status.Text = "可将模型文件拖入左侧列表，再多选模型拖到右侧分配目录。";
        }

        private bool AddResourceFiles(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(_files.Items.Cast<ListViewItem>().Select(item => (string)item.Tag),
                StringComparer.OrdinalIgnoreCase);
            var added = paths.Where(File.Exists).Where(ResourceConversion.Supports).Where(existing.Add).ToArray();
            foreach (string file in added)
                _files.Items.Add(new ListViewItem(new[] { Path.GetFileNameWithoutExtension(file), "" }) { Tag = file });
            UpdateFolderDisplays();
            UpdateActions();
            _status.Text = added.Length == 0 ? "没有可添加的模型文件，请检查格式或列表中是否已有该文件。"
                : "已添加 " + added.Length + " 个模型文件。";
            return added.Length > 0;
        }

        private void ConfigureStorage(Control source)
        {
            _storage.Panel1.Controls.Add(source);
            _storage.Panel2.Controls.Add(_folders);
            _cojtFolder.TextChanged += (sender, args) => UpdateFolderDisplays();
            _folders.BeforeExpand += (sender, args) => CojtFolderTree.Populate(args.Node, message => _status.Text = message);
            _folders.DragEnter += FolderDragOver;
            _folders.DragOver += FolderDragOver;
            _folders.DragDrop += (sender, args) =>
            {
                var target = CojtFolderTree.At(_folders, args);
                if (!(target?.Tag is string)) return;
                if (_parts && args.Data.GetData(typeof(TreeNode)) is TreeNode node && node.TreeView == _structure)
                    AssignFolder(PartAssets(node), target);
                else if (!_parts && args.Data.GetData(ResourceDragFormat) is string[] files)
                    AssignFolder(files, target);
            };
            ConfigureFolderMenu();
            WorkflowUi.EnableDragScrolling(_folders);
        }

        private void LoadFolders()
        {
            try
            {
                string root = SystemRoot();
                _cojtFolder.Text = _parts ? CatiaLibraryDestination.DefaultFolder(root) : Path.Combine(root, "TxToolsImports");
                var node = CojtFolderTree.CreateNode(root, "系统库目录");
                _folders.Nodes.Clear();
                _folders.Nodes.Add(node);
                node.Expand();
                SelectFolder(_cojtFolder.Text);
            }
            catch (Exception error) { _status.Text = error.GetBaseException().Message; }
        }

        private void SelectFolder(string path)
        {
            if (_folders.Nodes.Count == 0) return;
            var selected = CojtFolderTree.Find(_folders.Nodes[0], path, message => _status.Text = message);
            if (selected == null) return;
            _folders.SelectedNode = selected;
            selected.EnsureVisible();
        }

        private void FolderDragOver(object sender, DragEventArgs args)
        {
            var target = CojtFolderTree.At(_folders, args);
            bool source = _parts ? args.Data.GetData(typeof(TreeNode)) is TreeNode node && node.TreeView == _structure
                : args.Data.GetDataPresent(ResourceDragFormat);
            args.Effect = _work == null && source && target?.Tag is string ? DragDropEffects.Copy : DragDropEffects.None;
            if (args.Effect == DragDropEffects.Copy) _folders.SelectedNode = target;
        }

        private IEnumerable<string> SelectedAssets()
        {
            return _parts ? PartAssets(_structure.SelectedNode)
                : _files.SelectedItems.Cast<ListViewItem>().Select(item => (string)item.Tag);
        }

        private IEnumerable<string> PartAssets(TreeNode node)
        {
            if (node == null || _source == null) yield break;
            if (node.Tag is StructureNode source && _source.Parts.TryGetValue(source.Key, out var part)) yield return part.AssetKey;
            foreach (TreeNode child in node.Nodes)
            foreach (string asset in PartAssets(child)) yield return asset;
        }

        private void AssignFolder(IEnumerable<string> source, TreeNode folder, bool clear = false)
        {
            if (_work != null) return;
            try
            {
                var assets = source.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (assets.Length == 0) throw new InvalidOperationException("请先选择要分配的模型。");
                if (!clear)
                {
                    if (!(folder?.Tag is string path)) throw new InvalidOperationException("请在右侧选择系统库目录。");
                    LibraryInsertion.CheckDestination(Path.Combine(path, "model.cojt"), (string)_folders.Nodes[0].Tag, allowRoot: true);
                }
                foreach (string asset in assets)
                {
                    if (clear) _assignedFolders.Remove(asset);
                    else _assignedFolders[asset] = folder;
                }
                UpdateFolderDisplays();
                RefreshFolderPreview();
                if (!clear) folder.Expand();
                _status.Text = assets.Length + (clear ? " 个模型恢复使用默认目录。" : " 个模型已分配到 " + folder.Text + "。");
            }
            catch (Exception error) { _status.Text = error.GetBaseException().Message; }
        }

        private string FolderFor(string key) => _assignedFolders.TryGetValue(key, out var folder) ? (string)folder.Tag : _cojtFolder.Text;

        private void UpdateFolderDisplays()
        {
            foreach (ListViewItem row in _files.Items)
            {
                string file = (string)row.Tag, folder = FolderFor(file);
                var prototype = _resourceTypes.TryGetValue(file, out var chosen) ? chosen : DefaultPrototype;
                row.SubItems[1].Text = prototype.Label + " ▾";
                row.ImageKey = prototype.Type;
                row.ToolTipText = file + Environment.NewLine + "原型 · " + prototype.Label + Environment.NewLine + "COJT · " + folder;
            }
            foreach (TreeNode node in _structure.Nodes) UpdateFolderDisplay(node);
        }

        private void UpdateFolderDisplay(TreeNode node)
        {
            node.ToolTipText = PartTip((StructureNode)node.Tag);
            foreach (TreeNode child in node.Nodes) UpdateFolderDisplay(child);
        }

        private string CojtFolder(string systemRoot)
        {
            return !_parts && string.IsNullOrWhiteSpace(_cojtFolder.Text)
                ? Path.Combine(systemRoot, "TxToolsImports")
                : CatiaLibraryDestination.ResolveFolder(systemRoot, _cojtFolder.Text);
        }

        private void BrowseCojtFolder()
        {
            try
            {
                string root = SystemRoot();
                string folder = CojtFolder(root);
                while (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
                using (var picker = new FolderBrowserDialog
                {
                    Description = "选择共享系统库内的 COJT 保存目录",
                    SelectedPath = string.IsNullOrWhiteSpace(folder) ? root : folder,
                    ShowNewFolderButton = false
                })
                {
                    if (picker.ShowDialog(this) == DialogResult.OK)
                    {
                        LibraryInsertion.CheckDestination(Path.Combine(picker.SelectedPath, "model.cojt"), root, allowRoot: true);
                        _cojtFolder.Text = picker.SelectedPath;
                        SelectFolder(picker.SelectedPath);
                    }
                }
            }
            catch (Exception error) { _status.Text = error.GetBaseException().Message; }
        }

        private static string SystemRoot()
        {
            string root = TxApplication.SystemRootDirectory;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new InvalidOperationException("导入几何需要可访问的共享系统库。请检查 Process Designer 的系统库路径。");
            return Path.GetFullPath(root);
        }

        private void RenameOnF2(object sender, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.F2) return;
            if (_parts) _structure.SelectedNode?.BeginEdit();
            else if (_files.SelectedItems.Count == 1) _files.SelectedItems[0].BeginEdit();
            args.Handled = true;
        }

        private async Task ReadParts(Func<Task<CatiaImportSource>> read)
        {
            await Run(async () =>
            {
                _status.Text = "正在读取来源装配…";
                var source = await read();
                _work.Token.ThrowIfCancellationRequested();
                _source = source;
                _assignedFolders.Clear();
                RefreshFolderPreview();
                _structure.Nodes.Clear();
                _structure.Nodes.Add(Preview(source.Root));
                _structure.Nodes[0].Expand();
                _name.Text = Path.GetFileNameWithoutExtension(source.DocumentPath);
                _status.Text = "已读取 " + source.Parts.Count + " 个零件实例。可勾选、拖动分支或用 F2 改名，然后准备导入。";
            });
        }

        private TreeNode Preview(StructureNode source)
        {
            string icon = source.IsContainer ? "container" : "part";
            var node = new TreeNode(source.Label)
            {
                Tag = source, Checked = true, ToolTipText = PartTip(source), ImageKey = icon, SelectedImageKey = icon
            };
            foreach (var child in source.Children) node.Nodes.Add(Preview(child));
            return node;
        }

        private string PartTip(StructureNode source)
        {
            string issue = string.IsNullOrWhiteSpace(source.PartNumber) ? "" : "零件号 " + source.PartNumber;
            if (_source.Parts.TryGetValue(source.Key, out var part))
            {
                if (part.ConversionIssue != null) issue = part.ConversionIssue;
                else if (_source.GeometryIssues.TryGetValue(part.File, out var error)) issue = error;
                issue += (issue.Length == 0 ? "" : Environment.NewLine) + "COJT · " + FolderFor(part.AssetKey);
            }
            return issue;
        }

        private static StructureNode Draft(TreeNode visual)
        {
            var source = (StructureNode)visual.Tag;
            var node = new StructureNode { Key = source.Key, SourceName = source.SourceName, PartNumber = source.PartNumber,
                TargetName = visual.Text, IsContainer = source.IsContainer, Include = visual.Checked };
            foreach (TreeNode child in visual.Nodes) node.Add(Draft(child));
            return node;
        }

        private async Task Prepare()
        {
            await Run(async () =>
            {
                string name = StructureEngine.Clean(_name.Text);
                bool geometry = !_parts || _geometry.Checked;
                string systemRoot = geometry ? SystemRoot() : null;
                string outputFolder = geometry ? CojtFolder(systemRoot) : null;
                var folders = _assignedFolders.ToDictionary(pair => pair.Key, pair => (string)pair.Value.Tag, StringComparer.OrdinalIgnoreCase);
                var progress = new Progress<string>(message => { if (_work != null) _status.Text = message; });
                var token = _work.Token;
                DesignerImportPackage package;
                _status.Text = "正在准备 eBOP 数据…";
                if (_parts)
                {
                    var source = _source ?? throw new InvalidOperationException("请先读取装配。");
                    var draft = Draft(_structure.Nodes[0]);
                    draft.SkipLevel = !_keepRoot.Checked;
                    if (outputFolder != null && _documentFolder.Checked)
                        outputFolder = CatiaLibraryDestination.DocumentFolder(outputFolder, source.DocumentPath);
                    package = await DesignerCadPackage.BuildParts(source, draft, name, systemRoot, outputFolder, token, progress, folders);
                }
                else
                {
                    var files = _files.Items.Cast<ListViewItem>().Select(item => (File: (string)item.Tag, Name: item.Text)).ToArray();
                    var types = files.ToDictionary(item => item.File,
                        item => _resourceTypes.TryGetValue(item.File, out var type) ? type : DefaultPrototype, StringComparer.OrdinalIgnoreCase);
                    package = await Task.Run(() => DesignerCadPackage.BuildResources(files, name, systemRoot, outputFolder, token, progress, folders, types), token);
                }
                _folderEdits.RefreshMaterialized();
                var dialog = new DesignerImportDialog(package, systemRoot);
                dialog.Show(this);
                _status.Text = "已准备 “" + package.Name + "”。请在导入窗口执行导入。";
                if (outputFolder != null) _status.Text += " 默认 COJT 目录为 " + outputFolder + "。";
            });
        }

        private async Task Run(Func<Task> operation)
        {
            if (_work != null) return;
            using (var work = new CancellationTokenSource())
            {
                _work = work;
                UpdateActions();
                try { await operation(); }
                catch (OperationCanceledException) { _status.Text = "已取消。"; }
                catch (Exception error) { _status.Text = error.GetBaseException().Message; }
                finally
                {
                    _work = null;
                    UpdateActions();
                }
            }
        }

        private void UpdateActions()
        {
            bool idle = _work == null;
            _sourceActions.Enabled = _options.Enabled = _location.Enabled = _storage.Enabled = _keepRoot.Enabled = _geometry.Enabled = idle;
            _documentFolder.Enabled = idle && _geometry.Checked;
            _clearAssignment.Enabled = idle;
            _prepare.Enabled = idle && (_parts ? _source != null : _files.Items.Count > 0);
            _cancel.Enabled = !idle;
            UseWaitCursor = !idle;
        }

        private static Task<CatiaImportSource> RunSta(Func<CatiaImportSource> read)
        {
            var completion = new TaskCompletionSource<CatiaImportSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { completion.SetResult(read()); }
                catch (Exception error) { completion.SetException(error); }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return completion.Task;
        }
    }
}
