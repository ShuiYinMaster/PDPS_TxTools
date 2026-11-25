using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.ProcessDesigner;
using Theme = TxTools.Common.FormUiKit.Theme;

namespace TxTools.WeldPointImporter
{
    internal sealed class WeldPointImporterForm : TxForm
    {
        private enum Source { Points, GeometrySphere, SolidBody, ThreeDXml, BiW, Stud, Table }
        private enum NamingMode { Original, Standard, Custom }
        private const int NameMarkerPadding = 18;
        private static readonly Size Design = new Size(1040, 670), Minimum = new Size(920, 570);
        private readonly List<ImportPoint> _points = new List<ImportPoint>();
        private Dictionary<int, DuplicateMatch> _duplicates = new Dictionary<int, DuplicateMatch>();
        private int[] _visible = Array.Empty<int>();
        private readonly ComboBox _source = Choice(178, "CATIA 几何点", "CATIA 图形集球体", "CATIA 几何体重心", "3DXML 焊点符号", "BiW Joint Element", "CATIA 螺柱实例原点", "CSV / Excel 点表");
        private readonly ComboBox _biwSource = Choice(180, "CATIA 当前装配", "CATProduct / ABF 文件");
        private readonly ComboBox _xmlMode = Choice(140, "自动识别", "仅按名称筛选", "仅按几何识别", "包络中心（兼容）");
        private readonly ComboBox _xmlSource = Choice(160, "选择文件", "CATIA 当前 3DXML");
        private readonly ComboBox _filter = Choice(106, "全部点", "仅保留点", "仅重复点");
        private readonly TextBox _stud = FormUiKit.MkTextBox("", 270), _hint = FormUiKit.MkTextBox("XWS", 100);
        private readonly TextBox _operation = FormUiKit.MkTextBox("导入焊点", 180);
        private readonly NumericUpDown _radius = FormUiKit.MkNumeric(0.1m, 1000, 20, 1, 1);
        private readonly NumericUpDown _threshold = FormUiKit.MkNumeric(0.001m, 1000, 5, 0.5m, 3);
        private readonly CheckBox _append = FormUiKit.MkCheckBox("追加到列表", true);
        private readonly CheckBox _skip = FormUiKit.MkCheckBox("创建时跳过重复点", true);
        private readonly CheckBox _skipHidden = FormUiKit.MkCheckBox("排除隐藏对象");
        private readonly Label _instructions = MessageLabel(), _target = MessageLabel();
        private readonly PointStatisticsBar _statistics;
        private readonly PointStatusBar _status;
        private readonly FlowLayoutPanel _sourceRow = Row(), _sourceOptions = Row(), _namingRow = Row(), _targetRow = Row();
        private readonly DataGridView _grid = new DataGridView();
        private readonly PointPreview _preview = new PointPreview { Dock = DockStyle.Fill };
        private readonly Button _create = FormUiKit.MkButton("创建焊点", true), _details = FormUiKit.MkButton("读取详情", false);
        private readonly bool _designer;
        private PointReadResult _lastRead;
        private TxDocument _document;
        private long _generation;
        private bool _scaled, _busy;
        private PointNaming _naming = new PointNaming();
        private NamingMode _namingMode;

        internal WeldPointImporterForm(bool designer = false)
        {
            _designer = designer;
            FormUiKit.InitStandardForm(this, designer ? "焊点导入" : "焊点提取与创建", Design, Minimum);
            SemiModal = false;
            _statistics = new PointStatisticsBar();
            _status = new PointStatusBar();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10) };
            for (int index = 0; index < root.RowCount; index++)
                root.RowStyles.Add(new RowStyle(index == 4 ? SizeType.Percent : SizeType.AutoSize, index == 4 ? 100 : 0));
            root.Controls.Add(_sourceRow, 0, 0);
            root.Controls.Add(_sourceOptions, 0, 1);
            root.Controls.Add(_instructions, 0, 2);
            root.Controls.Add(_namingRow, 0, 3);
            root.Controls.Add(BuildReview(), 0, 4);
            root.Controls.Add(_targetRow, 0, 5);
            root.Controls.Add(_status, 0, 6);
            Controls.Add(root);

            _sourceRow.Controls.AddRange(new Control[] { Label("点位来源", _source), _source });
            var read = FormUiKit.MkButton("读取点位", true);
            _sourceRow.Controls.AddRange(new Control[] { read, _append, _details });
            read.Click += async (s, e) => await ReadSource();
            _details.Click += (s, e) => ShowReadDetails();
            _source.SelectedIndexChanged += (s, e) => ConfigureSource();
            _biwSource.SelectedIndexChanged += (s, e) => ConfigureSource();
            _xmlMode.SelectedIndexChanged += (s, e) => ConfigureSource();
            _xmlSource.SelectedIndexChanged += (s, e) => ConfigureSource();
            _namingRow.Controls.AddRange(new Control[] { Label("重复阈值 (mm)", _threshold), _threshold, _filter });
            AddButton(_namingRow, "原始名称", () => Rename(NamingMode.Original));
            AddButton(_namingRow, "自动重命名", () => Rename(NamingMode.Standard));
            AddButton(_namingRow, "自定义命名", CustomizeNames);
            AddButton(_namingRow, "移除所选点", RemoveSelected);
            _threshold.ValueChanged += (s, e) => Recheck(false);
            _filter.SelectedIndexChanged += (s, e) => RefreshRows(false);
            _skip.CheckedChanged += (s, e) => UpdateTarget();
            if (designer)
            {
                _skip.Text = "导入时跳过重复点";
                _targetRow.Controls.AddRange(new Control[] { _skip, _create, _target });
            }
            else
                _targetRow.Controls.AddRange(new Control[] { Label("新操作名称", _operation), _operation, _skip, _create, _target });
            _operation.TextChanged += (s, e) => UpdateTarget();
            _create.Click += (s, e) => CreatePoints();
            _preview.PointSelected += SelectInGrid;
            Activated += (s, e) => ReadTarget();
            Load += (s, e) => FormUiKit.ApplyDpiScaling(this, ref _scaled, Design, scale =>
                _grid.Columns[0].DefaultCellStyle.Padding = new Padding((int)Math.Round(NameMarkerPadding * scale), 0, 0, 0));
            FormClosing += (s, e) => { if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
            ConfigureSource();
            ReadTarget();
            Recheck(true);
            SetStatus(PointStatus.Ready, "读取点位后，在点表和预览中核对坐标。");
            _details.Enabled = false;
        }

        private Control BuildReview()
        {
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var reviewHeader = Row();
            reviewHeader.Margin = Padding.Empty;
            _statistics.Dock = DockStyle.None;
            _statistics.Anchor = AnchorStyles.None;
            reviewHeader.Controls.Add(_statistics);
            var previewTools = Row();
            previewTools.Dock = DockStyle.None;
            previewTools.Anchor = AnchorStyles.None;
            previewTools.WrapContents = false;
            previewTools.Margin = new Padding(8, 0, 3, 5);
            var views = Choice(111, "XZ 侧视", "XY 俯视", "YZ 前视", "3D 旋转");
            previewTools.Controls.AddRange(new Control[] { Label("预览", views), views });
            AddButton(previewTools, "适合窗口", _preview.Fit);
            views.SelectedIndexChanged += (s, e) => _preview.SetView(views.SelectedIndex);
            reviewHeader.Controls.Add(previewTools);
            split.Controls.Add(reviewHeader, 0, 0);
            split.SetColumnSpan(reviewHeader, 2);
            split.Controls.Add(_grid, 0, 1);
            split.Controls.Add(_preview, 1, 1);

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = _grid.AllowUserToDeleteRows = _grid.AllowUserToResizeRows = false;
            _grid.BackgroundColor = Theme.GridRowEven;
            _grid.GridColor = Theme.GridHeader;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.GridHeader;
            _grid.DefaultCellStyle.BackColor = Theme.GridRowEven;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridAlt;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.RowHeadersVisible = false;
            _grid.VirtualMode = true;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "点名称", FillWeight = 43, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(NameMarkerPadding, 0, 0, 0) } });
            foreach (string axis in new[] { "X", "Y", "Z" })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = axis + " (mm)", FillWeight = 19, ReadOnly = true,
                    SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
            _grid.CellValueNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _visible.Length) return;
                var point = _points[_visible[e.RowIndex]];
                e.Value = e.ColumnIndex == 0 ? point.Name : point.Axis(e.ColumnIndex - 1).ToString("0.######", CultureInfo.InvariantCulture);
            };
            _grid.CellValuePushed += (s, e) =>
            {
                if (e.ColumnIndex == 0 && e.RowIndex >= 0 && e.RowIndex < _visible.Length)
                    _points[_visible[e.RowIndex]].Name = Convert.ToString(e.Value)?.Trim() ?? "";
            };
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _visible.Length && _duplicates.ContainsKey(_visible[e.RowIndex]))
                    e.CellStyle.BackColor = Theme.RowWarn;
            };
            _grid.CellPainting += PaintNameMarker;
            _grid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _visible.Length) return;
                var point = _points[_visible[e.RowIndex]];
                string layer = point.LayerCount.HasValue ? point.LayerCount.Value + " 层板" : "双层（默认）";
                e.ToolTipText = point.Name + " · " + layer;
                if (_duplicates.TryGetValue(_visible[e.RowIndex], out var duplicate))
                    e.ToolTipText += "\n重复于 “" + _points[duplicate.Kept].Name + "”，距离 "
                        + duplicate.Distance.ToString("0.######", CultureInfo.InvariantCulture) + " mm";
            };
            _grid.CurrentCellChanged += (s, e) => _preview.SelectPoint(CurrentPoint());
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) _grid.BeginEdit(true); };
            return split;
        }

        private void PaintNameMarker(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= _visible.Length) return;
            e.Paint(e.ClipBounds, e.PaintParts);
            var state = e.Graphics.Save();
            e.Graphics.SetClip(Rectangle.Intersect(e.ClipBounds, e.CellBounds), CombineMode.Intersect);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float padding = e.CellStyle.Padding.Left;
            float size = Math.Min(padding * 0.52f, e.CellBounds.Height * 0.55f);
            var point = _points[_visible[e.RowIndex]];
            using (var brush = new SolidBrush(WeldLayerColors.Get(WeldLayers.Group(point.LayerCount))))
                e.Graphics.FillEllipse(brush, e.CellBounds.Left + (padding - size) / 2, e.CellBounds.Top + (e.CellBounds.Height - size) / 2, size, size);
            e.Graphics.Restore(state);
            e.Handled = true;
        }

        private void ConfigureSource()
        {
            _sourceOptions.SuspendLayout();
            foreach (Control control in _sourceOptions.Controls.Cast<Control>().ToArray())
            {
                _sourceOptions.Controls.Remove(control);
                if (control is Label) control.Dispose();
            }
            string text;
            switch ((Source)_source.SelectedIndex)
            {
                case Source.ThreeDXml:
                    _sourceOptions.Controls.AddRange(new Control[] { _xmlSource, Label("识别方式", _xmlMode), _xmlMode, Label("名称线索", _hint), _hint,
                        Label("包络半径上限 (mm)", _radius), _radius });
                    _hint.Enabled = _xmlMode.SelectedIndex == (int)ThreeDXmlPointMode.Auto || _xmlMode.SelectedIndex == (int)ThreeDXmlPointMode.NameOnly;
                    text = _xmlSource.SelectedIndex == 0 ? "选择 3DXML 文件" : "在 CATIA 中激活 3DXML 文档";
                    text += _xmlMode.SelectedIndex == (int)ThreeDXmlPointMode.EnvelopeCenters
                        ? "，然后在预览中核对几何包络中心。" : "，然后在预览中核对焊点符号候选。";
                    break;
                case Source.BiW:
                    _sourceOptions.Controls.AddRange(new Control[] { Label("BiW 来源", _biwSource), _biwSource });
                    text = _biwSource.SelectedIndex == 0 ? "在 CATIA 中激活已保存的 CATProduct，读取当前装配及其子装配的 BiW 坐标。"
                        : "选择 CATProduct 或 ABF Neutral 文件，文件坐标按毫米读取。";
                    break;
                case Source.Stud:
                    _sourceOptions.Controls.AddRange(new Control[] { Label("名称或零件号", _stud), _stud });
                    text = "在 CATIA 中激活 CATProduct，输入螺柱实例名称或零件号片段。多个关键词可用逗号隔开。";
                    break;
                case Source.Table:
                    text = "选择 CSV、TSV 或 XLSX 点表。表头应包含 X、Y、Z，坐标需与"
                        + (_designer ? " Process Designer " : " PS ") + "世界坐标对齐。";
                    break;
                case Source.GeometrySphere:
                    text = "在 CATIA 中激活 CATPart 或 CATProduct，读取图形集球特征的中心。";
                    break;
                case Source.SolidBody:
                    text = "在 CATIA 中激活 CATPart 或 CATProduct，读取可测量实体的重心，请在预览中移除非焊点实体。";
                    break;
                default:
                    text = "在 CATIA 中激活 CATPart 或 CATProduct，读取几何点。双击列表名称可单独修改。";
                    break;
            }
            var source = (Source)_source.SelectedIndex;
            if (source != Source.Table && (source != Source.BiW || _biwSource.SelectedIndex == 0))
                _sourceOptions.Controls.Add(_skipHidden);
            _sourceOptions.Visible = _sourceOptions.Controls.Count > 0;
            _sourceOptions.ResumeLayout(true);
            _instructions.Text = text;
        }

        private async Task ReadSource()
        {
            if (_busy || !_grid.EndEdit()) return;
            var source = (Source)_source.SelectedIndex;
            bool skipHidden = _skipHidden.Checked;
            Func<PointReadResult> reader;
            bool useCatia = false;
            switch (source)
            {
                case Source.ThreeDXml:
                    var mode = (ThreeDXmlPointMode)_xmlMode.SelectedIndex;
                    string hint = _hint.Text;
                    double radius = (double)_radius.Value;
                    if (_xmlSource.SelectedIndex == 1)
                    {
                        useCatia = true;
                        reader = () => ThreeDXmlPoints.Read(CatiaPointReader.ActiveThreeDXmlPath(), mode, hint, radius, skipHidden);
                    }
                    else
                    {
                        string xml = PickFile("3DXML 文件 (*.3dxml)|*.3dxml");
                        if (xml == null) return;
                        reader = () => ThreeDXmlPoints.Read(xml, mode, hint, radius, skipHidden);
                    }
                    break;
                case Source.BiW when _biwSource.SelectedIndex == 1:
                    string biw = PickFile("BiW 文件 (*.CATProduct;*.neu;*.txt;*.tsv)|*.CATProduct;*.neu;*.txt;*.tsv|所有文件 (*.*)|*.*");
                    if (biw == null) return;
                    reader = () => BiWPointFile.Read(biw);
                    break;
                case Source.Table:
                    string table = PickFile("点位表格 (*.csv;*.tsv;*.xlsx)|*.csv;*.tsv;*.xlsx");
                    if (table == null) return;
                    reader = () => PointTableFile.Read(table);
                    break;
                default:
                    var cadMode = source == Source.Stud ? CatiaPointMode.Studs : source == Source.BiW ? CatiaPointMode.BiW
                        : source == Source.GeometrySphere ? CatiaPointMode.GeometricalSpheres : source == Source.SolidBody ? CatiaPointMode.SolidBodies
                        : CatiaPointMode.Points;
                    string query = _stud.Text;
                    reader = () => CatiaPointReader.Read(cadMode, query, skipHidden);
                    useCatia = true;
                    break;
            }
            bool append = _append.Checked;
            SetBusy(true);
            SetStatus(PointStatus.Reading, _source.Text);
            _lastRead = null;
            try
            {
                _lastRead = await (useCatia ? RunCatia(reader) : Task.Run(reader));
                if (IsDisposed) return;
                if (_lastRead.Points.Count > 0)
                {
                    if (!append) _points.Clear();
                    int first = _points.Count;
                    _points.AddRange(_lastRead.Points);
                    ApplyNames(first);
                    _filter.SelectedIndex = 0;
                    Recheck(true);
                }
                int imported = _lastRead.Points.Count, warnings = _lastRead.WarningCount;
                string message = imported == 0 ? "未读到符合条件的点位。" : "已读入 " + imported + " 个点。";
                if (imported == 0 && _points.Count > 0) message += "列表保留原有 " + _points.Count + " 个点。";
                if (warnings > 0) message += "有 " + warnings + " 条提示，可打开“读取详情”。";
                SetStatus(imported == 0 || warnings > 0 ? PointStatus.Notice : PointStatus.Success, message);
            }
            catch (Exception error)
            {
                if (!IsDisposed) SetStatus(PointStatus.Error, "读取点位失败。" + error.GetBaseException().Message
                    + (_points.Count == 0 ? "" : "\n列表保留原有 " + _points.Count + " 个点。"));
            }
            finally { if (!IsDisposed) { SetBusy(false); ReadTarget(); } }
        }

        private static Task<PointReadResult> RunCatia(Func<PointReadResult> reader)
        {
            var completion = new TaskCompletionSource<PointReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { completion.SetResult(reader()); }
                catch (Exception error) { completion.SetException(error); }
            }) { IsBackground = true, Name = "TxTools CATIA weld points" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return completion.Task;
        }

        private string PickFile(string filter)
        {
            using (var dialog = new OpenFileDialog { Title = "选择点位来源文件", Filter = filter, CheckFileExists = true })
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }

        private void ShowReadDetails()
        {
            if (_lastRead == null || _lastRead.WarningCount == 0) return;
            string text = string.Join(Environment.NewLine, _lastRead.Warnings);
            int extra = _lastRead.WarningCount - _lastRead.Warnings.Count;
            if (extra > 0) text += Environment.NewLine + "另有 " + extra + " 条同批提示。";
            MessageBox.Show(this, text, "读取详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Recheck(bool fit)
        {
            _grid.EndEdit();
            _duplicates = DuplicatePoints.Find(_points, (double)_threshold.Value);
            RefreshRows(fit);
        }

        private void RefreshRows(bool fit)
        {
            _grid.EndEdit();
            int selected = CurrentPoint();
            _visible = Enumerable.Range(0, _points.Count).Where(index => _filter.SelectedIndex == 0
                || _duplicates.ContainsKey(index) == (_filter.SelectedIndex == 2)).ToArray();
            _grid.RowCount = _visible.Length;
            _grid.Invalidate();
            _preview.UpdatePoints(_points, _duplicates, _visible, fit);
            if (selected >= 0) SelectInGrid(selected);
            _preview.SelectPoint(CurrentPoint());
            _statistics.UpdateCounts(_points, _duplicates.Count, _visible.Length);
            UpdateTarget();
        }

        private int CurrentPoint()
        {
            int row = _grid.CurrentCell?.RowIndex ?? -1;
            return row >= 0 && row < _visible.Length ? _visible[row] : -1;
        }

        private void SelectInGrid(int index)
        {
            int row = Array.IndexOf(_visible, index);
            if (row < 0) return;
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[row].Cells[0];
            _grid.Rows[row].Selected = true;
            _preview.SelectPoint(index);
        }

        private void Rename(NamingMode mode)
        {
            if (_points.Count == 0 || !_grid.EndEdit()) return;
            _namingMode = mode;
            ApplyNames(0);
            _grid.Invalidate();
            SetStatus(PointStatus.Success, "已更新 " + _points.Count + " 个点的名称。");
        }

        private void ApplyNames(int first)
        {
            var usedNames = _namingMode == NamingMode.Custom
                ? new HashSet<string>(_points.Take(first).Select(point => point.Name), StringComparer.OrdinalIgnoreCase) : null;
            int sequence = first;
            for (int index = first; index < _points.Count; index++)
            {
                var point = _points[index];
                if (usedNames != null)
                {
                    do { point.Name = _naming.At(sequence++); } while (!usedNames.Add(point.Name));
                }
                else point.Name = _namingMode == NamingMode.Standard ? point.StandardName() : point.OriginalName;
            }
        }

        private void CustomizeNames()
        {
            if (_points.Count == 0) return;
            using (var dialog = new PointNamingDialog(_naming))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _naming = dialog.Value;
                Rename(NamingMode.Custom);
            }
        }

        private void RemoveSelected()
        {
            if (!_grid.EndEdit()) return;
            var indexes = _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => _visible[row.Index]).OrderByDescending(index => index).ToArray();
            foreach (var index in indexes) _points.RemoveAt(index);
            Recheck(true);
            if (indexes.Length > 0) SetStatus(PointStatus.Success, "已移除 " + indexes.Length + " 个点。");
        }

        private void ReadTarget()
        {
            if (_designer)
            {
                _target.Text = "焊点将导入当前工程的 MfgLibrary。";
                UpdateTarget();
                return;
            }
            try
            {
                _document = TxApplication.ActiveDocument;
                var study = _document?.CurrentStudy;
                if (study == null)
                {
                    _document = null;
                    _target.Text = "请在 PS 打开目标工程";
                }
                else
                {
                    _generation = SceneUndoScope.DocumentGeneration;
                    _target.Text = "目标工程  " + study.Name;
                }
            }
            catch (Exception)
            {
                _document = null;
                _target.Text = "无法取得 PS 当前工程";
            }
            UpdateTarget();
        }

        private void UpdateTarget()
        {
            int count = _points.Count - (_skip.Checked ? _duplicates.Count : 0);
            _create.Enabled = !_busy && count > 0 && (_designer || (_document != null && !string.IsNullOrWhiteSpace(_operation.Text)));
            _create.Text = _designer ? (count == 0 ? "准备 MFG 导入" : "准备 " + count + " 个 MFG 焊点")
                : (count == 0 ? "创建焊点" : "创建 " + count + " 个焊点");
        }

        private void CreatePoints()
        {
            if (_busy || !_grid.EndEdit()) return;
            var pending = _points.Where((point, index) => !_skip.Checked || !_duplicates.ContainsKey(index)).ToArray();
            if (pending.Length == 0) return;
            try
            {
                if (_designer)
                {
                    var package = DesignerXml.BuildMfg(pending);
                    new DesignerImportDialog(package).Show(this);
                    SetStatus(PointStatus.Success, "已准备 " + pending.Length + " 个 MFG 焊点。请在导入窗口执行导入。");
                    return;
                }
                var result = PsPointWriter.Create(_document, _generation, _operation.Text.Trim(), pending);
                if (result.Error == null)
                    SetStatus(PointStatus.Success, "已在 “" + result.OperationName + "” 创建 " + result.Created + " 个焊点。可在 PS 中按 Ctrl+Z 撤销。");
                else
                {
                    string undo = result.SceneMayHaveChanged ? "。如需撤销本批次，请在 PS 中按 Ctrl+Z。" : "。";
                    SetStatus(PointStatus.Error, "已确认创建 " + result.Created + "/" + pending.Length + " 个焊点。" + result.Error + undo);
                }
            }
            catch (Exception error) { SetStatus(PointStatus.Error, error.Message); }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _sourceRow.Enabled = _sourceOptions.Enabled = _namingRow.Enabled = _targetRow.Enabled = _grid.Enabled = !busy;
            _details.Enabled = !busy && _lastRead?.WarningCount > 0;
            UseWaitCursor = busy;
            UpdateTarget();
        }

        private void SetStatus(PointStatus status, string text)
        {
            _status.UpdateStatus(status, text);
            if (status == PointStatus.Error) MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static ComboBox Choice(int width, params string[] values)
        {
            var input = FormUiKit.MkComboBox(width);
            input.Items.AddRange(values);
            input.SelectedIndex = 0;
            return input;
        }

        private static FlowLayoutPanel Row()
        {
            var row = FormUiKit.MkRowFlow();
            row.Dock = DockStyle.Fill;
            row.WrapContents = true;
            return row;
        }

        private static Label MessageLabel() => new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6), ForeColor = Theme.TextDim };
        private static Label Label(string text, Control field) => new Label { AutoSize = true, Text = text, Margin = new Padding(3, 7, 3, 3), ForeColor = Theme.TextDim,
            TabIndex = field.TabIndex };

        private static void AddButton(Control row, string title, Action action)
        {
            var button = FormUiKit.MkButton(title, false);
            button.Click += (s, e) => action();
            row.Controls.Add(button);
        }
    }

    internal sealed class PointNamingDialog : Form
    {
        internal PointNaming Value { get; private set; }

        internal PointNamingDialog(PointNaming values)
        {
            Text = "自定义点名称";
            BackColor = FormUiKit.WinBg;
            Font = FormUiKit.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoSize = true, ColumnCount = 2, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(layout);
            var prefix = FormUiKit.MkTextBox(values.Prefix, 220);
            var main = FormUiKit.MkTextBox(values.Main, 220);
            var separator = FormUiKit.MkTextBox(values.Separator, 220);
            var suffix = FormUiKit.MkTextBox(values.Suffix, 220);
            var start = FormUiKit.MkNumeric(0, int.MaxValue, values.Start);
            var digits = FormUiKit.MkNumeric(0, 16, values.Digits);
            var sample = FormUiKit.MkLabel("");
            var ok = FormUiKit.MkButton("应用命名", true);
            var cancel = FormUiKit.MkButton("取消", false);
            cancel.DialogResult = DialogResult.Cancel;
            var actions = FormUiKit.MkRowFlow();
            actions.Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            void Field(string name, Control control, int row)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(FormUiKit.MkFieldLabel(name), 0, row);
                layout.Controls.Add(control, 1, row);
            }
            Field("前缀", prefix, 0); Field("主名称", main, 1); Field("分隔符", separator, 2); Field("后缀", suffix, 3);
            Field("起始编号", start, 4); Field("补零位数", digits, 5); Field("首点示例", sample, 6);
            layout.Controls.Add(actions, 1, 7);
            void RefreshSample()
            {
                Value = new PointNaming { Prefix = prefix.Text, Main = main.Text.Trim(), Separator = separator.Text, Suffix = suffix.Text,
                    Start = (int)start.Value, Digits = (int)digits.Value };
                sample.Text = Value.At(0);
                ok.Enabled = Value.Main.Length > 0;
            }
            foreach (var field in new[] { prefix, main, separator, suffix }) field.TextChanged += (s, e) => RefreshSample();
            start.ValueChanged += (s, e) => RefreshSample();
            digits.ValueChanged += (s, e) => RefreshSample();
            ok.Click += (s, e) => { RefreshSample(); if (ok.Enabled) DialogResult = DialogResult.OK; };
            RefreshSample();
            PluginUiPolicy.Attach(this);
        }
    }
}
