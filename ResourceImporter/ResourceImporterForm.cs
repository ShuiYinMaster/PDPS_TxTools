using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.CrossEnvIO;

namespace TxTools.ResourceImporter
{
    internal sealed class ResourceImporterForm : TxForm
    {
        private const int EmSetCueBanner = 0x1501;
        private static readonly Size Design = new Size(760, 610);
        private const string QueueDragFormat = "TxTools.ResourceImporter.Queue";
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
        private readonly TextBox _folderSearch;
        private readonly ListBox _searchResults = new ListBox
        {
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, TabStop = true,
            AccessibleName = "目录检索结果"
        };
        private readonly System.Windows.Forms.Timer _searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly ToolStripDropDown _searchPopup = new ToolStripDropDown
        {
            AutoSize = false, Padding = Padding.Empty, Margin = Padding.Empty
        };
        private readonly ToolStripControlHost _searchHost;
        private List<ResourceFolderMatch> _folderMatches = new List<ResourceFolderMatch>();
        private readonly CheckBox _skipHidden = FormUiKit.MkCheckBox("跳过隐藏几何体（3DXML / STEP）");
        private readonly Label _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly ProgressBar _progressBar = new ProgressBar
        {
            AccessibleName = "资源导入进度", Dock = DockStyle.Fill, Height = 10, Visible = false
        };
        private readonly Dictionary<ResourceImportItem, TreeNode> _placements = new Dictionary<ResourceImportItem, TreeNode>();
        private CojtFolderEdits _folderEdits;
        private readonly ContextMenuStrip _prototypeMenu = new ContextMenuStrip();
        private readonly ImageList _images = new ImageList();
        private ListViewItem[] _prototypeRows = Array.Empty<ListViewItem>();
        private CancellationTokenSource _active;
        private ResourceImportItem[] _dragItems;
        private bool _scaled, _applyingBatch;

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
            _folderSearch = FormUiKit.MkTextBox("", 140);
            _folderSearch.Dock = DockStyle.Fill;
            _folderSearch.AccessibleName = "检索目录";
            _folderSearch.Margin = new Padding(6, 2, 0, 2);
            _folderSearch.HandleCreated += (s, e) => SetCueBanner();
            _searchHost = new ToolStripControlHost(_searchResults)
            {
                AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty
            };
            _searchPopup.Items.Add(_searchHost);
            _files.Columns.Add("文件名称");
            _files.Columns.Add("设备原型");
            _files.SmallImageList = _images;
            var assignRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
            };
            assignRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            assignRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            assignRow.Controls.Add(_assign, 0, 0);
            assignRow.Controls.Add(_folderSearch, 1, 0);
            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.Controls.Add(Pane("待导入文件", "添加文件或拖入下方列表，从列表空白处拖动可框选。", _files,
                ButtonRow(_add, _prototype, _remove)), 0, 0);
            FileDropInput.Wire(() => _active == null, AddFiles, message => SetStatus(message), _add, _files);
            frame.Controls.Add(Pane("系统库目录", "拖入所选文件指定目录；输入目录名可检索。", _folders,
                assignRow), 1, 0);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(ButtonRow(_import, _cancel), 1, 0);
            frame.Controls.Add(_skipHidden, 0, 1);
            frame.SetColumnSpan(_skipHidden, 2);
            frame.Controls.Add(_progressBar, 0, 2);
            frame.SetColumnSpan(_progressBar, 2);
            frame.Controls.Add(footer, 0, 3);
            frame.SetColumnSpan(footer, 2);
            Controls.Add(frame);
            WireEvents();
            SetStatus();
        }

        private static Control Pane(string title, string hint, Control content, Control tools)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = hint, AutoSize = true, Margin = new Padding(3, 4, 3, 3) }, 0, 0);
            layout.Controls.Add(tools, 0, 1);
            layout.Controls.Add(content, 0, 2);
            box.Controls.Add(layout);
            return box;
        }

        private static FlowLayoutPanel ButtonRow(params Control[] tools)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            row.Controls.AddRange(tools);
            return row;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr parameter, string text);

        private void SetCueBanner()
        {
            if (_folderSearch.IsHandleCreated)
                SendMessage(_folderSearch.Handle, EmSetCueBanner, (IntPtr)0, "检索目录…");
        }

        private void ScheduleFolderSearch()
        {
            if (_active != null) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void RunFolderSearch()
        {
            _searchTimer.Stop();
            if (_active != null)
            {
                _searchPopup.Close();
                return;
            }
            if (!_folderSearch.Focused && !_searchResults.Focused)
            {
                _searchPopup.Close();
                return;
            }
            string query = _folderSearch.Text.Trim();
            if (query.Length == 0)
            {
                _folderMatches.Clear();
                _searchPopup.Close();
                return;
            }
            int total;
            _folderMatches = ResourceFolderSearch.Search(_session.Library, query, 40, out total);
            _searchResults.BeginUpdate();
            try
            {
                _searchResults.Items.Clear();
                _searchResults.Font = _folderSearch.Font;
                foreach (var match in _folderMatches) _searchResults.Items.Add(match.Relative);
            }
            finally { _searchResults.EndUpdate(); }
            if (_folderMatches.Count == 0)
            {
                _searchPopup.Close();
                SetStatus("未找到匹配目录：" + query);
                return;
            }
            ShowFolderMatches();
            SetStatus("匹配 " + total + " 个目录，已显示前 " + _folderMatches.Count + " 个");
        }

        private void ShowFolderMatches()
        {
            int rows = Math.Min(8, Math.Max(5, _folderMatches.Count));
            int width = Math.Max(220, _folderSearch.Width);
            int height = Math.Max(1, _searchResults.ItemHeight) * rows + 2;
            _searchResults.Size = new Size(width, height);
            _searchHost.Size = new Size(width, height);
            _searchPopup.Size = new Size(width, height);
            if (!_searchPopup.Visible)
                _searchPopup.Show(_folderSearch, new Point(0, _folderSearch.Height));
        }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                if (!_searchPopup.Visible || _searchTimer.Enabled) RunFolderSearch();
                if (_folderMatches.Count == 0) return;
                ShowFolderMatches();
                _searchResults.Focus();
                if (_searchResults.SelectedIndex < 0) _searchResults.SelectedIndex = 0;
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Enter)
            {
                RunFolderSearch();
                if (_folderMatches.Count != 0)
                    ChooseFolderMatch(_searchResults.SelectedIndex >= 0 ? _searchResults.SelectedIndex : 0);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (_searchPopup.Visible) _searchPopup.Close();
                else _folderSearch.Clear();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void SearchResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && _searchResults.SelectedIndex >= 0)
            {
                ChooseFolderMatch(_searchResults.SelectedIndex);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _searchPopup.Close();
                _folderSearch.Focus();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void ChooseFolderMatch(int index)
        {
            if (index < 0 || index >= _folderMatches.Count) return;
            var match = _folderMatches[index];
            var node = CojtFolderTree.Find(_folders.Nodes[0], match.Path, SetStatus);
            _searchPopup.Close();
            if (node == null)
            {
                SetStatus("目录未在系统库树中显示：" + match.Relative);
                return;
            }
            SelectFolderNode(node);
            SetStatus("已定位目录：" + match.Relative);
        }

        private void SelectFolderNode(TreeNode node)
        {
            for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.Expand();
            _folders.SelectedNode = node;
            node.EnsureVisible();
            node.Expand();
        }

        private void LocateFolderForPrototype(ResourcePrototype prototype)
        {
            _searchTimer.Stop();
            _searchPopup.Close();
            int total;
            string query = ResourceFolderSearch.PrototypeQuery((prototype.Label ?? "") + " " + (prototype.Type ?? ""));
            var matches = ResourceFolderSearch.Search(_session.Library, query, 200, out total);
            var best = matches.FirstOrDefault();
            if (best == null || best.Score < ResourceFolderSearch.AutoLocateScore)
            {
                SetStatus("已设原型 " + prototype.Label + "，系统库中没有匹配目录");
                return;
            }
            var node = CojtFolderTree.Find(_folders.Nodes[0], best.Path, SetStatus);
            if (node == null)
            {
                SetStatus("已设原型 " + prototype.Label + "，匹配目录未在树中显示：" + best.Relative);
                return;
            }
            SelectFolderNode(node);
            int same = matches.Count(match => match.Score == best.Score);
            SetStatus("已按原型 " + prototype.Label + " 定位目录：" + best.Relative +
                (same > 1 ? "（" + same + " 个匹配，已选第一个）" : ""));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, Design);
            _status.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 2 * _import.Width - 64), 0);
            LoadPrototypeImages();
            CojtFolderTree.AddFolderImage(_images);
            _folders.ImageList = _images;
            _folderEdits = new CojtFolderEdits(_folders, () => _active == null, SetStatus, RefreshFolderAssignments);
            var root = CojtFolderTree.CreateNode(_session.Library, "系统库 · " + new DirectoryInfo(_session.Library).Name);
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
                ResourcePrototypeImages.Add(_images, type, Font.FontFamily);
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
            _folderSearch.TextChanged += (s, e) => ScheduleFolderSearch();
            _folderSearch.KeyDown += SearchKeyDown;
            _searchTimer.Tick += (s, e) => RunFolderSearch();
            _searchResults.KeyDown += SearchResultsKeyDown;
            _searchResults.MouseClick += (s, e) =>
            {
                int index = _searchResults.IndexFromPoint(e.Location);
                if (index >= 0) ChooseFolderMatch(index);
            };
            _searchPopup.Closed += (s, e) => _searchTimer.Stop();
            _import.Click += ImportClick;
            _cancel.Click += (s, e) =>
            {
                if (_active == null || _applyingBatch) return;
                _cancel.Enabled = false;
                _status.Text = "正在取消导入…";
                _active.Cancel();
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
            _folders.BeforeExpand += (s, e) => CojtFolderTree.Populate(e.Node, SetStatus);
            _folders.DragEnter += FolderDragOver;
            _folders.DragOver += FolderDragOver;
            _folders.DragDrop += (s, e) =>
            {
                if (_active != null) return;
                if (_dragItems != null && e.Data.GetDataPresent(QueueDragFormat))
                    Assign(_dragItems, CojtFolderTree.At(_folders, e));
            };
            FileDropInput.Wire(() => _active == null && SelectedItems().Length > 0, AssignDroppedFolder,
                message => SetStatus(message), _folders);
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

        private bool AddFiles(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(Rows().Select(row => ((ResourceImportItem)row.Tag).File), StringComparer.OrdinalIgnoreCase);
            int added = 0, ignored = 0;
            _files.BeginUpdate();
            try
            {
                foreach (string file in paths)
                {
                    if (!File.Exists(file) || !ResourceConversion.Supports(file))
                    {
                        ignored++;
                        continue;
                    }
                    string path = Path.GetFullPath(file);
                    if (!existing.Add(path))
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
            return added > 0;
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
            if (rows.Length != 0) LocateFolderForPrototype(prototype);
            else SetStatus();
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
            if (_placements.TryGetValue(item, out var placementNode))
                placementNode.ImageKey = placementNode.SelectedImageKey = row.ImageKey;
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

        private void FolderDragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (_active != null) return;
            var node = CojtFolderTree.At(_folders, e);
            if (_dragItems != null && e.Data.GetDataPresent(QueueDragFormat) && node?.Tag is string)
            {
                e.Effect = DragDropEffects.Copy;
                _folders.SelectedNode = node;
            }
        }

        private bool AssignDroppedFolder(string[] paths)
        {
            if (paths.Length != 1 || !Directory.Exists(paths[0]))
            {
                SetStatus("请拖入系统库中的一个文件夹。模型文件请拖到左侧待导入文件区域。");
                return false;
            }
            string folder = Path.GetFullPath(paths[0]);
            _session.CheckFolder(folder);
            var node = CojtFolderTree.Find(_folders.Nodes[0], folder, SetStatus);
            if (node == null) throw new IOException("目录未在系统库树中显示：" + folder);
            Assign(SelectedItems(), node);
            return true;
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
                bool draft = _folderEdits.IsDraft(node);
                _session.CheckFolder(folder, draft);
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
                        item.CreateFolder = draft;
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

        private void RefreshFolderAssignments()
        {
            foreach (var row in Rows())
            {
                var item = (ResourceImportItem)row.Tag;
                if (!_placements.TryGetValue(item, out var placement) || !(placement.Parent?.Tag is string path)) continue;
                item.Folder = path;
                item.CreateFolder = _folderEdits.IsDraft(placement.Parent);
                RefreshRow(row);
            }
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
            _skipHidden.Enabled = idle;
            _remove.Enabled = _prototype.Enabled = idle && selected.Length > 0;
            _assign.Enabled = idle && selected.Length > 0 && _folders.SelectedNode?.Tag is string;
            _folderSearch.Enabled = idle;
            if (!idle) _searchPopup.Close();
            _import.Enabled = idle && Rows().Any(row => !((ResourceImportItem)row.Tag).SceneWriteAttempted);
            bool converting = !idle && !_applyingBatch;
            _cancel.Visible = converting;
            _cancel.Enabled = converting && !_active.IsCancellationRequested;
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
            var items = rows.Select(row => (ResourceImportItem)row.Tag).ToArray();
            bool skipHidden = _skipHidden.Checked;
            var active = _active = new CancellationTokenSource();
            _progressBar.Value = 0;
            _progressBar.Maximum = rows.Length;
            // The conversion worker has no intra-file percentage to report.
            _progressBar.Style = rows.Length == 1 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            _progressBar.Visible = true;
            UpdateControls();
            string stop = null;
            try
            {
                _status.Text = "准备批量转换 · " + rows.Length + " 个文件";
                var conversionProgress = new Progress<int>(completed =>
                {
                    if (_active != active || _applyingBatch || active.IsCancellationRequested) return;
                    if (rows.Length > 1) _progressBar.Value = completed;
                    _status.Text = rows.Length == 1 ? "转换 JT · " + items[0].Name :
                        "转换批次 · 已处理 " + completed + "/" + rows.Length;
                });
                await _session.ImportBatchAsync(items, skipHidden, active.Token, conversionProgress, phase =>
                {
                    _applyingBatch = true;
                    _progressBar.Style = ProgressBarStyle.Marquee;
                    UpdateControls();
                    _status.Text = phase;
                    _status.Update();
                });
            }
            catch (OperationCanceledException) { stop = "已取消导入"; }
            catch (Exception error) { stop = error.Message; }
            finally
            {
                _active = null;
                _applyingBatch = false;
                active.Dispose();
                _progressBar.Visible = false;
                _progressBar.Style = ProgressBarStyle.Continuous;
                _folderEdits.RefreshMaterialized();
                foreach (var row in rows) RefreshRow(row);
                int imported = items.Count(item => item.Imported);
                int partial = items.Count(item => item.Imported && item.Warnings.Length != 0);
                int failed = items.Count(item => !item.SceneWriteAttempted && item.Error != null);
                int review = items.Count(item => item.SceneWriteAttempted && !item.Imported);
                SetStatus((stop == null ? "" : stop + " · ") + "已导入 " + imported + " · 失败 " + failed +
                    " · 待核对 " + review + (partial == 0 ? "" : " · " + partial + " 个文件有转换提示") +
                    (failed + review + partial == 0 ? "" : "（悬停文件查看原因）"));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_active != null)
            {
                if (!_applyingBatch) _active.Cancel();
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
                _searchTimer.Dispose();
                _searchPopup.Dispose();
                if (!_searchResults.IsDisposed) _searchResults.Dispose();
                _images.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
