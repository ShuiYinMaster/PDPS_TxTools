using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.CrossEnvIO;

namespace TxTools.ResourceImporter
{
    internal sealed class ResourceImporterForm : TxForm
    {
        private static readonly Size Design = new Size(760, 610);
        private const string QueueDragFormat = "TxTools.ResourceImporter.Queue";
        private static readonly Color[] PrototypeColors =
            { Color.SteelBlue, Color.SeaGreen, Color.Firebrick, Color.SlateBlue, Color.DarkCyan };
        private readonly ResourceImportSession _session;
        private readonly ListView _files = new ListView
        {
            View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true,
            MultiSelect = true, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            ShowItemToolTips = true, AllowDrop = true, OwnerDraw = true
        };
        private readonly TreeView _folders = new TreeView
        {
            Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, AllowDrop = true
        };
        private readonly Button _add, _prototype, _remove, _assign, _import, _cancel;
        private readonly Label _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly ProgressBar _progressBar = new ProgressBar
        {
            AccessibleName = "资源导入进度", Dock = DockStyle.Fill, Height = 10, Visible = false
        };
        private readonly Dictionary<ResourceImportItem, TreeNode> _placements = new Dictionary<ResourceImportItem, TreeNode>();
        private readonly ContextMenuStrip _prototypeMenu = new ContextMenuStrip();
        private readonly ImageList _images = new ImageList();
        private ListViewItem[] _prototypeRows = Array.Empty<ListViewItem>();
        private CancellationTokenSource _active;
        private ResourceImportItem[] _dragItems;
        private bool _scaled;

        internal ResourceImporterForm(ResourceImportSession session)
        {
            _session = session;
            FormUiKit.InitStandardForm(this, "资源导入", Design, new Size(700, 460));
            _add = FormUiKit.MkFuncButton("添加文件…", FormUiKit.Theme.BtnSecondary);
            _prototype = FormUiKit.MkFuncButton("批量设原型 ▾", FormUiKit.Theme.BtnSecondary);
            _remove = FormUiKit.MkFuncButton("移除", FormUiKit.Theme.BtnMuted);
            _assign = FormUiKit.MkFuncButton("分配到所选目录", FormUiKit.Theme.BtnSecondary);
            _import = FormUiKit.MkFuncButton("导入根树", FormUiKit.Theme.BtnPrimary);
            _cancel = FormUiKit.MkFuncButton("取消导入", FormUiKit.Theme.BtnMuted);
            _files.Columns.Add("文件名称");
            _files.Columns.Add("设备原型");
            _files.SmallImageList = _images;
            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.Controls.Add(Pane("待导入文件", "添加或拖入文件，从列表空白处拖动可框选。", _files,
                _add, _prototype, _remove), 0, 0);
            frame.Controls.Add(Pane("系统库目录", "拖入左侧所选文件，指定 COJT 目录。", _folders,
                _assign), 1, 0);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(ButtonRow(_import, _cancel), 1, 0);
            frame.Controls.Add(_progressBar, 0, 1);
            frame.SetColumnSpan(_progressBar, 2);
            frame.Controls.Add(footer, 0, 2);
            frame.SetColumnSpan(footer, 2);
            Controls.Add(frame);
            WireEvents();
            SetStatus();
        }

        private static Control Pane(string title, string hint, Control content, params Button[] buttons)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = hint, AutoSize = true, Margin = new Padding(3, 4, 3, 3) }, 0, 0);
            layout.Controls.Add(ButtonRow(buttons), 0, 1);
            layout.Controls.Add(content, 0, 2);
            box.Controls.Add(layout);
            return box;
        }

        private static FlowLayoutPanel ButtonRow(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            row.Controls.AddRange(buttons);
            return row;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, Design);
            _status.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 2 * _import.Width - 64), 0);
            LoadPrototypeImages();
            var root = MakeFolderNode(_session.Library, "系统库 · " + new DirectoryInfo(_session.Library).Name);
            _folders.Nodes.Add(root);
            root.Expand();
            SizeColumns();
        }

        private void LoadPrototypeImages()
        {
            int size = Math.Max(16, (int)Math.Round(16 * DeviceDpi / 96.0));
            _images.ImageSize = new Size(size, size);
            _images.ColorDepth = ColorDepth.Depth32Bit;
            _ = _images.Handle;
            _prototypeMenu.Font = FormUiKit.BaseFont;
            _prototypeMenu.ImageScalingSize = _images.ImageSize;
            foreach (var type in _session.Prototypes)
            {
                Image image = null;
                bool hasPrefix = type.Type.Length > 2 && type.Type.StartsWith("Pm", StringComparison.Ordinal);
                string alias = hasPrefix ? type.Type.Substring(2) : "Pm" + type.Type;
                foreach (string key in new[] { type.Type, alias })
                {
                    try { image = TxImageProvider.GetImageByPlanningType(key); }
                    catch (Exception) { /* The standard fallback covers missing SDK icons. */ }
                    if (image != null) break;
                }
                if (image != null) _images.Images.Add(type.Type, image);
                else
                {
                    int index = (StringComparer.OrdinalIgnoreCase.GetHashCode(type.Type) & int.MaxValue) % PrototypeColors.Length;
                    string name = hasPrefix ? alias : type.Type;
                    string initials = name.Substring(0, Math.Min(2, name.Length)).ToUpperInvariant();
                    using (var fallback = new Bitmap(size, size))
                    using (var graphics = Graphics.FromImage(fallback))
                    using (var brush = new SolidBrush(PrototypeColors[index]))
                    using (var font = new Font(Font.FontFamily, size * .53f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        graphics.FillEllipse(brush, 1, 1, size - 2, size - 2);
                        graphics.DrawString(initials, font, Brushes.White,
                            new RectangleF(0, 0, size, size), centered);
                        _images.Images.Add(type.Type, fallback);
                    }
                }
                var option = new ToolStripMenuItem(type.Label == type.Type ? type.Type : type.Label + "  (" + type.Type + ")",
                    _images.Images[type.Type]);
                option.Click += (s, e) => ApplyPrototype(type);
                _prototypeMenu.Items.Add(option);
            }
        }

        private void WireEvents()
        {
            _add.Click += (s, e) => Browse();
            _prototype.Click += (s, e) => ShowPrototypeMenu(_prototype, new Point(0, _prototype.Height),
                _files.SelectedItems.Cast<ListViewItem>().ToArray());
            _prototypeMenu.Closed += (s, e) =>
            {
                if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked)
                    _prototypeRows = Array.Empty<ListViewItem>();
            };
            _remove.Click += (s, e) => RemoveSelected();
            _assign.Click += (s, e) => Assign(SelectedItems(), _folders.SelectedNode);
            _import.Click += ImportClick;
            _cancel.Click += (s, e) =>
            {
                _cancel.Enabled = false;
                _status.Text = "正在取消导入…";
                _active?.Cancel();
            };
            _files.Resize += (s, e) => SizeColumns();
            _files.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _files.DrawSubItem += DrawFileSubItem;
            _files.SelectedIndexChanged += (s, e) => UpdateControls();
            _folders.AfterSelect += (s, e) => UpdateControls();
            _files.KeyDown += (s, e) =>
            {
                if (_active != null) return;
                if (e.KeyCode == Keys.Delete) RemoveSelected();
                else if (e.Control && e.KeyCode == Keys.A)
                    foreach (ListViewItem row in _files.Items) row.Selected = true;
                else if ((e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) && _files.FocusedItem != null)
                    OpenRowPrototype(_files.FocusedItem);
                else return;
                e.Handled = e.SuppressKeyPress = true;
            };
            _files.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || (ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None) return;
                var row = _files.GetItemAt(e.X, e.Y);
                if (row != null && PrototypeBounds(row.SubItems[1].Bounds).Contains(e.Location)) OpenRowPrototype(row);
            };
            _files.DragEnter += FileDragOver;
            _files.DragOver += FileDragOver;
            _files.DragDrop += (s, e) =>
            {
                if (_active == null && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
            };
            _files.ItemDrag += (s, e) =>
            {
                var items = SelectedItems();
                if (_active == null && e.Button == MouseButtons.Left && items.Length > 0)
                {
                    _dragItems = items;
                    try { _files.DoDragDrop(new DataObject(QueueDragFormat, QueueDragFormat), DragDropEffects.Copy); }
                    finally { _dragItems = null; }
                }
            };
            _folders.BeforeExpand += (s, e) => Populate(e.Node);
            _folders.DragEnter += FolderDragOver;
            _folders.DragOver += FolderDragOver;
            _folders.DragDrop += (s, e) =>
            {
                if (_active != null) return;
                if (_dragItems != null && e.Data.GetDataPresent(QueueDragFormat))
                    Assign(_dragItems, FolderAt(e));
                else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 && Directory.Exists(paths[0]))
                {
                    try
                    {
                        string folder = Path.GetFullPath(paths[0]);
                        _session.CheckFolder(folder);
                        var node = FindFolder(folder);
                        if (node == null) throw new IOException("目录未在系统库树中显示：" + folder);
                        Assign(SelectedItems(), node);
                    }
                    catch (Exception error) { SetStatus(error.Message); }
                }
            };
        }

        private Rectangle PrototypeBounds(Rectangle cell)
        {
            int padding = Math.Max(2, 2 * DeviceDpi / 96);
            return Rectangle.Inflate(cell, -padding, -padding);
        }

        private void DrawFileSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 1 || ((ResourceImportItem)e.Item.Tag).SceneWriteAttempted)
            {
                e.DrawDefault = true;
                return;
            }
            var box = PrototypeBounds(e.Bounds);
            if (box.Width < 16 || box.Height < 8) return;
            using (var row = new SolidBrush(e.Item.Selected ? SystemColors.Highlight : _files.BackColor))
                e.Graphics.FillRectangle(row, e.Bounds);

            Color fill = FormUiKit.Theme.BtnSecondary;
            Color text = fill.GetBrightness() > 0.62f ? FormUiKit.Theme.BtnFore : Color.White;
            using (var background = new SolidBrush(fill)) e.Graphics.FillRectangle(background, box);
            Color border = e.Item.Selected && _files.Focused ? FormUiKit.Theme.BtnHover : FormUiKit.Theme.BtnBorder;
            ControlPaint.DrawBorder(e.Graphics, box, border, ButtonBorderStyle.Solid);

            int arrowWidth = Math.Min(box.Width / 2, Math.Max(16, 20 * DeviceDpi / 96));
            int divider = box.Right - arrowWidth;
            using (var line = new Pen(border)) e.Graphics.DrawLine(line, divider, box.Top + 1, divider, box.Bottom - 2);
            int x = divider + arrowWidth / 2, y = box.Top + box.Height / 2;
            int half = Math.Max(3, 3 * DeviceDpi / 96);
            using (var arrow = new SolidBrush(text))
                e.Graphics.FillPolygon(arrow, new[] { new Point(x - half, y - 1), new Point(x + half, y - 1), new Point(x, y + half) });
            var textBounds = new Rectangle(box.Left + 4, box.Top, Math.Max(0, divider - box.Left - 8), box.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _files.Font, textBounds, text,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void OpenRowPrototype(ListViewItem row)
        {
            if (_active != null || ((ResourceImportItem)row.Tag).SceneWriteAttempted) return;
            row.EnsureVisible();
            var box = PrototypeBounds(row.SubItems[1].Bounds);
            ShowPrototypeMenu(_files, new Point(box.Left, box.Bottom), new[] { row });
        }

        private void ShowPrototypeMenu(Control anchor, Point location, ListViewItem[] rows)
        {
            if (_active != null) return;
            _prototypeRows = rows.Where(row => !((ResourceImportItem)row.Tag).SceneWriteAttempted).ToArray();
            if (_prototypeRows.Length != 0) _prototypeMenu.Show(anchor, location);
        }

        private void SizeColumns()
        {
            int gutter = Math.Max(20, 24 * DeviceDpi / 96);
            int width = Math.Max(100, _files.ClientSize.Width - gutter);
            _files.Columns[0].Width = width * 55 / 100;
            _files.Columns[1].Width = width - _files.Columns[0].Width;
        }

        private ResourceImportItem[] SelectedItems() => _files.SelectedItems.Cast<ListViewItem>()
            .Select(row => (ResourceImportItem)row.Tag).Where(item => !item.SceneWriteAttempted).ToArray();

        private ListViewItem[] Rows() => _files.Items.Cast<ListViewItem>().ToArray();

        private void Browse()
        {
            using (var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Filter = ResourceConversion.FileFilter })
                if (picker.ShowDialog(this) == DialogResult.OK) AddFiles(picker.FileNames);
        }

        private void FileDragOver(object sender, DragEventArgs e)
        {
            e.Effect = _active == null && e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Any(file => File.Exists(file) && ResourceConversion.Supports(file))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(Rows().Select(row => ((ResourceImportItem)row.Tag).File), StringComparer.OrdinalIgnoreCase);
            int added = 0, ignored = 0;
            _files.BeginUpdate();
            try
            {
                foreach (string file in paths)
                {
                    string path = Path.GetFullPath(file);
                    if (!File.Exists(path) || !ResourceConversion.Supports(path) || !existing.Add(path))
                    {
                        ignored++;
                        continue;
                    }
                    var item = new ResourceImportItem(path);
                    var row = new ListViewItem(new[] { item.Name, "" }) { Tag = item };
                    _files.Items.Add(row);
                    RefreshRow(row);
                    added++;
                }
            }
            finally { _files.EndUpdate(); }
            SetStatus("已添加 " + added + " 个文件" + (ignored == 0 ? "" : "，跳过 " + ignored + " 个重复或不支持的项目"));
        }

        private void ApplyPrototype(ResourcePrototype prototype)
        {
            var rows = _prototypeRows;
            _prototypeRows = Array.Empty<ListViewItem>();
            foreach (var row in rows)
            {
                var item = (ResourceImportItem)row.Tag;
                if (item.SceneWriteAttempted) continue;
                item.Prototype = prototype;
                item.Error = null;
                RefreshRow(row);
            }
            SetStatus();
        }

        private void RemoveSelected()
        {
            foreach (var row in _files.SelectedItems.Cast<ListViewItem>().ToArray())
            {
                var item = (ResourceImportItem)row.Tag;
                if (item.SceneWriteAttempted) continue;
                RemovePlacement(item);
                row.Remove();
            }
            SetStatus();
        }

        private void RefreshRow(ListViewItem row)
        {
            var item = (ResourceImportItem)row.Tag;
            row.ImageKey = item.Prototype?.Type ?? "";
            string result = item.Imported ? (item.Warnings.Length == 0 ? " · 已导入" : " · 已导入（有转换提示）") :
                item.SceneWriteAttempted ? " · 核对场景" :
                item.Error == null ? "" : " · 失败";
            row.SubItems[1].Text = (item.Prototype?.Label ?? "请选择原型") + result;
            row.ToolTipText = item.File + "\nCOJT：" + (item.Package ?? item.Folder ?? "待指定") +
                (item.Error == null ? "" : "\n" + item.Error) +
                (item.Warnings.Length == 0 ? "" : "\n" + string.Join("\n", item.Warnings));
            if (_placements.TryGetValue(item, out var placement))
            {
                placement.Text = (item.Package == null ? item.Name : Path.GetFileName(item.Package)) + result;
                placement.ToolTipText = row.ToolTipText;
            }
        }

        private static TreeNode MakeFolderNode(string path, string title)
        {
            var node = new TreeNode(title) { Tag = path, ToolTipText = path };
            node.Nodes.Add(new TreeNode());
            return node;
        }

        private static bool IsPlaceholder(TreeNode node) => node.Tag == null;

        private void Populate(TreeNode node)
        {
            if (!(node.Tag is string path) || node.Nodes.Count == 0 || !IsPlaceholder(node.Nodes[0])) return;
            try
            {
                var dirs = new DirectoryInfo(path).GetDirectories()
                    .Where(dir => (dir.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        !dir.Name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase) &&
                        !dir.Name.StartsWith(ResourceConversion.LibraryStagePrefix, StringComparison.OrdinalIgnoreCase) &&
                        !dir.Name.StartsWith(".tx-resource-import-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(dir => dir.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                node.Nodes[0].Remove();
                foreach (var dir in dirs) node.Nodes.Add(MakeFolderNode(dir.FullName, dir.Name));
            }
            catch (Exception error) { SetStatus("无法读取目录：" + error.Message); }
        }

        private TreeNode FindFolder(string path)
        {
            var node = _folders.Nodes[0];
            if (ResourceImportSession.SamePath(path, _session.Library)) return node;
            foreach (string segment in CojtTransfer.RelativeToRoot(path, _session.Library).Split(Path.DirectorySeparatorChar))
            {
                Populate(node);
                node = node.Nodes.Cast<TreeNode>().FirstOrDefault(child => child.Tag is string &&
                    string.Equals(child.Text, segment, StringComparison.OrdinalIgnoreCase));
                if (node == null) return null;
            }
            return node;
        }

        private TreeNode FolderAt(DragEventArgs e)
        {
            var node = _folders.GetNodeAt(_folders.PointToClient(new Point(e.X, e.Y)));
            return node?.Tag is ResourceImportItem ? node.Parent : node;
        }

        private void FolderDragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (_active != null) return;
            var node = FolderAt(e);
            if (_dragItems != null && e.Data.GetDataPresent(QueueDragFormat) && node?.Tag is string)
            {
                e.Effect = DragDropEffects.Copy;
                _folders.SelectedNode = node;
            }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 &&
                Directory.Exists(paths[0]) && SelectedItems().Length > 0 &&
                (ResourceImportSession.SamePath(paths[0], _session.Library) || CojtTransfer.RelativeToRoot(paths[0], _session.Library) != null))
                e.Effect = DragDropEffects.Copy;
        }

        private void RemovePlacement(ResourceImportItem item)
        {
            if (!_placements.TryGetValue(item, out var node)) return;
            node.Remove();
            _placements.Remove(item);
        }

        private void Assign(ResourceImportItem[] items, TreeNode node)
        {
            if (!(node?.Tag is string folder) || items.Length == 0) return;
            try
            {
                _session.EnsureDocument();
                _session.CheckFolder(folder);
                _folders.BeginUpdate();
                try
                {
                    var selected = new HashSet<ResourceImportItem>(items);
                    foreach (var row in Rows())
                    {
                        var item = (ResourceImportItem)row.Tag;
                        if (!selected.Contains(item) || item.SceneWriteAttempted) continue;
                        RemovePlacement(item);
                        item.Folder = folder;
                        item.Error = null;
                        _placements.Add(item, node.Nodes.Add(item.Name));
                        _placements[item].Tag = item;
                        RefreshRow(row);
                    }
                    node.EnsureVisible();
                    node.Expand();
                    _folders.SelectedNode = node;
                }
                finally { _folders.EndUpdate(); }
                SetStatus("已指定 " + items.Length + " 个 COJT 的存放目录");
            }
            catch (Exception error) { SetStatus(error.Message); }
        }

        private void SetStatus(string message = null)
        {
            var pending = Rows().Select(row => (ResourceImportItem)row.Tag).Where(item => !item.SceneWriteAttempted).ToArray();
            int untyped = pending.Count(item => item.Prototype == null);
            int unassigned = pending.Count(item => item.Folder == null);
            string state = pending.Length == 0 ? "添加模型文件开始导入。" : "待导入 " + pending.Length + " 个";
            if (untyped != 0) state += " · " + untyped + " 个待指定原型";
            if (unassigned != 0) state += " · " + unassigned + " 个待指定目录";
            _status.Text = message == null ? state : message + (pending.Length == 0 ? "" : "。 " + state);
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool idle = _active == null;
            var selected = SelectedItems();
            _add.Enabled = idle;
            _remove.Enabled = _prototype.Enabled = idle && selected.Length > 0;
            _assign.Enabled = idle && selected.Length > 0 && _folders.SelectedNode?.Tag is string;
            _import.Enabled = idle && Rows().Any(row => !((ResourceImportItem)row.Tag).SceneWriteAttempted);
            _cancel.Visible = !idle;
            _cancel.Enabled = !idle && !_active.IsCancellationRequested;
        }

        private async void ImportClick(object sender, EventArgs e)
        {
            if (_active != null) return;
            var rows = Rows().Where(row => !((ResourceImportItem)row.Tag).SceneWriteAttempted).ToArray();
            if (rows.Length == 0) return;
            var incomplete = rows.FirstOrDefault(row => ((ResourceImportItem)row.Tag).Prototype == null ||
                ((ResourceImportItem)row.Tag).Folder == null);
            if (incomplete != null)
            {
                _files.SelectedItems.Clear();
                incomplete.Selected = true;
                incomplete.EnsureVisible();
                SetStatus("请补齐文件的原型和 COJT 目录后再导入");
                return;
            }
            var active = _active = new CancellationTokenSource();
            _progressBar.Value = 0;
            _progressBar.Maximum = rows.Length;
            // The conversion worker has no intra-file percentage to report.
            _progressBar.Style = rows.Length == 1 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            _progressBar.Visible = true;
            UpdateControls();
            int imported = 0, partial = 0, failed = 0, review = 0;
            string stop = null;
            try
            {
                for (int index = 0; index < rows.Length; index++)
                {
                    active.Token.ThrowIfCancellationRequested();
                    _session.EnsureDocument();
                    var row = rows[index];
                    var item = (ResourceImportItem)row.Tag;
                    item.Error = null;
                    item.Warnings = Array.Empty<string>();
                    string current = (index + 1) + "/" + rows.Length + " · ";
                    try
                    {
                        await _session.ImportAsync(item, active.Token, progress =>
                        {
                            _status.Text = current + progress;
                            _status.Update();
                        });
                        imported++;
                        if (item.Warnings.Length != 0) partial++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        item.Error = error.GetBaseException().Message;
                        if (item.SceneWriteAttempted) review++;
                        else failed++;
                    }
                    finally { RefreshRow(row); }
                    if (rows.Length > 1) _progressBar.Value = index + 1;
                }
            }
            catch (OperationCanceledException) { stop = "已取消剩余导入"; }
            catch (Exception error) { stop = error.Message; }
            finally
            {
                _active = null;
                active.Dispose();
                _progressBar.Visible = false;
                _progressBar.Style = ProgressBarStyle.Continuous;
                SetStatus((stop == null ? "" : stop + " · ") + "已导入 " + imported + " · 失败 " + failed +
                    " · 待核对 " + review + (partial == 0 ? "" : " · " + partial + " 个文件有转换提示") +
                    (failed + review + partial == 0 ? "" : "（悬停文件查看原因）"));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_active != null)
            {
                _active.Cancel();
                e.Cancel = true;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (ToolStripItem item in _prototypeMenu.Items)
                    item.Image?.Dispose();
                _prototypeMenu.Dispose();
                _images.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
