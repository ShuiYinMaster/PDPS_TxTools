using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.ExportGun;
using TxTools.WeldSpotAllocator;

namespace TxTools.WeldPointMatrix
{
    public sealed class WeldPointMatrixForm : TxForm
    {
        private static WeldPointMatrixForm _instance;
        private readonly Size _designSize = new Size(1120, 700);
        private readonly OpGridHost _picker = new OpGridHost();
        private readonly List<ITxObject> _operations = new List<ITxObject>();
        private readonly List<SpotData[]> _rows = new List<SpotData[]>();
        private Panel _pickerHost;
        private DataGridView _table;
        private Label _status;
        private bool _initialized;
        private bool _scaled;
        private bool _rendering;
        private Point _dragStart;
        private int _dragRow = -1;
        private int _dragColumn = -1;
        private DateTime _lastDropUtc;

        private sealed class PointDragData
        {
            internal ITxObject SourceOperation;
            internal ITxObject Location;
            internal string Name;
        }

        public static void ShowSingleton()
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new WeldPointMatrixForm();
                _instance.Show();
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized)
                    _instance.WindowState = FormWindowState.Normal;
                _instance.Activate();
            }
        }

        private WeldPointMatrixForm()
        {
            Name = typeof(WeldPointMatrixForm).FullName;
            FormUiKit.InitStandardForm(this, "焊点矩阵", _designSize,
                new Size(760, 480), sizable: true);
            try { SemiModal = false; } catch { }
            BuildUi();
            Load += (s, e) =>
            {
                EnsurePicker();
                FormUiKit.ApplyDpiScaling(this, ref _scaled, _designSize);
            };
            FormClosed += (s, e) => _instance = null;
        }

        public override void OnInitTxForm()
        {
            try { base.OnInitTxForm(); } catch (Exception ex) { SetStatus("窗口初始化提示：" + ex.Message); }
            EnsurePicker();
        }

        private void EnsurePicker()
        {
            if (_initialized || _pickerHost == null) return;
            _initialized = true;
            _picker.Init(_pickerHost, PointService.IsSupportedOperation,
                o => SetStatus("已忽略不支持的操作：" + o.Name),
                () => RefreshMatrix(false), SetStatus);
        }

        private void BuildUi()
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Padding = new Padding(10), BackColor = FormUiKit.WinBg
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 275));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var left = Card("选取焊接/连续点操作", 0);
            var leftLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(4), BackColor = FormUiKit.CardBack
            };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftLayout.Controls.Add(new Label
            {
                Text = "点选取行，再到 PS 树中依次选择焊接或连续点操作。",
                Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(240, 0),
                Font = FormUiKit.BaseFont
            }, 0, 0);
            _pickerHost = new Panel
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window
            };
            leftLayout.Controls.Add(_pickerHost, 0, 1);
            var clear = FormUiKit.MkFuncButton("清空操作", FormUiKit.Theme.BtnMuted);
            clear.Click += (s, e) => { _picker.Clear(); RefreshMatrix(true); };
            leftLayout.Controls.Add(clear, 0, 2);
            left.Controls.Add(leftLayout);

            var right = Card("操作点位", 0);
            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(4), BackColor = FormUiKit.CardBack
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
                BackColor = FormUiKit.CardBack
            };
            AddButton(actions, "刷新列表", FormUiKit.Theme.BtnMuted, () => RefreshMatrix(false));
            AddButton(actions, "插入空行", FormUiKit.Theme.BtnSecondary, InsertRow);
            AddButton(actions, "新增焊点", FormUiKit.Theme.BtnPrimary, () => AddPoint(true));
            AddButton(actions, "新增过渡点", FormUiKit.Theme.BtnPrimary, () => AddPoint(false));
            rightLayout.Controls.Add(actions, 0, 0);

            _table = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, MultiSelect = false,
                AllowDrop = true,
                RowHeadersVisible = true, RowHeadersWidth = 55,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                BackgroundColor = SystemColors.Window,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            _table.CellClick += TableCellClick;
            _table.MouseDown += TableMouseDown;
            _table.MouseMove += TableMouseMove;
            _table.DragEnter += TableDragOver;
            _table.DragOver += TableDragOver;
            _table.DragDrop += TableDragDrop;
            _table.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                var point = _table.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag as SpotData;
                if (point == null) return;
                e.CellStyle.ForeColor = point.Kind == PointType.WeldPoint
                    ? Color.FromArgb(0, 85, 140) : Color.FromArgb(100, 95, 50);
            };
            rightLayout.Controls.Add(_table, 0, 1);
            _status = new Label
            {
                Text = "点击点位可驱动机器人；拖动到同列或其他列，可调整顺序或所属操作。",
                Dock = DockStyle.Top, AutoSize = true, Font = FormUiKit.BaseFont,
                Padding = new Padding(4, 5, 4, 3)
            };
            rightLayout.Controls.Add(_status, 0, 2);
            right.Controls.Add(rightLayout);

            body.Controls.Add(left, 0, 0);
            body.Controls.Add(right, 1, 0);
            Controls.Add(body);
        }

        private static FormUiKit.ColoredGroupBox Card(string title, int height)
        {
            return new FormUiKit.ColoredGroupBox
            {
                Text = title, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0),
                Padding = new Padding(8, 18, 8, 8), Height = height,
                BackColor = FormUiKit.CardBack,
                BorderColor = FormUiKit.CardBorder,
                TitleColor = FormUiKit.Theme.CardTitle,
                Font = FormUiKit.BoldFont
            };
        }

        private static void AddButton(FlowLayoutPanel host, string text, Color color, Action click)
        {
            var button = FormUiKit.MkFuncButton(text, color);
            button.Click += (s, e) => click();
            host.Controls.Add(button);
        }

        private void SetStatus(string message)
        {
            if (_status != null) _status.Text = message;
        }

        private static bool Same(ITxObject a, ITxObject b)
        {
            if (ReferenceEquals(a, b)) return true;
            try { return a != null && b != null && a.Equals(b); }
            catch { return false; }
        }

        private bool OperationsChanged(List<ITxObject> selected)
        {
            if (selected.Count != _operations.Count) return true;
            for (int i = 0; i < selected.Count; i++)
                if (!Same(selected[i], _operations[i])) return true;
            return false;
        }

        private void RefreshMatrix(bool reset)
        {
            if (_table == null || _rendering) return;
            var selected = _picker.GetObjects();
            if (OperationsChanged(selected)) reset = true;
            _operations.Clear();
            _operations.AddRange(selected);

            var pointSets = new List<List<SpotData>>();
            foreach (ITxObject operation in _operations)
            {
                try { pointSets.Add(PointService.Read(operation, SetStatus).Ordered); }
                catch (Exception ex)
                {
                    pointSets.Add(new List<SpotData>());
                    SetStatus("读取操作 " + operation.Name + " 失败：" + ex.Message);
                }
            }
            if (reset)
            {
                _rows.Clear();
                int count = pointSets.Count == 0 ? 0 : pointSets.Max(p => p.Count);
                for (int row = 0; row < count; row++)
                {
                    var cells = new SpotData[_operations.Count];
                    for (int col = 0; col < cells.Length; col++)
                        if (row < pointSets[col].Count) cells[col] = pointSets[col][row];
                    _rows.Add(cells);
                }
            }
            else
            {
                for (int col = 0; col < pointSets.Count; col++)
                    ReconcileColumn(col, pointSets[col]);
            }
            RenderTable();
        }

        private void ReconcileColumn(int column, List<SpotData> fresh)
        {
            // 已插入的空行保留；按对象引用对齐外部改动或刚创建的点位。
            for (int row = 0; row < _rows.Count; row++)
            {
                var old = _rows[row][column];
                if (old == null) continue;
                var updated = fresh.FirstOrDefault(p => Same(p.LocOp, old.LocOp));
                _rows[row][column] = updated;
            }
            int cursor = 0;
            foreach (SpotData point in fresh)
            {
                int existing = -1;
                for (int row = cursor; row < _rows.Count; row++)
                    if (_rows[row][column] != null && Same(_rows[row][column].LocOp, point.LocOp))
                    { existing = row; break; }
                if (existing >= 0) { cursor = existing + 1; continue; }

                int boundary = _rows.Count;
                for (int row = cursor; row < _rows.Count; row++)
                    if (_rows[row][column] != null) { boundary = row; break; }
                int empty = -1;
                for (int row = cursor; row < boundary; row++)
                    if (_rows[row][column] == null) { empty = row; break; }
                if (empty < 0)
                {
                    _rows.Insert(boundary, new SpotData[_operations.Count]);
                    empty = boundary;
                }
                _rows[empty][column] = point;
                cursor = empty + 1;
            }
        }

        private void RenderTable()
        {
            int selectedRow = _table.CurrentCell?.RowIndex ?? -1;
            int selectedColumn = _table.CurrentCell?.ColumnIndex ?? -1;
            _rendering = true;
            try
            {
                _table.SuspendLayout();
                _table.Columns.Clear();
                foreach (ITxObject operation in _operations)
                {
                    _table.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        HeaderText = operation.Name, Width = 190,
                        SortMode = DataGridViewColumnSortMode.NotSortable,
                        ReadOnly = true, ToolTipText = operation.Name
                    });
                }
                foreach (SpotData[] row in _rows)
                {
                    int index = _table.Rows.Add();
                    _table.Rows[index].HeaderCell.Value = (index + 1).ToString();
                    for (int col = 0; col < row.Length; col++)
                    {
                        var point = row[col];
                        var cell = _table.Rows[index].Cells[col];
                        cell.Tag = point;
                        cell.Value = point == null ? "" : PointKindLabel(point) + "  " + point.Name;
                    }
                }
                if (selectedRow >= 0 && selectedRow < _table.RowCount
                    && selectedColumn >= 0 && selectedColumn < _table.ColumnCount)
                    _table.CurrentCell = _table.Rows[selectedRow].Cells[selectedColumn];
                else _table.ClearSelection();
            }
            finally { _table.ResumeLayout(); _rendering = false; }
        }

        private void TableCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_rendering || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if ((DateTime.UtcNow - _lastDropUtc).TotalMilliseconds < 300) return;
            var point = _rows[e.RowIndex][e.ColumnIndex];
            if (point == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                PointService.DriveRobot(_operations[e.ColumnIndex], point);
                SetStatus("已移动机器人到" + PointKindLabel(point) + "：" + point.Name);
            }
            catch (Exception ex) { SetStatus("定位失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; RefreshMatrix(false); }
        }

        private void TableMouseDown(object sender, MouseEventArgs e)
        {
            _dragRow = -1;
            _dragColumn = -1;
            if (e.Button != MouseButtons.Left) return;
            var hit = _table.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0 || hit.ColumnIndex < 0) return;
            if (_rows[hit.RowIndex][hit.ColumnIndex] == null) return;
            _dragStart = e.Location;
            _dragRow = hit.RowIndex;
            _dragColumn = hit.ColumnIndex;
        }

        private void TableMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragRow < 0 || _dragColumn < 0 || e.Button != MouseButtons.Left) return;
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(e.X - _dragStart.X) < dragSize.Width / 2
                && Math.Abs(e.Y - _dragStart.Y) < dragSize.Height / 2) return;
            var point = _rows[_dragRow][_dragColumn];
            if (point == null) return;
            var data = new PointDragData
            {
                SourceOperation = _operations[_dragColumn],
                Location = point.LocOp,
                Name = point.Name
            };
            _dragRow = -1;
            _dragColumn = -1;
            _table.DoDragDrop(data, DragDropEffects.Move);
        }

        private void TableDragOver(object sender, DragEventArgs e)
        {
            var data = e.Data.GetData(typeof(PointDragData)) as PointDragData;
            Point local = _table.PointToClient(new Point(e.X, e.Y));
            var hit = _table.HitTest(local.X, local.Y);
            bool valid = data != null && hit.RowIndex >= 0 && hit.ColumnIndex >= 0
                && hit.RowIndex < _rows.Count && hit.ColumnIndex < _operations.Count
                && _operations.Any(o => Same(o, data.SourceOperation));
            e.Effect = valid ? DragDropEffects.Move : DragDropEffects.None;
            if (valid)
            {
                bool after = DropAfter(hit.RowIndex, hit.ColumnIndex, local.Y);
                SetStatus("松开后将 “" + data.Name + "” 放到 “" +
                    _operations[hit.ColumnIndex].Name + "” 的第 " + (hit.RowIndex + 1) +
                    " 行" + (after ? "之后。" : "之前。"));
            }
        }

        private void TableDragDrop(object sender, DragEventArgs e)
        {
            var data = e.Data.GetData(typeof(PointDragData)) as PointDragData;
            if (data == null) return;
            Point local = _table.PointToClient(new Point(e.X, e.Y));
            var hit = _table.HitTest(local.X, local.Y);
            if (hit.RowIndex < 0 || hit.ColumnIndex < 0
                || hit.RowIndex >= _rows.Count || hit.ColumnIndex >= _operations.Count) return;
            ITxObject targetOperation = _operations[hit.ColumnIndex];
            var targetPoint = _rows[hit.RowIndex][hit.ColumnIndex];
            if (Same(targetOperation, data.SourceOperation)
                && targetPoint != null && Same(targetPoint.LocOp, data.Location))
            { SetStatus("点位仍在原位置。"); return; }
            bool after = DropAfter(hit.RowIndex, hit.ColumnIndex, local.Y);
            ITxObject predecessor = null;
            if (after && targetPoint != null) predecessor = targetPoint.LocOp;
            else for (int row = hit.RowIndex - 1; row >= 0; row--)
            {
                var candidate = _rows[row][hit.ColumnIndex]?.LocOp;
                if (candidate != null && !Same(candidate, data.Location))
                { predecessor = candidate; break; }
            }
            _lastDropUtc = DateTime.UtcNow;
            try
            {
                Cursor = Cursors.WaitCursor;
                PointService.MovePoint(data.SourceOperation, targetOperation,
                    data.Location, predecessor);
                RefreshMatrix(true);
                int movedRow = FindRow(hit.ColumnIndex, data.Location);
                if (movedRow >= 0)
                    _table.CurrentCell = _table.Rows[movedRow].Cells[hit.ColumnIndex];
                SetStatus("已调整点位 “" + data.Name + "” 的顺序/所属操作。");
            }
            catch (Exception ex)
            {
                RefreshMatrix(true);
                SetStatus("拖放失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, "调整点位失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private bool DropAfter(int row, int column, int localY)
        {
            if (_rows[row][column] == null) return false;
            Rectangle cell = _table.GetCellDisplayRectangle(column, row, false);
            return localY >= cell.Top + cell.Height / 2;
        }

        private int FindRow(int column, ITxObject location)
        {
            for (int row = 0; row < _rows.Count; row++)
                if (Same(_rows[row][column]?.LocOp, location)) return row;
            return -1;
        }

        private static string PointKindLabel(SpotData point)
        {
            if (point.Kind == PointType.WeldPoint) return "焊点";
            if (point.Kind == PointType.ContinuousPoint) return "连续点";
            return "过渡点";
        }

        private void InsertRow()
        {
            if (_operations.Count == 0) { SetStatus("请先选取操作。"); return; }
            int row = _table.CurrentCell?.RowIndex ?? _rows.Count;
            int col = _table.CurrentCell?.ColumnIndex ?? 0;
            _rows.Insert(row, new SpotData[_operations.Count]);
            RenderTable();
            _table.CurrentCell = _table.Rows[row].Cells[col];
            SetStatus("已插入空行；选中操作列后点击“新增焊点”或“新增过渡点”。");
        }

        private void AddPoint(bool weld)
        {
            if (_operations.Count == 0 || _table.CurrentCell == null)
            { SetStatus("请先选取操作，并选中右侧表格的单元格。"); return; }
            int row = _table.CurrentCell.RowIndex;
            int col = _table.CurrentCell.ColumnIndex;
            if (weld && !PointService.IsWeldOperation(_operations[col]))
            { SetStatus("连续点操作不能直接新增焊点；请选择焊接操作列。"); return; }
            bool insertedAutomatically = false;
            if (_rows[row][col] != null)
            {
                _rows.Insert(row, new SpotData[_operations.Count]);
                insertedAutomatically = true;
                RenderTable();
                _table.CurrentCell = _table.Rows[row].Cells[col];
            }
            ITxObject predecessor = null;
            SpotData neighbor = null;
            for (int i = row - 1; i >= 0; i--)
                if (_rows[i][col] != null)
                { predecessor = _rows[i][col].LocOp; neighbor = _rows[i][col]; break; }
            if (neighbor == null)
                for (int i = row + 1; i < _rows.Count; i++)
                    if (_rows[i][col] != null) { neighbor = _rows[i][col]; break; }

            double[] matrix = neighbor?.Matrix == null
                ? new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
                : (double[])neighbor.Matrix.Clone();
            using (var dialog = new PointInputDialog(weld, matrix))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    if (insertedAutomatically) { _rows.RemoveAt(row); RenderTable(); }
                    return;
                }
                try
                {
                    Cursor = Cursors.WaitCursor;
                    ITxObject created = PointService.CreatePoint(_operations[col], predecessor,
                        weld, dialog.PointName, dialog.Matrix);
                    var fresh = PointService.Read(_operations[col], SetStatus).Ordered;
                    _rows[row][col] = fresh.FirstOrDefault(p => Same(p.LocOp, created));
                    RefreshMatrix(false);
                    _table.CurrentCell = _table.Rows[row].Cells[col];
                    SetStatus("已创建" + (weld ? "焊点" : "过渡点") + "：" + dialog.PointName);
                }
                catch (Exception ex)
                {
                    SetStatus("新增点位失败：" + ex.Message);
                    MessageBox.Show(this, ex.Message, "新增点位失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    RefreshMatrix(false);
                }
                finally { Cursor = Cursors.Default; }
            }
        }
    }

    internal sealed class PointInputDialog : Form
    {
        private readonly TextBox _name = new TextBox();
        private readonly TextBox[] _coordinates = { new TextBox(), new TextBox(), new TextBox() };
        private readonly double[] _matrix;
        internal string PointName { get; private set; }
        internal double[] Matrix => _matrix;

        internal PointInputDialog(bool weld, double[] matrix)
        {
            _matrix = matrix;
            Text = weld ? "新增焊点" : "新增过渡点";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(340, 190);
            Font = FormUiKit.BaseFont;
            BackColor = FormUiKit.WinBg;
            _name.Text = (weld ? "Weld_" : "Via_") + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
            AddField("名称", _name, 12);
            string[] labels = { "X (mm)", "Y (mm)", "Z (mm)" };
            int[] offsets = { 3, 7, 11 };
            for (int i = 0; i < 3; i++)
            {
                _coordinates[i].Text = _matrix[offsets[i]].ToString("0.###", CultureInfo.InvariantCulture);
                AddField(labels[i], _coordinates[i], 44 + i * 32);
            }
            var ok = FormUiKit.MkFuncButton("创建", FormUiKit.Theme.BtnPrimary);
            ok.Location = new Point(170, 148);
            ok.Click += (s, e) => Submit();
            Controls.Add(ok);
            var cancel = FormUiKit.MkFuncButton("取消", FormUiKit.Theme.BtnMuted);
            cancel.Location = new Point(250, 148);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void AddField(string label, TextBox input, int y)
        {
            Controls.Add(new Label { Text = label, Location = new Point(12, y + 4), Size = new Size(90, 22) });
            input.Location = new Point(105, y);
            input.Width = 215;
            Controls.Add(input);
        }

        private void Submit()
        {
            string name = _name.Text.Trim();
            if (name.Length == 0) { MessageBox.Show(this, "请输入点位名称。"); return; }
            int[] offsets = { 3, 7, 11 };
            for (int i = 0; i < 3; i++)
            {
                double value;
                string text = _coordinates[i].Text.Trim();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                { MessageBox.Show(this, "坐标必须是有效数字。"); return; }
                _matrix[offsets[i]] = value;
            }
            PointName = name;
            DialogResult = DialogResult.OK;
        }
    }
}
